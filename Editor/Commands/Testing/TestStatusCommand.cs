using System;
using LitJson;
using UnityEditorInternal;
using VeryFS.UnityMCP.Editor.Infrastructure;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Testing
{
    // Editor 是否处于前台。失焦时 Unity 会被 OS/Editor 节流,
    // 长时间逐帧推进的测试运行会被拖慢 —— 把信号暴露给调用方自己判断。
    public interface IEditorFocusState
    {
        bool IsApplicationActive { get; }
    }

    public sealed class UnityEditorFocusState : IEditorFocusState
    {
        public bool IsApplicationActive => InternalEditorUtility.isApplicationActive;
    }

    // test.status: 只读。测试运行期间它是唯一放行的状态查询,
    // 也是 MCP 调用超时后复查结果的唯一入口。
    public sealed class TestStatusCommand : IRpcCommand
    {
        private readonly TestRunTracker tracker;
        private readonly IEditorBusyState busy;
        private readonly IEditorFocusState focus;
        private readonly IClock clock;

        public TestStatusCommand(
            TestRunTracker tracker, IEditorBusyState busy, IEditorFocusState focus, IClock clock)
        {
            this.tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
            this.busy = busy ?? throw new ArgumentNullException(nameof(busy));
            this.focus = focus ?? throw new ArgumentNullException(nameof(focus));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public string Method => RpcMethods.TestStatus;

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "test-status",
            RpcMethod = RpcMethods.TestStatus,
            Title = "Test / Status",
            Description =
                "Read the current test run progress or the most recent finished run. " +
                "Allowed while a test run is in progress. Use it after a test-run call times out " +
                "to recover the result, or to see why a run appears stuck (blockedReason).",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"),
                ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object())),
            Annotations = JsonRpcSerializer.Object(("readOnlyHint", true))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            bool running = tracker.IsRunning;
            var result = JsonRpcSerializer.Object(("running", running));

            // 运行结束后同样要给 testMode —— 这个字段的本职用途正是"MCP 调用超时后判断刚跑完
            // 的那次是 EditMode 还是 PlayMode"(后者临时改过 EditorSettings, 结果适用范围不同)。
            // MarkFinished 不清 TestMode, 历史值一直可读; 只有从没跑过时才没有这个字段。
            if (!string.IsNullOrEmpty(tracker.TestMode))
            {
                result["testMode"] = tracker.TestMode;
            }

            var progress = tracker.BuildProgressPayload();
            if (running && progress != null)
            {
                result["progress"] = Decorate(progress);
            }

            var lastRun = tracker.LastRunPayload();
            if (lastRun != null)
            {
                result["lastRun"] = lastRun;
            }

            return JsonRpcResponse.FromSuccess(request.Id, result);
        }

        private JsonData Decorate(JsonData progress)
        {
            var startedAt = tracker.StartedAt;
            progress["elapsedMs"] = startedAt.HasValue
                ? (long)(clock.UtcNow - startedAt.Value).TotalMilliseconds
                : 0L;

            bool stuck = IsStuck();
            progress["stuckSuspected"] = stuck;
            progress["editorIsFocused"] = focus.IsApplicationActive;
            if (stuck)
            {
                progress["blockedReason"] = BlockedReason();
            }

            return progress;
        }

        private bool IsStuck()
        {
            var caseStartedAt = tracker.CurrentCaseStartedAt;
            if (!caseStartedAt.HasValue)
            {
                return false;
            }

            return (clock.UtcNow - caseStartedAt.Value).TotalMilliseconds > TestRunCommand.StuckThresholdMs;
        }

        private string BlockedReason()
        {
            if (!focus.IsApplicationActive)
            {
                return "editor_unfocused";
            }

            if (busy.IsCompiling)
            {
                return "compiling";
            }

            if (busy.IsUpdating)
            {
                return "asset_import";
            }

            return "unknown";
        }
    }
}
