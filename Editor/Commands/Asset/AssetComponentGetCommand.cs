using System.Text;
using LitJson;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Commands.Serialization;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Asset
{
    public sealed class AssetComponentGetCommand : IGroupedCommand
    {
        public const long DefaultBudgetBytes = 8_384_512;

        private readonly IEditorBusyState busy;
        private readonly IAssetGateway gateway;
        private readonly long budgetBytes;

        public AssetComponentGetCommand(IEditorBusyState busy, IAssetGateway gateway)
            : this(busy, gateway, DefaultBudgetBytes) { }

        public AssetComponentGetCommand(IEditorBusyState busy, IAssetGateway gateway, long budgetBytes)
        {
            this.busy = busy;
            this.gateway = gateway;
            this.budgetBytes = budgetBytes;
        }

        public string Method => RpcMethods.AssetComponentGet;
        public string Group => "asset";
        public string Action => "component-get";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "asset-component-get",
            RpcMethod = RpcMethods.AssetComponentGet,
            Title = "Asset / Component / Get",
            Description = "Read [SerializeField]/public field values of components on a node inside a prefab or " +
                "imported model asset. Locate the node with path/guid plus childPath; omit typeName and " +
                "componentIndex for all components. Read-only.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"),
                ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("path", JsonRpcSerializer.Object(("type", "string"))),
                    ("guid", JsonRpcSerializer.Object(("type", "string"))),
                    ("childPath", JsonRpcSerializer.Object(("type", "string"))),
                    ("typeName", JsonRpcSerializer.Object(("type", "string"))),
                    ("componentIndex", JsonRpcSerializer.Object(
                        ("type", "integer"), ("minimum", 0),
                        ("description", "Only the component at this index; takes priority over typeName.")))))),
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

            Component[] all = node.GetComponents<Component>();
            int? componentIndex = AssetParams.ReadInt(request.Params, "componentIndex");
            string typeName = AssetParams.ReadString(request.Params, "typeName");

            var components = new JsonData();
            components.SetJsonType(JsonType.Array);
            long used = 0;
            bool truncated = false;
            bool matched = false;

            for (int i = 0; i < all.Length; i++)
            {
                Component component = all[i];
                if (component == null)
                {
                    continue;
                }
                if (componentIndex.HasValue)
                {
                    if (i != componentIndex.Value) { continue; }
                }
                else if (!string.IsNullOrEmpty(typeName))
                {
                    if (component.GetType().FullName != typeName) { continue; }
                }

                matched = true;
                JsonData json = ComponentFieldSerializer.SerializeComponent(component, i);
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
                return AssetResponses.ComponentNotFound(request.Id);
            }

            var result = JsonRpcSerializer.Object(("state", "ok"));
            result["components"] = components;
            result["truncated"] = truncated;
            return JsonRpcResponse.FromSuccess(request.Id, result);
        }
    }
}
