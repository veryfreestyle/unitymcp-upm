using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands
{
    public sealed class RpcToolDescriptor
    {
        public string Name { get; set; }
        public string RpcMethod { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Completion { get; set; } = "response";
        public string FailureMode { get; set; } = "error";
        public int DefaultTimeoutMs { get; set; }
        public JsonData InputSchema { get; set; }
        public JsonData OutputSchema { get; set; }
        public JsonData Annotations { get; set; }

        public JsonData ToJson()
        {
            var data = JsonRpcSerializer.Object(
                ("name", Name),
                ("rpcMethod", RpcMethod),
                ("description", Description ?? string.Empty),
                ("completion", Completion),
                ("failureMode", FailureMode));
            if (!string.IsNullOrEmpty(Title))
            {
                data["title"] = Title;
            }
            if (DefaultTimeoutMs > 0)
            {
                data["defaultTimeoutMs"] = DefaultTimeoutMs;
            }
            data["inputSchema"] = InputSchema ?? JsonRpcSerializer.Object(
                ("type", "object"), ("additionalProperties", false));
            if (OutputSchema != null)
            {
                data["outputSchema"] = OutputSchema;
            }
            if (Annotations != null)
            {
                data["annotations"] = Annotations;
            }
            return data;
        }
    }
}
