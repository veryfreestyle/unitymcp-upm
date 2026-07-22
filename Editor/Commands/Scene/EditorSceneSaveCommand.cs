using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Scene
{
    public sealed class EditorSceneSaveCommand : IGroupedCommand
    {
        private readonly ISceneGateway gateway;

        public EditorSceneSaveCommand(ISceneGateway gateway)
        {
            this.gateway = gateway;
        }

        public string Method => RpcMethods.EditorSceneSave;
        public string Group => "editor.scene";
        public string Action => "save";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "editor-scene-save",
            RpcMethod = RpcMethods.EditorSceneSave,
            Title = "Editor / Scene / Save",
            Description = "Save the active scene to its own path (no save-as). Rejects an unnamed/never-saved " +
                "scene (no_scene_path). Refused in play mode.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"), ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object())),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", true))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            if (gateway.IsPlaying)
            {
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                    ("state", "refused"), ("errorCode", "playmode_active")));
            }

            string path = gateway.ActiveScenePath;
            if (string.IsNullOrEmpty(path))
            {
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                    ("state", "error"), ("errorCode", "no_scene_path")));
            }

            var result = gateway.SaveActive();
            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                ("state", "ok"), ("path", path), ("saved", result.Success)));
        }
    }
}
