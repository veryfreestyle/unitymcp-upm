using FairyGUI;
using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public sealed class FairyGUISetValueCommand : IGroupedCommand
    {
        private readonly IPanelSource source;

        public FairyGUISetValueCommand(IPanelSource source)
        {
            this.source = source;
        }

        public string Method => RpcMethods.FairyGuiSetValue;
        public string Group => "fgui.state";
        public string Action => "set-value";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-set-value",
            RpcMethod = RpcMethods.FairyGuiSetValue,
            Title = "FairyGUI / Set Value",
            Description = "Set GSlider/GProgressBar numeric value (double). GSlider fires onChanged when " +
                "fireEvents (default true); GProgressBar has no events. Rejects other types with unsupported. Play mode only.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"), ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("panelInstanceId", JsonRpcSerializer.Object(("type", "integer"), ("description", FairyGUINodeLocator.PanelInstanceIdHelp))),
                    ("path", JsonRpcSerializer.Object(("type", "string"), ("description", FairyGUINodeLocator.PathSyntaxHelp))),
                    ("value", JsonRpcSerializer.Object(("type", "number"))),
                    ("fireEvents", JsonRpcSerializer.Object(("type", "boolean"))))),
                ("required", MakeRequired("value"))),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", true))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            int? panelInstanceId = ReadIntNullable(request.Params, "panelInstanceId");
            string path = ReadString(request.Params, "path");
            double value = ReadDoubleNullable(request.Params, "value") ?? 0.0;
            bool fireEvents = request.Params == null || !request.Params.ContainsKey("fireEvents") || ReadBool(request.Params, "fireEvents");

            var located = FairyGUINodeLocator.Locate(source, panelInstanceId, path);
            if (located.State != null)
                return JsonRpcResponse.FromSuccess(request.Id, FairyGUINodeLocator.FailurePayload(located));

            var obj = located.Node.Unwrap();

            if (obj is GSlider slider)
            {
                slider.value = value;
                if (fireEvents) slider.onChanged.Call();
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                    ("state", "ok"), ("value", slider.value)));
            }

            if (obj is GProgressBar bar)
            {
                bar.value = value;
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                    ("state", "ok"), ("value", bar.value)));
            }

            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                ("state", "unsupported"),
                ("type", obj != null ? obj.GetType().Name : located.Node.TypeName)));
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

        private static bool ReadBool(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsBoolean && (bool)p[key];

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
