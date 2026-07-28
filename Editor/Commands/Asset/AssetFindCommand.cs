using LitJson;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Commands.Serialization;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Asset
{
    public sealed class AssetFindCommand : IGroupedCommand
    {
        private readonly IEditorBusyState busy;
        private readonly IAssetGateway gateway;
        private readonly long budgetBytes;

        public AssetFindCommand(IEditorBusyState busy, IAssetGateway gateway)
            : this(busy, gateway, GameObjectNodeSerializer.DefaultBudgetBytes) { }

        public AssetFindCommand(IEditorBusyState busy, IAssetGateway gateway, long budgetBytes)
        {
            this.busy = busy;
            this.gateway = gateway;
            this.budgetBytes = budgetBytes;
        }

        public string Method => RpcMethods.AssetFind;
        public string Group => "asset";
        public string Action => "find";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "asset-find",
            RpcMethod = RpcMethods.AssetFind,
            Title = "Asset / Find",
            Description = "Locate a node inside a prefab or imported model asset by childPath, with optional " +
                "child hierarchy and shallow component type names. Only assets whose main object is a " +
                "GameObject are supported. node.instanceId is meaningful within this response only; it is " +
                "not a locator for later calls. Read-only.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"),
                ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("path", JsonRpcSerializer.Object(("type", "string"))),
                    ("guid", JsonRpcSerializer.Object(("type", "string"))),
                    ("childPath", JsonRpcSerializer.Object(
                        ("type", "string"),
                        ("description", "Transform.Find-style path relative to the asset root, e.g. Body/Icon/Image. " +
                            "Omit for the root node. There is no instanceId locator: prefab-asset instance ids " +
                            "are not stable across calls."))),
                    ("includeComponents", JsonRpcSerializer.Object(
                        ("type", "boolean"),
                        ("description", "Attach a shallow component type-name list to each node."))),
                    ("includeHierarchy", JsonRpcSerializer.Object(
                        ("type", "boolean"),
                        ("description", "Expand child nodes."))),
                    ("hierarchyDepth", JsonRpcSerializer.Object(
                        ("type", "integer"), ("minimum", 0),
                        ("description", "Expansion depth when includeHierarchy is true. Default 1.")))))),
            Annotations = JsonRpcSerializer.Object(("readOnlyHint", true), ("idempotentHint", true))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            if (busy.IsCompiling || busy.IsUpdating)
            {
                return AssetResponses.Busy(request.Id);
            }

            if (!AssetTargetLookup.TryLocateNode(gateway, request, out GameObject node,
                    out JsonRpcResponse failure))
            {
                return failure;
            }

            bool includeComponents = AssetParams.ReadBool(request.Params, "includeComponents");
            bool includeHierarchy = AssetParams.ReadBool(request.Params, "includeHierarchy");
            int depth = includeHierarchy
                ? AssetParams.ReadInt(request.Params, "hierarchyDepth") ?? 1
                : 0;

            var serializer = new GameObjectNodeSerializer(budgetBytes);
            JsonData nodeJson = serializer.SerializeNode(node, includeComponents, depth);

            var result = JsonRpcSerializer.Object(("state", "ok"));
            if (nodeJson != null)
            {
                result["node"] = nodeJson;
            }
            result["truncated"] = serializer.Truncated;
            return JsonRpcResponse.FromSuccess(request.Id, result);
        }
    }
}
