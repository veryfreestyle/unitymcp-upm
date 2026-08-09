using System;
using System.Collections;
using UnityEngine;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    /// <summary>
    /// 绑在某个 fork StageInputPlayer 实例上的强类型委托集合。纯数据容器 ——
    /// EditMode 测试直接塞假委托就能验命令层, 不必反射也不必进 Play。
    /// 委托在每次 Start() 后绑一次(不是每帧), 逐帧路径上是普通委托调用。
    /// </summary>
    public sealed class McpStageInputPlayer
    {
        public object Instance;

        public Func<Vector2, int, IEnumerator> MoveTo;            // MoveTo(to, steps)
        public Func<Vector2, float, IEnumerator> MoveAtSpeed;     // MoveAtSpeed(to, pixelsPerSecond)
        public Func<Vector2, int, IEnumerator> Click;             // Click(pos, button)
        public Func<Vector2, int, IEnumerator> Press;             // Press(pos, button)
        public Func<Vector2, int, IEnumerator> Release;           // Release(pos, button)

        // Drag(from, to, steps, holdBeforeFrames, holdAfterFrames, button)
        public Func<Vector2, Vector2, int, int, int, int, IEnumerator> Drag;
        // DragAtSpeed(from, to, pixelsPerSecond, holdBeforeMs, holdAfterMs, button)
        public Func<Vector2, Vector2, float, float, float, int, IEnumerator> DragAtSpeed;

        public Func<KeyCode, EventModifiers, IEnumerator> SendKey;
        public Func<string, int, IEnumerator> TypeText;           // TypeText(text, framesPerChar)
        public Func<string, float, IEnumerator> TypeTextAtRate;   // TypeTextAtRate(text, msPerChar)
        public Func<Vector2, float, EventModifiers, IEnumerator> Scroll;

        public Func<int, IEnumerator> Step;                       // Step(frames)
        public Func<float, IEnumerator> StepMs;                   // StepMs(ms)
        public Func<IEnumerator> ReleaseHeld;
    }
}
