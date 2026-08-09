using System.Collections.Generic;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Commands;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    /// <summary>
    /// fgui-input 的装配。整进程二选一, 之后不再切换 ——
    /// Stage 的旁路分支(SetCustomInput)优先级更高且是粘性的: 某帧只要有人调过它,
    /// 那一帧的 hit-test、touchPosition、事件分发全部走旁路, inputSource 注入的状态
    /// 被整帧吞掉。所以"click 走新路径、gesture 暂时走旧路径"会在相邻帧互相吃帧。
    /// </summary>
    public static class FguiInputRegistration
    {
        // 顺序与下面 RegisterCommands 里 legacy 分支的 Register 调用顺序一致
        // (click, double-click, gesture, hover) —— 那个调用顺序是既有行为, 不改;
        // 这个常量只用来拼组描述文案, 排错边跟着改。
        public static readonly string[] LegacyActions =
            { "click", "double-click", "gesture", "hover" };

        public static readonly string[] StageInputActions =
        {
            "move", "click", "double-click", "press", "release", "drag", "wheel",
            "send-key", "type-text", "step", "begin-session", "end-session", "visualize"
        };

        public const int TimeoutMs = 60000;

        // 组说明里的 action 列表装配期动态拼, 不写死。
        public static string DescriptionFor(IEnumerable<string> actions)
            => "Drive FairyGUI objects through the real input pipeline (async, cross-frame). action: "
                + string.Join(" | ", new List<string>(actions).ToArray()) + ". Play mode only.";

        /// <summary>
        /// 注册子命令。返回 null 表示走了 legacy 那批, 调用方据此决定 batch 用不用
        /// 隐式 session。
        /// </summary>
        public static McpStageInputSessionManager RegisterCommands(
            RpcCommandRegistry registry, IMcpStageInputProbe probe, IPanelSource panelSource,
            IStageInput legacyStageInput, IFrameStepper legacyStepper, string projectRoot)
        {
            if (!probe.TryBind(out McpStageInputBinding binding, out string reason))
            {
                // 反射方案下丢掉了编译期信号, 用运行期信号补: 这条 warning 与 action
                // 列表本身就是能力声明。工具描述里一律不写这些元信息。
                Debug.LogWarning("[UnityMCP] fgui-input is running in compatibility mode: " + reason);
                registry.Register(new FairyGUIClickCommand(panelSource, legacyStageInput, legacyStepper));
                registry.Register(new FairyGUIDoubleClickCommand(panelSource, legacyStageInput, legacyStepper));
                registry.Register(new FairyGUIGestureCommand(panelSource, legacyStageInput, legacyStepper));
                registry.Register(new FairyGUIHoverCommand(panelSource, legacyStageInput, legacyStepper));
                return null;
            }

            var gateway = new McpStageInputGateway(binding);
            var sessions = new McpStageInputSessionManager(gateway, projectRoot, null);

            registry.Register(new FguiInputMoveCommand(panelSource, gateway, sessions, projectRoot));
            registry.Register(new FguiInputClickCommand(panelSource, gateway, sessions, projectRoot));
            registry.Register(new FguiInputDoubleClickCommand(panelSource, gateway, sessions, projectRoot));
            registry.Register(new FguiInputPressCommand(panelSource, gateway, sessions, projectRoot));
            registry.Register(new FguiInputReleaseCommand(panelSource, gateway, sessions, projectRoot));
            registry.Register(new FguiInputDragCommand(panelSource, gateway, sessions, projectRoot));
            registry.Register(new FguiInputWheelCommand(panelSource, gateway, sessions, projectRoot));
            registry.Register(new FguiInputSendKeyCommand(panelSource, gateway, sessions, projectRoot));
            registry.Register(new FguiInputTypeTextCommand(panelSource, gateway, sessions, projectRoot));
            registry.Register(new FguiInputStepCommand(panelSource, gateway, sessions, projectRoot));
            registry.Register(new FguiInputBeginSessionCommand(panelSource, gateway, sessions, projectRoot));
            registry.Register(new FguiInputEndSessionCommand(panelSource, gateway, sessions, projectRoot));
            registry.Register(new FguiInputVisualizeCommand(panelSource, gateway, sessions, projectRoot));
            return sessions;
        }
    }
}
