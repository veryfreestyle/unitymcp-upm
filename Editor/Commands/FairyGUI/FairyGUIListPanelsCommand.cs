using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public sealed class FairyGUIListPanelsCommand : IGroupedCommand
    {
        private readonly IPanelSource source;

        public FairyGUIListPanelsCommand(IPanelSource source)
        {
            this.source = source;
        }

        public string Method => RpcMethods.FairyGuiListPanels;
        public string Group => "fgui.query";
        public string Action => "list-panels";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-list-panels",
            RpcMethod = RpcMethods.FairyGuiListPanels,
            Title = "FairyGUI / List Panels",
            Description = "List all FairyGUI UIPanel/UIPainter components in the scene (excludes GRoot). " +
                "Each entry carries the GameObject instanceId to pass to fgui-get-tree. Read-only.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"), ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object())),
            Annotations = JsonRpcSerializer.Object(("readOnlyHint", true), ("idempotentHint", true))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            var panels = new JsonData();
            panels.SetJsonType(JsonType.Array);
            if (!source.IsPlaying)
            {
                return JsonRpcResponse.FromSuccess(request.Id,
                    JsonRpcSerializer.Object(("state", "not_playing"), ("panels", panels)));
            }
            foreach (var p in source.ListPanels())
            {
                panels.Add(JsonRpcSerializer.Object(
                    ("source", p.Source),
                    ("objectName", p.ObjectName),
                    ("instanceId", p.InstanceId),
                    ("packageName", p.PackageName),
                    ("componentName", p.ComponentName),
                    ("uiCreated", p.UiCreated)));
            }
            return JsonRpcResponse.FromSuccess(request.Id,
                JsonRpcSerializer.Object(("state", "ok"), ("panels", panels)));
        }
    }
}
