using System;

namespace VeryFS.UnityMCP.Editor.Protocol
{
    public sealed class RpcProtocolException : Exception
    {
        public RpcProtocolException(int errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public RpcProtocolException(int errorCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }

        public int ErrorCode { get; }
    }
}
