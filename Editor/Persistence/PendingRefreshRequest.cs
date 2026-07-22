using System;
using System.Collections.Generic;
using VeryFS.UnityMCP.Editor.Compilation;

namespace VeryFS.UnityMCP.Editor.Persistence
{
    public sealed class PendingRefreshRequest
    {
        public PendingRefreshRequest()
        {
            CompilerErrors = new List<CompilerMessage>();
            CompilerWarnings = new List<CompilerMessage>();
        }

        public string OriginRequestId { get; set; }

        public string Method { get; set; }

        public string State { get; set; }

        public string StartedAt { get; set; }

        public string FinishedAt { get; set; }

        public long DurationMs { get; set; }

        public bool CompilationTriggered { get; set; }

        public List<CompilerMessage> CompilerErrors { get; set; }

        public List<CompilerMessage> CompilerWarnings { get; set; }

        public bool ReportAcknowledged { get; set; }

        public string ErrorCode { get; set; }

        public string ExecutionState { get; set; }

        public string TargetState { get; set; }

        public string ResultPayload { get; set; }

        public static PendingRefreshRequest StartLongRunning(
            string originRequestId, string method, string startedAt, string targetState)
        {
            return new PendingRefreshRequest
            {
                OriginRequestId = originRequestId,
                Method = method,
                State = "processing",
                ExecutionState = "accepted",
                StartedAt = startedAt,
                TargetState = targetState
            };
        }

        public static PendingRefreshRequest Start(string originRequestId, string startedAt)
        {
            return new PendingRefreshRequest
            {
                OriginRequestId = originRequestId,
                Method = "assets.refresh",
                State = "processing",
                ExecutionState = "accepted",
                StartedAt = startedAt
            };
        }

        public static PendingRefreshRequest TerminalSucceeded(
            string originRequestId,
            string startedAt,
            string finishedAt,
            bool compilationTriggered)
        {
            return new PendingRefreshRequest
            {
                OriginRequestId = originRequestId,
                Method = "assets.refresh",
                State = "succeeded",
                StartedAt = startedAt,
                FinishedAt = finishedAt,
                DurationMs = (long)(DateTimeOffset.Parse(finishedAt) - DateTimeOffset.Parse(startedAt)).TotalMilliseconds,
                CompilationTriggered = compilationTriggered
            };
        }
    }
}
