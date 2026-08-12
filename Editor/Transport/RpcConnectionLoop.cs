using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LitJson;
using UnityEditor;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Commands;
using VeryFS.UnityMCP.Editor.Compilation;
using VeryFS.UnityMCP.Editor.Infrastructure;
using VeryFS.UnityMCP.Editor.Persistence;
using VeryFS.UnityMCP.Editor.Protocol;
using Process = System.Diagnostics.Process;

namespace VeryFS.UnityMCP.Editor.Transport
{
    internal sealed class RpcConnectionLoop : IDisposable
    {
        private const int ReconnectDelayMs = 100;
        private const int MaxReconnectDelayMs = 5000;
        private const int RequestTimeoutMs = 3000;

        // A rejected token is not a transient failure, so it does not share the
        // "log the first failure of a streak, then go quiet" policy below. It still
        // gets throttled: reconnects settle at MaxReconnectDelayMs, and one Console
        // error every five seconds for the rest of the session is its own problem.
        private const int AuthenticationFailureLogThrottleMs = 30000;

        // Report loop polling. Idle polling is deliberately slow so we are not
        // hitting the disk on the main thread 100x/second when there is nothing
        // to report; once terminal reports exist we poll faster to retry the
        // acknowledgement until the store is drained.
        private const int ReportIdlePollMs = 250;
        private const int ReportActivePollMs = 25;

        private readonly RpcWebSocketClient client;
        private readonly RpcCommandRegistry registry;
        private readonly PendingRequestStore store;
        private readonly EditorMainThreadDispatcher dispatcher;
        private readonly bool ownsDispatcher;
        private readonly EditorSession editorSession;
        private readonly IIdGenerator idGenerator;
        private readonly object sync = new object();
        private readonly Dictionary<string, TaskCompletionSource<JsonRpcResponse>> pendingResponses =
            new Dictionary<string, TaskCompletionSource<JsonRpcResponse>>();
        private readonly HashSet<string> reportRequestIds = new HashSet<string>();

        // Built once on the main thread in the constructor because the supervision loop
        // that sends it runs on the thread pool, and two of its values (Application's
        // dataPath and unityVersion) are main-thread-only Editor APIs. Deliberately not
        // marshalled back through the dispatcher like the heartbeat's state is: register
        // is the message that decides whether we are connected at all, so it must not
        // queue behind whatever the main thread is doing. Every field in it is stable for
        // the life of the process — the server's handleRegister identifies a reconnecting
        // session by exactly this editorSessionId.
        private readonly JsonData registrationParams;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private TaskCompletionSource<object> connectionReady = new TaskCompletionSource<object>();
        private readonly TaskCompletionSource<object> initialConnection = new TaskCompletionSource<object>();
        private CancellationTokenSource activeConnectionCancellation;
        private Task supervisorTask;
        private Task reportTask;
        private bool started;
        private DateTime lastAuthenticationFailureLogUtc = DateTime.MinValue;
        private bool disposed;

        // Called before each connection attempt; returning false stops the supervision loop.
        // Null means no-op (always continue).
        //
        // Runs on the main thread, reached through the dispatcher: the production
        // implementation probes and may spawn the server, and on its way through
        // ServerLauncher it reads EditorPrefs and writes SessionState — neither of which
        // may be touched off the main thread, and both of which sit on the path taken
        // every time the server is already alive.
        private readonly Func<bool> ensureServerAlive;

        // Returns true while a Unity test run owns the Editor. Null means no-op
        // (never blocked). See TestRunGate for the method whitelist.
        private readonly Func<bool> testsRunning;

        public RpcConnectionLoop(
            Uri endpoint,
            RpcCommandRegistry registry,
            PendingRequestStore store,
            EditorMainThreadDispatcher dispatcher,
            EditorSession editorSession,
            IIdGenerator idGenerator,
            bool ownsDispatcher,
            string bearerToken,
            Func<bool> ensureServerAlive = null,
            Func<bool> testsRunning = null)
        {
            this.testsRunning = testsRunning;
            this.ensureServerAlive = ensureServerAlive;
            client = new RpcWebSocketClient(endpoint, bearerToken);
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            this.ownsDispatcher = ownsDispatcher;
            this.editorSession = editorSession ?? throw new ArgumentNullException(nameof(editorSession));
            this.idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));

