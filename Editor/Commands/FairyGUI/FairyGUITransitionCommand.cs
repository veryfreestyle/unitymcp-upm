using FairyGUI;
using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public sealed class FairyGUITransitionCommand : IGroupedCommand
    {
        private readonly IPanelSource source;

        public FairyGUITransitionCommand(IPanelSource source)
        {
            this.source = source;
        }

        public string Method => RpcMethods.FairyGuiTransition;
        public string Group => "fgui.state";
        public string Action => "transition";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-transition",
            RpcMethod = RpcMethods.FairyGuiTransition,
            Title = "FairyGUI / Transition",
            Description = "Control a GComponent transition: op play (default) | playReverse | stop. " +
                "stop uses stopSetToComplete (default true) to jump to end state for deterministic assertions. " +
                "Returns playing. Targets that are not GComponent return unsupported. Play mode only.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"), ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("panelInstanceId", JsonRpcSerializer.Object(("type", "integer"), ("description", FairyGUINodeLocator.PanelInstanceIdHelp))),
                    ("path", JsonRpcSerializer.Object(("type", "string"), ("description", FairyGUINodeLocator.PathSyntaxHelp))),
                    ("transitionName", JsonRpcSerializer.Object(("type", "string"))),
                    ("op", JsonRpcSerializer.Object(("type", "string"))),
                    ("stopSetToComplete", JsonRpcSerializer.Object(("type", "boolean"))))),
                ("required", MakeRequired("transitionName"))),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", false))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            int? panelInstanceId = ReadIntNullable(request.Params, "panelInstanceId");
            string path = ReadString(request.Params, "path");
            string transitionName = ReadString(request.Params, "transitionName");
            string action = ReadString(request.Params, "op") ?? "play";
            bool setToComplete = request.Params == null || !request.Params.ContainsKey("stopSetToComplete") || ReadBool(request.Params, "stopSetToComplete");

            var located = FairyGUINodeLocator.Locate(source, panelInstanceId, path);
            if (located.State != null)
                return JsonRpcResponse.FromSuccess(request.Id, FairyGUINodeLocator.FailurePayload(located));

            var comp = located.Node.Unwrap() as GComponent;
            if (comp == null)
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                    ("state", "unsupported"), ("type", located.Node.TypeName)));

            var t = comp.GetTransition(transitionName);
            if (t == null)
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                    ("state", "transition_not_found"), ("transitionName", transitionName ?? string.Empty)));

            switch (action)
            {
                case "play": t.Play(); break;
                case "playReverse": t.PlayReverse(); break;
                case "stop": t.Stop(setToComplete, false); break;
                default:
                    return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                        ("state", "error"), ("errorCode", "invalid_action")));
            }

            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                ("state", "ok"), ("transitionName", transitionName), ("playing", t.playing)));
        }

        private static JsonData MakeRequired(params string[] names)
        {
            var arr = new JsonData();
            arr.SetJsonType(JsonType.Array);
            foreach (var n in names) arr.Add(n);
            return arr;
        }

        private static int? ReadIntNullable(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsInt ? (int?)(int)p[key] : null;

        private static string ReadString(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsString ? (string)p[key] : null;

        private static bool ReadBool(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsBoolean && (bool)p[key];
    }
}
