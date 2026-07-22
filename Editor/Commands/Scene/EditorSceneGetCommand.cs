using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Scene
{
    public sealed class EditorSceneGetCommand : IGroupedCommand
    {
        private readonly ISceneGateway gateway;

        public EditorSceneGetCommand(ISceneGateway gateway)
        {
            this.gateway = gateway;
        }

        public string Method => RpcMethods.EditorSceneGet;
        public string Group => "editor.scene";
        public string Action => "get";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "editor-scene-get",
            RpcMethod = RpcMethods.EditorSceneGet,
            Title = "Editor / Scene / Get",
            Description = "Get the active scene state (path/name/isDirty/isLoaded) plus all project scenes " +
                "(AssetDatabase t:Scene, incl. non-build and Packages) with build-settings flags. Refused in play mode. Read-only.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"), ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object())),
            Annotations = JsonRpcSerializer.Object(("readOnlyHint", true), ("idempotentHint", true))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            if (gateway.IsPlaying)
            {
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                    ("state", "refused"), ("errorCode", "playmode_active")));
            }

            var active = gateway.GetActiveScene();
            var scenes = new JsonData();
            scenes.SetJsonType(JsonType.Array);
            foreach (var s in gateway.GetAllScenes())
            {
                scenes.Add(JsonRpcSerializer.Object(
                    ("path", s.Path), ("name", s.Name),
                    ("inBuildSettings", s.InBuildSettings), ("buildEnabled", s.BuildEnabled)));
            }

            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                ("state", "ok"),
                ("activeScene", JsonRpcSerializer.Object(
                    ("path", active.Path), ("name", active.Name),
                    ("isDirty", active.IsDirty), ("isLoaded", active.IsLoaded))),
                ("scenes", scenes)));
        }
    }
}
