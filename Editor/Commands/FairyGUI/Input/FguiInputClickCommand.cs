using System.Collections;
using Cysharp.Threading.Tasks;
using FairyGUI;
using LitJson;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Commands;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public sealed class FguiInputClickCommand : FguiInputCommandBase
    {
        public FguiInputClickCommand(IPanelSource panelSource, IMcpStageInput input,
            McpStageInputSessionManager sessions, string projectRoot)
            : base(panelSource, input, sessions, projectRoot) { }

        public override string Method => RpcMethods.FairyGuiInputClick;
        public override string Action => "click";

        public override RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-input-click",
            RpcMethod = RpcMethods.FairyGuiInputClick,
            Title = "FairyGUI / Input / Click",
            Description = "Press and release at a target. With no location it clicks wherever the pointer "
                + "currently is. Self-contained, so it needs no session. path resolves to the target's "
                + "on-screen centre and the press lands there, so the target must be visible and on-screen; "
                + "otherwise the press goes to whatever is actually at that point — check the returned target.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = Schema(p => { AddLocationSchema(p); AddMotionSchema(p); AddButtonSchema(p); }),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", false))
        };

        // 整段 body (含 Parse 和 PointerSpeedBase 读取) 都在 Guarded 里, 见 FguiInputMoveCommand
        // 顶上的注释: 这批 action 里没有代码站在 Guarded 外面。
        public override UniTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request)
        {
            return Guarded(request, async () =>
            {
                FguiInputRequest req = FguiInputRequest.Parse(request.Params, PointerSpeedBase);
                if (req.Error != null) { return InvalidParams(request, req.ErrorDetail); }

                if (!TryResolvePoint(req, out Vector2 point, out GObject located, out JsonData failure))
                {
                    return JsonRpcResponse.FromSuccess(request.Id, failure);
                }

                WarnIfUnreachable(req, located);

                // 预算校验必须在 Acquire (会碰 Stage 输入状态) 之前完成; PlanMoveSegment
                // 只读 req 和两个端点, 不需要 player。
                var budget = new FguiInputBudget();
                MoveSegmentPlan plan = PlanMoveSegment(req, Input.CurrentPointerPosition, point, budget);
                budget.AddFrames(3);   // down / hold / up
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
                    McpRunOutcome outcome = await Input.RunAsync(
                        McpInputSequences.Concat(move, lease.Player.Click(point, req.Button)));
                    if (!outcome.Completed) { return Fault(request, outcome); }
                    return JsonRpcResponse.FromSuccess(request.Id,
                        Payload("ok", Input.TouchTarget, req, plan.ActualMs));
                }
            });
        }
    }
}
