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
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private TaskCompletionSource<object> connectionReady = new TaskCompletionSource<object>();
        private readonly TaskCompletionSource<object> initialConnection = new TaskCompletionSource<object>();
        private CancellationTokenSource activeConnectionCancellation;
        private Task supervisorTask;
        private Task reportTask;
        private bool started;
        private bool disposed;

        // Called before each connection attempt; returning false stops the supervision loop.
        // Null means no-op (always continue).
        private readonly Func<bool> ensureServerAlive;

        public RpcConnectionLoop(
            Uri endpoint,
            RpcCommandRegistry registry,
            PendingRequestStore store,
            EditorMainThreadDispatcher dispatcher,
            EditorSession editorSession,
            IIdGenerator idGenerator,
            bool ownsDispatcher,
            string bearerToken,
            Func<bool> ensureServerAlive = null)
        {
            this.ensureServerAlive = ensureServerAlive;
            client = new RpcWebSocketClient(endpoint, bearerToken);
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            this.ownsDispatcher = ownsDispatcher;
            this.editorSession = editorSession ?? throw new ArgumentNullException(nameof(editorSession));
            this.idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
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
            supervisorTask = SuperviseConnectionAsync(cancellation.Token);
            reportTask = ReportLoopAsync(cancellation.Token);
            await initialConnection.Task;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

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
                    if (ensureServerAlive != null && !ensureServerAlive())
                    {
                        break;
                    }

                    await client.ConnectAsync(cancellationToken);
                    var registration = await SendRequestCoreAsync(
                        idGenerator.NewId("unity"),
                        RpcMethods.UnityRegister,
                        BuildRegistrationParams(),
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
                    // Log only the first failure in a streak so a server that is
                    // simply not running yet does not flood the Console with an
                    // identical warning every reconnect attempt.
                    if (consecutiveFailures == 0)
                    {
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
            var completion = new TaskCompletionSource<JsonRpcResponse>();
            lock (sync)
            {
                pendingResponses.Add(id, completion);
            }

            try
            {
                await client.SendAsync(JsonRpcSerializer.SerializeRequest(id, method, @params), cancellationToken);
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
                await client.SendAsync(JsonRpcSerializer.SerializeError(id, exception.ErrorCode, exception.Message, errorCode, null));
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

        private async Task SendAcceptanceAndScheduleExecutionAsync(
            string requestId, JsonRpcResponse response, ILongRunningCommand command)
        {
            try
            {
                await client.SendAsync(SerializeResponse(response));
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
                await client.SendAsync(SerializeResponse(response));
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
            return response.Error == null
                ? JsonRpcSerializer.SerializeSuccess(response.Id, response.Result)
                : JsonRpcSerializer.SerializeError(response.Id, response.Error.Code, response.Error.Message, response.Error.Data);
        }
    }
}
