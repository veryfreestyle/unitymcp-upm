using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using LitJson;
using VeryFS.UnityMCP.Editor.Infrastructure;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.AgentSkill
{
    internal static class InstallAgentSkillRequestParser
    {
        private const string DefaultName = "unitymcp";
        private const string DefaultExcludedTool = "install-agent-skill";
        private static readonly Regex NamePattern = new Regex(
            "^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

        public static InstallAgentSkillOptions Parse(JsonData parameters)
        {
            if (parameters != null && !parameters.IsObject)
            {
                Invalid("Parameters must be an object.");
            }

            ValidateProperties(parameters);

            string name = ReadName(parameters);
            bool overwrite = ReadBoolean(parameters, "overwrite", false);
            var clients = ReadClients(parameters);
            var includeTools = ReadArray(parameters, "includeTools", new string[0]);
            var excludeTools = ReadArray(parameters, "excludeTools", new[] { DefaultExcludedTool });
            var testAssemblies = ReadArray(parameters, "testAssemblies", new string[0]);
            string unityExecutable = ReadString(parameters, "unityExecutable", string.Empty);

            return new InstallAgentSkillOptions(
                name, overwrite, clients, includeTools, excludeTools, testAssemblies, unityExecutable);
        }

        private static void ValidateProperties(JsonData parameters)
        {
            if (parameters == null)
            {
                return;
            }

            foreach (string key in parameters.Keys)
            {
                if (key != "name" && key != "overwrite" && key != "clients" && key != "includeTools" &&
                    key != "excludeTools" && key != "testAssemblies" && key != "unityExecutable")
                {
                    Invalid("Unknown parameter: " + key + ".");
                }
            }
        }

        private static string ReadName(JsonData parameters)
        {
            string name = ReadString(parameters, "name", DefaultName);
            if (name.Length < 1 || name.Length > 64 || !NamePattern.IsMatch(name))
            {
                Invalid("'name' must be 1-64 lowercase letters, digits, and hyphens.");
            }

            return name;
        }

        private static IReadOnlyList<string> ReadClients(JsonData parameters)
        {
            IReadOnlyList<string> clients = ReadArray(parameters, "clients", McpClientTargets.DefaultClientNames);
            if (clients.Count == 0)
            {
                Invalid("'clients' must contain at least one supported client.");
            }

            foreach (string client in clients)
            {
                if (!McpClientTargets.IsKnownName(client))
                {
                    Invalid("'clients' must contain only claude, codex, or opencode.");
                }
            }

            return clients;
        }

        private static bool ReadBoolean(JsonData parameters, string key, bool defaultValue)
        {
            if (parameters == null || !parameters.ContainsKey(key))
            {
                return defaultValue;
            }

            JsonData value = parameters[key];
            if (value == null || !value.IsBoolean)
            {
                Invalid("'" + key + "' must be a boolean.");
            }

            return (bool)value;
        }

        private static string ReadString(JsonData parameters, string key, string defaultValue)
        {
            if (parameters == null || !parameters.ContainsKey(key))
            {
                return defaultValue;
            }

            JsonData value = parameters[key];
            if (value == null || !value.IsString)
            {
                Invalid("'" + key + "' must be a string.");
            }

            string result = (string)value;
            if (ContainsControlCharacter(result))
            {
                Invalid("'" + key + "' must not contain control characters.");
            }

            return result;
        }

        private static IReadOnlyList<string> ReadArray(
            JsonData parameters,
            string key,
            IReadOnlyList<string> defaultValue)
        {
            if (parameters == null || !parameters.ContainsKey(key))
            {
                return defaultValue;
            }

            JsonData value = parameters[key];
            if (value == null || !value.IsArray)
            {
                Invalid("'" + key + "' must be an array of strings.");
            }

            var result = new List<string>();
            var values = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < value.Count; i++)
            {
                JsonData item = value[i];
                if (item == null || !item.IsString || string.IsNullOrEmpty((string)item) ||
                    ContainsControlCharacter((string)item))
                {
                    Invalid("'" + key + "' must contain non-empty strings without control characters.");
                }

                string text = (string)item;
                if (values.Add(text))
                {
                    result.Add(text);
                }
            }

            return result;
        }

        private static bool ContainsControlCharacter(string value)
        {
            foreach (char character in value)
            {
                if (char.IsControl(character))
                {
                    return true;
                }
            }

            return false;
        }

        private static void Invalid(string message)
        {
            throw new AgentSkillOperationException(
                JsonRpcErrorCodes.InvalidParams,
                "invalid_params",
                message);
        }
    }
}
