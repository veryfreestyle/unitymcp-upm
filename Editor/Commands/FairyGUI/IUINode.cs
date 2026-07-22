using System.Collections.Generic;
using FairyGUI;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public readonly struct UIControllerInfo
    {
        public UIControllerInfo(string name, int selectedIndex, string selectedPage, int pageCount)
        {
            Name = name;
            SelectedIndex = selectedIndex;
            SelectedPage = selectedPage;
            PageCount = pageCount;
        }

        public string Name { get; }
        public int SelectedIndex { get; }
        public string SelectedPage { get; }
        public int PageCount { get; }
    }

    public readonly struct UITransitionInfo
    {
        public UITransitionInfo(string name, bool playing, double totalDuration)
        {
            Name = name;
            Playing = playing;
            TotalDuration = totalDuration;
        }

        public string Name { get; }
        public bool Playing { get; }
        public double TotalDuration { get; }
    }

    // Abstraction over a FairyGUI GObject so tree logic is unit-testable without a Stage.
    public interface IUINode
    {
        string Name { get; }
        string TypeName { get; }
        string Text { get; }            // null when the node type has no text
        bool Visible { get; }
        bool Grayed { get; }
        float X { get; }
        float Y { get; }
        float Width { get; }
        float Height { get; }
        int? GameObjectInstanceId { get; } // null when displayObject/gameObject absent
        bool IsComponent { get; }          // true for GComponent (has children/controllers/transitions)
        IReadOnlyList<IUINode> Children { get; }
        IReadOnlyList<UIControllerInfo> Controllers { get; }
        IReadOnlyList<UITransitionInfo> Transitions { get; }

        // 解包底层 GObject 供写操作使用; 非 FairyGUI-backed 实现 (测试 stub) 返 null。
        GObject Unwrap();
    }

    // Supplies the FairyGUI root; returns null when GRoot is not instantiated.
    public interface IUITreeSource
    {
        bool IsPlaying { get; }
        IUINode GetRoot();
    }

    public readonly struct PanelInfo
    {
        public PanelInfo(string source, string objectName, int instanceId,
            string packageName, string componentName, bool uiCreated)
        {
            Source = source;
            ObjectName = objectName;
            InstanceId = instanceId;
            PackageName = packageName;
            ComponentName = componentName;
            UiCreated = uiCreated;
        }

        public string Source { get; }        // "UIPanel" | "UIPainter"
        public string ObjectName { get; }
        public int InstanceId { get; }
        public string PackageName { get; }
        public string ComponentName { get; }
        public bool UiCreated { get; }
    }

    // 发现场景中的 FairyGUI UI 根: GRoot (代码模式) + UIPanel/UIPainter (组件模式)。
    public interface IPanelSource
    {
        bool IsPlaying { get; }
        IReadOnlyList<PanelInfo> ListPanels();     // 不含 GRoot
        IUINode GetPanelRoot(int instanceId);      // 找不到/未创建返 null
        IUINode GetGRoot();                         // GRoot.inst 反射; 未实例化返 null
    }
}
