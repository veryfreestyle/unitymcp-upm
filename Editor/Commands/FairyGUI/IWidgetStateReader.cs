using LitJson;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    // 读一个节点的控件专有状态 (GButton.selected / GSlider.value / GList.selectedIndex 等)。
    // 与 IUINode (纯只读结构抽象) 分离: 真实实现走 node.Unwrap() + 类型判断读真值;
    // 测试注入 stub 直接返回预置字段, 免于构造真实 FairyGUI GObject。
    // 返回一个 JsonData 对象 (可能为空); 序列化器把其 key 合并进节点 JSON。
    public interface IWidgetStateReader
    {
        JsonData ReadWidgetState(IUINode node);
    }
}
