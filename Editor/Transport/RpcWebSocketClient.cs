using System;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VeryFS.UnityMCP.Editor.Transport
{
    public sealed class RpcWebSocketClient : IDisposable
    {
        public const string Subprotocol = "veryfreestyle.unity-rpc.v1";
        public const int MaxMessageSizeBytes = 8 * 1024 * 1024;

        // Ceiling on one connect attempt. The TCP connect and the WebSocket handshake
        // share this single budget, so ConnectAsync returns within it no matter which
        // half stalls. Kept under the server's inbound-frame watchdog so a stalled
        // attempt is abandoned and retried rather than left for the peer to reap.
        private const int DefaultConnectTimeoutMs = 5000;

        // How long Dispose() gives the receive loop to unwind. Long enough for a loop
        // parked in a cancelled read or midway through raising MessageReceived, short
        // enough that a wedged one delays a domain reload rather than blocking it.
        private const int DisposeReceiveDrainMs = 200;

        private readonly Uri endpoint;
        private readonly string bearerToken;
        private readonly int connectTimeoutMs;
        private readonly object sync = new object();
        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        private WebSocket webSocket;
        private TcpClient tcpClient;

        // The socket of an attempt that is still being established. Without it the only
        // reference to that socket is ConnectAsync's local, so Dispose() and Abort() see
        // a null tcpClient and close nothing, leaving the close entirely to ConnectAsync's
        // own timeout branch. That branch is up to connectTimeoutMs away and its caller is
        // torn down long before then: Dispose() runs synchronously from
        // beforeAssemblyReload, so a connect in flight across a domain reload would hold an
        // ESTABLISHED connection the peer still sees, for as long as it took the CLR to get
        // around to the TcpClient finalizer.
        private TcpClient connectingTcpClient;
        private CancellationTokenSource receiveCancellation;
        private Task receiveTask;
        private bool disposed;

        public RpcWebSocketClient(Uri endpoint)
            : this(endpoint, null)
        {
        }

        public RpcWebSocketClient(Uri endpoint, string bearerToken, int connectTimeoutMs = DefaultConnectTimeoutMs)
        {
            this.endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            this.bearerToken = bearerToken;
            this.connectTimeoutMs = connectTimeoutMs;
        }

        // Both raised off the Editor's main thread — see ReceiveLoopAsync for why.
        // Subscribers that touch Unity APIs have to marshal for themselves.
        //
        // MessageReceived is ordered against itself: one loop raises it, and that loop
        // awaits the next read before raising again. ConnectionClosed carries no such
        // guarantee — the send path raises it too, so it can fire concurrently with a
        // MessageReceived already in flight.
        public event Action<string> MessageReceived;
        public event Action<Exception> ConnectionClosed;

        public Task ReceiveTask
        {
            get
            {
                lock (sync)
                {
                    return receiveTask;
                }
            }
        }

        public Task ConnectAsync()
        {
            return ConnectAsync(CancellationToken.None);
        }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            // Plaintext loopback is the only shape this transport is built for. TLS on
            // a stream we own is a different design; refuse rather than silently
            // downgrade a wss:// endpoint to an unencrypted connection.
            if (!string.Equals(endpoint.Scheme, "ws", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    "Only ws:// endpoints are supported, got " + endpoint.Scheme + "://.");
            }

            // Two addressing assumptions live in this line and the endpoint.Port below:
            // the parameterless TcpClient is IPv4-only (AddressFamily.InterNetwork), and
            // the URL must carry an explicit port. Both hold because the only endpoint this
            // transport is ever handed is the loopback ws://127.0.0.1:<port>/unity the
            // server publishes. A hostname resolving only to IPv6 would need more than this
            // connect path, and a portless URL fails quietly rather than loudly: Mono
            // registers the ws scheme with a default port, so ws://host/unity yields
            // Port 80 and we would connect to the wrong port instead of throwing (measured
            // on 2021.3.45f2c1 and 2022.3.62f3: Port=80, IsDefaultPort=True).
            var tcp = new TcpClient();
            WebSocket socket;

            lock (sync)
            {
                if (disposed)
                {
                    tcp.Close();
                    throw new ObjectDisposedException(nameof(RpcWebSocketClient));
                }

                // Published before the race starts, so Dispose() and Abort() can reach this
                // socket for the entire time the handshake is in flight.
                connectingTcpClient = tcp;
            }

            // Bound the whole attempt. The timeout is raced rather than handed to a
            // cancellation token, because neither the Mono socket connect nor
            // NetworkStream reads reliably observe one. What makes the race sufficient
            // is that Close() below actually severs the connection: we opened the
            // socket, so we can shut it. ClientWebSocket could not — its socket lived
            // inside the pending handshake state machine where Abort, Dispose,
            // cancellation, GC and reflection all failed to reach it, leaking one
            // ESTABLISHED connection per attempt.
            try
            {
                var handshake = ConnectAndHandshakeAsync(tcp, cancellationToken);
                var timeout = Task.Delay(connectTimeoutMs, cancellationToken);
                if (await Task.WhenAny(handshake, timeout) != handshake)
                {
                    Unpublish(tcp);
                    tcp.Close();
                    ObserveAbandoned(handshake);
                    throw new TimeoutException(
                        "Timed out connecting to " + endpoint + " after " + connectTimeoutMs + " ms.");
                }

                socket = await handshake;
            }
            catch (Exception)
            {
                Unpublish(tcp);
                tcp.Close();
                throw;
            }

            lock (sync)
            {
                // This attempt owns tcp from here on, either through tcpClient below or
                // through the disposed branch's close.
                if (connectingTcpClient == tcp)
                {
                    connectingTcpClient = null;
                }

                if (disposed)
                {
                    socket.Dispose();
                    tcp.Close();
                    throw new ObjectDisposedException(nameof(RpcWebSocketClient));
                }

                receiveCancellation?.Cancel();
                receiveCancellation?.Dispose();
                webSocket?.Dispose();
                tcpClient?.Close();
                webSocket = socket;
                tcpClient = tcp;
                receiveCancellation = new CancellationTokenSource();
                receiveTask = ReceiveLoopAsync(socket, receiveCancellation.Token);
            }
        }

        // Runs off the Editor's synchronization context on purpose. ConnectAsync is
        // awaited from the supervision loop, i.e. on the main thread, and the handshake
        // reads its response header one byte at a time — roughly 180 sequential awaits.
        // Left on that context every one of them resumes through the Editor's update
        // pump, so the handshake costs 180 Editor ticks instead of 180 loopback reads.
        // Measured against the real server with the Editor unfocused: the server logged
        // "Unity connected" (its 101 was on the wire) and every attempt still blew the
        // 5 s budget, so the Editor never reconnected at all.
        private Task<WebSocket> ConnectAndHandshakeAsync(TcpClient tcp, CancellationToken cancellationToken)
        {
            return Task.Run(async () =>
            {
                await tcp.ConnectAsync(endpoint.Host, endpoint.Port);
                return await WebSocketHandshake.PerformAsync(
                    tcp.GetStream(),
                    endpoint.Authority,
                    endpoint.PathAndQuery,
                    Subprotocol,
                    bearerToken,
                    null,
                    cancellationToken);
            });
        }

        // Withdraws one attempt's socket from connectingTcpClient. Guarded on identity
        // because a later ConnectAsync may already have published its own socket there,
        // and clearing that registration would put its in-flight connect back out of
        // Dispose()'s and Abort()'s reach.
        private void Unpublish(TcpClient tcp)
        {
            lock (sync)
            {
                if (connectingTcpClient == tcp)
                {
                    connectingTcpClient = null;
                }
            }
        }

        public Task SendAsync(string json)
        {
            return SendAsync(json, CancellationToken.None);
        }

        // Context-free on purpose, unlike everything else that is reached from the main
        // thread. This method touches no Unity API — a semaphore and a socket write — yet
        // command responses are sent from a dispatcher callback, i.e. on the main thread,
        // so without ConfigureAwait its continuations resume through whatever
        // SynchronizationContext the Editor's main thread carries. The continuation that
        // matters is the finally below: a project that swaps Unity's context out for one
        // Unity never pumps would orphan sendLock.Release(), and a send lock that is never
        // released wedges every later send on the connection, not just this one.
        public async Task SendAsync(string json, CancellationToken cancellationToken)
        {
            if (json == null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            WebSocket socket = null;
            try
            {
                lock (sync)
                {
                    socket = webSocket;
                }

                if (socket == null || socket.State != WebSocketState.Open)
                {
                    throw new WebSocketException("RPC WebSocket is not connected.");
                }

                var bytes = Encoding.UTF8.GetBytes(json);
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SignalClosed(socket, ex);
                throw;
            }
            finally
            {
                sendLock.Release();
            }
        }

        public void Abort()
        {
            WebSocket socket;
            TcpClient tcp;
            TcpClient connecting;
            lock (sync)
            {
                socket = webSocket;
                tcp = tcpClient;
                connecting = connectingTcpClient;
            }

            socket?.Abort();

            // The WebSocket only wraps the stream. Closing our socket is what the peer
            // actually observes, and what releases the file descriptor.
            tcp?.Close();

            // An attempt still handshaking owns a connected socket that is not in
            // tcpClient yet. Abort is supposed to leave nothing connected, so it has to
            // close that one too.
            connecting?.Close();
            SignalClosed(socket, new WebSocketException("RPC WebSocket was aborted."));
        }

        public void Dispose()
        {
            Task loop;
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                loop = receiveTask;
                disposed = true;
                receiveCancellation?.Cancel();
                webSocket?.Dispose();

                // Double-closing on purpose: WebSocket.Dispose already disposed the
                // stream we handed it, but a stream is not a socket. Stream.Dispose is
                // idempotent, whereas closing the TcpClient is what makes the peer read
                // EOF and what releases the file descriptor, so we close it regardless
                // of what the WebSocket already did.
                tcpClient?.Close();

                // A connect still in flight owns a socket the field swap has not published
                // into tcpClient yet. Closing it here is the only thing that makes the peer
                // read EOF before a domain reload: the timeout branch that would otherwise
                // close it resumes on the Editor's synchronization context, which stops
                // being pumped the moment this Dispose returns to beforeAssemblyReload.
                connectingTcpClient?.Close();
                receiveCancellation?.Dispose();
            }

            // Outside the lock, and it has to be: the loop reaches SignalClosed on its way
            // out, which takes this same lock.
            //
            // Waiting at all is new. While the loop resumed on the Editor's
            // synchronization context it could not run concurrently with a Dispose()
            // called from the main thread, so there was nothing to wait for — and waiting
            // would have deadlocked, since the loop needed the very thread doing the
            // waiting. On the thread pool it can be mid-MessageReceived right now, and
            // unsubscribing does not recall a call already in flight.
            //
            // That matters because RpcConnectionLoop.Dispose() runs synchronously from
            // beforeAssemblyReload: whatever is still running when this returns keeps
            // touching this domain's state while it is torn down, and its handler ends up
            // calling into an already-disposed dispatcher. Bounded rather than unbounded
            // because a stuck loop must not be able to hang a domain reload; the
            // cancellation and the closed socket above are what actually stop it, and this
            // only gives it the moment it needs to unwind.
            try
            {
                loop?.Wait(DisposeReceiveDrainMs);
            }
            catch (AggregateException)
            {
                // Only waiting for the loop to stop, not for it to have succeeded. A loop
                // that ended on a protocol violation reports that through ReceiveTask,
                // which callers read; rethrowing it here would turn every such teardown
                // into a failure at the wrong layer.
            }

            sendLock.Dispose();
        }

        // Runs off the Editor's synchronization context, for the same reason
        // ConnectAndHandshakeAsync does and with more riding on it. A message arrives in
        // as many chunks as the WebSocket's receive buffer allows and every chunk resumes
        // this loop once; left on that context each of those resumes waits for an Editor
        // tick. Measured on 2022.3.62f3 by timing the two halves separately: the read
        // itself costs 0.30 ms, while getting back onto the context costs 51 ms with the
        // Editor focused and 100 ms without. The same resume off the context costs about
        // 79 us.
        //
        // Large messages are only the loud symptom. Every inbound message used to spend
        // one tick arriving here and a second reaching the main thread through the
        // dispatcher; only the dispatcher hop is left, so each RPC round trip sheds
        // roughly 50 ms whatever its size.
        //
        // MessageReceived and ConnectionClosed consequently fire on a thread pool thread.
        // RpcConnectionLoop's handlers are built for that: every command runs through
        // EditorMainThreadDispatcher rather than inline, the test-run gate reads a
        // volatile mirror field instead of an Editor API, and the command registry
        // publishes itself under a lock and is read-only afterwards.
        private Task ReceiveLoopAsync(WebSocket socket, CancellationToken cancellationToken)
        {
            return Task.Run(async () =>
            {
                // Matches the WebSocket's own receive buffer deliberately. That buffer is
                // what bounds a single ReceiveAsync, so a smaller one here would be the
                // binding constraint and a larger one would never be filled.
                var buffer = new byte[WebSocketHandshake.ReceiveBufferSize];

                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        using (var message = new MemoryStream())
                        {
                            WebSocketReceiveResult result;
                            do
                            {
                                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                                if (result.MessageType == WebSocketMessageType.Close)
                                {
                                    SignalClosed(socket, null);
                                    return;
                                }

                                if (result.MessageType != WebSocketMessageType.Text)
                                {
                                    throw new InvalidOperationException("RPC WebSocket messages must be UTF-8 text.");
                                }

                                if (message.Length + result.Count > MaxMessageSizeBytes)
                                {
                                    throw new InvalidOperationException("RPC WebSocket message exceeds 8 MiB.");
                                }

                                message.Write(buffer, 0, result.Count);
                            }
                            while (!result.EndOfMessage);

                            MessageReceived?.Invoke(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    SignalClosed(socket, ex);
                    throw;
                }
            });
        }

        // Cleans up after a handshake we have stopped waiting on. Two things can arrive
        // late: a fault, which has to be observed or it resurfaces as an unobserved task
        // exception; and a WebSocket, which has to be disposed. A handshake that wins its
        // race against the timeout by a hair still hands back a live WebSocket, and the
        // BCL's ManagedWebSocket registers itself as the state of a keep-alive Timer in
        // its constructor. Only DisposeCore cancels that timer, so an undisposed one is
        // rooted for the life of the domain and keeps pinging a stream we already closed.
        private static void ObserveAbandoned(Task<WebSocket> task)
        {
            task.ContinueWith(
                t =>
                {
                    _ = t.Exception;
                    if (t.Status == TaskStatus.RanToCompletion)
                    {
                        t.Result.Dispose();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void SignalClosed(WebSocket socket, Exception exception)
        {
            var shouldNotify = false;
            lock (sync)
            {
                if (!disposed && socket != null && socket == webSocket)
                {
                    shouldNotify = true;
                }
            }

            if (shouldNotify)
            {
                ConnectionClosed?.Invoke(exception);
            }
        }
    }
}
