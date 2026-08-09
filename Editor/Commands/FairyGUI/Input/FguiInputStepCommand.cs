using Cysharp.Threading.Tasks;
using LitJson;
using VeryFS.UnityMCP.Editor.Commands;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public sealed class FguiInputStepCommand : FguiInputCommandBase
    {
        public FguiInputStepCommand(IPanelSource panelSource, IMcpStageInput input,
            McpStageInputSessionManager sessions, string projectRoot)
            : base(panelSource, input, sessions, projectRoot) { }

        public override string Method => RpcMethods.FairyGuiInputStep;
        public override string Action => "step";

        public override RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-input-step",
            RpcMethod = RpcMethods.FairyGuiInputStep,
            Title = "FairyGUI / Input / Step",
            Description = "Hold whatever state is currently set for a while, without changing it. Give "
                + "exactly one of frames or ms. Requires an open session, otherwise there is no state to hold.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = Schema(p =>
            {
                p["frames"] = JsonRpcSerializer.Object(
                    ("type", "integer"), ("description", "Hold this many frames. Mutually exclusive with ms."));
                p["ms"] = JsonRpcSerializer.Object(
                    ("type", "number"), ("description", "Hold this many milliseconds. Mutually exclusive with frames."));
            }),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", false))
        };

        // 整段 body 都在 Guarded 里, 见 FguiInputMoveCommand 顶上的注释:
        // 这批 action 里没有代码站在 Guarded 外面。
        public override UniTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request)
        {
            return Guarded(request, async () =>
            {
                string error;
                int? frames = FguiInputRequest.ReadIntNullable(request.Params, "frames", out error);
                if (error != null) { return InvalidParams(request, error); }
                float? ms = FguiInputRequest.ReadFloatNullable(request.Params, "ms", out error);
                if (error != null) { return InvalidParams(request, error); }

                if (frames.HasValue == ms.HasValue)
                {
                    return InvalidParams(request, "'step' takes exactly one of frames or ms: frames is "
                        + "frame-driven, ms is wall-clock driven.");
                }
                if (frames.HasValue && frames.Value < 1)
                {
                    return InvalidParams(request, "frames must be at least 1.");
                }
                if (ms.HasValue && ms.Value < 0f)
                {
                    return InvalidParams(request, "ms cannot be negative.");
                }

                // 预算校验必须在 Acquire (会碰 Stage 输入状态) 之前完成。
                var budget = new FguiInputBudget();
                if (frames.HasValue) { budget.AddFrames(frames.Value); } else { budget.AddMs(ms.Value); }
                string violation = budget.Violation();
                if (violation != null) { return InvalidParams(request, violation); }

                // 同 FguiInputPressCommand 顶上的注释: IsPlaying 必须排在 HasSession 之前,
                // 否则冷启动(没开过 session, 不在 Play 模式)永远先撞 session_required,
                // 真正的阻塞原因要等下一轮 begin-session 才浮出来。
                if (!Input.IsPlaying)
                {
                    return JsonRpcResponse.FromSuccess(request.Id,
                        JsonRpcSerializer.Object(("state", "not_playing")));
                }
                if (!Sessions.HasSession) { return SessionRequired(request, Action); }

                using (McpInputLease lease = Sessions.Acquire(Method))
                {
                    if (lease.Error != null)
                    {
                        return JsonRpcResponse.FromSuccess(request.Id,
                            JsonRpcSerializer.Object(("state", lease.Error)));
                    }

                    McpRunOutcome outcome = await Input.RunAsync(frames.HasValue
                        ? lease.Player.Step(frames.Value)
                        : lease.Player.StepMs(ms.Value));
                    if (!outcome.Completed) { return Fault(request, outcome); }

                    return JsonRpcResponse.FromSuccess(request.Id,
                        Payload("ok", Input.TouchTarget, null, null));
                }
            });
        }
    }
}
