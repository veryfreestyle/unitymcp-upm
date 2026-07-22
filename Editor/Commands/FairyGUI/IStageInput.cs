using Cysharp.Threading.Tasks;
using FairyGUI;
using UnityEditor;
using UnityEngine;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    // B 层真实输入 seam: 包 Stage.inst.SetCustomInput, 便于单测注入记录序列的 stub。
    public interface IStageInput
    {
        bool IsPlaying { get; }
        Vector2 StageSize { get; }   // Stage 逻辑尺寸 (像素), 坐标换算用
        void SetCustomInput(Vector2 screenPos, bool buttonDown);
    }

    // 逐帧推进抽象: 真实实现 await 到下一帧的 LastPostLateUpdate (SetCustomInput 靠 LateUpdate 生效)。
    public interface IFrameStepper
    {
        UniTask NextFrame();
    }

    public sealed class UnityStageInput : IStageInput
    {
        public bool IsPlaying => EditorApplication.isPlaying;
        // Stage.inst.size comes from base class DisplayObject.size (Core/DisplayObject.cs:460)
        public Vector2 StageSize => Stage.inst.size;
        public void SetCustomInput(Vector2 screenPos, bool buttonDown)
            => Stage.inst.SetCustomInput(screenPos, buttonDown);
    }

    public sealed class UniTaskFrameStepper : IFrameStepper
    {
        public UniTask NextFrame() => UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate, default(System.Threading.CancellationToken));
    }
}
