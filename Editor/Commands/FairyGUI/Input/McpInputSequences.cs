using System;
using System.Collections;
using UnityEngine;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    /// <summary>
    /// 序列拼接。fork 的每个动作各自返回一个 IEnumerator, 一条命令常常要把
    /// "移动段 + 动作" 串起来交给 StageInputSimulator.Run 一次跑完 ——
    /// 分两次 Run 会在中间掉帧, 而且 Run 不允许并发。
    /// </summary>
    public static class McpInputSequences
    {
        public static IEnumerator Concat(params IEnumerator[] parts)
        {
            if (parts == null) { yield break; }
            foreach (IEnumerator part in parts)
            {
                if (part == null) { continue; }
                while (part.MoveNext()) { yield return part.Current; }
            }
        }

        /// <summary>
        /// 跑两遍同一个工厂, 并把每遍最后一次推进的时刻记进 stamps。
        /// double-click 用它: TouchInfo.End 的 0.35 秒双击窗口比的是两次抬起的
        /// Time.unscaledTime (Stage.cs:1745), 而抬起写在 Click 的最后一次推进上。
        /// </summary>
        public static IEnumerator TimedPair(Func<IEnumerator> make, float[] stamps)
        {
            if (make == null) { yield break; }
            for (int i = 0; i < 2; i++)
            {
                IEnumerator part = make();
                if (part == null) { continue; }
                while (part.MoveNext())
                {
                    if (stamps != null && i < stamps.Length) { stamps[i] = Time.unscaledTime; }
                    yield return part.Current;
                }
            }
        }
    }
}
