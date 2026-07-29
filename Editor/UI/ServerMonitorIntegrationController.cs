using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LitJson;
using VeryFS.UnityMCP.Editor.Infrastructure;

namespace VeryFS.UnityMCP.Editor.UI
{
    internal enum McpClientConfigStatus
    {
        Off,
        Current,
        Stale,
        Missing,
        Invalid
    }

    internal sealed class McpClientStatus
    {
        public McpClientStatus(
            string client,
            bool enabled,
            McpClientConfigStatus configStatus,
            string configPath)
        {
            Client = client;
            Enabled = enabled;
            ConfigStatus = configStatus;
            ConfigPath = configPath;
        }

        public string Client { get; }
        public bool Enabled { get; }
        public McpClientConfigStatus ConfigStatus { get; }
        public string ConfigPath { get; }
    }

    internal sealed class McpClientIntegrationSnapshot
    {
        public McpClientIntegrationSnapshot(McpClientTarget enabledTargets, IReadOnlyList<McpClientStatus> clients)
        {
            EnabledTargets = enabledTargets;
            Clients = clients;
        }

        public McpClientTarget EnabledTargets { get; }
        public IReadOnlyList<McpClientStatus> Clients { get; }
    }

    internal sealed class McpClientActionResult
    {
        public McpClientActionResult(bool succeeded, string message)
        {
            Succeeded = succeeded;
            Message = message;
        }

        public bool Succeeded { get; }
        public string Message { get; }
    }

    internal sealed class ServerMonitorIntegrationController
    {
        private readonly string projectRoot;
        private readonly Func<DiscoveryDocument> discoveryProvider;

        public ServerMonitorIntegrationController(
            string projectRoot,
            Func<DiscoveryDocument> discoveryProvider)
        {
            this.projectRoot = projectRoot;
            this.discoveryProvider = discoveryProvider;
        }

        public McpClientTarget LoadTargets()
        {
            return McpClientIntegrationPreferences.Load(projectRoot);
        }

        public McpClientIntegrationSnapshot ReadStatuses()
        {
            McpClientTarget enabled = LoadTargets();
            DiscoveryDocument doc = discoveryProvider();
            var clients = new List<McpClientStatus>
            {
                StatusFor(McpClientTargets.Claude, enabled, doc),
                StatusFor(McpClientTargets.Codex, enabled, doc),
                StatusFor(McpClientTargets.OpenCode, enabled, doc)
            };
            return new McpClientIntegrationSnapshot(enabled, clients);
        }

        public McpClientActionResult ApplyTargets(McpClientTarget nextTargets)
        {
            McpClientTarget currentTargets = LoadTargets();
            McpClientIntegrationSnapshot snapshot = ReadStatuses();
            foreach (McpClientStatus status in snapshot.Clients)
            {
                if (status.ConfigStatus != McpClientConfigStatus.Invalid)
                {
                    continue;
                }

                McpClientTarget target = TargetFor(status.Client);
                if ((currentTargets & target) != (nextTargets & target))
                {
                    return new McpClientActionResult(
                        false,
                        "Config for " + status.Client + " is invalid: " + status.ConfigPath);
                }
            }

            try
            {
                DiscoveryFileWriter.Write(projectRoot, discoveryProvider(), nextTargets);
                McpClientIntegrationPreferences.Save(projectRoot, nextTargets);
                return new McpClientActionResult(true, "MCP client settings updated.");
            }
            catch (Exception exception)
            {
                return new McpClientActionResult(false, exception.Message);
            }
        }

        private McpClientStatus StatusFor(string client, McpClientTarget enabledTargets, DiscoveryDocument doc)
        {
            bool enabled = (enabledTargets & TargetFor(client)) != 0;
            if (!enabled)
            {
                return new McpClientStatus(
                    client,
                    false,
                    McpClientConfigStatus.Off,
                    ConfigPathFor(client));
            }

            return new McpClientStatus(
                client,
                true,
                ConfigStatusFor(client, doc),
                ConfigPathFor(client));
        }

