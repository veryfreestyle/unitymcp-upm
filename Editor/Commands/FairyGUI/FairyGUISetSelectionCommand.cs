using FairyGUI;
using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public sealed class FairyGUISetSelectionCommand : IGroupedCommand
    {
        private readonly IPanelSource source;

        public FairyGUISetSelectionCommand(IPanelSource source) { this.source = source; }

        public string Method => RpcMethods.FairyGuiSetSelection;
        public string Group => "fgui.state";
        public string Action => "set-selection";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-set-selection",
            RpcMethod = RpcMethods.FairyGuiSetSelection,
            Title = "FairyGUI / Set Selection",
            Description = "Set GList/GComboBox selection. mode: set (default) | add | remove | clear | all | none. " +
                "GComboBox supports set only and fires onChanged when fireEvents (default true). Validates index. " +
                "Rejects other types with unsupported. Play mode only.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"), ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("panelInstanceId", JsonRpcSerializer.Object(("type", "integer"), ("description", FairyGUINodeLocator.PanelInstanceIdHelp))),
                    ("path", JsonRpcSerializer.Object(("type", "string"), ("description", FairyGUINodeLocator.PathSyntaxHelp))),
                    ("mode", JsonRpcSerializer.Object(("type", "string"))),
                    ("index", JsonRpcSerializer.Object(("type", "integer"))),
                    ("fireEvents", JsonRpcSerializer.Object(("type", "boolean")))))),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", false))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            int? panelInstanceId = ReadIntNullable(request.Params, "panelInstanceId");
            string path = ReadString(request.Params, "path");
            string mode = ReadString(request.Params, "mode") ?? "set";
            int? index = ReadIntNullable(request.Params, "index");
            bool fireEvents = request.Params == null || !request.Params.ContainsKey("fireEvents") || ReadBool(request.Params, "fireEvents");

            var located = FairyGUINodeLocator.Locate(source, panelInstanceId, path);
            if (located.State != null)
                return JsonRpcResponse.FromSuccess(request.Id, FairyGUINodeLocator.FailurePayload(located));

            var obj = located.Node.Unwrap();

            if (obj is GList list)
            {
                switch (mode)
                {
                    case "clear":
                        list.ClearSelection();
                        break;
                    case "all":
                        list.SelectAll();
                        break;
                    case "none":
                        list.SelectNone();
                        break;
                    case "add":
                    case "remove":
                    case "set":
                        if (!index.HasValue)
                            return Err(request.Id, "missing_index");
                        if (index.Value < 0 || index.Value >= list.numItems)
                            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                                ("state", "invalid_index"), ("index", index.Value), ("numItems", list.numItems)));
                        if (mode == "add") list.AddSelection(index.Value, false);
                        else if (mode == "remove") list.RemoveSelection(index.Value);
                        else list.selectedIndex = index.Value;
                        break;
                    default:
                        return Err(request.Id, "invalid_mode");
                }
                var sel = list.GetSelection();
                var arr = new JsonData();
                arr.SetJsonType(JsonType.Array);
                foreach (var i in sel) arr.Add(i);
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                    ("state", "ok"), ("selectedIndex", list.selectedIndex), ("selection", arr)));
            }

            if (obj is GComboBox combo)
            {
                if (mode != "set")
                    return Err(request.Id, "combo_set_only");
                if (!index.HasValue)
                    return Err(request.Id, "missing_index");
                if (index.Value < 0 || index.Value >= combo.items.Length)
                    return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                        ("state", "invalid_index"), ("index", index.Value), ("itemCount", combo.items.Length)));
                combo.selectedIndex = index.Value;
                if (fireEvents) combo.onChanged.Call();
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                    ("state", "ok"), ("selectedIndex", combo.selectedIndex), ("value", combo.value ?? string.Empty)));
            }

            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                ("state", "unsupported"), ("type", obj != null ? obj.GetType().Name : located.Node.TypeName)));
        }

        private static JsonRpcResponse Err(string id, string errorCode)
            => JsonRpcResponse.FromSuccess(id, JsonRpcSerializer.Object(("state", "error"), ("errorCode", errorCode)));

        private static int? ReadIntNullable(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsInt ? (int?)(int)p[key] : null;

        private static string ReadString(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsString ? (string)p[key] : null;

        private static bool ReadBool(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsBoolean && (bool)p[key];
    }
}
