using System;
using System.Collections.Generic;
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
        private const float AgentClientNameColumnWidth = 120;
        private const float AgentClientStatusColumnWidth = 84;

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
                BuildDiscoveryDocument,
                new VeryFS.UnityMCP.Editor.Commands.AgentSkill.AgentSkillFileStore(
                    new VeryFS.UnityMCP.Editor.Commands.AgentSkill.SystemAgentSkillFileSystem(),
                    new UlidLikeIdGenerator(),
                    Application.platform == RuntimePlatform.WindowsEditor
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal),
                VeryFS.UnityMCP.Editor.UnityMcpPlugin.InstallAgentSkillForMonitor);
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
            DrawAgentClients();

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

        private void DrawAgentClients()
        {
            EditorGUILayout.LabelField("Agent Clients", EditorStyles.boldLabel);

            AgentClientIntegrationSnapshot snapshot = integrationController.ReadStatuses();
            DrawAgentConfigHeader();
            DrawClientConfigRow(snapshot, McpClientTargets.Claude, "Claude");
            DrawClientConfigRow(snapshot, McpClientTargets.Codex, "Codex");
            DrawClientConfigRow(snapshot, McpClientTargets.OpenCode, "OpenCode");

            using (new EditorGUI.DisabledScope(snapshot.EnabledTargets == McpClientTarget.None))
            {
                if (GUILayout.Button("Install Agent Skill", GUILayout.Width(160)))
                {
                    OnInstallAgentSkill(snapshot);
                }
            }

            EditorGUILayout.Space(2);
            DrawSkillRow("Claude/Codex", SharedAgentsSkillStatus(snapshot));
            DrawSkillRow("OpenCode", FindStatus(snapshot, McpClientTargets.OpenCode));
        }

        private static void DrawAgentConfigHeader()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(AgentClientNameColumnWidth);
                GUILayout.Label("Config", EditorStyles.miniBoldLabel, GUILayout.Width(AgentClientStatusColumnWidth));
            }
        }

        private void DrawClientConfigRow(AgentClientIntegrationSnapshot snapshot, string client, string label)
        {
            AgentClientStatus status = FindStatus(snapshot, client);
            if (status == null)
            {
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                bool nextEnabled = EditorGUILayout.ToggleLeft(
                    label, status.Enabled, GUILayout.Width(AgentClientNameColumnWidth));
                DrawStatusCell(
                    ConfigStatusText(status.ConfigStatus),
                    status.ConfigStatus == AgentClientConfigStatus.Current,
                    status.ConfigStatus == AgentClientConfigStatus.Off,
                    AgentClientStatusPresentation.ConfigPathLabel(status, projectRoot));

                if (nextEnabled != status.Enabled)
                {
                    McpClientTarget nextTargets = SetTarget(snapshot.EnabledTargets, ClientTarget(client), nextEnabled);
                    AgentClientActionResult result = integrationController.ApplyTargets(nextTargets);
                    lastActionMessage = result.Message;
                    Repaint();
                }
            }
        }

        private void DrawSkillRow(string label, AgentClientStatus status)
        {
            AgentClientSkillStatus skillStatus = status == null ? AgentClientSkillStatus.Off : status.SkillStatus;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(label, GUILayout.Width(AgentClientNameColumnWidth));
                DrawStatusCell(
                    skillStatus.ToString(),
                    skillStatus == AgentClientSkillStatus.Installed,
                    skillStatus == AgentClientSkillStatus.Off,
                    AgentClientStatusPresentation.SkillPathLabel(status, projectRoot));
            }
        }

        private static string ConfigStatusText(AgentClientConfigStatus status)
        {
            return status == AgentClientConfigStatus.Current ? "OK" : status.ToString();
        }

        private static void DrawStatusCell(string text, bool isOk, bool isOff, string pathLabel)
        {
            Color previous = GUI.color;
            GUI.color = StatusColor(isOk, isOff);
            GUILayout.Label(text, GUILayout.Width(AgentClientStatusColumnWidth));
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

        private static AgentClientStatus SharedAgentsSkillStatus(AgentClientIntegrationSnapshot snapshot)
        {
            AgentClientStatus claude = FindStatus(snapshot, McpClientTargets.Claude);
            AgentClientStatus codex = FindStatus(snapshot, McpClientTargets.Codex);
            if (claude != null && claude.Enabled)
            {
                return claude;
            }
            if (codex != null && codex.Enabled)
            {
                return codex;
            }
            return null;
        }

        private void OnInstallAgentSkill(AgentClientIntegrationSnapshot snapshot)
        {
            IReadOnlyList<string> customPaths = snapshot.EnabledCustomSkillPaths();
            bool allowOverwrite = true;
            if (customPaths.Count > 0)
            {
                allowOverwrite = EditorUtility.DisplayDialog(
                    "Overwrite Custom Skill?",
                    "The enabled clients include custom skill files:\n\n" +
                    string.Join("\n", customPaths) +
                    "\n\nOverwrite them with the generated UnityMCP skill?",
                    "Overwrite",
                    "Cancel");
            }

            AgentClientActionResult result = integrationController.InstallEnabledSkills(allowOverwrite);
            lastActionMessage = result.Message;
            Repaint();
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

        private static AgentClientStatus FindStatus(AgentClientIntegrationSnapshot snapshot, string client)
        {
            foreach (AgentClientStatus status in snapshot.Clients)
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

    internal static class AgentClientStatusPresentation
    {
        public static string ConfigPathLabel(AgentClientStatus status, string projectRoot)
        {
            return status != null && status.ConfigStatus == AgentClientConfigStatus.Current
                ? ProjectRelativePath(projectRoot, status.ConfigPath)
                : string.Empty;
        }

        public static string SkillPathLabel(AgentClientStatus status, string projectRoot)
        {
            return status != null && status.SkillStatus == AgentClientSkillStatus.Installed
                ? ProjectRelativePath(projectRoot, status.SkillPath)
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
