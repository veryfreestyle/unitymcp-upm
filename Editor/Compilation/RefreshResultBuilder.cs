using System;
using LitJson;
using VeryFS.UnityMCP.Editor.Persistence;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Compilation
{
    public static class RefreshResultBuilder
    {
        public static PendingRefreshRequest MarkSucceeded(
            PendingRefreshRequest request,
            string finishedAt,
            bool compilationTriggered)
        {
            request.State = "succeeded";
            request.FinishedAt = finishedAt;
            request.DurationMs = DurationInMilliseconds(request.StartedAt, finishedAt);
            request.CompilationTriggered = compilationTriggered;
            request.ErrorCode = null;
            return request;
        }

        public static PendingRefreshRequest MarkFailedFromCompilerErrors(
            PendingRefreshRequest request,
            string finishedAt)
        {
            request.State = "failed";
            request.FinishedAt = finishedAt;
            request.DurationMs = DurationInMilliseconds(request.StartedAt, finishedAt);
            request.CompilationTriggered = true;
            request.ErrorCode = "compilation_failed";
            return request;
        }

        public static PendingRefreshRequest MarkFailedFromExistingErrors(
            PendingRefreshRequest request,
            string finishedAt)
        {
            request.State = "failed";
            request.FinishedAt = finishedAt;
            request.DurationMs = DurationInMilliseconds(request.StartedAt, finishedAt);
            // No compilation ran during this refresh; the errors were already
            // present in the project, so CompilationTriggered stays false.
            request.CompilationTriggered = false;
            request.ErrorCode = "compilation_failed";
            return request;
        }

        public static PendingRefreshRequest MarkRefreshFailed(
            PendingRefreshRequest request,
            string finishedAt)
        {
            return MarkRefreshFailed(request, finishedAt, "refresh_failed");
        }

        public static PendingRefreshRequest MarkRefreshFailed(
            PendingRefreshRequest request,
            string finishedAt,
            string errorCode)
        {
            request.State = "failed";
            request.FinishedAt = finishedAt;
            request.DurationMs = DurationInMilliseconds(request.StartedAt, finishedAt);
            request.CompilationTriggered = false;
            request.ErrorCode = errorCode;
            return request;
        }

        public static JsonData BuildReportParams(PendingRefreshRequest request)
        {
            var report = JsonRpcSerializer.Object(
                ("originRequestId", request.OriginRequestId),
                ("method", request.Method),
                ("state", request.State),
                ("startedAt", request.StartedAt),
                ("finishedAt", request.FinishedAt),
                ("durationMs", request.DurationMs),
                ("compilationTriggered", request.CompilationTriggered),
                ("compilerErrors", BuildCompilerMessages(request.CompilerErrors)),
                ("compilerWarnings", BuildCompilerMessages(request.CompilerWarnings)));

            if (!string.IsNullOrEmpty(request.ErrorCode))
            {
                report["errorCode"] = request.ErrorCode;
            }

            return report;
        }

        private static long DurationInMilliseconds(string startedAt, string finishedAt)
        {
            return (long)(DateTimeOffset.Parse(finishedAt) - DateTimeOffset.Parse(startedAt)).TotalMilliseconds;
        }

        private static JsonData BuildCompilerMessages(System.Collections.Generic.IEnumerable<CompilerMessage> messages)
        {
            var data = new JsonData();
            data.SetJsonType(JsonType.Array);
            foreach (var message in messages)
            {
                data.Add(JsonRpcSerializer.Object(
                    ("assembly", message.Assembly),
                    ("file", message.File),
                    ("line", message.Line),
                    ("column", message.Column),
                    ("message", message.Message)));
            }

            return data;
        }
    }
}
