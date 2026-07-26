using System;
using System.Collections.Generic;

namespace VeryFS.UnityMCP.Editor.Commands.AgentSkill
{
    internal sealed class AgentSkillOperationException : Exception
    {
        public AgentSkillOperationException(
            int rpcCode,
            string errorCode,
            string message,
            IReadOnlyList<string> unknownTools = null,
            string path = null)
            : base(message)
        {
            RpcCode = rpcCode;
            ErrorCode = errorCode;
            UnknownTools = unknownTools;
            Path = path;
        }

        public int RpcCode { get; }
        public string ErrorCode { get; }
        public IReadOnlyList<string> UnknownTools { get; }
        public string Path { get; }
    }
}
