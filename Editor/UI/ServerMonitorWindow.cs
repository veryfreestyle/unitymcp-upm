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
        private const float McpClientNameColumnWidth = 120;
        private const float McpClientStatusColumnWidth = 84;

        private readonly ServerMonitorState state = new ServerMonitorState();
        private readonly IServerHealthClient healthClient = new HttpServerHealthClient();
        private readonly ServerKiller killer =
            new ServerKiller(new SystemProcessController(), ExpectedBinaryName);

        private string projectRoot;
        private int port;
        private string clientToken;
        private ServerMonitorIntegrationController integrationController;

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
            integrationController = new ServerMonitorIntegrationController(
                projectRoot,
                BuildDiscoveryDocument);
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

            EditorGUILayout.Space();
            DrawMcpClients();

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

        private void DrawMcpClients()
        {
            EditorGUILayout.LabelField("MCP Clients", EditorStyles.boldLabel);

            McpClientIntegrationSnapshot snapshot = integrationController.ReadStatuses();
            DrawMcpConfigHeader();
            DrawClientConfigRow(snapshot, McpClientTargets.Claude, "Claude");
            DrawClientConfigRow(snapshot, McpClientTargets.Codex, "Codex");
            DrawClientConfigRow(snapshot, McpClientTargets.OpenCode, "OpenCode");
        }

        private static void DrawMcpConfigHeader()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(McpClientNameColumnWidth);
                GUILayout.Label("Config", EditorStyles.miniBoldLabel, GUILayout.Width(McpClientStatusColumnWidth));
            }
        }

        private void DrawClientConfigRow(McpClientIntegrationSnapshot snapshot, string client, string label)
        {
            McpClientStatus status = FindStatus(snapshot, client);
            if (status == null)
            {
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                bool nextEnabled = EditorGUILayout.ToggleLeft(
                    label, status.Enabled, GUILayout.Width(McpClientNameColumnWidth));
                DrawStatusCell(
                    ConfigStatusText(status.ConfigStatus),
                    status.ConfigStatus == McpClientConfigStatus.Current,
                    status.ConfigStatus == McpClientConfigStatus.Off,
                    McpClientStatusPresentation.ConfigPathLabel(status, projectRoot));

                if (nextEnabled != status.Enabled)
                {
                    McpClientTarget nextTargets = SetTarget(snapshot.EnabledTargets, ClientTarget(client), nextEnabled);
                    McpClientActionResult result = integrationController.ApplyTargets(nextTargets);
                    lastActionMessage = result.Message;
                    Repaint();
                }
            }
        }

        private static string ConfigStatusText(McpClientConfigStatus status)
        {
            return status == McpClientConfigStatus.Current ? "OK" : status.ToString();
        }

        private static void DrawStatusCell(string text, bool isOk, bool isOff, string pathLabel)
        {
            Color previous = GUI.color;
            GUI.color = StatusColor(isOk, isOff);
            GUILayout.Label(text, GUILayout.Width(McpClientStatusColumnWidth));
            GUI.color = previous;
            if (!string.IsNullOrEmpty(pathLabel))
            {
                GUILayout.Label(pathLabel);
            }
        }

        private static Color StatusColor(bool isOk, bool isOff)
        {
            if (isOk)
            {
                return Color.green;
            }
            return isOff ? Color.gray : Color.red;
        }

        private DiscoveryDocument BuildDiscoveryDocument()
        {
            return new DiscoveryDocument
            {
                ProjectPath = projectRoot,
                EditorPid = System.Diagnostics.Process.GetCurrentProcess().Id,
                EditorSessionId = EditorSession.Current.EditorSessionId,
                ServerPid = ResolvePid(),
                Port = port,
                ServerUrl = "http://127.0.0.1:" + port + "/mcp",
                ClientToken = clientToken
            };
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

        private static McpClientStatus FindStatus(McpClientIntegrationSnapshot snapshot, string client)
        {
            foreach (McpClientStatus status in snapshot.Clients)
            {
                if (status.Client == client)
                {
                    return status;
                }
            }

            return null;
        }

        private static McpClientTarget ClientTarget(string client)
        {
            if (client == McpClientTargets.Claude)
            {
                return McpClientTarget.Claude;
            }
            if (client == McpClientTargets.Codex)
            {
                return McpClientTarget.Codex;
            }
            return McpClientTarget.OpenCode;
        }

        private static McpClientTarget SetTarget(McpClientTarget targets, McpClientTarget target, bool enabled)
        {
            return enabled ? targets | target : targets & ~target;
        }
    }

    internal static class McpClientStatusPresentation
    {
        public static string ConfigPathLabel(McpClientStatus status, string projectRoot)
        {
            return status != null && status.ConfigStatus == McpClientConfigStatus.Current
                ? ProjectRelativePath(projectRoot, status.ConfigPath)
                : string.Empty;
        }

        private static string ProjectRelativePath(string projectRoot, string path)
        {
            if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            string root = Path.GetFullPath(projectRoot).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(root.Length).Replace('\\', '/');
            }
            return fullPath.Replace('\\', '/');
        }
    }
}
