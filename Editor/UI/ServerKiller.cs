using System;

namespace VeryFS.UnityMCP.Editor.UI
{
    public readonly struct KillOutcome
    {
        private KillOutcome(bool killed, string message)
        {
            Killed = killed;
            Message = message;
        }

        public bool Killed { get; }
        public string Message { get; }

        public static KillOutcome Fail(string message) => new KillOutcome(false, message);
        public static KillOutcome Success(string message) => new KillOutcome(true, message);
    }

    /// <summary>
    /// Force-kills the MCP server process by pid, but only after verifying the
    /// pid actually belongs to the server (name or module path contains the
    /// expected binary name). This guards against a stale pid that the OS has
    /// since reassigned to an unrelated process.
    /// </summary>
    public sealed class ServerKiller
    {
        private readonly IProcessController controller;
        private readonly string expectedBinaryName;

        public ServerKiller(IProcessController controller, string expectedBinaryName)
        {
            this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
            this.expectedBinaryName = expectedBinaryName;
        }

        public KillOutcome Kill(int pid)
        {
            if (pid <= 0)
            {
                return KillOutcome.Fail("No server pid known; cannot kill.");
            }

            var identity = controller.Find(pid);
            if (identity == null)
            {
                return KillOutcome.Fail("Process " + pid + " not found (already gone?).");
            }

            if (!IdentityMatches(identity.Value))
            {
                return KillOutcome.Fail(
                    "Pid " + pid + " is '" + identity.Value.Name +
                    "', not the mcp server; refusing to kill.");
            }

            controller.Kill(pid);
            return KillOutcome.Success("Killed server pid " + pid + ".");
        }

        private bool IdentityMatches(ProcessIdentity identity)
        {
            return Contains(identity.Name, expectedBinaryName) ||
                Contains(identity.MainModulePath, expectedBinaryName);
        }

        private static bool Contains(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack) &&
                haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
