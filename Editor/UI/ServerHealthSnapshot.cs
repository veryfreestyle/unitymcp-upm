using LitJson;

namespace VeryFS.UnityMCP.Editor.UI
{
    /// <summary>Parsed /health body for the monitor window.</summary>
    public readonly struct ServerHealthSnapshot
    {
        public ServerHealthSnapshot(
            bool reachable, bool unityConnected, int port,
            string lastHeartbeatAt, string editorSessionId, int serverPid)
        {
            Reachable = reachable;
            UnityConnected = unityConnected;
            Port = port;
            LastHeartbeatAt = lastHeartbeatAt;
            EditorSessionId = editorSessionId;
            ServerPid = serverPid;
        }

        public bool Reachable { get; }
        public bool UnityConnected { get; }
        public int Port { get; }
        public string LastHeartbeatAt { get; }
        public string EditorSessionId { get; }
        public int ServerPid { get; }

        /// <summary>Parses a /health JSON body. Reachable is always true (the body
        /// arrived); missing optional fields default to false/0/null.</summary>
        public static ServerHealthSnapshot Parse(string json)
        {
            var data = JsonMapper.ToObject(json);
            bool unityConnected = data.ContainsKey("unityConnected") &&
                data["unityConnected"].IsBoolean && (bool)data["unityConnected"];
            int port = data.ContainsKey("port") && data["port"].IsInt ? (int)data["port"] : 0;
            string heartbeat = data.ContainsKey("lastHeartbeatAt") && data["lastHeartbeatAt"].IsString
                ? (string)data["lastHeartbeatAt"] : null;
            string sessionId = data.ContainsKey("editorSessionId") && data["editorSessionId"].IsString
                ? (string)data["editorSessionId"] : null;
            int serverPid = data.ContainsKey("serverPid") && data["serverPid"].IsInt
                ? (int)data["serverPid"] : 0;
            return new ServerHealthSnapshot(true, unityConnected, port, heartbeat, sessionId, serverPid);
        }
    }
}
