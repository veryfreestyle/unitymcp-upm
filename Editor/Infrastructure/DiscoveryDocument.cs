namespace VeryFS.UnityMCP.Editor.Infrastructure
{
    /// <summary>
    /// Plain data for the UnityMCP.json discovery file. Deliberately holds only
    /// the client token: the unity token has no field here and must never be
    /// serialized into the file.
    /// </summary>
    public sealed class DiscoveryDocument
    {
        public string ProjectPath { get; set; }
        public int EditorPid { get; set; }
        public string EditorSessionId { get; set; }
        public int ServerPid { get; set; }
        public int Port { get; set; }
        public string ServerUrl { get; set; }
        public string ClientToken { get; set; }
    }
}
