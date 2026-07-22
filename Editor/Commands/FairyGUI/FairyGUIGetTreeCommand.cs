using System.Collections.Generic;
using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public sealed class FairyGUIGetTreeCommand : IGroupedCommand
    {
        private readonly IPanelSource source;

        public FairyGUIGetTreeCommand(IPanelSource source)
        {
            this.source = source;
        }

        public string Method => RpcMethods.FairyGuiGetTree;
        public string Group => "fgui.query";
        public string Action => "get-tree";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-get-tree",
            RpcMethod = RpcMethods.FairyGuiGetTree,
            Title = "FairyGUI / Get UI Tree",
            Description = "Traverse the FairyGUI GRoot tree. Reads name/type/text/visible/grayed/geometry, " +
                "optional controllers/transitions, and the associated GameObject instanceId. Read-only.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"),
                ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("path", JsonRpcSerializer.Object(("type", "string"))),
                    ("maxDepth", JsonRpcSerializer.Object(("type", "integer"), ("minimum", 0))),
                    ("includeParents", JsonRpcSerializer.Object(("type", "boolean"))),
                    ("panelInstanceId", JsonRpcSerializer.Object(("type", "integer")))))),
            Annotations = JsonRpcSerializer.Object(("readOnlyHint", true), ("idempotentHint", true))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            if (!source.IsPlaying)
            {
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(("state", "not_playing"), ("truncated", false)));
            }
            int? panelInstanceId = ReadIntNullable(request.Params, "panelInstanceId");
            var root = panelInstanceId.HasValue ? source.GetPanelRoot(panelInstanceId.Value) : source.GetGRoot();
            if (root == null)
            {
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(("state", "not_found"), ("truncated", false)));
            }

            string path = ReadString(request.Params, "path");
            bool includeParents = ReadBool(request.Params, "includeParents");
            int remainingDepth = ReadDepth(request.Params, "maxDepth");

            var chain = ResolvePath(root, path); // includes target as last element
            if (chain == null)
            {
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(("state", "not_found"), ("truncated", false)));
            }
            var target = chain[chain.Count - 1];

            var serializer = new FairyGUINodeSerializer(
                FairyGUINodeSerializer.DefaultBudgetBytes, new FairyGUIWidgetStateReader());
            var rootJson = serializer.SerializeNode(target, remainingDepth);

            var result = JsonRpcSerializer.Object(("state", "ok"));
            if (rootJson != null)
            {
                result["root"] = rootJson;
            }
            if (includeParents && chain.Count > 1)
            {
                var parents = new JsonData();
                parents.SetJsonType(JsonType.Array);
                for (int i = 0; i < chain.Count - 1; i++)
                {
                    var parentJson = serializer.SerializeNode(chain[i], 0);
                    if (parentJson != null)
                    {
                        parents.Add(parentJson);
                    }
                }
                result["parents"] = parents;
            }
            result["truncated"] = serializer.Truncated;
            return JsonRpcResponse.FromSuccess(request.Id, result);
        }

        // Returns the node chain root..target (inclusive), or null when a segment is missing.
        private static List<IUINode> ResolvePath(IUINode root, string path)
        {
            var chain = new List<IUINode> { root };
            if (string.IsNullOrEmpty(path))
            {
                return chain;
            }
            var current = root;
            foreach (var segment in path.Split('/'))
            {
                if (segment.Length == 0)
                {
                    continue;
                }
                var next = FairyGUINodeLocator.MatchSegment(current, segment);
                if (next == null)
                {
                    return null;
                }
                chain.Add(next);
                current = next;
            }
            return chain;
        }

        private static int ReadDepth(JsonData p, string key)
        {
            if (p != null && p.IsObject && p.ContainsKey(key) && p[key].IsInt)
            {
                return (int)p[key];
            }
            return int.MaxValue; // no maxDepth = full expansion (budget-guarded)
        }

        private static bool ReadBool(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsBoolean && (bool)p[key];

        private static string ReadString(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsString ? (string)p[key] : null;

        private static int? ReadIntNullable(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsInt ? (int?)(int)p[key] : null;
    }
}
