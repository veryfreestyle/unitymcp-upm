using System;
using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using FairyGUI;
using UnityEngine;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public readonly struct McpRunOutcome
    {
        public McpRunOutcome(string result, Exception error, int frames, float seconds)
        {
            Result = result;
            Error = error;
            Frames = frames;
            Seconds = seconds;
        }

        public string Result { get; }     // "Completed" | "Canceled" | "Faulted" | "Abandoned"
        public Exception Error { get; }
        public int Frames { get; }
        public float Seconds { get; }

        public bool Completed => Result == "Completed";
    }

    /// <summary>
    /// 命令层看到的唯一 seam。真实实现是 McpStageInputGateway (反射 + fork 推帧),
    /// EditMode 测试塞记录调用的假实现。
    /// </summary>
    public interface IMcpStageInput
    {
        bool IsPlaying { get; }
        Vector2 StageSize { get; }
        Vector2 CurrentPointerPosition { get; }
        bool Active { get; }

        McpStageInputPlayer Start(string label, bool syncMousePositionFromCurrent);
        void Dispose(McpStageInputPlayer player);
        void ForceReset();

        UniTask<McpRunOutcome> RunAsync(IEnumerator sequence);

        void UseDefaultVisualizer(IDictionary<string, object> styleOverrides);
        void DisableVisualizer();
        void ClearVisualizer();

        // 关键帧的命中控件与当前焦点控件。null 表示没命中/没焦点。
        GObject TouchTarget { get; }
        GObject FocusTarget { get; }
    }
}
