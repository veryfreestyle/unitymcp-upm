using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;

namespace VeryFS.UnityMCP.Editor.Infrastructure
{
    /// <summary>
    /// Mints and persists tokens:
    ///   - Client token: stored in UserSettings/VeryFS.UnityMCP/client-token (survives Editor restart,
    ///     so .mcp.json stays valid across sessions without re-running `claude mcp add`).
    ///   - Unity token: stored in SessionState (rotates on every full Editor restart for defense-in-depth;
    ///     it is only used on the internal WS /unity path, never exposed to AI clients).
    /// </summary>
    public static class TokenStore
    {
        internal const string DefaultUnityTokenKey = "VeryFS.UnityMCP.UnityToken";
        private const int TokenBytes = 32;

        internal static Func<string> ClientTokenFilePathOverride;

        /// <summary>
        /// Redirects the SessionState key holding the Unity token, so tests can mint
        /// and Clear tokens without touching the live Editor's own.
        ///
        /// Without this seam, a test calling Clear() erases the running Editor's Unity
        /// token while the server it spawned keeps the token it was given at launch.
        /// The next domain reload then mints a different one, and every reconnect is
        /// rejected 401 for the rest of the Editor session — the server's token is
        /// fixed at spawn, so nothing short of restarting it recovers.
        /// </summary>
        internal static Func<string> UnityTokenKeyOverride;

        private static string UnityTokenKey =>
            UnityTokenKeyOverride?.Invoke() ?? DefaultUnityTokenKey;

        private static string ClientTokenFilePath =>
            ClientTokenFilePathOverride?.Invoke()
            ?? Path.Combine(
                Directory.GetParent(UnityEngine.Application.dataPath).FullName,
                "UserSettings",
                "VeryFS.UnityMCP",
                "client-token");

        /// <summary>Returns the cached token pair, minting it on first use.</summary>
        public static ServerTokens GetOrCreate()
        {
            var client = LoadOrMintClientToken();
            var unity = LoadOrMintUnityToken();
            return new ServerTokens(client, unity);
        }

        /// <summary>
        /// Erases the cached tokens. The next GetOrCreate mints fresh ones.
        /// In tests this also removes the client-token file so the test dir stays clean.
        /// </summary>
        public static void Clear()
        {
            SessionState.EraseString(UnityTokenKey);
            var path = ClientTokenFilePath;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static string LoadOrMintClientToken()
        {
            var path = ClientTokenFilePath;
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (!string.IsNullOrEmpty(existing))
                {
                    return existing;
                }
            }

            var token = GenerateToken();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, token);
            return token;
        }

        private static string LoadOrMintUnityToken()
        {
            var cached = SessionState.GetString(UnityTokenKey, string.Empty);
            if (!string.IsNullOrEmpty(cached))
            {
                return cached;
            }

            var token = GenerateToken();
            SessionState.SetString(UnityTokenKey, token);
            return token;
        }

        private static string GenerateToken()
        {
            var bytes = new byte[TokenBytes];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            return Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }
    }
}
