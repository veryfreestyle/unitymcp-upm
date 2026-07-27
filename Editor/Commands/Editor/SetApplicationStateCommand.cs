using System;
using LitJson;
using UnityEditor;
using VeryFS.UnityMCP.Editor.Infrastructure;
using VeryFS.UnityMCP.Editor.Persistence;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Editor
{
    public sealed class SetApplicationStateCommand : ILongRunningCommand
    {
        private const int DeadlineMs = 60000;

        private readonly IPlayModeController controller;
        private readonly IEditorStateProvider state;
        private readonly IEditorBusyState busy;
        private readonly PendingRequestStore store;
        private readonly IClock clock;

        public SetApplicationStateCommand(
            IPlayModeController controller, IEditorStateProvider state,
            IEditorBusyState busy, PendingRequestStore store, IClock clock)
        {
            this.controller = controller;
            this.state = state;
            this.busy = busy;
            this.store = store;
            this.clock = clock;
        }

        public string Method => RpcMethods.EditorApplicationSetState;

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "editor-set-state",
            RpcMethod = RpcMethods.EditorApplicationSetState,
            Title = "Editor / Application / Set State",
            Description = "Start/stop/pause playmode. Refuses when the project has compilation errors. " +
                "Blocks until the play-mode transition completes (survives domain reload) and returns the post-transition state.",
            Completion = "report",
            FailureMode = "error",
            DefaultTimeoutMs = DeadlineMs,
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"),
                ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("isPlaying", JsonRpcSerializer.Object(("type", "boolean"))),
                    ("isPaused", JsonRpcSerializer.Object(("type", "boolean"))),
                    ("timeoutMs", JsonRpcSerializer.Object(("type", "integer"), ("minimum", 1000), ("maximum", 600000)))))),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", true))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            if (controller.ScriptCompilationFailed)
            {
                return JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                    JsonRpcErrorCodes.CompilationFailed,
                    controller.CompilationErrorDetails,
                    JsonRpcSerializer.Object(("errorCode", "compilation_failed"))));
            }
            if (busy.IsCompiling || busy.IsUpdating)
            {
                return JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                    JsonRpcErrorCodes.EditorBusy,
                    "Editor is busy.",
                    JsonRpcSerializer.Object(("errorCode", "editor_busy"))));
            }

            bool targetPlaying = ReadBool(request.Params, "isPlaying");
            bool targetPaused = ReadBool(request.Params, "isPaused");
            var target = JsonRpcSerializer.Object(("isPlaying", targetPlaying), ("isPaused", targetPaused));
            store.Save(PendingRefreshRequest.StartLongRunning(
                request.Id, Method, clock.UtcNow.ToString("O"), JsonMapper.ToJson(target)));

            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                ("state", "processing"), ("requestId", request.Id)));
        }

        public void ExecuteAccepted(string requestId)
        {
            var entry = Find(requestId);
            if (entry == null || entry.State != "processing")
            {
                return;
            }
            var target = JsonMapper.ToObject(entry.TargetState);
            controller.IsPlaying = (bool)target["isPlaying"];
            controller.IsPaused = (bool)target["isPaused"];
            ObserveOrSchedule(entry);
        }

        public void RecoverPending(PendingRefreshRequest entry)
        {
            if (entry == null || entry.State != "processing")
            {
                return;
            }
            ObserveOrSchedule(entry);
        }

        public JsonData BuildReportParams(PendingRefreshRequest entry)
        {
            var report = JsonRpcSerializer.Object(
                ("originRequestId", entry.OriginRequestId),
                ("method", entry.Method),
                ("state", entry.State),
                ("startedAt", entry.StartedAt),
                ("finishedAt", entry.FinishedAt));
            if (!string.IsNullOrEmpty(entry.ErrorCode))
            {
                report["errorCode"] = entry.ErrorCode;
            }
            if (!string.IsNullOrEmpty(entry.ResultPayload))
            {
                var payload = JsonMapper.ToObject(entry.ResultPayload);
                report["state"] = entry.State; // keep protocol state
                report["result"] = payload;
            }
            return report;
        }

        private void ObserveOrSchedule(PendingRefreshRequest entry)
        {
            if (TargetMet(entry))
            {
                MarkSucceeded(entry);
                return;
            }
            if (DeadlinePassed(entry))
            {
                MarkTimeout(entry);
                return;
            }
            EditorApplication.CallbackFunction poll = null;
            poll = () =>
            {
                var current = Find(entry.OriginRequestId);
                if (current == null || current.State != "processing")
                {
                    EditorApplication.update -= poll;
                    return;
                }
                if (TargetMet(current))
                {
                    EditorApplication.update -= poll;
                    MarkSucceeded(current);
                }
                else if (DeadlinePassed(current))
                {
                    EditorApplication.update -= poll;
                    MarkTimeout(current);
                }
            };
            EditorApplication.update += poll;
        }

        private bool TargetMet(PendingRefreshRequest entry)
        {
            var target = JsonMapper.ToObject(entry.TargetState);
            return state.IsPlaying == (bool)target["isPlaying"] &&
                   state.IsPaused == (bool)target["isPaused"];
        }

        private bool DeadlinePassed(PendingRefreshRequest entry)
        {
            var started = DateTimeOffset.Parse(entry.StartedAt);
            return (clock.UtcNow - started).TotalMilliseconds >= DeadlineMs;
        }

        private void MarkSucceeded(PendingRefreshRequest entry)
        {
            entry.State = "succeeded";
            entry.FinishedAt = clock.UtcNow.ToString("O");
            entry.ErrorCode = null;
            entry.ResultPayload = JsonMapper.ToJson(EditorStateData.ToJson(state));
            store.Save(entry);
        }

        private void MarkTimeout(PendingRefreshRequest entry)
        {
            entry.State = "failed";
            entry.FinishedAt = clock.UtcNow.ToString("O");
            entry.ErrorCode = "request_timeout";
            entry.ResultPayload = JsonMapper.ToJson(EditorStateData.ToJson(state));
            store.Save(entry);
        }

        private PendingRefreshRequest Find(string requestId)
        {
            foreach (var entry in store.LoadAll())
            {
                if (entry.OriginRequestId == requestId)
                {
                    return entry;
                }
            }
            return null;
        }

        private static bool ReadBool(JsonData @params, string key)
        {
            return @params != null && @params.IsObject && @params.ContainsKey(key) &&
                   @params[key].IsBoolean && (bool)@params[key];
        }
    }
}
