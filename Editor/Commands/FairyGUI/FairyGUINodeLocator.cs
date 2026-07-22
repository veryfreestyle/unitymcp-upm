using System;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    // 写命令共用: 选根 (GRoot 或 panel) + path 逐段下钻。State 为 null 表示成功。
    // Path segment syntax:
    //   "name"   — match by Name
    //   "[N]"    — match by zero-based child index (handles unnamed/anonymous nodes)
    //   "#id"    — match by gameObjectInstanceId (runtime value, use fgui.query to obtain)
    public static class FairyGUINodeLocator
    {
        public readonly struct LocateResult
        {
            public LocateResult(string state, IUINode node) { State = state; Node = node; }
            public string State { get; }   // null=ok; "not_playing"|"not_found"
            public IUINode Node { get; }
        }

        public static LocateResult Locate(IPanelSource source, int? panelInstanceId, string path)
        {
            if (!source.IsPlaying)
            {
                return new LocateResult("not_playing", null);
            }
            var root = panelInstanceId.HasValue ? source.GetPanelRoot(panelInstanceId.Value) : source.GetGRoot();
            if (root == null)
            {
                return new LocateResult("not_found", null);
            }
            var current = root;
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var segment in path.Split('/'))
                {
                    if (segment.Length == 0)
                    {
                        continue;
                    }
                    IUINode next = null;
                    next = MatchSegment(current, segment);
                    if (next == null)
                    {
                        return new LocateResult("not_found", null);
                    }
                    current = next;
                }
            }
            return new LocateResult(null, current);
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
