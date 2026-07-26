using System;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Commands;
using VeryFS.UnityMCP.Editor.Infrastructure;

namespace VeryFS.UnityMCP.Editor.Commands.AgentSkill
{
    internal static class AgentSkillRegistration
    {
        internal static void Register(
            RpcCommandRegistry registry,
            string projectRoot,
            string unityVersion,
            IAgentSkillTemplateReader templateReader = null,
            IAgentSkillMarkdownGenerator generator = null,
            IAgentSkillFileStore fileStore = null,
            IClock clock = null)
        {
            if (templateReader == null)
            {
                templateReader = new GuidAgentSkillTemplateReader(
                    projectRoot, new UnityAssetPathResolver(), new SystemAgentSkillTextFile());
            }
            if (generator == null)
            {
                generator = new AgentSkillMarkdownGenerator();
            }
            if (fileStore == null)
            {
                fileStore = new AgentSkillFileStore(
                    new SystemAgentSkillFileSystem(),
                    new UlidLikeIdGenerator(),
                    Application.platform == RuntimePlatform.WindowsEditor
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal);
            }
            if (clock == null)
            {
                clock = new SystemClock();
            }

            registry.Register(new InstallAgentSkillCommand(
                () => registry.Descriptors,
                new AgentSkillProjectContext(projectRoot, unityVersion),
                templateReader,
                generator,
                fileStore,
                clock));
        }

        private sealed class AgentSkillProjectContext : IAgentSkillProjectContext
        {
            public AgentSkillProjectContext(string projectRoot, string unityVersion)
            {
                ProjectRoot = projectRoot;
                UnityVersion = unityVersion;
            }

            public string ProjectRoot { get; }
            public string UnityVersion { get; }
        }
    }
}
