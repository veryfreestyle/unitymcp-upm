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
    /// 拖拽: 按下 - 移动 - 抬起。运动方式沿用 speedScale/steps 的时间/帧驱动二选一,
    /// 落到 fork 的 DragAtSpeed / 六参 Drag 上; hold 参数决定按下后与抬起前各停多久,
    /// 与移动方式独立配对(见 Drag_FrameDrivenMoveWithMsHold_IsAllowed 的用例注释)。
    /// </summary>
    public sealed class FguiInputDragCommand : FguiInputCommandBase
    {
        // holdBefore 不能为 0: down 在帧 N、第一个 move 在 N+1, 差值 1 会让整个拖拽的
        // 第一次 onTouchMove 上 evt.holdTime == -1f。缺省随移动方式联动。
        public const float DefaultHoldBeforeMs = 100f;
        public const int DefaultHoldBeforeFrames = 3;

        public FguiInputDragCommand(IPanelSource panelSource, IMcpStageInput input,
            McpStageInputSessionManager sessions, string projectRoot)
            : base(panelSource, input, sessions, projectRoot) { }

        public override string Method => RpcMethods.FairyGuiInputDrag;
        public override string Action => "drag";

        public override RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-input-drag",
            RpcMethod = RpcMethods.FairyGuiInputDrag,
            Title = "FairyGUI / Input / Drag",
            Description = "Press, travel to 'to', then release. 'to' is {path, panelInstanceId} or {x, y} or "
                + "{dx, dy} relative to the start. holdAfter decides fling inertia: pausing before release "
                + "stops the scroll where it is, releasing while still moving lets it keep sliding. "
                + "For a custom (non-straight) path use a session with press, several moves and release. "
                + "Returns dropTarget: the control under the pointer on the release frame.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = Schema(p =>
            {
                AddLocationSchema(p);
                AddMotionSchema(p);
                AddButtonSchema(p);
                p["to"] = JsonRpcSerializer.Object(
                    ("type", "object"),
                    ("description", "Destination: {path, panelInstanceId} or {x, y} or {dx, dy}."));
                p["holdBeforeMs"] = JsonRpcSerializer.Object(
                    ("type", "number"), ("description", "Pause after pressing, in milliseconds. Default 100. "
                        + "Combined with steps, the whole move becomes time-driven at the project's base "
                        + "speed instead of following steps."));
                p["holdBeforeFrames"] = JsonRpcSerializer.Object(
                    ("type", "integer"),
                    ("description", "Pause after pressing, in frames. Minimum 1. Default 3 when steps is used."));
                p["holdAfterMs"] = JsonRpcSerializer.Object(
                    ("type", "number"), ("description", "Pause before releasing, in milliseconds. Default 0. "
                        + "Combined with steps, the whole move becomes time-driven at the project's base "
                        + "speed instead of following steps."));
                p["holdAfterFrames"] = JsonRpcSerializer.Object(
                    ("type", "integer"), ("description", "Pause before releasing, in frames. Default 0."));
            }),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", false))
        };

        // 整段 body (含 Parse 和 PointerSpeedBase 读取) 都在 Guarded 里, 见 FguiInputMoveCommand
        // 顶上的注释: 这批 action 里没有代码站在 Guarded 外面。drag 是自足动作(不需要预先存在
        // 的 session), 跟 click/move/double-click 一样不加独立的 IsPlaying 门 —— not_playing
        // 从 Sessions.Acquire 的返回值里天然浮出来, 加一道重复的门只会让同一个工具在同一种情况
        // 下吐两种形状。
        public override UniTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request)
        {
            return Guarded(request, async () =>
            {
                FguiInputRequest req = FguiInputRequest.Parse(request.Params, PointerSpeedBase);
                if (req.Error != null) { return InvalidParams(request, req.ErrorDetail); }

                if (!TryReadTo(request.Params, out JsonData to, out string toTypeError))
                {
                    return InvalidParams(request, toTypeError);
                }

                if (!TryResolvePoint(req, out Vector2 from, out _, out JsonData fromFailure))
                {
                    return JsonRpcResponse.FromSuccess(request.Id, fromFailure);
                }
                if (!TryResolveDestination(to, from, out Vector2 target, out JsonData toFailure, out string toError))
                {
                    if (toError != null) { return InvalidParams(request, toError); }
                    return JsonRpcResponse.FromSuccess(request.Id, toFailure);
                }

                string error;
                float? holdBeforeMs = FguiInputRequest.ReadFloatNullable(request.Params, "holdBeforeMs", out error);
                if (error != null) { return InvalidParams(request, error); }
                int? holdBeforeFrames = FguiInputRequest.ReadIntNullable(request.Params, "holdBeforeFrames", out error);
                if (error != null) { return InvalidParams(request, error); }
                float? holdAfterMs = FguiInputRequest.ReadFloatNullable(request.Params, "holdAfterMs", out error);
                if (error != null) { return InvalidParams(request, error); }
                int? holdAfterFrames = FguiInputRequest.ReadIntNullable(request.Params, "holdAfterFrames", out error);
                if (error != null) { return InvalidParams(request, error); }

                if (holdBeforeMs.HasValue && holdBeforeFrames.HasValue)
                {
                    return InvalidParams(request, "holdBeforeMs and holdBeforeFrames are mutually exclusive.");
                }
                if (holdAfterMs.HasValue && holdAfterFrames.HasValue)
                {
                    return InvalidParams(request, "holdAfterMs and holdAfterFrames are mutually exclusive.");
                }
                if (holdBeforeMs.HasValue && holdBeforeMs.Value < 0f)
                {
                    return InvalidParams(request, "holdBeforeMs cannot be negative.");
                }
                if (holdAfterMs.HasValue && holdAfterMs.Value < 0f)
                {
                    return InvalidParams(request, "holdAfterMs cannot be negative.");
                }
                if (holdAfterFrames.HasValue && holdAfterFrames.Value < 0)
                {
                    return InvalidParams(request, "holdAfterFrames cannot be negative.");
                }
                // fork 的 CheckHoldBeforeFrames 在 holdBeforeFrames < 1 时抛 ArgumentOutOfRangeException
                // (六参 Drag 专属); 在这里挡住, 不能让那个异常穿到 Guarded 才被吞成 run_threw。
                if (holdBeforeFrames.HasValue && holdBeforeFrames.Value < 1)
                {
                    return InvalidParams(request, "holdBeforeFrames must be at least 1: a press and its "
                        + "first move on adjacent frames degrade the first onTouchMove's holdTime to -1.");
                }

                // 六参 Drag(帧驱动) 只有 motion 本身是 steps、且两个 hold 都没用毫秒表达时才用得上;
                // 混用(steps + holdBeforeMs/holdAfterMs) 统一走 DragAtSpeed, 因为它的 hold 参数是
                // 毫秒 —— 这正是"缺省联动、显式自由"里"自由"的那一半: hold 单位不强制跟随移动方式。
                bool useFrameDrag = req.Motion.ByFrames && !holdBeforeMs.HasValue && !holdAfterMs.HasValue;

                var budget = new FguiInputBudget();
                budget.AddFrames(2); // down + up

                int frameSteps = 0, frameHoldBefore = 0, frameHoldAfter = 0;
                float speedPps = 0f, msHoldBefore = 0f, msHoldAfter = 0f;

                if (useFrameDrag)
                {
                    frameSteps = req.Motion.Steps;
                    frameHoldBefore = holdBeforeFrames ?? DefaultHoldBeforeFrames;
                    frameHoldAfter = holdAfterFrames ?? 0;
                    budget.AddFrames(frameSteps + frameHoldBefore + frameHoldAfter);
                }
                else
                {
                    // motion 是 steps 但被 ms hold 拽进时间驱动分支时, 移动本身不再尝试保留
                    // steps 的帧数含义(帧数与墙钟没有固定换算) —— 直接用基准速度(等同 speedScale
                    // 缺省值 1), 因为这种用例真正关心的是 hold 的墙钟时长, 不是移动速度。
                    speedPps = req.Motion.ByFrames ? PointerSpeedBase : req.PixelsPerSecond(PointerSpeedBase);
                    msHoldBefore = holdBeforeMs ?? (holdBeforeFrames.HasValue
                        ? holdBeforeFrames.Value / 60f * 1000f : DefaultHoldBeforeMs);
                    msHoldAfter = holdAfterMs ?? (holdAfterFrames.HasValue
                        ? holdAfterFrames.Value / 60f * 1000f : 0f);

                    if (holdBeforeFrames.HasValue) { budget.AddFrames(holdBeforeFrames.Value); }
                    else { budget.AddMs(msHoldBefore); }
                    if (holdAfterFrames.HasValue) { budget.AddFrames(holdAfterFrames.Value); }
                    else { budget.AddMs(msHoldAfter); }

                    float distance = Vector2.Distance(from, target);
                    float durationMs = speedPps <= 0f ? 0f : distance / speedPps * 1000f;
                    budget.AddMs(durationMs);
                }

                string violation = budget.Violation();
                if (violation != null) { return InvalidParams(request, violation); }

                using (McpInputLease lease = Sessions.Acquire(Method))
                {
                    if (lease.Error != null)
                    {
                        return JsonRpcResponse.FromSuccess(request.Id,
                            JsonRpcSerializer.Object(("state", lease.Error)));
                    }

                    IEnumerator sequence = useFrameDrag
                        ? lease.Player.Drag(from, target, frameSteps, frameHoldBefore, frameHoldAfter, req.Button)
                        : lease.Player.DragAtSpeed(from, target, speedPps, msHoldBefore, msHoldAfter, req.Button);

                    McpRunOutcome outcome = await Input.RunAsync(sequence);
                    if (!outcome.Completed) { return Fault(request, outcome); }

                    // 拖放测试真正关心的是"放到了谁身上", 且无法从请求参数推出 —— TouchTarget
                    // 报的是抬起帧的实际命中, 不是 'to' 参数的回显。
                    //
                    // dropTarget 与 Payload() 写的 target 字段内容故意相同(两者都读同一个
                    // Input.TouchTarget): drag 只有一个"命中"概念, target 是所有 action 共用的
                    // 字段, dropTarget 是这个 action 专属、语义更明确的别名, 不是各自独立的两个
                    // 命中结果——重复是设计使然, 不要"去重"。
                    GObject drop = Input.TouchTarget;
                    JsonData payload = Payload("ok", drop, req, null);
                    payload["dropTarget"] = drop == null
                        ? null
                        : JsonRpcSerializer.Object(
                            ("name", drop.name ?? string.Empty), ("type", drop.GetType().Name));
                    return JsonRpcResponse.FromSuccess(request.Id, payload);
                }
            });
        }

        // 'to' 走跟顶层参数一样的三态契约: 缺失/显式 null 都算"没给"(drag 必填, 所以都报错);
        // 给了但不是 object 才是类型错。
        private static bool TryReadTo(JsonData p, out JsonData to, out string error)
        {
            to = null;
            error = null;
            JsonData raw = p != null && p.IsObject && p.ContainsKey("to") ? p["to"] : null;
            if (raw == null)
            {
                error = "'to' is required and must be an object.";
                return false;
            }
            if (!raw.IsObject)
            {
                error = "'to' must be an object.";
                return false;
            }
            to = raw;
            return true;
        }

        private bool TryResolveDestination(JsonData to, Vector2 from,
            out Vector2 target, out JsonData failure, out string error)
        {
            target = Vector2.zero;
            failure = null;
            error = null;

            float? dx = FguiInputRequest.ReadFloatNullable(to, "dx", out error);
            if (error != null) { return false; }
            float? dy = FguiInputRequest.ReadFloatNullable(to, "dy", out error);
            if (error != null) { return false; }
            float? x = FguiInputRequest.ReadFloatNullable(to, "x", out error);
            if (error != null) { return false; }
            float? y = FguiInputRequest.ReadFloatNullable(to, "y", out error);
            if (error != null) { return false; }
            string path = FguiInputRequest.ReadString(to, "path", out error);
            if (error != null) { return false; }
            int? panelInstanceId = FguiInputRequest.ReadIntNullable(to, "panelInstanceId", out error);
            if (error != null) { return false; }

            bool hasDelta = dx.HasValue || dy.HasValue;
            bool hasXy = x.HasValue || y.HasValue;
            bool hasPath = path != null;

            int groups = (hasDelta ? 1 : 0) + (hasXy ? 1 : 0) + (hasPath ? 1 : 0);
            if (groups > 1)
            {
                error = "'to' must contain exactly one of path, x/y, or dx/dy.";
                return false;
            }

            if (hasDelta)
            {
                if (dx.HasValue != dy.HasValue) { error = "'to' needs both dx and dy."; return false; }
                target = from + new Vector2(dx.Value, dy.Value);
                return true;
            }
            if (hasXy)
            {
                if (x.HasValue != y.HasValue) { error = "'to' needs both x and y."; return false; }
                target = new Vector2(x.Value, y.Value);
                return true;
            }
            if (hasPath)
            {
                FairyGUINodeLocator.LocateResult located = FairyGUINodeLocator.Locate(
                    PanelSource, panelInstanceId, path);
                if (located.State != null) { failure = FairyGUINodeLocator.FailurePayload(located); return false; }
                GObject obj = located.Node.Unwrap();
                if (obj == null) { failure = JsonRpcSerializer.Object(("state", "not_found")); return false; }
                target = FairyGUIScreenPoint.ScreenCenterOf(obj, Input.StageSize);
                return true;
            }

            error = "'to' must contain path, x/y, or dx/dy.";
            return false;
        }
    }
}
