using UnityEditor;

namespace VeryFS.UnityMCP.Editor.Infrastructure
{
    /// <summary>
    /// Project-scoped default for whether screenshot results carry an inline
    /// base64 image. Keyed by a hash of the project root so several projects on
    /// one machine do not share (or clobber) each other's preference.
    /// </summary>
    internal static class ScreenshotPreferences
    {
        private const string KeyPrefix = "VeryFS.UnityMCP.ScreenshotInlineImageDefault.";

        /// <summary>
        /// Unset means true: the pre-P24 behaviour (always inline) stays the
        /// default so upgrading the plugin is not a breaking change.
        /// </summary>
        public static bool LoadInlineImageDefault(string projectRoot)
        {
            string key = Key(projectRoot);
            if (!EditorPrefs.HasKey(key))
            {
                return true;
            }

            return EditorPrefs.GetBool(key, true);
        }

        public static void SaveInlineImageDefault(string projectRoot, bool inlineImage)
        {
            EditorPrefs.SetBool(Key(projectRoot), inlineImage);
        }

        internal static void Delete(string projectRoot)
        {
            EditorPrefs.DeleteKey(Key(projectRoot));
        }

        private static string Key(string projectRoot)
        {
            return KeyPrefix + ProjectRootHash.Compute(projectRoot);
        }
    }
}
