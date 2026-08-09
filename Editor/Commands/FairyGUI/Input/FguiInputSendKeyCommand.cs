using Cysharp.Threading.Tasks;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Commands;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    /// <summary>
    /// 发一次按键(KeyDown+KeyUp, 外加修饰键释放共 3 帧, 见 fork StageInputPlayer.SendKeyRoutine)
    /// 到当前焦点控件。自足动作, 跟 wheel 一样不需要预先存在的 session、也不加独立的 IsPlaying
    /// 门 —— not_playing 从 Sessions.Acquire 的返回值里天然浮出来。target 报的是发键时的
    /// FocusTarget, 不是 TouchTarget: 键盘事件不经过命中测试, 走的是 Stage 独立的焦点状态。
    /// </summary>
    public sealed class FguiInputSendKeyCommand : FguiInputCommandBase
    {
        public FguiInputSendKeyCommand(IPanelSource panelSource, IMcpStageInput input,
            McpStageInputSessionManager sessions, string projectRoot)
            : base(panelSource, input, sessions, projectRoot) { }

        public override string Method => RpcMethods.FairyGuiInputSendKey;
        public override string Action => "send-key";

        public override RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-input-send-key",
            RpcMethod = RpcMethods.FairyGuiInputSendKey,
            Title = "FairyGUI / Input / Send key",
            Description = "Send one key press to the focused control. key is a KeyCode name such as Return, "
                + "Escape, A or Delete. Text fields treat control and command alike, so [\"control\"] works "
                + "on every platform; command alone never registers outside macOS. Keyboard focus survives "
                + "between calls, so this needs no session — set focus with fgui-state focus or a click first. "
                + "To replace a field's contents send A with [\"control\"] (select all), then Delete.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = Schema(p =>
            {
                p["key"] = JsonRpcSerializer.Object(
                    ("type", "string"), ("description", "KeyCode name, for example Return, Escape, A, Delete."));
                p["modifiers"] = JsonRpcSerializer.Object(
                    ("type", "array"), ("description", "Held modifiers: control, shift, alt, command."));
            }),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", false))
        };

        // 整段 body (含参数解析) 都在 Guarded 里, 见 FguiInputWheelCommand 顶上的注释:
        // 这批 action 里没有代码站在 Guarded 外面。
        public override UniTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request)
        {
            return Guarded(request, async () =>
            {
                if (!FguiInputRequest.TryReadKeyCode(request.Params, "key", out KeyCode key, out string keyError))
                {
                    return InvalidParams(request, keyError);
                }
                if (!FguiInputRequest.TryReadModifiers(request.Params, "modifiers",
                        out EventModifiers modifiers, out string modifierError))
                {
                    return InvalidParams(request, modifierError);
                }

                // 预算校验必须在 Acquire (会碰 Stage 输入状态) 之前完成。
                var budget = new FguiInputBudget();
                budget.AddFrames(3);   // KeyDown / KeyUp / 释放修饰键
                string violation = budget.Violation();
                if (violation != null) { return InvalidParams(request, violation); }

                using (McpInputLease lease = Sessions.Acquire(Method))
                {
                    if (lease.Error != null)
                    {
                        return JsonRpcResponse.FromSuccess(request.Id,
                            JsonRpcSerializer.Object(("state", lease.Error)));
                    }

                    McpRunOutcome outcome = await Input.RunAsync(lease.Player.SendKey(key, modifiers));
                    if (!outcome.Completed) { return Fault(request, outcome); }

                    // 键盘类取发键时的焦点控件, 不是命中控件。
                    return JsonRpcResponse.FromSuccess(request.Id,
                        Payload("ok", Input.FocusTarget, null, null));
                }
            });
        }
    }
}
