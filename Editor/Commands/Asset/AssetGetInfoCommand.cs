using System;
using System.Collections.Generic;
using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Asset
{
    public sealed class AssetGetInfoCommand : IGroupedCommand
    {
        private readonly IEditorBusyState busy;
        private readonly IAssetGateway gateway;

        public AssetGetInfoCommand(IEditorBusyState busy, IAssetGateway gateway)
        {
            this.busy = busy;
            this.gateway = gateway;
        }

        public string Method => RpcMethods.AssetGetInfo;
        public string Group => "asset";
        public string Action => "get-info";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "asset-get-info",
            RpcMethod = RpcMethods.AssetGetInfo,
            Title = "Asset / Get Info",
            Description = "Read one asset's path, guid, main object type, importer type and sub-assets, " +
                "plus a type-specialised 'details' object. A broken internal reference " +
                "(details.shaderResolved: false) is a normal successful result, not a call failure. Read-only.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"),
                ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("path", JsonRpcSerializer.Object(
                        ("type", "string"),
                        ("description", "Project-relative asset path, e.g. Assets/Art/Foo.mat."))),
                    ("guid", JsonRpcSerializer.Object(
                        ("type", "string"),
                        ("description", "Asset guid. Takes priority when both path and guid are supplied.")))))),
            Annotations = JsonRpcSerializer.Object(("readOnlyHint", true), ("idempotentHint", true))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            if (busy.IsCompiling || busy.IsUpdating)
            {
                return AssetResponses.Busy(request.Id);
            }

            string path = AssetParams.ReadString(request.Params, "path");
            string guid = AssetParams.ReadString(request.Params, "guid");
            if (!AssetTargetResolver.TryResolve(gateway, path, guid,
                    out AssetTarget target, out string errorCode, out string error))
            {
                return errorCode == "asset_not_found"
                    ? AssetResponses.AssetNotFound(request.Id, error)
                    : AssetResponses.InvalidParams(request.Id, error);
            }

            var result = JsonRpcSerializer.Object(
                ("state", "ok"),
                ("path", target.Path),
                ("guid", target.Guid),
                ("mainObjectType", gateway.GetMainAssetTypeName(target.Path)),
                ("importerType", gateway.GetImporterTypeName(target.Path)),
                ("isPackageAsset", target.Path.StartsWith("Packages/", StringComparison.Ordinal)));

            var subAssets = new JsonData();
            subAssets.SetJsonType(JsonType.Array);
            IReadOnlyList<UnityEngine.Object> representations = gateway.LoadAllRepresentations(target.Path);
            if (representations != null)
            {
                foreach (UnityEngine.Object representation in representations)
                {
                    if (representation == null) { continue; }
                    subAssets.Add(JsonRpcSerializer.Object(
                        ("name", representation.name),
                        ("type", representation.GetType().Name)));
                }
            }
            result["subAssets"] = subAssets;
            result["details"] = AssetDetailsSerializer.Serialize(target.MainObject);
            return JsonRpcResponse.FromSuccess(request.Id, result);
        }
    }
}
