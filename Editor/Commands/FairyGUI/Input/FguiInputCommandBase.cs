using System;
using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using FairyGUI;
using LitJson;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Commands;
using VeryFS.UnityMCP.Editor.Infrastructure;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    /// <summary>
    /// fgui.input 新一批 action 的公共骨架。每个 action 自己写 HandleAsync ——
    /// 13 个 action 的形状差得远(有的 0 帧, 有的要 session, 有的没有定位),
    /// 硬套一个模板方法只会让每个子类都在绕开它。这里只提供共用零件。
    /// </summary>
    public abstract class FguiInputCommandBase : IGroupedCommand, IAsyncRpcCommand
    {
        // speedScale 模式下距离过短不足 2 帧时抬到 2 帧: 低于 2 帧会撞
        // TouchInfo.Move/End 的 (Time.frameCount - downFrame) == 1 分支,
        // 而 Application.targetFrameRate 默认 -1 会让 evt.holdTime 变成 -1f。
        // 请求期不知道真实帧率, 按 60fps 折算。
        protected const float MinMoveDurationMs = 34f;

        protected readonly IPanelSource PanelSource;
        protected readonly IMcpStageInput Input;
        protected readonly McpStageInputSessionManager Sessions;
        protected readonly string ProjectRoot;

        protected FguiInputCommandBase(IPanelSource panelSource, IMcpStageInput input,
            McpStageInputSessionManager sessions, string projectRoot)
        {
            PanelSource = panelSource;
            Input = input;
            Sessions = sessions;
            ProjectRoot = projectRoot;
        }

        public string Group => RpcMethods.FairyGuiInputGroup;
        public abstract string Action { get; }
        public abstract string Method { get; }
        public abstract RpcToolDescriptor Descriptor { get; }
        public abstract UniTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request);

        public JsonRpcResponse Handle(JsonRpcRequest request)
            => JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                JsonRpcErrorCodes.InternalError, "async command; use HandleAsync",
                JsonRpcSerializer.Object(("errorCode", "async_command"))));

        protected float PointerSpeedBase => FguiInputPreferences.LoadPointerSpeedBase(ProjectRoot);
        protected float WheelScale => FguiInputPreferences.LoadWheelScale(ProjectRoot);

        protected JsonRpcResponse InvalidParams(JsonRpcRequest request, string detail)
            => JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                JsonRpcErrorCodes.InvalidParams, detail,
                JsonRpcSerializer.Object(("errorCode", "invalid_params"))));

        protected JsonRpcResponse SessionRequired(JsonRpcRequest request, string action)
            => JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                JsonRpcErrorCodes.InvalidParams,
                "'" + action + "' needs an open session: without one the command ends by clearing "
                + "every TouchInfo, so the state it leaves behind does not survive to the next call. "
                + "Call begin-session first, then end-session when done.",
                JsonRpcSerializer.Object(("errorCode", "session_required"))));

        // McpStageInputGateway.RunAsync 的 XML doc 明说了: binding.Run 的四道门
        // (sequence 为 null / 不在 Play 模式 / 未 Start / 已有序列在跑) 任一没过时
        // 同步抛出原始异常, 不会被吞成 McpRunOutcome.Faulted —— 调用方必须 try/catch。
        // 同样真实的还有 Sessions.Acquire -> input.Start 撞 "已被会话占用" 抛出,
        // 以及 lease.Dispose -> input.Dispose 抛出 (McpStageInputSessionManager 自己的
        // 注释称之为"真实路径", 不是假设)。不接住这些, 请求就永久不响应, 客户端只能
        // 干等到 60s 工具超时, 而这些异常本该在毫秒级就能报出诊断信息。
        //
        // 覆盖面: 调用方把 TryResolvePoint / 预算校验 / Sessions.Acquire / 序列 Run /
        // lease 释放全部放进这个 wrapper 的 body 里, 一次 try/catch 接住整段。
        protected async UniTask<JsonRpcResponse> Guarded(
            JsonRpcRequest request, Func<UniTask<JsonRpcResponse>> body)
        {
            try
            {
                return await body();
            }
            catch (Exception ex)
            {
                return Fault(request, ex);
            }
        }

        // target 报的是实际结果而非请求参数的回显 —— 控件可能在移动途中被 Transition
        // 移走、被遮挡、或滚出屏幕, 返回实际命中的才有信息量。
        protected JsonData Payload(string state, GObject target, FguiInputRequest req, float? actualMs)
        {
            JsonData payload = JsonRpcSerializer.Object(("state", state));
            // JsonType.None (new JsonData() 的缺省类型) 落进 WriteJson 时哪个分支都不
            // 匹配, 一个值 token 都不写, 会把 "target": 和下一个属性的冒号焊在一起
            // (WriteJson 只认 obj == null 才写字面量 null)。这里必须是真正的 C# null。
            payload["target"] = target == null
                ? null
                : JsonRpcSerializer.Object(
                    ("name", target.name ?? string.Empty), ("type", target.GetType().Name));

            if (Sessions.HasSession)
            {
                payload["session"] = JsonRpcSerializer.Object(
                    ("label", Sessions.SessionLabel ?? string.Empty),
                    ("ageSeconds", Math.Round(Sessions.SessionAgeSeconds, 1)));
            }
            if (actualMs.HasValue)
            {
                payload["actualMs"] = Math.Round((double)actualMs.Value, 1);
            }
            if (req != null && req.Warnings.Count > 0)
            {
                var warnings = new JsonData();
                warnings.SetJsonType(JsonType.Array);
                foreach (string w in req.Warnings) { warnings.Add(w); }
                payload["warnings"] = warnings;
            }
            return payload;
        }

        // 按面板自己声明的 Source 判, 不去嗅探 layer / hitArea —— PanelInfo.Source 本来就是
        // "UIPanel" | "UIPainter" 两选一, 用声明值比用结构特征稳。只在给了 panelInstanceId
        // 时才查: UIPainter 的内容不挂在 GRoot 下, 不给 panelInstanceId 根本定位不到它。
        private bool IsUIPainterPanel(int panelInstanceId)
        {
            IReadOnlyList<PanelInfo> panels = PanelSource.ListPanels();
            for (int i = 0; i < panels.Count; i++)
            {
                if (panels[i].InstanceId == panelInstanceId)
                {
                    return panels[i].Source == "UIPainter";
                }
            }
            return false;
        }

        /// <summary>
        /// 目标结构上就收不到指针输入时挂一条 warning。注入之前判, 不看注入结果。
        ///
        /// 为什么需要: TryResolvePoint 只把 path 解析成一个点, 不做任何可达性检查。目标
        /// visible:false 或 touchable:false 时, 注入照样发生在那个点上, 打到的是它下面的
        /// 别的东西 —— 而 state 仍然是 "ok", 调用方看不出请求的对象根本没收到输入。两种
        /// 情况都实测复现过(不可见的 btn_Back 打到了 n21; touchable:false 的 n32 穿透到了
        /// 父节点)。
        ///
        /// 为什么不拿返回里的 target 跟请求目标比对: target 是整段序列跑完之后读的实际命中,
        /// 而不是按下那一刻的命中(见 Payload 的注释)。点击引发翻页/弹窗/列表重排时, 新内容
        /// 会滑到静止的指针底下, 比对必然误报 —— 实测点 btn_Button 就是这样: 按下时命中的
        /// 是它自己的 icon, 读 target 时页面已经滑走, 命中变成了新页面里另一个 icon。
        ///
        /// 覆盖不到的两种: 被别的东西盖住(要真做命中测试才知道), 以及 UIPainter 渲染的面板
        /// (它的坐标不是屏幕坐标, 指针根本够不到)。两条都还挂在待办上。
        /// </summary>
        protected static void WarnIfUnreachable(FguiInputRequest req, GObject located)
        {
            if (req == null || located == null) { return; }

            string what = string.IsNullOrEmpty(req.Path) ? "The requested object" : "'" + req.Path + "'";

            for (GObject node = located; node != null; node = node.parent)
            {
                if (node.visible) { continue; }
                req.Warnings.Add(what + " is not reachable by the pointer: "
                    + (node == located ? "it is visible:false" : "its ancestor '" + (node.name ?? string.Empty)
                        + "' is visible:false")
                    + ", so this input went to whatever is actually at that point instead. "
                    + "Use fgui-state if you need to drive it anyway.");
                return;
            }

            if (!located.touchable)
            {
                req.Warnings.Add(what + " is not reachable by the pointer: it is touchable:false, so this "
                    + "input passed through to whatever is behind it. Use fgui-state if you need to drive "
                    + "it anyway.");
            }
        }

        // 定位三选一: path (+ panelInstanceId) / x,y / 都不给取指针当前位置。
        protected bool TryResolvePoint(FguiInputRequest req,
            out Vector2 point, out GObject located, out JsonData failure)
        {
            located = null;
            failure = null;

            if (req.HasXy) { point = req.Xy; return true; }
            if (!req.HasPath) { point = Input.CurrentPointerPosition; return true; }

            // UIPainter 的内容拿不到屏幕坐标, 指针类 action 对它一律不可用 —— 与其算出一个
            // 落在视口外的点、静默打空, 不如直接拒掉并指路。
            //
            // 机制: UIPainter 把自己的 container 设成 WorldSpace 但丢在 CaptureCamera 的
            // 隐藏层上, 经 CaptureCamera.Capture 渲染进一张 RenderTexture, 再由使用方把这张
            // 贴图贴到任意几何体(曲面/方块/…)。所以 container 的世界坐标在隐藏层上, 跟屏幕上
            // 看到的位置毫无关系, LocalToGlobal 给出的数字当屏幕点用必然落到视口外(实测
            // Example 21 - Curve UI: contentWidth 8020 的列表, 第 3 项算出来 x≈3200)。
            //
            // 它的交互是反方向做的(UIPainter 给 container 挂 MeshColliderHitTest): 从真实相机
            // 发射线打 MeshCollider, 用 hit.textureCoord 的 UV 反推回 UI 局部坐标。整条链路
            // 是 屏幕 -> 射线 -> UV -> UI 坐标, 单向; 逆映射要在网格里找出携带该 UV 的三角形
            // 再投影回屏幕, 而且贴图平铺/贴在多个物体上时根本不唯一。
            if (req.PanelInstanceId.HasValue && IsUIPainterPanel(req.PanelInstanceId.Value))
            {
                point = Vector2.zero;
                failure = JsonRpcSerializer.Object(
                    ("state", "unsupported"),
                    ("reason", "uipainter_not_addressable"));
                failure["detail"] = "This panel is a UIPainter: its UI is rendered off-screen into a "
                    + "texture that you map onto arbitrary geometry, so its coordinates are not screen "
                    + "coordinates and the pointer cannot be aimed at it. Drive it with fgui-state "
                    + "(call-event / set-selection / set-text / ...) instead.";
                return false;
            }

            FairyGUINodeLocator.LocateResult result =
                FairyGUINodeLocator.Locate(PanelSource, req.PanelInstanceId, req.Path);
            if (result.State != null)
            {
                point = Vector2.zero;
                failure = FairyGUINodeLocator.FailurePayload(result);
                return false;
            }
            located = result.Node.Unwrap();
            if (located == null)
            {
                point = Vector2.zero;
                failure = JsonRpcSerializer.Object(("state", "not_found"));
                return false;
            }
            point = FairyGUIScreenPoint.ScreenCenterOf(located, Input.StageSize);
            return true;
        }

        /// <summary>
        /// 移动段的预算部分: 只算这一段要花多少墙钟/帧预算, 不摸 player、不碰 session。
        /// 定位参数不给时是 no-op (0 帧)。
        ///
        /// 为什么要从原来一体的 MoveSegment 里把这半段单独拎出来: 预算校验必须能在
        /// Sessions.Acquire 之前就跑完。StageInputSimulator.Start/Restore 会重置 Stage
        /// 的 hover/rollover 与按下状态 (ResetInputState 不派发 onRollOut) —— 一个纯参数
        /// 错误 (比如 steps 超过 1800) 不该先把这些状态擦掉才被拒。这一半只需要 req 和两个
        /// 端点, 不需要 player, 所以可以、也应该排在 Acquire 之前。
        /// </summary>
        protected MoveSegmentPlan PlanMoveSegment(
            FguiInputRequest req, Vector2 from, Vector2 to, FguiInputBudget budget)
        {
            if (!req.HasLocation) { return new MoveSegmentPlan(false, false, to, 0, 0f, null); }

            if (req.Motion.ByFrames)
            {
                budget.AddFrames(req.Motion.Steps);
                return new MoveSegmentPlan(true, true, to, req.Motion.Steps, 0f, null);
            }

            float pps = req.PixelsPerSecond(PointerSpeedBase);
            float distance = Vector2.Distance(from, to);
            float durationMs = pps <= 0f ? 0f : distance / pps * 1000f;
            float? actualMs = null;
            if (distance > 0f && durationMs < MinMoveDurationMs)
            {
                durationMs = MinMoveDurationMs;
                pps = distance / (MinMoveDurationMs / 1000f);
                actualMs = MinMoveDurationMs;
            }
            budget.AddMs(durationMs);
            return new MoveSegmentPlan(true, false, to, 0, pps, actualMs);
        }

        /// <summary>
        /// 移动段的执行部分: 拿到 session (即拿到 player) 之后, 把预算阶段已经算好的
        /// plan 兑成 player 能跑的序列。plan.HasMove 为 false (定位参数没给) 时返回
        /// null (McpInputSequences.Concat 会跳过 null 段)。
        /// </summary>
        protected IEnumerator BuildMoveSegment(McpStageInputPlayer player, MoveSegmentPlan plan)
        {
            if (!plan.HasMove) { return null; }
            return plan.ByFrames
                ? player.MoveTo(plan.To, plan.Steps)
                : player.MoveAtSpeed(plan.To, plan.PixelsPerSecond);
        }

        /// <summary>PlanMoveSegment 与 BuildMoveSegment 之间传递的纯数据, 不持有任何引用。</summary>
        protected readonly struct MoveSegmentPlan
        {
            internal MoveSegmentPlan(bool hasMove, bool byFrames, Vector2 to,
                int steps, float pixelsPerSecond, float? actualMs)
            {
                HasMove = hasMove;
                ByFrames = byFrames;
                To = to;
                Steps = steps;
                PixelsPerSecond = pixelsPerSecond;
                ActualMs = actualMs;
            }

            public bool HasMove { get; }
            public bool ByFrames { get; }
            public Vector2 To { get; }
            public int Steps { get; }
            public float PixelsPerSecond { get; }

            // 只在因 MinMoveDurationMs 下限被抬速时非 null。
            public float? ActualMs { get; }
        }

        protected JsonRpcResponse Fault(JsonRpcRequest request, McpRunOutcome outcome)
            => JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                JsonRpcErrorCodes.InternalError,
                "input sequence " + (outcome.Result ?? "failed").ToLowerInvariant()
                    + (outcome.Error != null ? ": " + outcome.Error.Message : string.Empty),
                JsonRpcSerializer.Object(
                    ("errorCode", "run_" + (outcome.Result ?? "failed").ToLowerInvariant()),
                    ("frames", outcome.Frames))));

        // Fault(outcome) 的另一半: 接的是 Acquire / TryResolvePoint / RunAsync / lease
        // 释放同步抛出的原始异常 (McpStageInputBinding.InvokeUnwrapped 用 ExceptionDispatchInfo
        // 保留了原始类型与消息), 不用通用字符串替换掉 fork 侧写给调用方看的诊断。
        protected JsonRpcResponse Fault(JsonRpcRequest request, Exception exception)
            => JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                JsonRpcErrorCodes.InternalError,
                "input sequence threw: " + exception.Message,
                JsonRpcSerializer.Object(("errorCode", "run_threw"))));

        // ---- schema 片段。组合并时同名参数类型必须一致, 所以只在这里定义一次。----

        protected static void AddLocationSchema(JsonData properties)
        {
            properties["path"] = JsonRpcSerializer.Object(
                ("type", "string"), ("description", FairyGUINodeLocator.PathSyntaxHelp));
            properties["panelInstanceId"] = JsonRpcSerializer.Object(
                ("type", "integer"), ("description", FairyGUINodeLocator.PanelInstanceIdHelp));
            properties["x"] = JsonRpcSerializer.Object(
                ("type", "number"), ("description", "Raw screen x. Mutually exclusive with path."));
            properties["y"] = JsonRpcSerializer.Object(
                ("type", "number"), ("description", "Raw screen y. Mutually exclusive with path."));
        }

        protected static void AddMotionSchema(JsonData properties)
        {
            properties["speedScale"] = JsonRpcSerializer.Object(
                ("type", "number"),
                ("description", "Time-driven pointer travel. 1 is the project's configured base speed; "
                    + "0.5 halves it, 2 doubles it. Default 1. Mutually exclusive with steps."));
            properties["steps"] = JsonRpcSerializer.Object(
                ("type", "integer"),
                ("description", "Frame-driven pointer travel: cut the whole displacement into N frames. "
                    + "Minimum 1, which teleports. Mutually exclusive with speedScale."));
        }

        protected static void AddButtonSchema(JsonData properties)
        {
            properties["button"] = JsonRpcSerializer.Object(
                ("type", "integer"), ("description", "0 left (default), 1 right, 2 middle."));
        }

        protected static JsonData Schema(Action<JsonData> fill)
        {
            JsonData properties = JsonRpcSerializer.Object();
            fill(properties);
            JsonData schema = JsonRpcSerializer.Object(
                ("type", "object"), ("additionalProperties", false));
            schema["properties"] = properties;
            return schema;
        }
    }
}
