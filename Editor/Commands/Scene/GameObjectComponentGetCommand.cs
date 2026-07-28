using System.Text;
using LitJson;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Commands.Serialization;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Scene
{
    public sealed class GameObjectComponentGetCommand : IGroupedCommand
    {
        public const long DefaultBudgetBytes = 8_384_512;

        private readonly IEditorBusyState busy;
        private readonly IGameObjectLocator locator;
        private readonly long budgetBytes;

        public GameObjectComponentGetCommand(IEditorBusyState busy, IGameObjectLocator locator)
            : this(busy, locator, DefaultBudgetBytes) { }

        public GameObjectComponentGetCommand(IEditorBusyState busy, IGameObjectLocator locator, long budgetBytes)
        {
            this.busy = busy;
            this.locator = locator;
            this.budgetBytes = budgetBytes;
        }

        public string Method => RpcMethods.GameObjectComponentGet;
        public string Group => "gameobject";
        public string Action => "component-get";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "gameobject-component-get",
            RpcMethod = RpcMethods.GameObjectComponentGet,
            Title = "GameObject / Component / Get",
            Description = "Read [SerializeField]/public field values of components on a GameObject. " +
                "Omit typeName/componentIndex for all components, or supply one to target a single component. Read-only.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"),
                ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("findPath", JsonRpcSerializer.Object(("type", "string"))),
                    ("instanceId", JsonRpcSerializer.Object(("type", "integer"))),
                    ("typeName", JsonRpcSerializer.Object(("type", "string"))),
                    ("componentIndex", JsonRpcSerializer.Object(("type", "integer"), ("minimum", 0)))))),
            Annotations = JsonRpcSerializer.Object(("readOnlyHint", true), ("idempotentHint", true))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            if (busy.IsCompiling || busy.IsUpdating)
            {
                return JsonRpcResponse.FromSuccess(request.Id,
                    JsonRpcSerializer.Object(("state", "editor_busy"), ("truncated", false)));
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
                return JsonRpcResponse.FromSuccess(request.Id,
                    JsonRpcSerializer.Object(("state", "not_found"), ("truncated", false)));
            }

            var all = go.GetComponents<Component>();
            int? componentIndex = ReadInt(request.Params, "componentIndex");
            string typeName = ReadString(request.Params, "typeName");

            var components = new JsonData();
            components.SetJsonType(JsonType.Array);
            long used = 0;
            bool truncated = false;
            bool matched = false;

            for (int i = 0; i < all.Length; i++)
            {
                var component = all[i];
                if (component == null)
                {
                    continue;
                }
                if (componentIndex.HasValue)
                {
                    if (i != componentIndex.Value)
                    {
                        continue;
                    }
                }
                else if (!string.IsNullOrEmpty(typeName))
                {
                    if (component.GetType().FullName != typeName)
                    {
                        continue;
                    }
                }

                matched = true;
                var json = ComponentFieldSerializer.SerializeComponent(component, i);
                used += Encoding.UTF8.GetByteCount(JsonMapper.ToJson(json));
                if (used > budgetBytes)
                {
                    truncated = true;
                    break;
                }
                components.Add(json);
            }

            bool filtered = componentIndex.HasValue || !string.IsNullOrEmpty(typeName);
            if (filtered && !matched)
            {
                return JsonRpcResponse.FromSuccess(request.Id,
                    JsonRpcSerializer.Object(("state", "component_not_found"), ("truncated", false)));
            }

            var result = JsonRpcSerializer.Object(("state", "ok"));
            result["components"] = components;
            result["truncated"] = truncated;
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

        private static string ReadString(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsString ? (string)p[key] : null;
    }
}
