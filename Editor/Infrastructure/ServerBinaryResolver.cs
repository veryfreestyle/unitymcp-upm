using System;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEditor;

namespace VeryFS.UnityMCP.Editor.Infrastructure
{
    /// <summary>
    /// Resolves the absolute path of the prebuilt Go server binary in a fixed
    /// order: (1) env UNITY_MCP_SERVER_BIN or EditorPrefs UnityMCP.ServerBinPath
    /// (absolute override), (2) Server~/&lt;rid&gt;/unity-mcp-server[.exe] next to
    /// this plugin, (3) none. All external inputs are injected so precedence can
    /// be unit-tested without a real filesystem.
    /// </summary>
    public sealed class ServerBinaryResolver
    {
        public const string EnvVarName = "UNITY_MCP_SERVER_BIN";
        public const string EditorPrefsKey = "UnityMCP.ServerBinPath";

        private readonly Func<string, string> envReader;
        private readonly Func<string, string> prefsReader;
        private readonly Func<string> baseDirProvider;
        private readonly Func<string, bool> fileExists;
        private readonly string rid;

        public ServerBinaryResolver(
            Func<string, string> envReader,
            Func<string, string> prefsReader,
            Func<string> baseDirProvider,
            Func<string, bool> fileExists,
            string rid)
        {
            this.envReader = envReader ?? throw new ArgumentNullException(nameof(envReader));
            this.prefsReader = prefsReader ?? throw new ArgumentNullException(nameof(prefsReader));
            this.baseDirProvider = baseDirProvider ?? throw new ArgumentNullException(nameof(baseDirProvider));
            this.fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
            this.rid = rid ?? throw new ArgumentNullException(nameof(rid));
        }

        /// <summary>Returns the binary's absolute path, or null if none resolves.</summary>
        public string Resolve()
        {
            var overridePath = FirstNonEmpty(envReader(EnvVarName), prefsReader(EditorPrefsKey));
            if (!string.IsNullOrEmpty(overridePath) && fileExists(overridePath))
            {
                return overridePath;
            }

            var ridPath = Path.Combine(baseDirProvider(), "Server~", rid, BinaryFileName());
            if (fileExists(ridPath))
            {
                return ridPath;
            }

            return null;
        }

        /// <summary>
        /// Builds a resolver bound to real env vars, EditorPrefs, the current rid,
        /// and the Editor/ folder that holds this source file (Server~ lives next
        /// to it). The tilde folder is invisible to the AssetDatabase, so the
        /// Editor/ folder is located via this file's own compiled path rather than
        /// AssetDatabase APIs.
        /// </summary>
        public static ServerBinaryResolver CreateDefault()
        {
            return new ServerBinaryResolver(
                Environment.GetEnvironmentVariable,
                key => EditorPrefs.GetString(key, string.Empty),
                DefaultBaseDir,
                File.Exists,
                ServerRuntimeId.Current);
        }

        // Captured via CallerFilePath at this call site (inside this very file), so
        // it always resolves to this source file's real compiled location on disk —
        // unlike a hardcoded "Assets/Scripts/UnityMCP" prefix, it survives the
        // plugin being copied under any other folder name/path or shipped as a UPM
        // package.
        private static readonly string ThisFilePath = CaptureThisFilePath();

        private static string CaptureThisFilePath([CallerFilePath] string path = "") => path;

        private static string DefaultBaseDir()
        {
            // This file lives at <plugin-root>/Editor/Infrastructure/
            // ServerBinaryResolver.cs; Server~ sits directly under Editor/.
            var infrastructureDir = Path.GetDirectoryName(ThisFilePath);
            return Directory.GetParent(infrastructureDir).FullName;
        }

        private string BinaryFileName()
        {
            return rid.StartsWith("win-", StringComparison.Ordinal)
                ? "unity-mcp-server.exe"
                : "unity-mcp-server";
        }

        private static string FirstNonEmpty(string a, string b)
        {
            return !string.IsNullOrEmpty(a) ? a : b;
        }
    }
}
