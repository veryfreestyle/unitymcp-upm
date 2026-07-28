using System;
using System.Collections.Generic;
using LitJson;
using VeryFS.UnityMCP.Editor.Commands;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Asset
{
    public sealed class AssetSearchCommand : IGroupedCommand
    {
        private readonly IEditorBusyState busy;
        private readonly IAssetGateway gateway;

        public AssetSearchCommand(IEditorBusyState busy, IAssetGateway gateway)
        {
            this.busy = busy;
            this.gateway = gateway;
        }

        public string Method => RpcMethods.AssetSearch;
        public string Group => "asset";
        public string Action => "search";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "asset-search",
            RpcMethod = RpcMethods.AssetSearch,
            Title = "Asset / Search",
            Description = "Search assets by structured conditions. Results are sorted by path ascending " +
                "and capped by maxResults; narrow the conditions instead of paging. Read-only.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"),
                ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("nameContains", JsonRpcSerializer.Object(
                        ("type", "string"),
                        ("description", "Substring of the asset name; goes into the bare-word part of the Unity search filter."))),
                    ("typeName", JsonRpcSerializer.Object(
                        ("type", "string"),
                        ("description", "For search: asset type filter, mapped to 't:<typeName>' (e.g. Material, GameObject, Texture2D). " +
                            "For component-get: full component type name (e.g. UnityEngine.RectTransform)."))),
                    ("labels", JsonRpcSerializer.Object(
                        ("type", "array"),
                        ("items", JsonRpcSerializer.Object(("type", "string"))),
                        ("description", "Each entry maps to 'l:<label>'."))),
                    ("folders", JsonRpcSerializer.Object(
                        ("type", "array"),
                        ("items", JsonRpcSerializer.Object(("type", "string"))),
                        ("description", "Search roots; each must start with 'Assets' or 'Packages'. Overrides searchInPackages."))),
                    ("searchInPackages", JsonRpcSerializer.Object(
                        ("type", "boolean"),
                        ("description", "Search the whole library including Packages. Ignored when folders is set. Default false (Assets only)."))),
                    ("maxResults", JsonRpcSerializer.Object(
                        ("type", "integer"), ("minimum", 1), ("maximum", 500),
                        ("description", "Result cap. Default 50, clamped to [1, 500]. There is no paging.")))))),
            Annotations = JsonRpcSerializer.Object(("readOnlyHint", true), ("idempotentHint", true))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            if (busy.IsCompiling || busy.IsUpdating)
            {
                return AssetResponses.Busy(request.Id);
            }

            var query = new AssetSearchQuery
            {
                NameContains = AssetParams.ReadString(request.Params, "nameContains"),
                TypeName = AssetParams.ReadString(request.Params, "typeName"),
                Labels = AssetParams.ReadStringArray(request.Params, "labels"),
                Folders = AssetParams.ReadStringArray(request.Params, "folders"),
                SearchInPackages = AssetParams.ReadBool(request.Params, "searchInPackages"),
                MaxResults = AssetParams.ReadInt(request.Params, "maxResults")
            };

            if (!AssetSearchFilterBuilder.TryBuild(query, out AssetSearchPlan plan, out string error))
            {
                return AssetResponses.InvalidParams(request.Id, error);
            }

            if (plan.SearchInFolders != null)
            {
                foreach (string folder in plan.SearchInFolders)
                {
                    if (!gateway.IsValidFolder(folder))
                    {
                        return AssetResponses.InvalidParams(request.Id, "folder does not exist: " + folder);
                    }
                }
            }

            // FindAssets 不保证顺序; 按 path 序号排序保证同一指令在不同时间/机器上结果一致。
            var hits = new List<KeyValuePair<string, string>>();
            IReadOnlyList<string> guids = gateway.FindAssets(plan.Filter, plan.SearchInFolders);
            if (guids != null)
            {
                foreach (string guid in guids)
                {
                    string path = gateway.GuidToPath(guid);
                    if (string.IsNullOrEmpty(path)) { continue; }
                    hits.Add(new KeyValuePair<string, string>(path, guid));
                }
            }
            hits.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            int returned = Math.Min(hits.Count, plan.MaxResults);
            var items = new JsonData();
            items.SetJsonType(JsonType.Array);
            for (int i = 0; i < returned; i++)
            {
                items.Add(JsonRpcSerializer.Object(
                    ("path", hits[i].Key),
                    ("guid", hits[i].Value),
                    ("type", gateway.GetMainAssetTypeName(hits[i].Key))));
            }

            var result = JsonRpcSerializer.Object(("state", "ok"));
            result["items"] = items;
            result["returnedCount"] = returned;
            result["totalMatched"] = hits.Count;
            result["truncated"] = hits.Count > returned;
            return JsonRpcResponse.FromSuccess(request.Id, result);
        }
    }
}
