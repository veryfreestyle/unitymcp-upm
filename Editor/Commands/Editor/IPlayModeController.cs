using UnityEditor;

namespace VeryFS.UnityMCP.Editor.Commands.Editor
{
    public interface IPlayModeController
    {
        bool ScriptCompilationFailed { get; }
        string CompilationErrorDetails { get; }
        bool IsPlaying { get; set; }
        // play mode 转换在路上时 IsPlaying 还是 false —— 需要在这段窗口里拒绝的调用方
        // (比如 test.run) 只看 IsPlaying 会漏。
        bool IsPlayingOrWillChangePlaymode { get; }
        bool IsPaused { get; set; }
    }

    public sealed class UnityPlayModeController : IPlayModeController
    {
        public bool ScriptCompilationFailed => EditorUtility.scriptCompilationFailed;
        public string CompilationErrorDetails => "Unity project has compilation errors; fix them before changing play state.";
        public bool IsPlaying { get => EditorApplication.isPlaying; set => EditorApplication.isPlaying = value; }
        public bool IsPlayingOrWillChangePlaymode => EditorApplication.isPlayingOrWillChangePlaymode;
        public bool IsPaused { get => EditorApplication.isPaused; set => EditorApplication.isPaused = value; }
    }
}
