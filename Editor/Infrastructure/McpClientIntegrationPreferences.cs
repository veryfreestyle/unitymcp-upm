using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;

namespace VeryFS.UnityMCP.Editor.Infrastructure
{
    internal static class McpClientIntegrationPreferences
    {
        private const string KeyPrefix = "VeryFS.UnityMCP.McpClientTargets.";

        public static McpClientTarget Load(string projectRoot)
        {
            string key = Key(projectRoot);
            if (!EditorPrefs.HasKey(key))
            {
                return McpClientTargets.DefaultTargets;
            }

            string raw = EditorPrefs.GetString(key, string.Empty);
            if (string.IsNullOrEmpty(raw))
            {
                return McpClientTarget.None;
            }

            string[] names = raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            McpClientTarget targets = McpClientTargets.FromNames(names);
            return targets;
        }

        public static void Save(string projectRoot, McpClientTarget targets)
        {
            EditorPrefs.SetString(Key(projectRoot), string.Join(",", McpClientTargets.ToNames(targets)));
        }

        internal static void Delete(string projectRoot)
        {
            EditorPrefs.DeleteKey(Key(projectRoot));
        }

        private static string Key(string projectRoot)
        {
            return KeyPrefix + Sha256Hex(NormalizeProjectRoot(projectRoot));
        }

        private static string NormalizeProjectRoot(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot))
            {
                return string.Empty;
            }

            return Path.GetFullPath(projectRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string Sha256Hex(string value)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes)
                {
                    builder.Append(b.ToString("x2"));
                }
                return builder.ToString();
            }
        }
    }
}
