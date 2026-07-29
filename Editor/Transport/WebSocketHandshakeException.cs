using System;

namespace VeryFS.UnityMCP.Editor.Transport
{
    /// <summary>
    /// A WebSocket opening handshake the peer answered but refused, or answered
    /// unusably. Carries the HTTP status so callers can tell a rejected token apart
    /// from a server that is merely not up yet — the two need very different
    /// treatment, and the BCL client collapsed both into one opaque message.
    /// Not a WebSocketException: that type is sealed. Nothing here catches it by
    /// type anyway, so the relationship would have bought nothing.
    /// </summary>
    internal sealed class WebSocketHandshakeException : Exception
    {
        public WebSocketHandshakeException(string message, int? statusCode, string reasonPhrase)
            : base(message)
        {
            StatusCode = statusCode;
            ReasonPhrase = reasonPhrase;
        }

        /// <summary>HTTP status of the upgrade response, null when we never got one.</summary>
        public int? StatusCode { get; }

        public string ReasonPhrase { get; }

        /// <summary>
        /// True when the server rejected our bearer token. This never self-heals by
        /// retrying, so the supervision loop reports it differently from a transient
        /// failure.
        /// </summary>
        public bool IsAuthenticationFailure => StatusCode == 401;
    }
}
