using System.Collections.Generic;
using System.Reflection;
using FairyGUI;
using UnityEditor;
using UnityEngine;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    // 发现场景所有 FairyGUI UI 根。反射读 UIPanel/UIPainter 的私有 _ui 字段
    // (避 .ui getter 在 play 模式的创建副作用) 与 GRoot._inst。锁定 FairyGUI 5.2.0。
    public sealed class FairyGUIPanelSource : IPanelSource
    {
        private static readonly FieldInfo GRootInstField =
            typeof(GRoot).GetField("_inst", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly FieldInfo UIPanelUiField =
            typeof(UIPanel).GetField("_ui", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo UIPainterUiField =
            typeof(UIPainter).GetField("_ui", BindingFlags.NonPublic | BindingFlags.Instance);

        public bool IsPlaying => EditorApplication.isPlaying;

        public IReadOnlyList<PanelInfo> ListPanels()
        {
            var list = new List<PanelInfo>();
            foreach (var panel in Resources.FindObjectsOfTypeAll<UIPanel>())
            {
                var ui = UIPanelUiField?.GetValue(panel) as GComponent;
                list.Add(new PanelInfo("UIPanel", panel.gameObject.name,
                    panel.gameObject.GetInstanceID(), panel.packageName ?? string.Empty,
                    panel.componentName ?? string.Empty, ui != null));
            }
            foreach (var painter in Resources.FindObjectsOfTypeAll<UIPainter>())
            {
                var ui = UIPainterUiField?.GetValue(painter) as GComponent;
                list.Add(new PanelInfo("UIPainter", painter.gameObject.name,
                    painter.gameObject.GetInstanceID(), painter.packageName ?? string.Empty,
                    painter.componentName ?? string.Empty, ui != null));
            }
            return list;
        }

        public IUINode GetPanelRoot(int instanceId)
        {
            foreach (var panel in Resources.FindObjectsOfTypeAll<UIPanel>())
            {
                if (panel.gameObject.GetInstanceID() != instanceId)
                {
                    continue;
                }
                var ui = UIPanelUiField?.GetValue(panel) as GComponent;
                return ui == null ? null : new GObjectNodeAdapter(ui);
            }
            foreach (var painter in Resources.FindObjectsOfTypeAll<UIPainter>())
            {
                if (painter.gameObject.GetInstanceID() != instanceId)
                {
                    continue;
                }
                var ui = UIPainterUiField?.GetValue(painter) as GComponent;
                return ui == null ? null : new GObjectNodeAdapter(ui);
            }
            return null;
        }

        public IUINode GetGRoot()
        {
            var inst = GRootInstField?.GetValue(null) as GRoot;
            return IsLive(inst) ? new GObjectNodeAdapter(inst) : null;
        }

        // GRoot._inst 退出 play 之后并不会变 null: GRoot 是 GComponent, 普通 C# 对象, 只有它
        // 挂的 GameObject 被销毁了。光判 inst == null 会把这份残骸当成活的 UI 树交出去 ——
        // 调用方(fgui-query 等)拿到的每个节点都指向已销毁对象。判到 gameObject 这一层才算数,
        // 那才是 UnityEngine.Object, 销毁后 == null 成立。
        private static bool IsLive(GRoot inst)
        {
            return inst != null && inst.displayObject != null && inst.displayObject.gameObject != null;
        }
    }
}
