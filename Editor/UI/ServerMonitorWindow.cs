using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor.Infrastructure;
using VeryFS.UnityMCP.Editor.Infrastructure;

namespace VeryFS.UnityMCP.Editor.UI
{
    /// <summary>
    /// Monitors the MCP server via /health polling and offers a force-kill for a
    /// hung server (the supervisor auto-respawns afterwards). Polling runs only
    /// while the window is open, off the main thread (single in-flight) so the
    /// 1s HTTP timeout never stalls the Editor.
    /// </summary>
    public sealed class ServerMonitorWindow : EditorWindow
    {
        private const double PollIntervalSeconds = 3.0;
        private const string ExpectedBinaryName = "unity-mcp-server";

        private readonly ServerMonitorState state = new ServerMonitorState();
        private readonly IServerHealthClient healthClient = new HttpServerHealthClient();
        private readonly ServerKiller killer =
            new ServerKiller(new SystemProcessController(), ExpectedBinaryName);

        private string projectRoot;
        private int port;
        private string clientToken;

        private double nextPollAt;
        private volatile bool pollInFlight;
        private volatile bool hasPending;
        private ServerHealthSnapshot pendingSnapshot;
        private volatile bool pendingIsFailure;
        private string lastActionMessage;
        private volatile bool _disposed;

        [MenuItem("Window/UnityMCP - VeryFS")]
        public static void Open()
        {
            var window = GetWindow<ServerMonitorWindow>();
            window.titleContent = new GUIContent("UnityMCP - VeryFS");
            window.Show();
        }

        private void OnEnable()
        {
            _disposed = false;
            projectRoot = Directory.GetParent(Application.dataPath).FullName;
            port = ProjectPortCalculator.GetPort(projectRoot);
            clientToken = TokenStore.GetOrCreate().ClientToken;
            nextPollAt = 0; // poll immediately on first update
            EditorApplication.update += OnUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnUpdate;
            _disposed = true;
        }

        private void OnUpdate()
        {
            // Apply any completed background poll on the main thread.
            if (hasPending)
            {
                if (pendingIsFailure) { state.ObserveFailure(); }
                else
                {
                    state.Observe(pendingSnapshot);
                    if (pendingSnapshot.ServerPid > 0)
                    {
                        VeryFS.UnityMCP.Editor.Infrastructure.ServerPidHolder.Set(pendingSnapshot.ServerPid);
                    }
                    if (state.Status == MonitorStatus.Connected)
                    {
                        lastActionMessage = null;
                    }
                }
                hasPending = false;
                Repaint();
            }

            if (!pollInFlight && EditorApplication.timeSinceStartup >= nextPollAt)
            {
                StartPoll();
            }
        }

        private void StartPoll()
        {
            pollInFlight = true;
            nextPollAt = EditorApplication.timeSinceStartup + PollIntervalSeconds;
            int p = port;
            string token = clientToken;
            Task.Run(() =>
            {
                try
                {
                    var snapshot = healthClient.Poll(p, token);
                    pendingSnapshot = snapshot;
                    pendingIsFailure = false;
                }
                catch (Exception)
                {
                    pendingIsFailure = true;
                }
                finally
                {
                    pollInFlight = false;
                    if (!_disposed) { hasPending = true; }
                }
            });
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("MCP Server", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            DrawStatusRow();
            EditorGUILayout.LabelField("Port", port.ToString());
            EditorGUILayout.LabelField("Last heartbeat", HeartbeatText());
            EditorGUILayout.LabelField("Server PID", ServerPidText());

            EditorGUILayout.Space();
            DrawButtons();

            if (!string.IsNullOrEmpty(lastActionMessage))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(lastActionMessage, MessageType.Info);
            }
        }

        private void DrawStatusRow()
        {
            var (text, color) = StatusDisplay(state.Status);
            var prev = GUI.color;
            GUI.color = color;
            EditorGUILayout.LabelField("Status", text, EditorStyles.boldLabel);
            GUI.color = prev;
        }

        private static (string, Color) StatusDisplay(MonitorStatus status)
        {
            switch (status)
            {
                case MonitorStatus.Connected: return ("Connected", Color.green);
                case MonitorStatus.ServerUpNoUnity: return ("Server up, Unity not connected", Color.yellow);
                case MonitorStatus.Checking: return ("Checking...", Color.yellow);
                case MonitorStatus.Unresponsive: return ("Unresponsive", Color.red);
                default: return ("Unknown", Color.gray);
            }
        }

        private string HeartbeatText()
        {
            if (!state.HasSnapshot || string.IsNullOrEmpty(state.Last.LastHeartbeatAt))
            {
                return "-";
            }
            if (System.DateTimeOffset.TryParse(state.Last.LastHeartbeatAt,
                    null, System.Globalization.DateTimeStyles.RoundtripKind,
                    out var dto))
            {
                return dto.ToLocalTime().ToString("HH:mm:ss");
            }
            return state.Last.LastHeartbeatAt;
        }

        private string ServerPidText()
        {
            int pid = ResolvePid();
            return pid > 0 ? pid.ToString() : "-";
        }

        private void DrawButtons()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var btnStyle = GUILayout.Width(120);
                if (GUILayout.Button("Kill Server", btnStyle)) { OnKill(); }
                if (GUILayout.Button("Open Log", btnStyle)) { OnOpenLog(); }
            }
        }

        private void OnKill()
        {
            int pid = ResolvePid();
            var outcome = killer.Kill(pid);
            lastActionMessage = outcome.Message;
            nextPollAt = 0; // re-poll soon to reflect the kill
        }

        private void OnOpenLog()
        {
            var logPath = Path.Combine(projectRoot, "Temp", "UnityMCP", "server.log");
            if (File.Exists(logPath))
            {
                EditorUtility.RevealInFinder(logPath);
            }
            else
            {
                lastActionMessage = "No server log yet at " + logPath;
            }
        }

        private int ResolvePid()
        {
            return ServerPidResolver.Resolve(
                ServerPidHolder.Get,
                () => DiscoveryPidReader.ReadServerPid(projectRoot));
        }
    }
}
