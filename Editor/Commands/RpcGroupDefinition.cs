using LitJson;

namespace VeryFS.UnityMCP.Editor.Commands
{
    // 组门面定义: 登记一个聚合组对外暴露成什么工具。
    // Group 与实现 IGroupedCommand 的子命令 Group 值匹配。
    public sealed class RpcGroupDefinition
    {
        public string Group { get; set; }
        public string ToolName { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Completion { get; set; } = "response";
        public string FailureMode { get; set; } = "error";
        public int DefaultTimeoutMs { get; set; }
        public JsonData Annotations { get; set; }
    }
}
