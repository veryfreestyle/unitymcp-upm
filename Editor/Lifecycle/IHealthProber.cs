namespace VeryFS.UnityMCP.Editor.Lifecycle
{
    /// <summary>Outcome of a /health probe: whether it answered, and if so, which
    /// editorSessionId currently owns the server.</summary>
    public readonly struct HealthProbeResult
    {
        public HealthProbeResult(bool alive, string editorSessionId)
        {
            Alive = alive;
            EditorSessionId = editorSessionId;
        }

        public bool Alive { get; }
        public string EditorSessionId { get; }

        /// <summary>No server answered on the port.</summary>
        public static HealthProbeResult Down()
        {
            return new HealthProbeResult(false, null);
        }
    }

    /// <summary>Probes GET /health with the client token. Injected so ServerLauncher
    /// is testable without a live HTTP server.</summary>
    public interface IHealthProber
    {
        HealthProbeResult Probe(int port, string clientToken);
    }
}
