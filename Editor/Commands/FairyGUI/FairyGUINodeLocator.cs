using System.Collections.Generic;
using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    // 写命令共用: 选根 (GRoot 或 panel) + path 逐段下钻。State 为 null 表示成功。
    // Path segment syntax:
    //   "name"   — match by Name
    //   "[N]"    — match by zero-based child index (handles unnamed/anonymous nodes)
    //   "#id"    — match by gameObjectInstanceId (runtime value, use fgui.query to obtain)
    public static class FairyGUINodeLocator
    {
        // 失败时最多回报多少个同层子节点; 超出只给计数, 避免长列表撑爆响应。
        public const int MaxReportedChildren = 50;

        // path 段语法说明, 同时进 InputSchema 与失败响应, 让调用方无需读源码。
        public const string PathSyntaxHelp =
            "Path segments are separated by '/'. Segment forms: \"name\" matches by node name; " +
            "\"[N]\" matches the zero-based child index (use for unnamed/anonymous nodes); " +
            "\"#id\" matches gameObjectInstanceId. Path is relative to GRoot, " +
            "or to the panel root when panelInstanceId is given. Get names/indexes/ids from fgui-get-tree.";

        // panelInstanceId 取值范围说明; 代码建 UI (GRoot.inst.AddChild) 场景下必须省略。
        public const string PanelInstanceIdHelp =
            "GameObject instanceId of a UIPanel/UIPainter, from fgui-list-panels. Omit it for UI created in code " +
            "(UIPackage.CreateObject + GRoot.inst.AddChild): such UI has no UIPanel component, fgui-list-panels " +
            "returns an empty list, and path then resolves from GRoot.";

        public readonly struct LocateResult
        {
            public LocateResult(string state, IUINode node)
                : this(state, node, null, null, -1, null) { }

            public LocateResult(string state, IUINode node, string reason,
                string failedSegment, int failedAt, IUINode failedParent)
            {
                State = state;
                Node = node;
                Reason = reason;
                FailedSegment = failedSegment;
                FailedAt = failedAt;
                FailedParent = failedParent;
            }

            public string State { get; }   // null=ok; "not_playing"|"not_found"
            public IUINode Node { get; }

            // null=ok; 否则 "not_playing"|"panel_not_found"|"groot_not_instantiated"|"segment_not_found"
            public string Reason { get; }
            public string FailedSegment { get; }   // 只在 segment_not_found 时非 null
            public int FailedAt { get; }           // 失败段的序号 (跳过空段); 无段失败时 -1
            public IUINode FailedParent { get; }   // 子节点里没匹配上的那个父节点
        }

        public static LocateResult Locate(IPanelSource source, int? panelInstanceId, string path)
            => Locate(source, panelInstanceId, path, null);

        // chain 非 null 时按 root..target 顺序填入途经节点 (失败时保留已走到的前缀)。
        public static LocateResult Locate(IPanelSource source, int? panelInstanceId, string path,
            List<IUINode> chain)
        {
            if (!source.IsPlaying)
            {
                return new LocateResult("not_playing", null, "not_playing", null, -1, null);
            }
            IUINode root;
            if (panelInstanceId.HasValue)
            {
                root = source.GetPanelRoot(panelInstanceId.Value);
                if (root == null)
                {
                    return new LocateResult("not_found", null, "panel_not_found", null, -1, null);
                }
            }
            else
            {
                root = source.GetGRoot();
                if (root == null)
                {
                    return new LocateResult("not_found", null, "groot_not_instantiated", null, -1, null);
                }
            }
            chain?.Add(root);
            var current = root;
            if (!string.IsNullOrEmpty(path))
            {
                int index = 0;
                foreach (var segment in path.Split('/'))
                {
                    if (segment.Length == 0)
                    {
                        continue;
                    }
                    var next = MatchSegment(current, segment);
                    if (next == null)
                    {
                        return new LocateResult("not_found", null, "segment_not_found", segment, index, current);
                    }
                    chain?.Add(next);
                    current = next;
                    index++;
                }
            }
            return new LocateResult(null, current);
        }

        // 失败响应体: state + reason + (段失败时) 失败段与同层可用子节点, 便于调用方直接改路径。
        public static JsonData FailurePayload(LocateResult result)
        {
            var payload = JsonRpcSerializer.Object(
                ("state", result.State), ("reason", result.Reason ?? result.State),
                ("pathSyntax", PathSyntaxHelp));
            if (result.Reason != "segment_not_found")
            {
                return payload;
            }
            payload["failedSegment"] = result.FailedSegment;
            payload["failedAt"] = result.FailedAt;
            var children = result.FailedParent?.Children;
            int total = children?.Count ?? 0;
            payload["childCount"] = total;
            var list = new JsonData();
            list.SetJsonType(JsonType.Array);
            for (int i = 0; i < total && i < MaxReportedChildren; i++)
            {
                var child = children[i];
                var entry = JsonRpcSerializer.Object(
                    ("index", i), ("name", child.Name ?? string.Empty), ("type", child.TypeName ?? string.Empty));
                if (child.GameObjectInstanceId.HasValue)
                {
                    entry["gameObjectInstanceId"] = child.GameObjectInstanceId.Value;
                }
                list.Add(entry);
            }
            payload["availableChildren"] = list;
            payload["availableChildrenTruncated"] = total > MaxReportedChildren;
            return payload;
        }

        // Match a single path segment against a node's children.
        // Supports: "name" (by Name), "[N]" (by zero-based index), "#id" (by GameObjectInstanceId).
        public static IUINode MatchSegment(IUINode parent, string segment)
        {
            if (segment.Length > 2 && segment[0] == '[' && segment[segment.Length - 1] == ']')
            {
                if (int.TryParse(segment.Substring(1, segment.Length - 2), out int idx))
                {
                    int i = 0;
                    foreach (var child in parent.Children)
                    {
                        if (i == idx) return child;
                        i++;
                    }
                }
                return null;
            }
            if (segment.Length > 1 && segment[0] == '#')
            {
                if (int.TryParse(segment.Substring(1), out int instanceId))
                {
                    foreach (var child in parent.Children)
                    {
                        if (child.GameObjectInstanceId == instanceId) return child;
                    }
                }
                return null;
            }
            foreach (var child in parent.Children)
            {
                if (child.Name == segment) return child;
            }
            return null;
        }
    }
}
