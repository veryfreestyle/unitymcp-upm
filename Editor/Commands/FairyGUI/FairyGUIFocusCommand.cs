using System.Reflection;
using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public sealed class FairyGUIFocusCommand : IGroupedCommand
    {
        // 反射读 Stage 的私有静态字段 _inst, 不走公开的 Stage.inst getter ——
        // 后者第一次访问 (_inst == null) 会 new Stage(), 里面调
        // UnityEngine.Object.DontDestroyOnLoad, 在非 play 模式下直接抛
        // InvalidOperationException。读操作不该有"顺手建一个 Stage"的副作用:
        // 没有 Stage 就是没有焦点, 直接答 null, 跟 FairyGUIPanelSource 读
        // GRoot._inst/UIPanel._ui 避开 getter 副作用是同一个套路。
        private static readonly FieldInfo StageInstField =
            typeof(global::FairyGUI.Stage).GetField("_inst", BindingFlags.NonPublic | BindingFlags.Static);

        private readonly IPanelSource source;
        public FairyGUIFocusCommand(IPanelSource source) { this.source = source; }
        public string Method => RpcMethods.FairyGuiFocus;
        public string Group => "fgui.state";
        public string Action => "focus";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-focus",
            RpcMethod = RpcMethods.FairyGuiFocus,
            Title = "FairyGUI / Focus",
            Description = "With path: request focus on the located GObject (any GObject is accepted; targets "
                + "that cannot take focus return state ok with focused:false). Without path: read the current "
                + "focus and get back a path you can feed straight back in. Read it before sending keys — "
                + "a misdirected Enter may already have submitted a form. Play mode only.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"), ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("panelInstanceId", JsonRpcSerializer.Object(("type", "integer"), ("description", FairyGUINodeLocator.PanelInstanceIdHelp))),
                    ("path", JsonRpcSerializer.Object(("type", "string"), ("description", FairyGUINodeLocator.PathSyntaxHelp)))))),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", true))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            int? panelInstanceId = ReadIntNullable(request.Params, "panelInstanceId");
            // path 走 Task 6 定的三态约定(缺失/显式 null 都算没给, 类型给错才报 invalid_params):
            // 它现在还兼一层调度职责(有没有决定读还是设), 类型给错悄悄当"没给"会把 typo 错发成读焦点。
            string path = FguiInputRequest.ReadString(request.Params, "path", out string pathError);
            if (pathError != null)
            {
                return JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                    JsonRpcErrorCodes.InvalidParams, pathError,
                    JsonRpcSerializer.Object(("errorCode", "invalid_params"))));
            }

            if (path == null)
            {
                return ReadFocus(request);
            }

            var located = FairyGUINodeLocator.Locate(source, panelInstanceId, path);
            if (located.State != null)
                return JsonRpcResponse.FromSuccess(request.Id, FairyGUINodeLocator.FailurePayload(located));
            var obj = located.Node.Unwrap();
            if (obj == null)
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(("state", "not_found")));

            obj.RequestFocus();
            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                ("state", "ok"), ("focused", obj.focused)));
        }

        // 现状没有任何入口能问"当前焦点是谁": focus 只能设, fgui-query 的树序列化不含
        // focused 字段, AI 只能逐个控件穷举。只靠命令返回值事后确认不够 ——
        // 那意味着先发一次按键才知道打给了谁。
        private JsonRpcResponse ReadFocus(JsonRpcRequest request)
        {
            if (!source.IsPlaying)
            {
                return JsonRpcResponse.FromSuccess(request.Id,
                    JsonRpcSerializer.Object(("state", "not_playing")));
            }

            var stage = StageInstField?.GetValue(null) as global::FairyGUI.Stage;
            global::FairyGUI.GObject focused = stage?.focus?.gOwner;

            JsonData payload = JsonRpcSerializer.Object(("state", "ok"));
            if (focused == null)
            {
                payload["focus"] = null;
                return JsonRpcResponse.FromSuccess(request.Id, payload);
            }

            FocusPath resolved = FairyGUIFocusPathResolver.Resolve(source, focused);
            JsonData focus = JsonRpcSerializer.Object(
                ("name", focused.name ?? string.Empty),
                ("type", focused.GetType().Name),
                ("path", resolved.Found ? resolved.Path : null));
            if (resolved.PanelInstanceId.HasValue)
            {
                focus["panelInstanceId"] = resolved.PanelInstanceId.Value;
            }
            payload["focus"] = focus;
            return JsonRpcResponse.FromSuccess(request.Id, payload);
        }

        private static int? ReadIntNullable(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsInt ? (int?)(int)p[key] : null;
    }
}
