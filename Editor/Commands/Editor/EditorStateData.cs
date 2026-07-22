using LitJson;
using UnityEditor;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Editor
{
    public interface IEditorStateProvider
    {
        bool IsPlaying { get; }
        bool IsPaused { get; }
        bool IsCompiling { get; }
        bool IsPlayingOrWillChangePlaymode { get; }
        bool IsUpdating { get; }
        string ApplicationContentsPath { get; }
        string ApplicationPath { get; }
        double TimeSinceStartup { get; }
    }

    public sealed class UnityEditorStateProvider : IEditorStateProvider
    {
        public bool IsPlaying => EditorApplication.isPlaying;
        public bool IsPaused => EditorApplication.isPaused;
        public bool IsCompiling => EditorApplication.isCompiling;
        public bool IsPlayingOrWillChangePlaymode => EditorApplication.isPlayingOrWillChangePlaymode;
        public bool IsUpdating => EditorApplication.isUpdating;
        public string ApplicationContentsPath => EditorApplication.applicationContentsPath;
        public string ApplicationPath => EditorApplication.applicationPath;
        public double TimeSinceStartup => EditorApplication.timeSinceStartup;
    }

    public static class EditorStateData
    {
        public static JsonData ToJson(IEditorStateProvider provider)
        {
            return JsonRpcSerializer.Object(
                ("isPlaying", provider.IsPlaying),
                ("isPaused", provider.IsPaused),
                ("isCompiling", provider.IsCompiling),
                ("isPlayingOrWillChangePlaymode", provider.IsPlayingOrWillChangePlaymode),
                ("isUpdating", provider.IsUpdating),
                ("applicationContentsPath", provider.ApplicationContentsPath),
                ("applicationPath", provider.ApplicationPath),
                ("timeSinceStartup", provider.TimeSinceStartup));
        }
    }
}
