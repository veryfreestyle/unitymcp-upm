using System;
using System.Collections.Generic;
using LitJson;
using VeryFS.UnityMCP.Editor.Commands;
using VeryFS.UnityMCP.Editor.Infrastructure;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.AgentSkill
{
    internal interface IAgentSkillProjectContext
    {
        string ProjectRoot { get; }
        string UnityVersion { get; }
    }

    public sealed class InstallAgentSkillCommand : IRpcCommand
    {
        private readonly Func<IReadOnlyList<RpcToolDescriptor>> descriptors;
        private readonly IAgentSkillProjectContext context;
        private readonly IAgentSkillTemplateReader templateReader;
        private readonly IAgentSkillMarkdownGenerator generator;
        private readonly IAgentSkillFileStore fileStore;
        private readonly IClock clock;

        internal InstallAgentSkillCommand(
            Func<IReadOnlyList<RpcToolDescriptor>> descriptors,
            IAgentSkillProjectContext context,
            IAgentSkillTemplateReader templateReader,
            IAgentSkillMarkdownGenerator generator,
            IAgentSkillFileStore fileStore,
            IClock clock)
        {
            this.descriptors = descriptors;
            this.context = context;
            this.templateReader = templateReader;
            this.generator = generator;
            this.fileStore = fileStore;
            this.clock = clock;
        }

        public string Method => RpcMethods.AgentSkillInstall;

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "install-agent-skill",
            RpcMethod = RpcMethods.AgentSkillInstall,
            Title = "Agent Skill / Install",
            Description = "Generate and install a UnityMCP agent skill for the current project.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = InputSchema(),
            Annotations = JsonRpcSerializer.Object(
                ("readOnlyHint", false),
                ("destructiveHint", true),
                ("idempotentHint", true))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            try
            {
                InstallAgentSkillOptions options = InstallAgentSkillRequestParser.Parse(request.Params);
                IReadOnlyList<RpcToolDescriptor> currentDescriptors = descriptors();
                generator.ValidateRequestedTools(options, currentDescriptors);
                string template = templateReader.Read();
                var input = new AgentSkillGenerationInput(options, context.ProjectRoot, context.UnityVersion);
                AgentSkillGenerationResult generated = generator.Generate(template, input, currentDescriptors);
                AgentSkillWriteResult written = fileStore.Write(
                    context.ProjectRoot, options.Name, generated.Content, options.Overwrite, options.Clients);

                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                    ("path", written.RelativePath),
                    ("absolutePath", written.AbsolutePath),
                    ("paths", PathsObject(written.Paths, false)),
                    ("absolutePaths", PathsObject(written.Paths, true)),
                    ("clients", StringArray(options.Clients)),
                    ("toolCount", generated.ToolCount),
                    ("overwritten", written.Overwritten),
                    ("generatedAt", clock.UtcNow.ToString("O"))));
            }
            catch (AgentSkillOperationException exception)
            {
                var data = JsonRpcSerializer.Object(("errorCode", exception.ErrorCode));
                if (exception.UnknownTools != null)
                {
                    data["unknownTools"] = StringArray(exception.UnknownTools);
                }
                if (exception.ErrorCode == "skill_exists" && !string.IsNullOrEmpty(exception.Path))
                {
                    data["path"] = exception.Path;
                }

                return JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                    exception.RpcCode,
                    exception.Message,
                    data));
            }
        }

        private static JsonData InputSchema()
        {
            return JsonRpcSerializer.Object(
                ("type", "object"),
                ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("name", JsonRpcSerializer.Object(
                        ("type", "string"), ("default", "unitymcp"))),
                    ("overwrite", JsonRpcSerializer.Object(
                        ("type", "boolean"), ("default", false))),
                    ("clients", JsonRpcSerializer.Object(
                        ("type", "array"),
                        ("items", JsonRpcSerializer.Object(
                            ("type", "string"),
                            ("enum", StringArray(McpClientTargets.AllClientNames)))),
                        ("default", StringArray(McpClientTargets.DefaultClientNames)))),
                    ("includeTools", ArraySchema()),
                    ("excludeTools", JsonRpcSerializer.Object(
                        ("type", "array"),
                        ("items", JsonRpcSerializer.Object(("type", "string"))),
                        ("default", StringArray(new[] { "install-agent-skill" })))),
                    ("testAssemblies", ArraySchema()),
                    ("unityExecutable", JsonRpcSerializer.Object(("type", "string"))))));
        }

        private static JsonData ArraySchema()
        {
            return JsonRpcSerializer.Object(
                ("type", "array"),
                ("items", JsonRpcSerializer.Object(("type", "string"))));
        }

        private static JsonData StringArray(IReadOnlyList<string> values)
        {
            var result = new JsonData();
            result.SetJsonType(JsonType.Array);
            foreach (string value in values)
            {
                result.Add(value);
            }
            return result;
        }

        private static JsonData PathsObject(IReadOnlyList<AgentSkillPathResult> paths, bool absolute)
        {
            var result = new JsonData();
            result.SetJsonType(JsonType.Object);
            foreach (AgentSkillPathResult path in paths)
            {
                result[path.Key] = absolute ? path.AbsolutePath : path.RelativePath;
            }
            return result;
        }
    }
}
