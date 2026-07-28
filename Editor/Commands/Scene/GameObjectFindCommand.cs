using LitJson;
using VeryFS.UnityMCP.Editor.Commands;
using VeryFS.UnityMCP.Editor.Commands.Serialization;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Scene
{
    public sealed class GameObjectFindCommand : IGroupedCommand
    {
        private readonly IEditorBusyState busy;
        private readonly IGameObjectLocator locator;

        public GameObjectFindCommand(IEditorBusyState busy, IGameObjectLocator locator)
        {
            this.busy = busy;
            this.locator = locator;
        }

        public string Method => RpcMethods.GameObjectFind;
        public string Group => "gameobject";
        public string Action => "find";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "gameobject-find",
            RpcMethod = RpcMethods.GameObjectFind,
            Title = "GameObject / Find",
            Description = "Find a GameObject by instanceId (preferred) or GameObject.Find path. Returns basics, " +
                "inlined Transform, optional component type list, and optional child hierarchy. Read-only.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"),
                ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("findPath", JsonRpcSerializer.Object(("type", "string"))),
                    ("instanceId", JsonRpcSerializer.Object(("type", "integer"))),
                    ("includeComponents", JsonRpcSerializer.Object(("type", "boolean"))),
                    ("includeHierarchy", JsonRpcSerializer.Object(("type", "boolean"))),
                    ("hierarchyDepth", JsonRpcSerializer.Object(("type", "integer"), ("minimum", 0)))))),
            Annotations = JsonRpcSerializer.Object(("readOnlyHint", true), ("idempotentHint", true))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            if (busy.IsCompiling || busy.IsUpdating)
            {
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(("state", "editor_busy"), ("truncated", false)));
            }

            int? instanceId = ReadInt(request.Params, "instanceId");
            string findPath = ReadString(request.Params, "findPath");
            if (!instanceId.HasValue && string.IsNullOrEmpty(findPath))
            {
                return JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                    JsonRpcErrorCodes.InvalidParams, "instanceId or findPath required",
                    JsonRpcSerializer.Object(("errorCode", "invalid_params"))));
            }

            var go = locator.Locate(instanceId, findPath);
            if (go == null)
            {
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(("state", "not_found"), ("truncated", false)));
            }

            bool includeComponents = ReadBool(request.Params, "includeComponents");
            bool includeHierarchy = ReadBool(request.Params, "includeHierarchy");
            int depth = includeHierarchy ? ReadIntOr(request.Params, "hierarchyDepth", 1) : 0;

            var serializer = new GameObjectNodeSerializer(GameObjectNodeSerializer.DefaultBudgetBytes);
            var node = serializer.SerializeNode(go, includeComponents, depth);

            var result = JsonRpcSerializer.Object(("state", "ok"));
            if (node != null)
            {
                result["gameObject"] = node;
            }
            result["truncated"] = serializer.Truncated;
            return JsonRpcResponse.FromSuccess(request.Id, result);
        }

        private static int? ReadInt(JsonData p, string key)
        {
            if (p != null && p.IsObject && p.ContainsKey(key) && p[key].IsInt)
            {
                return (int)p[key];
            }
            return null;
        }

        private static int ReadIntOr(JsonData p, string key, int fallback)
            => ReadInt(p, key) ?? fallback;

        private static bool ReadBool(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsBoolean && (bool)p[key];

        private static string ReadString(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsString ? (string)p[key] : null;
    }
}
