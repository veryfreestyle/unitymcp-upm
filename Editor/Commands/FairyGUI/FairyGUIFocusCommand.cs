using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public sealed class FairyGUIFocusCommand : IGroupedCommand
    {
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
            Description = "Request focus on a located GObject (e.g. GTextInput). Returns focused. Play mode only.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"), ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("panelInstanceId", JsonRpcSerializer.Object(("type", "integer"))),
                    ("path", JsonRpcSerializer.Object(("type", "string")))))),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", true))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            int? panelInstanceId = ReadIntNullable(request.Params, "panelInstanceId");
            string path = ReadString(request.Params, "path");

            var located = FairyGUINodeLocator.Locate(source, panelInstanceId, path);
            if (located.State != null)
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(("state", located.State)));
            var obj = located.Node.Unwrap();
            if (obj == null)
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(("state", "not_found")));

            obj.RequestFocus();
            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                ("state", "ok"), ("focused", obj.focused)));
        }

        private static int? ReadIntNullable(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsInt ? (int?)(int)p[key] : null;
        private static string ReadString(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsString ? (string)p[key] : null;
    }
}
