using FairyGUI;
using UnityEngine;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    // 两批 fgui-input 命令共用的坐标换算。原先长在 FairyGUIGesturePlayer 上,
    // 提出来是为了让 legacy 与 fork 两条路径用同一份换算, 排除点位不同的可能。
    // 不反射 fork 的 StageInputSimulator.ScreenPointOf: 它就是从这两个方法搬过去的
    // (P22 §5.9), 复用只是多一个绑定面。
    public static class FairyGUIScreenPoint
    {
        // Stage → 屏幕坐标: Y 翻转。
        // 依据 Stage.cs 内部对 _customInputPos 做 `pos.y = _contentRect.height - pos.y`,
        // 且 stageHeight = (int)_contentRect.height; LocalToGlobal 返回的 Stage 坐标 Y 向下。
        public static Vector2 StageToScreen(Vector2 stagePos, Vector2 stageSize)
            => new Vector2(stagePos.x, stageSize.y - stagePos.y);

        // 控件中心的 Stage 坐标。
        public static Vector2 CenterOf(GObject obj)
            => obj.LocalToGlobal(new Vector2(obj.width / 2f, obj.height / 2f));

        // 控件中心的屏幕坐标。新命令的定位统一走这里。
        public static Vector2 ScreenCenterOf(GObject obj, Vector2 stageSize)
            => StageToScreen(CenterOf(obj), stageSize);
    }
}
