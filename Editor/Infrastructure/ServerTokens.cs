namespace VeryFS.UnityMCP.Editor.Infrastructure
{
    /// <summary>
    /// The two secrets minted by Unity: ClientToken authenticates MCP clients on
    /// /mcp and /health; UnityToken authenticates the Editor's own /unity
    /// WebSocket. Only ClientToken is ever written to UnityMCP.json.
    /// </summary>
    public readonly struct ServerTokens
    {
        public ServerTokens(string clientToken, string unityToken)
        {
            ClientToken = clientToken;
            UnityToken = unityToken;
        }

        public string ClientToken { get; }
        public string UnityToken { get; }
    }
}
