using FairyGUI;
using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public sealed class FairyGUIScrollCommand : IGroupedCommand
    {
        private readonly IPanelSource source;

        public FairyGUIScrollCommand(IPanelSource source) { this.source = source; }

        public string Method => RpcMethods.FairyGuiScroll;
        public string Group => "fgui.state";
        public string Action => "scroll";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-scroll",
            RpcMethod = RpcMethods.FairyGuiScroll,
            Title = "FairyGUI / Scroll",
            Description = "Scroll a GComponent's scrollPane to percX/percY (0..1) or scroll a GList item into view. " +
                "Immediate (no animation), fires onScroll. Rejects non-scrollable with unsupported. Play mode only.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"), ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("panelInstanceId", JsonRpcSerializer.Object(("type", "integer"), ("description", FairyGUINodeLocator.PanelInstanceIdHelp))),
                    ("path", JsonRpcSerializer.Object(("type", "string"), ("description", FairyGUINodeLocator.PathSyntaxHelp))),
                    ("percX", JsonRpcSerializer.Object(("type", "number"))),
                    ("percY", JsonRpcSerializer.Object(("type", "number"))),
                    ("scrollToViewIndex", JsonRpcSerializer.Object(("type", "integer")))))),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", true))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            int? panelInstanceId = ReadIntNullable(request.Params, "panelInstanceId");
            string path = ReadString(request.Params, "path");
            double? percX = ReadDoubleNullable(request.Params, "percX");
            double? percY = ReadDoubleNullable(request.Params, "percY");
            int? viewIndex = ReadIntNullable(request.Params, "scrollToViewIndex");

            var located = FairyGUINodeLocator.Locate(source, panelInstanceId, path);
            if (located.State != null)
                return JsonRpcResponse.FromSuccess(request.Id, FairyGUINodeLocator.FailurePayload(located));

            var comp = located.Node.Unwrap() as GComponent;
            if (comp == null || comp.scrollPane == null)
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                    ("state", "unsupported"), ("type", located.Node.TypeName)));

            var sp = comp.scrollPane;
            if (percX.HasValue) sp.SetPercX((float)percX.Value, false);
            if (percY.HasValue) sp.SetPercY((float)percY.Value, false);
            if (viewIndex.HasValue && comp is GList list) list.ScrollToView(viewIndex.Value);

            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                ("state", "ok"),
                ("percX", (double)sp.percX), ("percY", (double)sp.percY),
                ("isBottomMost", sp.isBottomMost), ("isRightMost", sp.isRightMost)));
        }

        private static int? ReadIntNullable(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsInt ? (int?)(int)p[key] : null;

        private static string ReadString(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsString ? (string)p[key] : null;

        private static double? ReadDoubleNullable(JsonData p, string key)
        {
            if (p == null || !p.IsObject || !p.ContainsKey(key)) return null;
            var v = p[key];
            if (v.IsDouble) return (double)v;
            if (v.IsInt) return (int)v;
            return null;
        }
    }
}
