using System.Collections;
using Cysharp.Threading.Tasks;
using FairyGUI;
using LitJson;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Commands;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public sealed class FguiInputPressCommand : FguiInputCommandBase
    {
        public FguiInputPressCommand(IPanelSource panelSource, IMcpStageInput input,
            McpStageInputSessionManager sessions, string projectRoot)
            : base(panelSource, input, sessions, projectRoot) { }

        public override string Method => RpcMethods.FairyGuiInputPress;
        public override string Action => "press";

        public override RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-input-press",
            RpcMethod = RpcMethods.FairyGuiInputPress,
            Title = "FairyGUI / Input / Press",
            Description = "Press a button down and hold it. Requires an open session, because the pressed "
                + "state must survive to the next call. Pair it with release.",
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
                budget.AddFrames(1);   // press 落地那一帧
                string violation = budget.Violation();
                if (violation != null) { return InvalidParams(request, violation); }

                // 必须在 HasSession 门之前判 IsPlaying: 冷启动(从没开过 session, 也不在
                // Play 模式)时如果先判 HasSession, 拿到的永远是 session_required, 真正的
                // 阻塞原因(not_playing)要等调用方按提示去 begin-session 才会在下一轮暴露,
                // 多花一次往返。click/move/double-click 不用补这条是因为它们直接把
                // Acquire 排在预算校验之后, not_playing 从 Acquire 的返回值里天然浮出来
                // (McpStageInputSessionManager.Acquire 的 !input.IsPlaying 分支); press 在
                // Acquire 之前插了 HasSession 门, 挡住了那条路径, 只能在这里补上同一个判断,
                // 且响应形状跟 Acquire 那条路径给的一致(state: not_playing), 不能让同一个
                // 工具在同一种情况下吐两种形状。
                if (!Input.IsPlaying)
                {
                    return JsonRpcResponse.FromSuccess(request.Id,
                        JsonRpcSerializer.Object(("state", "not_playing")));
                }

                // press 需要预先存在的显式 session: 没有的话 Acquire 会临时接管一次,
                // using 块结束就 Dispose -> ResetInputState(), 刚按下的状态活不到下一条命令。
                if (!Sessions.HasSession) { return SessionRequired(request, Action); }

                using (McpInputLease lease = Sessions.Acquire(Method))
                {
                    if (lease.Error != null)
                    {
                        return JsonRpcResponse.FromSuccess(request.Id,
                            JsonRpcSerializer.Object(("state", lease.Error)));
                    }

                    IEnumerator move = BuildMoveSegment(lease.Player, plan);
                    McpRunOutcome outcome = await Input.RunAsync(
                        McpInputSequences.Concat(move, lease.Player.Press(point, req.Button)));
                    if (!outcome.Completed) { return Fault(request, outcome); }

                    Sessions.NotePressed(req.Button);
                    return JsonRpcResponse.FromSuccess(request.Id,
                        Payload("ok", Input.TouchTarget, req, plan.ActualMs));
                }
            });
        }
    }
}
