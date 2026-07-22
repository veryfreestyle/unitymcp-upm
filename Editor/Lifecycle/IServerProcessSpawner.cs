using VeryFS.UnityMCP.Editor.Infrastructure;

namespace VeryFS.UnityMCP.Editor.Lifecycle
{
    /// <summary>Launches the Go server process with the fixed CLI args + token env.
    /// Injected so ServerLauncher branch logic is testable without spawning.</summary>
    public interface IServerProcessSpawner
    {
        /// <summary>Starts the binary and returns its OS process id.</summary>
        int Spawn(string binaryPath, string projectRoot, int editorPid, int port, ServerTokens tokens);
    }
}
