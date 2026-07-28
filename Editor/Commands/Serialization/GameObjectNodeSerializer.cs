using System.Text;
using LitJson;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Serialization
{
    // Serializes a GameObject subtree: instanceId/name/active/tag/layer, inlined
    // Transform, optional component type list, depth-limited children, UTF-8 budget.
    public sealed class GameObjectNodeSerializer
    {
        public const long DefaultBudgetBytes = 8_384_512;

        private readonly long budgetBytes;
        private long usedBytes;

        public GameObjectNodeSerializer(long budgetBytes)
        {
            this.budgetBytes = budgetBytes;
        }

        public bool Truncated { get; private set; }

        public JsonData SerializeNode(GameObject go, bool includeComponents, int remainingDepth)
        {
            var t = go.transform;
            var node = JsonRpcSerializer.Object(
                ("instanceId", go.GetInstanceID()),
                ("name", go.name),
                ("activeSelf", go.activeSelf),
                ("activeInHierarchy", go.activeInHierarchy),
                ("tag", go.tag),
                ("layer", go.layer));
            node["transform"] = JsonRpcSerializer.Object(
                ("localPosition", Vector3Json(t.localPosition)),
                ("localRotation", QuaternionJson(t.localRotation)),
                ("localScale", Vector3Json(t.localScale)),
                ("worldPosition", Vector3Json(t.position)));

            if (includeComponents)
            {
                var comps = new JsonData();
                comps.SetJsonType(JsonType.Array);
                foreach (var c in go.GetComponents<Component>())
                {
                    comps.Add(c == null ? "<missing>" : c.GetType().FullName);
                }
                node["components"] = comps;
            }

            usedBytes += Encoding.UTF8.GetByteCount(JsonMapper.ToJson(node));
            if (usedBytes > budgetBytes)
            {
                Truncated = true;
                return null;
            }

            if (remainingDepth > 0)
            {
                var children = new JsonData();
                children.SetJsonType(JsonType.Array);
                for (int i = 0; i < t.childCount; i++)
                {
                    var childJson = SerializeNode(t.GetChild(i).gameObject, includeComponents, remainingDepth - 1);
                    if (childJson == null)
                    {
                        Truncated = true;
                        break;
                    }
                    children.Add(childJson);
                }
                if (children.Count > 0)
                {
                    node["children"] = children;
                }
            }
            return node;
        }

        public static JsonData Vector3Json(Vector3 v)
            => JsonRpcSerializer.Object(("x", (double)v.x), ("y", (double)v.y), ("z", (double)v.z));

        public static JsonData QuaternionJson(Quaternion q)
            => JsonRpcSerializer.Object(("x", (double)q.x), ("y", (double)q.y), ("z", (double)q.z), ("w", (double)q.w));
    }
}
