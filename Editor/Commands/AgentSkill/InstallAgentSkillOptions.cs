using System.Collections.Generic;

namespace VeryFS.UnityMCP.Editor.Commands.AgentSkill
{
    internal sealed class InstallAgentSkillOptions
    {
        public InstallAgentSkillOptions(
            string name,
            bool overwrite,
            IReadOnlyList<string> clients,
            IReadOnlyList<string> includeTools,
            IReadOnlyList<string> excludeTools,
            IReadOnlyList<string> testAssemblies,
            string unityExecutable)
        {
            Name = name;
            Overwrite = overwrite;
            Clients = clients;
            IncludeTools = includeTools;
            ExcludeTools = excludeTools;
            TestAssemblies = testAssemblies;
            UnityExecutable = unityExecutable;
        }

        public string Name { get; }
        public bool Overwrite { get; }
        public IReadOnlyList<string> Clients { get; }
        public IReadOnlyList<string> IncludeTools { get; }
        public IReadOnlyList<string> ExcludeTools { get; }
        public IReadOnlyList<string> TestAssemblies { get; }
        public string UnityExecutable { get; }
    }
}
