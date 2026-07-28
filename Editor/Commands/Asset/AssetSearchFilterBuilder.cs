using System;
using System.Collections.Generic;
using System.Text;

namespace VeryFS.UnityMCP.Editor.Commands.Asset
{
    // search 的结构化入参。
    public sealed class AssetSearchQuery
    {
        public string NameContains { get; set; }
        public string TypeName { get; set; }
        public IReadOnlyList<string> Labels { get; set; }
        public IReadOnlyList<string> Folders { get; set; }
        public bool SearchInPackages { get; set; }
        public int? MaxResults { get; set; }
    }

    // 组装结果: Unity filter 字符串 + 搜索根 (null = 全库) + 夹紧后的上限。
    public sealed class AssetSearchPlan
    {
        public string Filter { get; set; }
        public string[] SearchInFolders { get; set; }
        public int MaxResults { get; set; }
    }

    // 纯函数: 结构化条件 -> filter + 搜索根。不碰 AssetDatabase, 便于单测。
    public static class AssetSearchFilterBuilder
    {
        public const int DefaultMaxResults = 50;
        public const int MinMaxResults = 1;
        public const int MaxMaxResults = 500;

        public static bool TryBuild(AssetSearchQuery query, out AssetSearchPlan plan, out string error)
        {
            plan = null;
            error = null;

            if (query == null)
            {
                error = "query required";
                return false;
            }

            bool hasName = !string.IsNullOrWhiteSpace(query.NameContains);
            bool hasType = !string.IsNullOrWhiteSpace(query.TypeName);
            bool hasLabels = HasAnyLabel(query.Labels);
            if (!hasName && !hasType && !hasLabels)
            {
                error = "at least one of nameContains, typeName, labels is required; " +
                    "folders alone is not a condition";
                return false;
            }

            string[] roots = null;
            if (query.Folders != null && query.Folders.Count > 0)
            {
                var list = new List<string>(query.Folders.Count);
                foreach (string folder in query.Folders)
                {
                    if (!IsUnderProjectRoot(folder))
                    {
                        error = "folders entries must start with 'Assets' or 'Packages': " +
                            (folder ?? "<null>");
                        return false;
                    }
                    list.Add(folder.TrimEnd('/'));
                }
                roots = list.ToArray();
            }
            else if (!query.SearchInPackages)
            {
                roots = new[] { "Assets" };
            }

            var filter = new StringBuilder();
            if (hasName)
            {
                filter.Append(query.NameContains.Trim());
            }
            if (hasType)
            {
                AppendSeparator(filter);
                filter.Append("t:").Append(query.TypeName.Trim());
            }
            if (hasLabels)
            {
                foreach (string label in query.Labels)
                {
                    if (string.IsNullOrWhiteSpace(label)) { continue; }
                    AppendSeparator(filter);
                    filter.Append("l:").Append(label.Trim());
                }
            }

            plan = new AssetSearchPlan
            {
                Filter = filter.ToString(),
                SearchInFolders = roots,
                MaxResults = Clamp(query.MaxResults ?? DefaultMaxResults)
            };
            return true;
        }

        private static bool HasAnyLabel(IReadOnlyList<string> labels)
        {
            if (labels == null) { return false; }
            foreach (string label in labels)
            {
                if (!string.IsNullOrWhiteSpace(label)) { return true; }
            }
            return false;
        }

        private static void AppendSeparator(StringBuilder sb)
        {
            if (sb.Length > 0) { sb.Append(' '); }
        }

        private static int Clamp(int value)
            => value < MinMaxResults ? MinMaxResults : (value > MaxMaxResults ? MaxMaxResults : value);

        // 只认 "Assets" / "Packages" 本身或它们的子目录; "AssetsBackup" 这类前缀撞名要拒。
        private static bool IsUnderProjectRoot(string folder)
        {
            if (string.IsNullOrEmpty(folder)) { return false; }
            return folder == "Assets"
                || folder == "Packages"
                || folder.StartsWith("Assets/", StringComparison.Ordinal)
                || folder.StartsWith("Packages/", StringComparison.Ordinal);
        }
    }
}
