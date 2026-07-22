using UnityEditor;

namespace VeryFS.UnityMCP.Editor.Commands.Editor
{
    public interface IPlayModeController
    {
        bool ScriptCompilationFailed { get; }
        string CompilationErrorDetails { get; }
        bool IsPlaying { get; set; }
        bool IsPaused { get; set; }
    }

    public sealed class UnityPlayModeController : IPlayModeController
    {
        public bool ScriptCompilationFailed => EditorUtility.scriptCompilationFailed;
        public string CompilationErrorDetails => "Unity project has compilation errors; fix them before changing play state.";
        public bool IsPlaying { get => EditorApplication.isPlaying; set => EditorApplication.isPlaying = value; }
        public bool IsPaused { get => EditorApplication.isPaused; set => EditorApplication.isPaused = value; }
    }
}
