using System.Collections;
using Cysharp.Threading.Tasks;
using FairyGUI;
using LitJson;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Commands;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public sealed class FguiInputDoubleClickCommand : FguiInputCommandBase
    {
        // TouchInfo.End 用 Time.unscaledTime 判 0.35 秒双击窗口 (Stage.cs:1745)。
        public const float DoubleClickWindowMs = 350f;

        // 测试注入实测间隔; 生产路径下为 null, 取 TimedPair 打的时间戳。
        public float? TestOnlyClickGapMs;

        public FguiInputDoubleClickCommand(IPanelSource panelSource, IMcpStageInput input,
            McpStageInputSessionManager sessions, string projectRoot)
            : base(panelSource, input, sessions, projectRoot) { }

        public override string Method => RpcMethods.FairyGuiInputDoubleClick;
        public override string Action => "double-click";

        public override RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-input-double-click",
            RpcMethod = RpcMethods.FairyGuiInputDoubleClick,
            Title = "FairyGUI / Input / Double click",
            Description = "Two clicks in a row at a target. When the measured gap between the two releases "
                + "exceeds the 350 ms double-click window (possible at low frame rates), FairyGUI dispatches "
                + "two single clicks instead and this returns state degraded with the measured actualMs.",
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
                budget.AddFrames(6);   // 两次 Click, 各 3 帧
                string violation = budget.Violation();
                if (violation != null) { return InvalidParams(request, violation); }

                using (McpInputLease lease = Sessions.Acquire(Method))
                {
                    if (lease.Error != null)
                    {
                        return JsonRpcResponse.FromSuccess(request.Id,
                            JsonRpcSerializer.Object(("state", lease.Error)));
                    }

                    // fork 的 DoubleClick(Vector2) 没有 button 参数, 所以自己串两次 Click ——
                    // 帧数一样是 6, 顺带能在两次抬起之间打时间戳算 degraded。
                    IEnumerator move = BuildMoveSegment(lease.Player, plan);
                    var stamps = new float[2];
                    McpRunOutcome outcome = await Input.RunAsync(McpInputSequences.Concat(
                        move,
                        McpInputSequences.TimedPair(() => lease.Player.Click(point, req.Button), stamps)));
                    if (!outcome.Completed) { return Fault(request, outcome); }

                    float gapMs = TestOnlyClickGapMs ?? (stamps[1] - stamps[0]) * 1000f;
                    bool degraded = gapMs > DoubleClickWindowMs;
                    return JsonRpcResponse.FromSuccess(request.Id, Payload(
                        degraded ? "degraded" : "ok",
                        Input.TouchTarget,
                        req,
                        degraded ? gapMs : plan.ActualMs));
                }
            });
        }
    }
}