            // Freezes the registry here, one construction earlier than the first inbound
            // request used to. Every assembly point registers its commands before
            // constructing the loop, and one that ever stops doing so gets a loud
            // "already built" from the registry rather than a silent omission.
            registrationParams = BuildRegistrationParams();
            client.MessageReceived += OnMessageReceived;
            client.ConnectionClosed += OnConnectionClosed;
        }

        public async Task StartAsync()
        {
            if (started)
            {
                throw new InvalidOperationException("The RPC connection loop has already started.");
            }

            started = true;

            // Both loops run off the Editor's synchronization context, and for a harsher
            // reason than the receive loop's tick cost. A project that installs its own
            // SynchronizationContext on the main thread (a UniTask-like framework doing
            // SetSynchronizationContext in its own bootstrap, for instance) orphans every
            // continuation already queued on Unity's: UnitySynchronizationContext.ExecuteTasks
            // starts with `Current as UnitySynchronizationContext` and returns immediately
            // when that is null, so the queue the connect continuation is sitting in is
            // never drained again for the life of the domain. Measured against a project
            // like that: entering play mode reconnected the socket but never sent register,
            // and /health reported unityConnected:false until the next domain reload.
            //
            // On the pool there is no ambient context to capture, so nothing in these two
            // loops depends on which SynchronizationContext the main thread happens to have.
            // What genuinely needs the main thread goes through EditorMainThreadDispatcher,
            // which is driven by EditorApplication.update and does not care either.
            //
            // The token is read here rather than inside the lambdas on purpose. Inside, the
            // read would happen on a pool thread at an unknown later moment, and
            // CancellationTokenSource.Token throws once Dispose has run — so a loop started
            // and torn down in the same frame (StartAsync followed by a domain reload) would
            // fault its task with an ObjectDisposedException nobody observes. Read on this
            // thread it cannot race Dispose, which runs on this thread too, and the struct
            // the lambdas capture keeps reporting cancellation after the source is gone.
            var cancellationToken = cancellation.Token;
            supervisorTask = Task.Run(() => SuperviseConnectionAsync(cancellationToken));
            reportTask = Task.Run(() => ReportLoopAsync(cancellationToken));
            await initialConnection.Task;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            // Returns without waiting for supervisorTask or reportTask, deliberately
            // unlike RpcWebSocketClient.Dispose which does give its receive loop a bounded
            // moment to unwind. The receive loop needs that because it fans messages out to
            // subscribers, and a call already in flight keeps dispatching commands into an
            // Editor being torn down. These two loops fan out to nothing: Cancel() below
            // makes every exception they can still raise land in their
            // `when (cancellationToken.IsCancellationRequested)` filters and end the loop
            // quietly. Waiting would also invert a dependency — a supervisor parked on
            // dispatcher.Enqueue is waiting for the main thread's Drain, which is the very
            // thread that would be doing the waiting, so the whole bounded wait would be
            // burned on every reload that hits that window. What actually stops them is
            // this cancellation plus the socket the client closes below.
            disposed = true;
            cancellation.Cancel();
            FailPendingResponses(new OperationCanceledException("The RPC connection loop was disposed."));
            initialConnection.TrySetCanceled();
            client.MessageReceived -= OnMessageReceived;
            client.ConnectionClosed -= OnConnectionClosed;
            client.Dispose();
            if (ownsDispatcher)
            {
                dispatcher.Dispose();
            }

            cancellation.Dispose();
        }

        private async Task SuperviseConnectionAsync(CancellationToken cancellationToken)
        {
            var consecutiveFailures = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!await ShouldKeepConnectingAsync())
                    {
                        break;
                    }

                    await client.ConnectAsync(cancellationToken);
                    var registration = await SendRequestCoreAsync(
                        idGenerator.NewId("unity"),
                        RpcMethods.UnityRegister,
                        registrationParams,
                        cancellationToken);
                    var heartbeatIntervalMs = ReadHeartbeatInterval(registration);
                    await RecoverPendingRequestsAsync();
                    MarkConnected();
                    if (consecutiveFailures > 0)
                    {
                        Debug.Log("Unity MCP: reconnected to server after " + consecutiveFailures + " failed attempt(s).");
                    }
                    else
                    {
                        Debug.Log("Unity MCP: connected to server.");
                    }
                    consecutiveFailures = 0;
                    lastAuthenticationFailureLogUtc = DateTime.MinValue;
                    initialConnection.TrySetResult(null);

                    using (var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        lock (sync)
                        {
                            activeConnectionCancellation = connectionCancellation;
                        }

                        var heartbeat = HeartbeatLoopAsync(heartbeatIntervalMs, connectionCancellation.Token);
                        var receive = client.ReceiveTask;
                        await Task.WhenAny(receive, heartbeat);
                        connectionCancellation.Cancel();
                        await ObserveConnectionTaskAsync(receive);
                        await ObserveConnectionTaskAsync(heartbeat);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    if (IsAuthenticationFailure(exception))
                    {
                        LogAuthenticationFailure(exception);
                    }
                    else if (consecutiveFailures == 0)
                    {
                        // Log only the first failure in a streak so a server that is
                        // simply not running yet does not flood the Console with an
                        // identical warning every reconnect attempt.
                        Debug.LogWarning("Unity MCP RPC connection closed: " + exception.Message +
                            " (retrying; further attempts will be silent until reconnected)");
                    }

                    consecutiveFailures++;
                }
                finally
                {
                    MarkDisconnected(new IOException("The RPC WebSocket connection closed."));
                    lock (sync)
                    {
                        activeConnectionCancellation = null;
                    }
                }

                try
                {
                    await Task.Delay(ReconnectDelay(consecutiveFailures), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        // The gate itself belongs on the main thread — see the ensureServerAlive field for
        // which Editor APIs it reaches — so the loop hops there for it rather than calling
        // it inline on the pool. This does not put back the dependency StartAsync's
        // Task.Run removed: the dispatcher is a ConcurrentQueue drained from
        // EditorApplication.update, so it works whatever SynchronizationContext the main
        // thread carries.
        private async Task<bool> ShouldKeepConnectingAsync()
        {
            if (ensureServerAlive == null)
            {
                return true;
            }

            var keepConnecting = true;
            await dispatcher.Enqueue(() => keepConnecting = ensureServerAlive());
            return keepConnecting;
        }

        // Exponential backoff capped at MaxReconnectDelayMs. A successful
        // connection resets the streak, so the first retry after a live
        // disconnect is still fast (ReconnectDelayMs).
        private static int ReconnectDelay(int consecutiveFailures)
        {
            if (consecutiveFailures <= 1)
            {
                return ReconnectDelayMs;
            }

            var exponent = Math.Min(consecutiveFailures - 1, 30);
            var scaled = (long)ReconnectDelayMs * (1L << exponent);
            return (int)Math.Min(scaled, MaxReconnectDelayMs);
        }

        private static bool IsAuthenticationFailure(Exception exception)
        {
            return (exception as WebSocketHandshakeException)?.IsAuthenticationFailure == true ||
                (exception?.InnerException as WebSocketHandshakeException)?.IsAuthenticationFailure == true;
        }

        private void LogAuthenticationFailure(Exception exception)
        {
            var now = DateTime.UtcNow;
            if (now - lastAuthenticationFailureLogUtc < TimeSpan.FromMilliseconds(AuthenticationFailureLogThrottleMs))
            {
                return;
            }

            lastAuthenticationFailureLogUtc = now;
            Debug.LogError("Unity MCP: the server rejected this Editor's Unity token (" + exception.Message +
                "). They disagree on the token and reconnecting will not fix it — restart Unity, " +
                "or kill the server process so a new one is spawned with a matching token.");
        }

        private async Task HeartbeatLoopAsync(int intervalMs, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(intervalMs, cancellationToken);
                await SendRequestAsync(
                    idGenerator.NewId("hb"),
                    RpcMethods.UnityHeartbeat,
                    await BuildHeartbeatParamsAsync(),
                    cancellationToken);
            }
        }

        private async Task ReportLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var terminalReports = await LoadTerminalReportsAsync();
                    foreach (var request in terminalReports)
                    {
                        try
                        {
                            await SendTerminalReportAsync(request, cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch
                        {
                            break;
                        }
                    }

                    // Poll quickly while reports remain (to retry acks), slowly
                    // when idle to avoid constant main-thread disk reads.
                    var delayMs = terminalReports.Count > 0 ? ReportActivePollMs : ReportIdlePollMs;
                    await Task.Delay(delayMs, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // Teardown, not a failure. Running on the pool, this loop can now be midway
                // through LoadTerminalReportsAsync when Dispose disposes the dispatcher, and
                // the ObjectDisposedException that comes back is not an OperationCanceled.
                // Left to escape it would fault reportTask, which nobody awaits, and resurface
                // at GC time as an unobserved task exception in whatever ran next.
                // (IsCancellationRequested stays readable after the source is disposed; only
                // Token and Register throw there.)
            }
        }

        private async Task SendTerminalReportAsync(PendingRefreshRequest request, CancellationToken cancellationToken)
        {
            lock (sync)
            {
                if (!reportRequestIds.Add(request.OriginRequestId))
                {
                    return;
                }
            }

            try
            {
                JsonData reportParams;
                if (registry.TryGet(request.Method, out var command) && command is ILongRunningCommand longRunning)
                {
                    reportParams = longRunning.BuildReportParams(request);
                }
                else
                {
                    reportParams = RefreshResultBuilder.BuildReportParams(request);
                }

                var response = await SendRequestAsync(
                    idGenerator.NewId("report"),
                    RpcMethods.RequestsReport,
                    reportParams,
                    cancellationToken);
                if (IsAcknowledged(response))
                {
                    await dispatcher.Enqueue(() => store.Delete(request.OriginRequestId));
                }
            }
            finally
            {
                lock (sync)
                {
                    reportRequestIds.Remove(request.OriginRequestId);
                }
            }
        }

        private Task<List<PendingRefreshRequest>> LoadTerminalReportsAsync()
        {
            var completion = new TaskCompletionSource<List<PendingRefreshRequest>>();
            dispatcher.Enqueue(() =>
            {
                var terminalReports = new List<PendingRefreshRequest>();
                foreach (var request in store.LoadAll())
                {
                    if (IsTerminal(request) && !request.ReportAcknowledged)
                    {
                        terminalReports.Add(request);
                    }
                }

                completion.TrySetResult(terminalReports);
            });
            return completion.Task;
        }

        private async Task RecoverPendingRequestsAsync()
        {
            await dispatcher.Enqueue(() =>
            {
                foreach (var request in store.LoadAll())
                {
                    if (IsTerminal(request) || request.ReportAcknowledged)
                    {
                        continue;
                    }

                    if (registry.TryGet(request.Method, out var command) && command is ILongRunningCommand longRunning)
                    {
                        longRunning.RecoverPending(request);
                    }
                }
            });
        }

        private async Task<JsonData> BuildHeartbeatParamsAsync()
        {
            var completion = new TaskCompletionSource<JsonData>();
            await dispatcher.Enqueue(() => completion.TrySetResult(JsonRpcSerializer.Object(
                ("editorSessionId", editorSession.EditorSessionId),
                ("state", JsonRpcSerializer.Object(
                    ("isCompiling", EditorApplication.isCompiling),
                    ("isUpdating", EditorApplication.isUpdating))))));
            return await completion.Task;
        }

        private async Task<JsonRpcResponse> SendRequestAsync(
            string id,
            string method,
            JsonData @params,
            CancellationToken cancellationToken)
        {
            await WaitForConnectionAsync(cancellationToken);
            return await SendRequestCoreAsync(id, method, @params, cancellationToken);
        }

        private async Task<JsonRpcResponse> SendRequestCoreAsync(
            string id,
            string method,
            JsonData @params,
            CancellationToken cancellationToken)
        {
            // Completed by CompleteResponse on the receive loop's thread pool thread, so
            // TrySetResult would otherwise run continuations inline on the thread that
            // has to get back to reading. Defensive rather than load-bearing today: every
            // current caller awaits from the main thread, so what runs inline is a Post
            // back to it. A caller that ever awaits from the pool would run its own
            // continuation on the receive thread instead.
            var completion = new TaskCompletionSource<JsonRpcResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (sync)
            {
                pendingResponses.Add(id, completion);
            }

            try
            {
                await client.SendAsync(JsonRpcSerializer.SerializeRequest(id, method, @params), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RemovePendingResponse(id);
                HandleConnectionFailure(exception);
                throw;
            }

            var timeout = Task.Delay(RequestTimeoutMs, cancellationToken);
            var completed = await Task.WhenAny(completion.Task, timeout);
            if (completed != completion.Task)
            {
                RemovePendingResponse(id);
                var timeoutException = new TimeoutException("Timed out waiting for RPC response " + id + ".");
                HandleConnectionFailure(timeoutException);
                throw timeoutException;
            }

            return await completion.Task;
        }

        private async Task WaitForConnectionAsync(CancellationToken cancellationToken)
        {
            Task ready;
            lock (sync)
            {
                ready = connectionReady.Task;
            }

            var cancellationTask = Task.Delay(Timeout.Infinite, cancellationToken);
            if (await Task.WhenAny(ready, cancellationTask) != ready)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            await ready;
        }

        private void OnMessageReceived(string json)
        {
            try
            {
                var message = JsonRpcSerializer.Parse(json);
                if (message.Request != null)
                {
                    HandleServerRequest(message.Request);
                    return;
                }

                CompleteResponse(message);
            }
            catch (RpcProtocolException exception)
            {
                _ = SendProtocolErrorAsync(JsonRpcSerializer.TryGetStringId(json), exception);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        private async Task SendProtocolErrorAsync(string id, RpcProtocolException exception)
        {
            var errorCode = exception.ErrorCode == JsonRpcErrorCodes.ParseError ? "parse_error" : "invalid_request";
            try
            {
                await client.SendAsync(JsonRpcSerializer.SerializeError(id, exception.ErrorCode, exception.Message, errorCode, null))
                    .ConfigureAwait(false);
            }
            catch (Exception sendException)
            {
                HandleConnectionFailure(sendException);
            }
        }

        private void HandleServerRequest(JsonRpcRequest request)
        {
            if (request.Method != RpcMethods.UnityHeartbeat)
            {
                Debug.Log("Unity MCP: executing " + request.Method);
            }

            if (!registry.TryGet(request.Method, out var command))
            {
                _ = SendResponseAsync(JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                    JsonRpcErrorCodes.MethodNotFound,
                    "Unknown RPC method.",
                    JsonRpcSerializer.Object(("errorCode", "method_not_found")))));
                return;
            }

            if (testsRunning != null && testsRunning() && !TestRunGate.IsAllowedDuringTestRun(request.Method))
            {
                _ = SendResponseAsync(JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                    JsonRpcErrorCodes.EditorBusy,
                    "A Unity test run is in progress.",
                    JsonRpcSerializer.Object(("errorCode", "tests_running")))));
                return;
            }

            if (command is IAsyncRpcCommand asyncCommand)
            {
                dispatcher.Enqueue(async () =>
                {
                    var response = await asyncCommand.HandleAsync(request);
                    await SendResponseAsync(response);
                });
                return;
            }

            if (command is ILongRunningCommand longRunning)
            {
                dispatcher.Enqueue(() =>
                {
                    var response = command.Handle(request);
                    _ = SendAcceptanceAndScheduleExecutionAsync(request.Id, response, longRunning);
                });
                return;
            }

            dispatcher.Enqueue(() =>
            {
                var response = command.Handle(request);
                _ = SendResponseAsync(response);
            });
        }

        // The four awaits of client.SendAsync in this class all use ConfigureAwait(false),
        // and the rule is worth stating as one thing rather than four: no continuation of a
        // send in this class needs the main thread. Each either only touches fields and
        // locks, or already hops back explicitly through the dispatcher (ExecuteAccepted
        // below). Stated per-call-site it would be unauditable, and an audit is the point —
        // the failure mode of a missed one is silent.
        private async Task SendAcceptanceAndScheduleExecutionAsync(
            string requestId, JsonRpcResponse response, ILongRunningCommand command)
        {
            try
            {
                await client.SendAsync(SerializeResponse(response)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                HandleConnectionFailure(exception);
                return;
            }

            if (response.Error == null)
            {
                await dispatcher.Enqueue(() => command.ExecuteAccepted(requestId));
            }
        }

        private async Task SendResponseAsync(JsonRpcResponse response)
        {
            try
            {
                await client.SendAsync(SerializeResponse(response)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                HandleConnectionFailure(exception);
            }
        }

        private void CompleteResponse(JsonRpcResponse response)
        {
            TaskCompletionSource<JsonRpcResponse> completion = null;
            lock (sync)
            {
                if (response.Id != null && pendingResponses.TryGetValue(response.Id, out completion))
                {
                    pendingResponses.Remove(response.Id);
                }
            }

            completion?.TrySetResult(response);
        }

        private void OnConnectionClosed(Exception exception)
        {
            MarkDisconnected(exception ?? new IOException("The RPC WebSocket connection closed."));
            lock (sync)
            {
                activeConnectionCancellation?.Cancel();
            }
        }

        private void HandleConnectionFailure(Exception exception)
        {
            MarkDisconnected(exception);
            client.Abort();
        }

        private void MarkConnected()
        {
            lock (sync)
            {
                connectionReady.TrySetResult(null);
            }
        }

        private void MarkDisconnected(Exception exception)
        {
            FailPendingResponses(exception);
            lock (sync)
            {
                if (connectionReady.Task.IsCompleted)
                {
                    connectionReady = new TaskCompletionSource<object>();
                }
            }
        }

        private void FailPendingResponses(Exception exception)
        {
            List<TaskCompletionSource<JsonRpcResponse>> completions;
            lock (sync)
            {
                completions = new List<TaskCompletionSource<JsonRpcResponse>>(pendingResponses.Values);
                pendingResponses.Clear();
            }

            foreach (var completion in completions)
            {
                completion.TrySetException(exception);
            }
        }

        private void RemovePendingResponse(string id)
        {
            lock (sync)
            {
                pendingResponses.Remove(id);
            }
        }

        private JsonData BuildRegistrationParams()
        {
            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return JsonRpcSerializer.Object(
                ("protocolVersion", 1),
                ("editorSessionId", editorSession.EditorSessionId),
                ("editorPid", Process.GetCurrentProcess().Id),
                ("projectPath", projectRoot),
                ("unityVersion", Application.unityVersion),
                ("pluginVersion", "0.1.0"),
                ("tools", registry.BuildToolsArray()));
        }

        private static async Task ObserveConnectionTaskAsync(Task task)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static int ReadHeartbeatInterval(JsonRpcResponse response)
        {
            if (response.Error != null || response.Result == null || !response.Result.IsObject ||
                !response.Result.ContainsKey("accepted") || !response.Result["accepted"].IsBoolean ||
                !(bool)response.Result["accepted"])
            {
                throw new InvalidOperationException("The RPC server rejected unity.register.");
            }

            if (!response.Result.ContainsKey("heartbeatIntervalMs") || !response.Result["heartbeatIntervalMs"].IsInt)
            {
                throw new InvalidOperationException("The RPC server did not provide heartbeatIntervalMs.");
            }

            return Math.Max(1, (int)response.Result["heartbeatIntervalMs"]);
        }

        private static bool IsAcknowledged(JsonRpcResponse response)
        {
            return response.Error == null && response.Result != null && response.Result.IsObject &&
                response.Result.ContainsKey("acknowledged") && response.Result["acknowledged"].IsBoolean &&
                (bool)response.Result["acknowledged"];
        }

        private static bool IsTerminal(PendingRefreshRequest request)
        {
            return request.State == "succeeded" || request.State == "failed";
        }

        private static string SerializeResponse(JsonRpcResponse response)
        {
            return response.ToJson();
        }
    }
}
