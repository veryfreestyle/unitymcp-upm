using System;
using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using FairyGUI;
using UnityEditor;
using UnityEngine;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    /// <summary>
    /// IMcpStageInput 的真实实现。推帧不归 MCP —— StageInputSimulator.Run 内部走
    /// Timers.inst.StartCoroutine, 恢复点天然在 Update 之后 LateUpdate 之前。
    /// 这里只做三件事: 把 Run 的回调桥成 UniTask、超时时 Cancel 并等收尾、
    /// 运行期帧上限兜底。
    /// </summary>
    public sealed class McpStageInputGateway : IMcpStageInput
    {
        // 请求期上限(30 秒墙钟 / 1800 帧)挡不住"帧率异常低导致墙钟部分推了极多帧"。
        // 这两条是运行期兜底, 定在 Go 侧 60 秒工具超时之下, 好让我们先返回。
        public const float MaxRunSeconds = 50f;
        public const int MaxRunFrames = 36000;
        // Cancel 的收尾占一帧(P22.1 §4.3), 给足余量; 超了就 ForceReset 强行归还。
        // 这个余量是相对"开始 Cancel 那一刻的帧号"算的, 不是相对 MaxRunFrames ——
        // 帧率异常低时 elapsed 触发 Cancel 时 frames 可能远小于 MaxRunFrames, 若拿
        // MaxRunFrames + CancelGraceFrames 当绝对门槛, ForceReset 兜底在这种场景下
        // 形同虚设(要等到 frames 追到 36030, 10fps 下是另外约 3550 秒)。
        public const int CancelGraceFrames = 30;

        private readonly McpStageInputBinding binding;

        public McpStageInputGateway(McpStageInputBinding binding)
        {
            this.binding = binding ?? throw new ArgumentNullException(nameof(binding));
        }

        public bool IsPlaying => EditorApplication.isPlaying;
        public Vector2 StageSize => Stage.inst == null ? Vector2.zero : Stage.inst.size;
        public Vector2 CurrentPointerPosition => binding.CurrentPointerPosition;
        public bool Active => binding.Active;

        public McpStageInputPlayer Start(string label, bool syncMousePositionFromCurrent)
            => binding.Start(label, syncMousePositionFromCurrent);

        public void Dispose(McpStageInputPlayer player) => binding.Dispose(player);
        public void ForceReset() => binding.ForceReset();

        public void UseDefaultVisualizer(IDictionary<string, object> styleOverrides)
            => binding.UseDefaultVisualizer(styleOverrides);
        public void DisableVisualizer() => binding.DisableVisualizer();
        public void ClearVisualizer() => binding.ClearVisualizer();

        public GObject TouchTarget => Stage.inst == null ? null : Stage.inst.touchTarget?.gOwner;
        public GObject FocusTarget => Stage.inst == null ? null : Stage.inst.focus?.gOwner;

        /// <summary>
        /// 桥接 binding.Run 的回调。binding.Run 本身在四道门(sequence 为 null / 不在 Play
        /// 模式 / 未 Start / 已有序列在跑)任一没过时同步抛出原始异常(McpStageInputBinding
        /// 用 ExceptionDispatchInfo 保留了类型与堆栈), 不会被吞成 McpRunOutcome.Faulted ——
        /// 那些是调用前置条件错误, 不是"序列跑起来后失败", 调用方(Tasks 7-12 的命令层)
        /// 要用 try/catch 接住, 不能只看返回值的 Result 字段。
        /// </summary>
        public async UniTask<McpRunOutcome> RunAsync(IEnumerator sequence)
        {
            // Run 会同步跑完序列第一段(比如 Click 的 MoveMouse + PressMouse), 所以调用点
            // 必须在本帧 LateUpdate 之前 —— StageEngine 在 LateUpdate 读注入状态。
            // RPC dispatcher 的 tick 落在哪一相不确定, 先 yield 到 Update 相再调。
            await UniTask.Yield(PlayerLoopTiming.Update, default(System.Threading.CancellationToken));

            string result = null;
            Exception error = null;
            bool done = false;
            binding.Run(sequence, (r, ex) => { result = r; error = ex; done = true; });

            int frames = 0;
            double startedAt = EditorApplication.timeSinceStartup;
            bool canceling = false;
            int cancelStartFrame = 0;

            while (!done)
            {
                await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate,
                    default(System.Threading.CancellationToken));
                frames++;
                float elapsed = (float)(EditorApplication.timeSinceStartup - startedAt);

                RunGuardDecision decision = EvaluateGuard(frames, elapsed, canceling, cancelStartFrame);
                if (decision == RunGuardDecision.StartCanceling)
                {
                    // 不能直接 Dispose 走 fork 的同步强清路径: 业务的拖拽状态机收不到
                    // onTouchEnd 会永远停在拖拽中 (P22.1 §4.5)。Cancel 后等收尾那一帧。
                    canceling = true;
                    cancelStartFrame = frames;
                    binding.Cancel();
                    Debug.LogWarning("[UnityMCP] fgui-input run exceeded the runtime guard ("
                        + frames + " frames / " + elapsed.ToString("0.0") + "s); canceling.");
                }
                else if (decision == RunGuardDecision.Abandon)
                {
                    binding.ForceReset();
                    return new McpRunOutcome("Abandoned", null, frames, elapsed);
                }
            }

            return new McpRunOutcome(result, error, frames,
                (float)(EditorApplication.timeSinceStartup - startedAt));
        }

        internal enum RunGuardDecision
        {
            Continue,
            StartCanceling,
            Abandon
        }

        /// <summary>
        /// 纯决策函数, 不摸任何 Unity API, 供 EditMode 单测直接驱动。
        /// cancelStartFrame 只在 canceling 为 true 时有意义, 是 StartCanceling 那一次
        /// 记下的 frames —— Abandon 的门槛必须相对它算(frames - cancelStartFrame), 不能
        /// 相对 MaxRunFrames 算: 帧率异常低时 elapsed 先触发 StartCanceling, 此时 frames
        /// 可能远小于 MaxRunFrames, 相对 MaxRunFrames 的门槛在合理时间内根本追不到。
        /// </summary>
        internal static RunGuardDecision EvaluateGuard(
            int frames, float elapsed, bool canceling, int cancelStartFrame)
        {
            if (!canceling)
            {
                return (frames > MaxRunFrames || elapsed > MaxRunSeconds)
                    ? RunGuardDecision.StartCanceling
                    : RunGuardDecision.Continue;
            }

            return (frames - cancelStartFrame > CancelGraceFrames)
                ? RunGuardDecision.Abandon
                : RunGuardDecision.Continue;
        }
    }
}
