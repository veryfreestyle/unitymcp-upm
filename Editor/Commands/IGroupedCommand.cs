namespace VeryFS.UnityMCP.Editor.Commands
{
    // 标记接口: 声明本命令属于某个聚合组 (Group) 下的某个 action。
    // 只有需要聚合的子命令实现它; 独立命令继续只实现 IRpcCommand。
    // 子命令的 Descriptor.InputSchema 被复用为该 action 的 schema 片段。
    public interface IGroupedCommand : IRpcCommand
    {
        string Group { get; }
        string Action { get; }
    }
}
