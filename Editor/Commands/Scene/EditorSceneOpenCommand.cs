using System.IO;
using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Scene
{
    public sealed class EditorSceneOpenCommand : IGroupedCommand
    {
        private readonly ISceneGateway gateway;

        public EditorSceneOpenCommand(ISceneGateway gateway)
        {
            this.gateway = gateway;
        }

        public string Method => RpcMethods.EditorSceneOpen;
        public string Group => "editor.scene";
        public string Action => "open";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "editor-scene-open",
            RpcMethod = RpcMethods.EditorSceneOpen,
            Title = "Editor / Scene / Open",
            Description = "Open a scene by path in Single mode. Refuses (dirty_refused) if the active scene has " +
                "unsaved changes — save first, no force bypass. Refused in play mode.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"), ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("path", JsonRpcSerializer.Object(("type", "string"))))),
                ("required", MakeRequired("path"))),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", false))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            if (gateway.IsPlaying)
            {
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                    ("state", "refused"), ("errorCode", "playmode_active")));
            }

            string path = ReadString(request.Params, "path");
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".unity") || !File.Exists(path))
            {
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                    ("state", "not_found"), ("path", path ?? string.Empty)));
            }

            if (gateway.ActiveSceneDirty)
            {
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                    ("state", "dirty_refused"), ("dirtyScene", gateway.ActiveScenePath)));
            }

            var result = gateway.OpenSingle(path);
            if (!result.Success)
            {
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                    ("state", "error"), ("errorCode", "open_failed"), ("path", path)));
            }

            var active = gateway.GetActiveScene();
            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                ("state", "ok"),
                ("activeScene", JsonRpcSerializer.Object(
                    ("path", active.Path), ("name", active.Name),
                    ("isDirty", active.IsDirty), ("isLoaded", active.IsLoaded)))));
        }

        private static JsonData MakeRequired(params string[] names)
        {
            var arr = new JsonData();
            arr.SetJsonType(JsonType.Array);
            foreach (var n in names) { arr.Add(n); }
            return arr;
        }

        private static string ReadString(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsString ? (string)p[key] : null;
    }
}
