using System;
using System.IO;
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

        private readonly Uri endpoint;
        private readonly string bearerToken;
        private readonly object sync = new object();
        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        private ClientWebSocket webSocket;
        private CancellationTokenSource receiveCancellation;
        private Task receiveTask;
        private bool disposed;

        public RpcWebSocketClient(Uri endpoint)
            : this(endpoint, null)
        {
        }

        public RpcWebSocketClient(Uri endpoint, string bearerToken)
        {
            this.endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            this.bearerToken = bearerToken;
        }

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
            var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol(Subprotocol);
            if (!string.IsNullOrEmpty(bearerToken))
            {
                socket.Options.SetRequestHeader("Authorization", "Bearer " + bearerToken);
            }

            await socket.ConnectAsync(endpoint, cancellationToken);

            lock (sync)
            {
                if (disposed)
                {
                    socket.Dispose();
                    throw new ObjectDisposedException(nameof(RpcWebSocketClient));
                }

                receiveCancellation?.Cancel();
                receiveCancellation?.Dispose();
                webSocket?.Dispose();
                webSocket = socket;
                receiveCancellation = new CancellationTokenSource();
                receiveTask = ReceiveLoopAsync(socket, receiveCancellation.Token);
            }
        }

        public Task SendAsync(string json)
        {
            return SendAsync(json, CancellationToken.None);
        }

        public async Task SendAsync(string json, CancellationToken cancellationToken)
        {
            if (json == null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            await sendLock.WaitAsync(cancellationToken);
            ClientWebSocket socket = null;
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
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
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
            ClientWebSocket socket;
            lock (sync)
            {
                socket = webSocket;
            }

            socket?.Abort();
            SignalClosed(socket, new WebSocketException("RPC WebSocket was aborted."));
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                receiveCancellation?.Cancel();
                webSocket?.Dispose();
                receiveCancellation?.Dispose();
            }

            sendLock.Dispose();
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];

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
        }

        private void SignalClosed(ClientWebSocket socket, Exception exception)
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
