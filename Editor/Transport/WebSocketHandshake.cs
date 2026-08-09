using System;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VeryFS.UnityMCP.Editor.Transport
{
    /// <summary>
    /// Performs the client half of an RFC 6455 opening handshake over a stream the
    /// caller owns, then hands that stream to the BCL's managed WebSocket for framing.
    /// Deliberately knows nothing about sockets: ownership staying with the caller is
    /// the entire point, because a ClientWebSocket whose handshake stalls holds its
    /// TCP connection somewhere unreachable and cannot be shut down at all.
    ///
    /// Do not await this on a synchronization context that dispatches at frame rate.
    /// Reading the header a byte at a time means ~180 sequential awaits for a typical
    /// response; on Unity's context each one resumes on an Editor tick, so the handshake
    /// costs 180 ticks rather than 180 loopback reads and an unfocused Editor never
    /// finishes it. Callers hand this work to the thread pool.
    /// </summary>
    internal static class WebSocketHandshake
    {
        // RFC 6455 section 1.3.
        private const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private const int MaxResponseHeaderBytes = 16 * 1024;

        // Caps how much payload a single WebSocket.ReceiveAsync can hand back, and so
        // how many times reading one message goes round RpcWebSocketClient's loop.
        //
        // That count used to dominate everything. While the loop resumed on Unity's
        // synchronization context each iteration waited for an Editor tick, so an 8 MiB
        // message cost 1024 of them: 61.0 s at 8 KiB against 3.50 s at 128 KiB
        // (2022.3.62f3, Client_RejectsOversizedServerMessage). The loop now runs on the
        // thread pool, where an iteration costs microseconds rather than ~51 ms, and the
        // same test finishes in 0.19 s — so this value went back to being an ordinary
        // buffer-size trade-off rather than a latency lever.
        //
        // 32 KiB keeps a few large reads instead of hundreds of small ones without
        // holding much: the allocation is resident for the life of the connection, twice
        // over, since RpcWebSocketClient's own buffer has to match. Production messages
        // inbound are small anyway — the large payloads go the other way through
        // SendAsync, which awaits once per message whatever its size.
        //
        // No framework ceiling constrains this. The 64 KiB limit documented for .NET
        // Framework lives in WebSocketHelpers, part of the native WSPC path; Unity's Mono
        // ships the managed CoreFX ManagedWebSocket instead, whose only check is a lower
        // bound (verified in the net_4_x-macos and net_4_x-win32 System.dll of both
        // 2021.3.45f2c1 and 2022.3.62f3).
        //
        // Internal rather than private because RpcWebSocketClient's buffer must match it:
        // a smaller one there would bound the read instead, a larger one never fill.
        internal const int ReceiveBufferSize = 32 * 1024;

        // Left at 8 KiB on purpose. The send path awaits once per message no matter how
        // large it is, so there is no tick cost to cut here, and changing this would
        // change how outbound messages are fragmented on the wire for no gain.
        private const int SendBufferSize = 8192;

        /// <param name="secWebSocketKey">
        /// Pass null in production so a fresh nonce is generated; tests pass a fixed
        /// key so the expected accept value can be asserted independently.
        /// </param>
        /// <param name="cancellationToken">
        /// Cannot unblock a read already issued against a real socket: once
        /// stream.ReadAsync is in flight on a NetworkStream, this token has nothing
        /// left to cancel. Enforcing a timeout is therefore the caller's job, and the
        /// only thing that works is closing the stream it owns — hence the contract
        /// that the caller keeps that ownership.
        /// </param>
        public static async Task<WebSocket> PerformAsync(
            Stream stream,
            string hostHeader,
            string path,
            string subprotocol,
            string bearerToken,
            string secWebSocketKey,
            CancellationToken cancellationToken)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            var key = string.IsNullOrEmpty(secWebSocketKey) ? NewSecWebSocketKey() : secWebSocketKey;
            var request = BuildRequest(hostHeader, path, subprotocol, bearerToken, key);
            await stream.WriteAsync(request, 0, request.Length, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            var header = await ReadResponseHeaderAsync(stream, cancellationToken);
            Validate(header, subprotocol, key);

            return WebSocket.CreateClientWebSocket(
                stream,
                subprotocol,
                ReceiveBufferSize,
                SendBufferSize,
                WebSocket.DefaultKeepAliveInterval,
                false,
                default(ArraySegment<byte>));
        }

        private static string NewSecWebSocketKey()
        {
            var nonce = new byte[16];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(nonce);
            }

            return Convert.ToBase64String(nonce);
        }

        private static byte[] BuildRequest(
            string hostHeader, string path, string subprotocol, string bearerToken, string key)
        {
            var builder = new StringBuilder();
            builder.Append("GET ").Append(path).Append(" HTTP/1.1\r\n");
            builder.Append("Host: ").Append(hostHeader).Append("\r\n");
            builder.Append("Upgrade: websocket\r\n");
            builder.Append("Connection: Upgrade\r\n");
            builder.Append("Sec-WebSocket-Version: 13\r\n");
            builder.Append("Sec-WebSocket-Key: ").Append(key).Append("\r\n");
            builder.Append("Sec-WebSocket-Protocol: ").Append(subprotocol).Append("\r\n");
            if (!string.IsNullOrEmpty(bearerToken))
            {
                builder.Append("Authorization: Bearer ").Append(bearerToken).Append("\r\n");
            }

            builder.Append("\r\n");
            return Encoding.ASCII.GetBytes(builder.ToString());
        }

        // Reads a byte at a time and stops on the blank line. A bulk read would
        // overshoot into the first WebSocket frame whenever the peer writes the
        // response and that frame together, and CreateClientWebSocket cannot take
        // those bytes back: its internalBuffer is scratch space, not a pre-filled
        // receive buffer (measured on 2021.3 and 2022.3; the frame is simply lost).
        // ~200 one-byte reads once per connect on loopback is not worth optimising.
        private static async Task<string> ReadResponseHeaderAsync(
            Stream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[1];
            var header = new StringBuilder();
            var matched = 0;
            while (header.Length < MaxResponseHeaderBytes)
            {
                var read = await stream.ReadAsync(buffer, 0, 1, cancellationToken);
                if (read <= 0)
                {
                    throw new WebSocketHandshakeException(
                        "The peer closed the connection during the WebSocket handshake.", null, null);
                }

                var current = (char)buffer[0];
                header.Append(current);
                if (current == '\r')
                {
                    matched = matched == 2 ? 3 : 1;
                }
                else if (current == '\n' && (matched == 1 || matched == 3))
                {
                    matched++;
                }
                else
                {
                    matched = 0;
                }

                if (matched == 4)
                {
                    return header.ToString(0, header.Length - 4);
                }
            }

            throw new WebSocketHandshakeException(
                "The WebSocket handshake response header exceeded " + MaxResponseHeaderBytes + " bytes.",
                null,
                null);
        }

        private static void Validate(string header, string subprotocol, string key)
        {
            var lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var statusLine = lines.Length > 0 ? lines[0] : string.Empty;
            var statusCode = ParseStatusCode(statusLine);
            if (statusCode != 101)
            {
                throw new WebSocketHandshakeException(
                    "The server refused the WebSocket upgrade: " + statusLine,
                    statusCode,
                    ParseReasonPhrase(statusLine));
            }

            string accept = null;
            string negotiated = null;
            for (var index = 1; index < lines.Length; index++)
            {
                var separator = lines[index].IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                var name = lines[index].Substring(0, separator).Trim();
                var value = lines[index].Substring(separator + 1).Trim();
                if (string.Equals(name, "Sec-WebSocket-Accept", StringComparison.OrdinalIgnoreCase))
                {
                    accept = value;
                }
                else if (string.Equals(name, "Sec-WebSocket-Protocol", StringComparison.OrdinalIgnoreCase))
                {
                    negotiated = value;
                }
            }

            if (accept != CreateAcceptValue(key))
            {
                throw new WebSocketHandshakeException(
                    "The server's Sec-WebSocket-Accept does not match the key we sent.",
                    statusCode,
                    null);
            }

            if (negotiated != subprotocol)
            {
                throw new WebSocketHandshakeException(
                    "The server negotiated subprotocol '" + negotiated + "' instead of '" + subprotocol + "'.",
                    statusCode,
                    null);
            }
        }

        private static int? ParseStatusCode(string statusLine)
        {
            var parts = statusLine.Split(' ');
            if (parts.Length < 2)
            {
                return null;
            }

            return int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code)
                ? code
                : (int?)null;
        }

        private static string ParseReasonPhrase(string statusLine)
        {
            var parts = statusLine.Split(new[] { ' ' }, 3);
            return parts.Length == 3 ? parts[2] : null;
        }

        private static string CreateAcceptValue(string key)
        {
            using (var sha1 = SHA1.Create())
            {
                return Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + AcceptGuid)));
            }
        }
    }
}
