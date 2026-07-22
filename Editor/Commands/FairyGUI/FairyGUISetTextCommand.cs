using FairyGUI;
using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public sealed class FairyGUISetTextCommand : IGroupedCommand
    {
        private readonly IPanelSource source;

        public FairyGUISetTextCommand(IPanelSource source)
        {
            this.source = source;
        }

        public string Method => RpcMethods.FairyGuiSetText;
        public string Group => "fgui.state";
        public string Action => "set-text";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-set-text",
            RpcMethod = RpcMethods.FairyGuiSetText,
            Title = "FairyGUI / Set Text",
            Description = "Write text to a text-bearing GObject (GTextField/GButton/GLabel/GComboBox and subclasses). " +
                "Rejects non-text controls with unsupported. Reads the value back. Play mode only.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"), ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("panelInstanceId", JsonRpcSerializer.Object(("type", "integer"))),
                    ("path", JsonRpcSerializer.Object(("type", "string"))),
                    ("text", JsonRpcSerializer.Object(("type", "string"))))),
                ("required", MakeRequired("text"))),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", true))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            int? panelInstanceId = ReadIntNullable(request.Params, "panelInstanceId");
            string path = ReadString(request.Params, "path");
            string text = ReadString(request.Params, "text") ?? string.Empty;

            var located = FairyGUINodeLocator.Locate(source, panelInstanceId, path);
            if (located.State != null)
            {
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(("state", located.State)));
            }

            var obj = located.Node.Unwrap();
            if (!IsTextBearing(obj))
            {
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                    ("state", "unsupported"), ("type", obj != null ? obj.GetType().Name : located.Node.TypeName)));
            }

            obj.text = text;
            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                ("state", "ok"), ("text", obj.text ?? string.Empty)));
        }

        // GTextInput/GRichTextField 是 GTextField 子类, 自动覆盖。
        private static bool IsTextBearing(GObject obj)
            => obj is GTextField || obj is GButton || obj is GLabel || obj is GComboBox;

        private static JsonData MakeRequired(params string[] names)
        {
            var arr = new JsonData();
            arr.SetJsonType(JsonType.Array);
            foreach (var n in names) { arr.Add(n); }
            return arr;
        }

        private static int? ReadIntNullable(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsInt ? (int?)(int)p[key] : null;

        private static string ReadString(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsString ? (string)p[key] : null;
    }
}
