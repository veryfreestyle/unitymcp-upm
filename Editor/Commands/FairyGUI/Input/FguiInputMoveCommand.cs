using System.Collections;
using Cysharp.Threading.Tasks;
using FairyGUI;
using LitJson;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Commands;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public sealed class FguiInputMoveCommand : FguiInputCommandBase
    {
        public FguiInputMoveCommand(IPanelSource panelSource, IMcpStageInput input,
            McpStageInputSessionManager sessions, string projectRoot)
            : base(panelSource, input, sessions, projectRoot) { }

        public override string Method => RpcMethods.FairyGuiInputMove;
        public override string Action => "move";

        public override RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-input-move",
            RpcMethod = RpcMethods.FairyGuiInputMove,
            Title = "FairyGUI / Input / Move",
            Description = "Move the pointer to a target; a location is required. Rollover semantics are only "
                + "complete inside a session: without one the command ends by clearing lastRollOver without "
                + "dispatching onRollOut, so a tooltip stays visible on screen while FairyGUI already believes "
                + "the pointer left. Call begin-session first when testing tooltips or rollover chains.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = Schema(p => { AddLocationSchema(p); AddMotionSchema(p); }),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", false))
        };

        // 整段 body (含 Parse 和 PointerSpeedBase 读取) 都在 Guarded 里: PointerSpeedBase
        // 读 EditorPrefs, 理论上也可能抛; 挪到 Guarded 外面就是把"同步抛出等于请求永久
        // 不响应"这个 Critical 挪到了 Guarded 上游, 没真的堵住。这批 action 里没有代码
        // 站在 Guarded 外面。
        public override UniTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request)
        {
            return Guarded(request, async () =>
            {
                FguiInputRequest req = FguiInputRequest.Parse(request.Params, PointerSpeedBase);
                if (req.Error != null) { return InvalidParams(request, req.ErrorDetail); }
                if (!req.HasLocation)
                {
                    return InvalidParams(request, "'move' requires a location: give path "
                        + "(with optional panelInstanceId) or x/y.");
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
                    McpRunOutcome outcome = await Input.RunAsync(McpInputSequences.Concat(move));
                    if (!outcome.Completed) { return Fault(request, outcome); }
                    return JsonRpcResponse.FromSuccess(request.Id,
                        Payload("ok", Input.TouchTarget, req, plan.ActualMs));
                }
            });
        }
    }
}
