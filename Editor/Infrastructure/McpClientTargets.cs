using System;
using System.Collections.Generic;

namespace VeryFS.UnityMCP.Editor.Infrastructure
{
    [Flags]
    public enum McpClientTarget
    {
        None = 0,
        Claude = 1,
        Codex = 2,
        OpenCode = 4,
        All = Claude | Codex | OpenCode
    }

    internal static class McpClientTargets
    {
        public const string Claude = "claude";
        public const string Codex = "codex";
        public const string OpenCode = "opencode";

        public const McpClientTarget DefaultTargets = McpClientTarget.Claude | McpClientTarget.Codex;
        public static readonly string[] AllClientNames = { Claude, Codex, OpenCode };
        public static readonly string[] DefaultClientNames = { Claude, Codex };

        public static bool IsKnownName(string name)
        {
            return string.Equals(name, Claude, StringComparison.Ordinal) ||
                   string.Equals(name, Codex, StringComparison.Ordinal) ||
                   string.Equals(name, OpenCode, StringComparison.Ordinal);
        }

        public static McpClientTarget FromNames(IReadOnlyList<string> names)
        {
            if (names == null || names.Count == 0)
            {
                return McpClientTarget.None;
            }

            McpClientTarget targets = McpClientTarget.None;
            foreach (string name in names)
            {
                if (string.Equals(name, Claude, StringComparison.Ordinal))
                {
                    targets |= McpClientTarget.Claude;
                }
                else if (string.Equals(name, Codex, StringComparison.Ordinal))
                {
                    targets |= McpClientTarget.Codex;
                }
                else if (string.Equals(name, OpenCode, StringComparison.Ordinal))
                {
                    targets |= McpClientTarget.OpenCode;
                }
            }

            return targets;
        }

        public static IReadOnlyList<string> ToNames(McpClientTarget targets)
        {
            var result = new List<string>();
            if ((targets & McpClientTarget.Claude) != 0)
            {
                result.Add(Claude);
            }
            if ((targets & McpClientTarget.Codex) != 0)
            {
                result.Add(Codex);
            }
            if ((targets & McpClientTarget.OpenCode) != 0)
            {
                result.Add(OpenCode);
            }
            return result;
        }

        public static bool HasAgentsSkillTarget(IReadOnlyList<string> clients)
        {
            McpClientTarget targets = FromNames(clients);
            return (targets & (McpClientTarget.Claude | McpClientTarget.Codex)) != 0;
        }
    }
}
