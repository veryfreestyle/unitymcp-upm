using FairyGUI;
using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public sealed class FairyGUICallEventCommand : IGroupedCommand
    {
        private readonly IPanelSource source;

        public FairyGUICallEventCommand(IPanelSource source)
        {
            this.source = source;
        }

        public string Method => RpcMethods.FairyGuiCallEvent;
        public string Group => "fgui.state";
        public string Action => "call-event";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-call-event",
            RpcMethod = RpcMethods.FairyGuiCallEvent,
            Title = "FairyGUI / Call Event",
            Description = "Dispatch a FairyGUI EventListener (default onClick) on a located GObject via Call(). " +
                "Supports onClick/onRightClick/onTouchBegin/onTouchEnd/onChanged/onSubmit/onRollOver/onRollOut. Play mode only.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"), ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("panelInstanceId", JsonRpcSerializer.Object(("type", "integer"))),
                    ("path", JsonRpcSerializer.Object(("type", "string"))),
                    ("event", JsonRpcSerializer.Object(("type", "string")))))),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", false))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            int? panelInstanceId = ReadIntNullable(request.Params, "panelInstanceId");
            string path = ReadString(request.Params, "path");
            string eventName = ReadString(request.Params, "event") ?? "onClick";

            var located = FairyGUINodeLocator.Locate(source, panelInstanceId, path);
            if (located.State != null)
            {
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(("state", located.State)));
            }

            var obj = located.Node.Unwrap();
            var listener = obj == null ? null : GetListener(obj, eventName);
            if (listener == null)
            {
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                    ("state", "unsupported"), ("event", eventName)));
            }

            bool hadListener = !listener.isEmpty;
            bool defaultPrevented = listener.Call();
            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                ("state", "ok"),
                ("invoked", true),
                ("hadListener", hadListener),
                ("defaultPrevented", defaultPrevented),
                ("target", JsonRpcSerializer.Object(
                    ("name", obj.name ?? string.Empty), ("type", obj.GetType().Name)))));
        }

        // 白名单事件名 -> GObject 的 EventListener 属性; 未知返 null。
        // onClick/onRightClick/onTouchBegin/onTouchEnd/onRollOver/onRollOut 是 GObject 基类属性;
        // onChanged/onSubmit 是特定子类属性, 按类型转换取。
        private static EventListener GetListener(GObject obj, string eventName)
        {
            switch (eventName)
            {
                case "onClick": return obj.onClick;
                case "onRightClick": return obj.onRightClick;
                // GComboBox 的 __touchBegin 挂在 displayObject 上 (GComboBox.cs:452), 非 GObject.onTouchBegin。
                // 用 GObject 的会空派发、打不开下拉。故 combobox 走 displayObject。
                case "onTouchBegin":
                    return obj is GComboBox && obj.displayObject != null
                        ? obj.displayObject.onTouchBegin
                        : obj.onTouchBegin;
                case "onTouchEnd": return obj.onTouchEnd;
                case "onRollOver": return obj.onRollOver;
                case "onRollOut": return obj.onRollOut;
                case "onChanged":
                    if (obj is GButton gb) return gb.onChanged;
                    if (obj is GComboBox gc) return gc.onChanged;
                    if (obj is GTextInput gt) return gt.onChanged;
                    return null;
                case "onSubmit":
                    if (obj is GTextInput gti) return gti.onSubmit;
                    return null;
                default: return null;
            }
        }

        private static int? ReadIntNullable(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsInt ? (int?)(int)p[key] : null;

        private static string ReadString(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsString ? (string)p[key] : null;
    }
}
