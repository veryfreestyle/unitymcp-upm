using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using FairyGUI;
using UnityEngine;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    // B 层手势逐帧执行器。坐标映射 + SetCustomInput 序列, 帧推进靠 IFrameStepper。
    // 帧上限护栏: 超 maxFrames 的路径返回 false (命令层映射为 timeout)。
    public sealed class FairyGUIGesturePlayer
    {
        private readonly IStageInput input;
        private readonly IFrameStepper stepper;
        private readonly int maxFrames;
        private int framesUsed;

        public FairyGUIGesturePlayer(IStageInput input, IFrameStepper stepper, int maxFrames)
        {
            this.input = input;
            this.stepper = stepper;
            this.maxFrames = maxFrames;
        }

        // 换算逻辑与依据见 FairyGUIScreenPoint; 这里保留签名给 legacy 调用方与既有测试。
        public static Vector2 StageToScreen(Vector2 stagePos, Vector2 stageSize)
            => FairyGUIScreenPoint.StageToScreen(stagePos, stageSize);

        public static Vector2 CenterOf(GObject obj)
            => FairyGUIScreenPoint.CenterOf(obj);

        // 推进一帧 (先喂输入再 yield)。返回 false 表示已超帧上限。
        private async UniTask<bool> Step(Vector2 pos, bool down)
        {
            if (framesUsed >= maxFrames) return false;
            input.SetCustomInput(pos, down);
            framesUsed++;
            await stepper.NextFrame();
            return true;
        }

        public async UniTask<bool> PlayClick(Vector2 screenPos)
        {
            if (!await Step(screenPos, true)) return false;
            if (!await Step(screenPos, false)) return false;
            return true;
        }

        public async UniTask<bool> PlayDoubleClick(Vector2 screenPos)
        {
            if (!await Step(screenPos, true)) return false;
            if (!await Step(screenPos, false)) return false;
            if (!await Step(screenPos, true)) return false;
            if (!await Step(screenPos, false)) return false;
            return true;
        }

        public async UniTask<bool> PlayPath(Vector2 startScreen, IReadOnlyList<Vector2> pathScreen, int holdFrames)
        {
            if (!await Step(startScreen, true)) return false;      // down
            for (int i = 0; i < holdFrames; i++)
                if (!await Step(startScreen, true)) return false;  // 长按
            foreach (var p in pathScreen)
                if (!await Step(p, true)) return false;            // move
            var end = pathScreen.Count > 0 ? pathScreen[pathScreen.Count - 1] : startScreen;
            if (!await Step(end, false)) return false;             // up
            return true;
        }

        public async UniTask PlayHover(Vector2 screenPos)
        {
            await Step(screenPos, false);
        }
    }
}
