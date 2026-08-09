namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    /// <summary>
    /// 装配期问它: 这个项目装的 FairyGUI 支不支持输入注入。
    /// 可注入是为了在本仓库(装的就是 fork)里验降级路径 —— 塞恒返回 false 的实现,
    /// 装配就会注册 legacy 那批, 不必换包。
    /// </summary>
    public interface IMcpStageInputProbe
    {
        bool TryBind(out McpStageInputBinding binding, out string reason);
    }
}
