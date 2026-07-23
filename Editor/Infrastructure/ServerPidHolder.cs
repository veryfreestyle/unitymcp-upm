using UnityEditor;

namespace VeryFS.UnityMCP.Editor.Infrastructure
{
    /// <summary>
    /// Global, window-independent store of the current MCP server process pid,
    /// so the monitor window can kill a hung server even if the window was never
    /// open during the server's healthy period. Backed by SessionState so it
    /// survives Domain Reload (a full Editor restart clears it, but that also
    /// retires the old server via editor-pid monitoring -> fresh spawn -> repopulate).
    /// Populated by ServerLauncher.EnsureServer via UnityMcpPlugin.
    /// </summary>
    public static class ServerPidHolder
    {
        private const string Key = "VeryFS.UnityMCP.ServerPid";

        public static int Get() => SessionState.GetInt(Key, 0);

        public static void Set(int pid) => SessionState.SetInt(Key, pid);

        public static void Clear() => SessionState.EraseInt(Key);
    }
}