        private McpClientConfigStatus ConfigStatusFor(string client, DiscoveryDocument doc)
        {
            if (client == McpClientTargets.Claude)
            {
                return JsonMcpStatus(
                    Path.Combine(projectRoot, DiscoveryFileWriter.McpConfigFileName),
                    "mcpServers",
                    doc,
                    false);
            }
            if (client == McpClientTargets.OpenCode)
            {
                return JsonMcpStatus(
                    Path.Combine(projectRoot, DiscoveryFileWriter.OpenCodeConfigDirectoryName,
                        DiscoveryFileWriter.OpenCodeConfigFileName),
                    "mcp",
                    doc,
                    true);
            }

            return CodexStatus(doc);
        }

        private McpClientConfigStatus JsonMcpStatus(
            string path,
            string containerKey,
            DiscoveryDocument doc,
            bool requireEnabled)
        {
            if (!File.Exists(path))
            {
                return McpClientConfigStatus.Missing;
            }

            JsonData root;
            try
            {
                root = JsonMapper.ToObject(File.ReadAllText(path));
            }
            catch (Exception)
            {
                return McpClientConfigStatus.Invalid;
            }

            if (root == null || !root.IsObject)
            {
                return McpClientConfigStatus.Invalid;
            }

            if (!root.ContainsKey(containerKey))
            {
                return McpClientConfigStatus.Missing;
            }

            if (root[containerKey] == null || !root[containerKey].IsObject)
            {
                return McpClientConfigStatus.Invalid;
            }

            JsonData servers = root[containerKey];
            if (!servers.ContainsKey(DiscoveryFileWriter.McpServerName))
            {
                return McpClientConfigStatus.Missing;
            }

            JsonData server = servers[DiscoveryFileWriter.McpServerName];
            if (server == null || !server.IsObject)
            {
                return McpClientConfigStatus.Invalid;
            }

            if (!HasString(server, "url", doc.ServerUrl) ||
                !server.ContainsKey("headers") ||
                server["headers"] == null ||
                !server["headers"].IsObject ||
                !HasString(server["headers"], "Authorization", "Bearer " + doc.ClientToken))
            {
                return McpClientConfigStatus.Stale;
            }

            if (requireEnabled && (!server.ContainsKey("enabled") || !server["enabled"].IsBoolean ||
                    !(bool)server["enabled"]))
            {
                return McpClientConfigStatus.Stale;
            }

            return McpClientConfigStatus.Current;
        }

        private McpClientConfigStatus CodexStatus(DiscoveryDocument doc)
        {
            string path = Path.Combine(projectRoot, DiscoveryFileWriter.CodexConfigDirectoryName,
                DiscoveryFileWriter.CodexConfigFileName);
            if (!File.Exists(path))
            {
                return McpClientConfigStatus.Missing;
            }

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception)
            {
                return McpClientConfigStatus.Invalid;
            }

            string header = "[mcp_servers." + DiscoveryFileWriter.McpServerName + "]";
            if (!text.Contains(header))
            {
                return McpClientConfigStatus.Missing;
            }

            bool current = text.Contains("url = " + TomlString(doc.ServerUrl)) &&
                text.Contains("http_headers = { Authorization = " + TomlString("Bearer " + doc.ClientToken) + " }");
            return current ? McpClientConfigStatus.Current : McpClientConfigStatus.Stale;
        }

        private string ConfigPathFor(string client)
        {
            if (client == McpClientTargets.Claude)
            {
                return Path.Combine(projectRoot, DiscoveryFileWriter.McpConfigFileName);
            }
            if (client == McpClientTargets.OpenCode)
            {
                return Path.Combine(projectRoot, DiscoveryFileWriter.OpenCodeConfigDirectoryName,
                    DiscoveryFileWriter.OpenCodeConfigFileName);
            }

            return Path.Combine(projectRoot, DiscoveryFileWriter.CodexConfigDirectoryName,
                DiscoveryFileWriter.CodexConfigFileName);
        }

        private static bool HasString(JsonData data, string key, string expected)
        {
            return data.ContainsKey(key) && data[key] != null && data[key].IsString &&
                string.Equals((string)data[key], expected, StringComparison.Ordinal);
        }

        private static McpClientTarget TargetFor(string client)
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

        private static string TomlString(string value)
        {
            var builder = new StringBuilder();
            builder.Append('"');
            foreach (char c in value ?? string.Empty)
            {
                if (c == '\\')
                {
                    builder.Append("\\\\");
                }
                else if (c == '"')
                {
                    builder.Append("\\\"");
                }
                else
                {
                    builder.Append(c);
                }
            }
            builder.Append('"');
            return builder.ToString();
        }
    }
}
