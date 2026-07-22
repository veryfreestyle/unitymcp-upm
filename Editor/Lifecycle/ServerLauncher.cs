using System;
using System.Diagnostics;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Infrastructure;
using Debug = UnityEngine.Debug;

namespace VeryFS.UnityMCP.Editor.Lifecycle
{
    /// <summary>
    /// Decision returned by EnsureServer: whether the Editor should open the
    /// /unity WebSocket, plus a human-readable reason when it should not.
    /// </summary>
    public readonly struct ServerLaunchResult
    {
        public ServerLaunchResult(bool shouldConnect, string reason)
        {
            ShouldConnect = shouldConnect;
            Reason = reason;
        }

        public bool ShouldConnect { get; }
        public string Reason { get; }

        public static ServerLaunchResult Connect()
        {
            return new ServerLaunchResult(true, null);
        }

        public static ServerLaunchResult Refuse(string reason)
        {
            return new ServerLaunchResult(false, reason);
        }
    }

    /// <summary>
    /// Orchestrates auto-launch: resolve the binary, probe /health, then reuse a
    /// same-session server / refuse a foreign-session server / spawn a new one,
    /// and write UnityMCP.json when we own the server. All side effects are behind
    /// injected seams (prober, spawner, binary resolver) so the three branches are
    /// unit-testable without real processes or sockets.
    /// </summary>
    public sealed class ServerLauncher
    {
        private readonly IHealthProber prober;
        private readonly IServerProcessSpawner spawner;
        private readonly Func<string> binaryResolver;

        public ServerLauncher(
            IHealthProber prober,
            IServerProcessSpawner spawner,
            Func<string> binaryResolver)
        {
            this.prober = prober ?? throw new ArgumentNullException(nameof(prober));
            this.spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            this.binaryResolver = binaryResolver ?? throw new ArgumentNullException(nameof(binaryResolver));
        }

        public ServerLaunchResult EnsureServer(
            string projectRoot,
            string editorSessionId,
            int editorPid,
            int port,
            ServerTokens tokens)
        {
            var health = prober.Probe(port, tokens.ClientToken);
            if (health.Alive)
            {
                if (health.EditorSessionId == editorSessionId || string.IsNullOrEmpty(health.EditorSessionId))
                {
                    // Two cases that both mean "this server is ours to use":
                    //   1. Same session ID: survived a Domain Reload, just reconnect.
                    //   2. Empty session ID: server is up but has no active Unity
                    //      connection (e.g. previous Editor exited cleanly or a
                    //      play-mode domain reload disconnected C# before [InitializeOnLoad]
                    //      ran again). Safe to take over.
                    WriteDiscovery(projectRoot, editorSessionId, editorPid, port, 0, tokens);
                    return ServerLaunchResult.Connect();
                }

                var reason = "Unity MCP: port " + port + " is owned by another Editor session (" +
                    health.EditorSessionId + "); not taking over.";
                Debug.LogWarning(reason);
                return ServerLaunchResult.Refuse(reason);
            }

            var binary = binaryResolver();
            if (string.IsNullOrEmpty(binary))
            {
                var reason = "Unity MCP: no server binary found (set " + ServerBinaryResolver.EnvVarName +
                    " or place one under Server~/<rid>/). Auto-launch skipped; the server can still be run manually.";
                Debug.LogError(reason);
                return ServerLaunchResult.Refuse(reason);
            }

            var serverPid = spawner.Spawn(binary, projectRoot, editorPid, port, tokens);
            Debug.Log("Unity MCP: server spawned (pid " + serverPid + ") on port " + port + ".");
            WriteDiscovery(projectRoot, editorSessionId, editorPid, port, serverPid, tokens);
            return ServerLaunchResult.Connect();
        }

        private static void WriteDiscovery(
            string projectRoot, string editorSessionId, int editorPid, int port, int serverPid, ServerTokens tokens)
        {
            DiscoveryFileWriter.Write(projectRoot, new DiscoveryDocument
            {
                ProjectPath = projectRoot,
                EditorPid = editorPid,
                EditorSessionId = editorSessionId,
                ServerPid = serverPid,
                Port = port,
                ServerUrl = "http://127.0.0.1:" + port + "/mcp",
                ClientToken = tokens.ClientToken
            });
        }

        /// <summary>Wires the production prober + spawner + binary resolver.</summary>
        public static ServerLauncher CreateDefault()
        {
            return new ServerLauncher(
                new HttpHealthProber(),
                new ProcessServerSpawner(),
                () => ServerBinaryResolver.CreateDefault().Resolve());
        }

        // --- production seams -------------------------------------------------

        private sealed class HttpHealthProber : IHealthProber
        {
            public HealthProbeResult Probe(int port, string clientToken)
            {
                try
                {
                    var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(
                        "http://127.0.0.1:" + port + "/health");
                    request.Method = "GET";
                    request.Timeout = 1000;
                    request.Headers.Add("Authorization", "Bearer " + clientToken);
                    using (var response = (System.Net.HttpWebResponse)request.GetResponse())
                    using (var reader = new System.IO.StreamReader(response.GetResponseStream()))
                    {
                        var body = reader.ReadToEnd();
                        var json = LitJson.JsonMapper.ToObject(body);
                        var sessionId = json.ContainsKey("editorSessionId")
                            ? (string)json["editorSessionId"]
                            : null;
                        return new HealthProbeResult(true, sessionId);
                    }
                }
                catch (Exception)
                {
                    // Connection refused / timeout / 401 => treat as "no server we
                    // can use". A 401 means a foreign server with a different token;
                    // we still must not take it over, and spawning our own on the
                    // same port would fail — so Down() keeps us from clobbering it.
                    return HealthProbeResult.Down();
                }
            }
        }

        private sealed class ProcessServerSpawner : IServerProcessSpawner
        {
            public int Spawn(string binaryPath, string projectRoot, int editorPid, int port, ServerTokens tokens)
            {
                var psi = new ProcessStartInfo(binaryPath)
                {
                    Arguments = "--project \"" + projectRoot + "\" --editor-pid " + editorPid + " --port " + port,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.EnvironmentVariables["UNITY_MCP_CLIENT_TOKEN"] = tokens.ClientToken;
                psi.EnvironmentVariables["UNITY_MCP_UNITY_TOKEN"] = tokens.UnityToken;
                var proc = Process.Start(psi);
                return proc != null ? proc.Id : 0;
            }
        }
    }
}
