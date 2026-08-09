using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VeryFS.UnityMCP.Editor.Infrastructure
{
    /// <summary>
    /// Shared project-root hashing for EditorPrefs keys that must stay isolated
    /// per project. Normalization (full path, trim both separator chars,
    /// empty/null collapses to string.Empty) and hex encoding (lowercase "x2"
    /// per byte) must stay byte-for-byte stable: callers such as
    /// ScreenshotPreferences already have real users' EditorPrefs keyed on this
    /// exact output, and changing either step would silently reset their saved
    /// preferences.
    /// </summary>
    internal static class ProjectRootHash
    {
        internal static string Compute(string projectRoot)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(Normalize(projectRoot)));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes)
                {
                    builder.Append(b.ToString("x2"));
                }
                return builder.ToString();
            }
        }

        private static string Normalize(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot))
            {
                return string.Empty;
            }

            return Path.GetFullPath(projectRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
