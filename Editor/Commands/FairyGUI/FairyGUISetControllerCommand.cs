using FairyGUI;
using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public sealed class FairyGUISetControllerCommand : IGroupedCommand
    {
        private readonly IPanelSource source;

        public FairyGUISetControllerCommand(IPanelSource source)
        {
            this.source = source;
        }

        public string Method => RpcMethods.FairyGuiSetController;
        public string Group => "fgui.state";
        public string Action => "set-controller";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-set-controller",
            RpcMethod = RpcMethods.FairyGuiSetController,
            Title = "FairyGUI / Set Controller",
            Description = "Set a GComponent controller page by page name (preferred) or index, via the " +
                "selectedPage/selectedIndex setter (fires onChanged). Validates before writing. " +
                "Targets that are not GComponent (GButton/GTextField/GImage/GLoader/...) return unsupported. Play mode only.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"), ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("panelInstanceId", JsonRpcSerializer.Object(("type", "integer"), ("description", FairyGUINodeLocator.PanelInstanceIdHelp))),
                    ("path", JsonRpcSerializer.Object(("type", "string"), ("description", FairyGUINodeLocator.PathSyntaxHelp))),
                    ("controllerName", JsonRpcSerializer.Object(("type", "string"))),
                    ("page", JsonRpcSerializer.Object(("type", "string"))),
                    ("index", JsonRpcSerializer.Object(("type", "integer"), ("minimum", 0))))),
                ("required", MakeRequired("controllerName"))),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", true))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            int? panelInstanceId = ReadIntNullable(request.Params, "panelInstanceId");
            string path = ReadString(request.Params, "path");
            string controllerName = ReadString(request.Params, "controllerName");
            string page = ReadString(request.Params, "page");
            int? index = ReadIntNullable(request.Params, "index");

            var located = FairyGUINodeLocator.Locate(source, panelInstanceId, path);
            if (located.State != null)
            {
                return JsonRpcResponse.FromSuccess(request.Id, FairyGUINodeLocator.FailurePayload(located));
            }

            var comp = located.Node.Unwrap() as GComponent;
            if (comp == null)
            {
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                    ("state", "unsupported"), ("type", located.Node.TypeName)));
            }

            var ctrl = comp.GetController(controllerName);
            if (ctrl == null)
            {
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                    ("state", "controller_not_found"), ("controllerName", controllerName ?? string.Empty)));
            }

            // 双参优先级: page 优先 index。
            if (!string.IsNullOrEmpty(page))
            {
                if (!ctrl.HasPage(page))
                {
                    return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                        ("state", "invalid_page"), ("page", page)));
                }
                ctrl.selectedPage = page; // 触发 onChanged
            }
            else if (index.HasValue)
            {
                if (index.Value < 0 || index.Value >= ctrl.pageCount)
                {
                    return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                        ("state", "invalid_index"), ("index", index.Value), ("pageCount", ctrl.pageCount)));
                }
                ctrl.selectedIndex = index.Value; // 触发 onChanged
            }
            else
            {
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                    ("state", "error"), ("errorCode", "missing_page_or_index")));
            }

            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                ("state", "ok"),
                ("selectedIndex", ctrl.selectedIndex),
                ("selectedPage", ctrl.selectedPage ?? string.Empty)));
        }

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
