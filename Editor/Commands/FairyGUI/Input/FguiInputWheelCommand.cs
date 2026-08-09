using System.Collections;
using Cysharp.Threading.Tasks;
using FairyGUI;
using LitJson;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Commands;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    /// <summary>
    /// 滚轮: 自足动作(不需要预先存在的 session), 跟 click/move 一样不加独立的
    /// IsPlaying 门 —— not_playing 从 Sessions.Acquire 的返回值里天然浮出来。
    /// </summary>
    public sealed class FguiInputWheelCommand : FguiInputCommandBase
    {
        public FguiInputWheelCommand(IPanelSource panelSource, IMcpStageInput input,
            McpStageInputSessionManager sessions, string projectRoot)
            : base(panelSource, input, sessions, projectRoot) { }

        public override string Method => RpcMethods.FairyGuiInputWheel;
        public override string Action => "wheel";

        public override RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-input-wheel",
            RpcMethod = RpcMethods.FairyGuiInputWheel,
            Title = "FairyGUI / Input / Wheel",
            Description = "Scroll the wheel over a target. delta counts scroll steps and positive scrolls "
                + "down (towards content further down). Horizontal scroll panes are driven by the same value. "
                + "Lists that snap to items round any |delta| below 1 up to 1, so fractional deltas do nothing "
                + "there. Needs no session.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = Schema(p =>
            {
                AddLocationSchema(p);
                AddMotionSchema(p);
                p["delta"] = JsonRpcSerializer.Object(
                    ("type", "number"), ("description", "How many scroll steps; positive scrolls down."));
                p["modifiers"] = JsonRpcSerializer.Object(
                    ("type", "array"),
                    ("description", "Held modifiers: control, shift, alt, command."));
            }),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", false))
        };

        /// <summary>
        /// 反算注入用的 Event.delta.y, 抵消 FGUI 消费链上的两层缩放, 让 delta 的语义
        /// 严格等于"滚动几个 scrollStep"、跨平台一致:
        ///   Event.delta.y × mouseWheelScale = evt.mouseWheelDelta   (Stage.cs:990)
        ///   evt.mouseWheelDelta ÷ devicePixelRatio = ScrollPane 用的 delta (ScrollPane.cs:1720)
        /// FGUI 自己承认 devicePixelRatio 的自动判断在外接显示器等情况下不可靠
        /// (Stage.cs:240 注释), 所以再乘一个面板系数兜底。
        /// </summary>
        public static float EventDeltaFor(float delta, float devicePixelRatio,
            float mouseWheelScale, float wheelScale)
        {
            float ratio = devicePixelRatio <= 0f ? 1f : devicePixelRatio;
            float scale = Mathf.Approximately(mouseWheelScale, 0f) ? 1f : mouseWheelScale;
            return delta * ratio / scale * wheelScale;
        }

        // 整段 body (含 Parse 和 PointerSpeedBase 读取) 都在 Guarded 里, 见 FguiInputMoveCommand
        // 顶上的注释: 这批 action 里没有代码站在 Guarded 外面。
        public override UniTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request)
        {
            return Guarded(request, async () =>
            {
                FguiInputRequest req = FguiInputRequest.Parse(request.Params, PointerSpeedBase);
                if (req.Error != null) { return InvalidParams(request, req.ErrorDetail); }

                string error;
                float? delta = FguiInputRequest.ReadFloatNullable(request.Params, "delta", out error);
                if (error != null) { return InvalidParams(request, error); }
                if (!delta.HasValue) { return InvalidParams(request, "'delta' is required."); }

                if (!FguiInputRequest.TryReadModifiers(request.Params, "modifiers",
                        out EventModifiers modifiers, out string modifierError))
                {
                    return InvalidParams(request, modifierError);
                }

                if (!TryResolvePoint(req, out Vector2 point, out GObject located, out JsonData failure))
                {
                    return JsonRpcResponse.FromSuccess(request.Id, failure);
                }

                WarnIfUnreachable(req, located);

                // 预算校验必须在 Acquire (会碰 Stage 输入状态) 之前完成; PlanMoveSegment
                // 只读 req 和两个端点, 不需要 player。
                var budget = new FguiInputBudget();
                MoveSegmentPlan plan = PlanMoveSegment(req, Input.CurrentPointerPosition, point, budget);
                // fork 的 Scroll 自带 2 帧: 第一帧把指针放到目标处让 LateUpdate 算出
                // _touchTarget, 第二帧才投事件 —— 同帧投递会打到上一帧的目标上。
                budget.AddFrames(2);
                string violation = budget.Violation();
                if (violation != null) { return InvalidParams(request, violation); }

                using (McpInputLease lease = Sessions.Acquire(Method))
                {
                    if (lease.Error != null)
                    {
                        return JsonRpcResponse.FromSuccess(request.Id,
                            JsonRpcSerializer.Object(("state", lease.Error)));
                    }

                    IEnumerator move = BuildMoveSegment(lease.Player, plan);
                    float eventDelta = EventDeltaFor(delta.Value,
                        Stage.devicePixelRatio, Stage.mouseWheelScale, WheelScale);

                    McpRunOutcome outcome = await Input.RunAsync(McpInputSequences.Concat(
                        move, lease.Player.Scroll(point, eventDelta, modifiers)));
                    if (!outcome.Completed) { return Fault(request, outcome); }

                    return JsonRpcResponse.FromSuccess(request.Id,
                        Payload("ok", Input.TouchTarget, req, plan.ActualMs));
                }
            });
        }
    }
}
