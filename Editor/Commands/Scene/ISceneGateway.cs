using System.Collections.Generic;

namespace VeryFS.UnityMCP.Editor.Commands.Scene
{
    public readonly struct SceneSnapshot
    {
        public SceneSnapshot(string path, string name, bool isDirty, bool isLoaded)
        { Path = path; Name = name; IsDirty = isDirty; IsLoaded = isLoaded; }
        public string Path { get; }
        public string Name { get; }
        public bool IsDirty { get; }
        public bool IsLoaded { get; }
    }

    public readonly struct SceneEntry
    {
        public SceneEntry(string path, string name, bool inBuildSettings, bool buildEnabled)
        { Path = path; Name = name; InBuildSettings = inBuildSettings; BuildEnabled = buildEnabled; }
        public string Path { get; }
        public string Name { get; }
        public bool InBuildSettings { get; }
        public bool BuildEnabled { get; }
    }

    public readonly struct OpenResult
    {
        public OpenResult(bool success) { Success = success; }
        public bool Success { get; }
    }

    public readonly struct SaveResult
    {
        public SaveResult(bool success) { Success = success; }
        public bool Success { get; }
    }

    public readonly struct DirtyScene
    {
        public DirtyScene(string name, string path) { Name = name; Path = path; }
        public string Name { get; }
        public string Path { get; }
    }

    // 包装 EditorSceneManager (编辑期), 便于 stub 测。
    public interface ISceneGateway
    {
        bool IsPlaying { get; }
        SceneSnapshot GetActiveScene();
        IReadOnlyList<SceneEntry> GetAllScenes();     // AssetDatabase.FindAssets("t:Scene"), 不过滤 Packages
        bool ActiveSceneDirty { get; }
        string ActiveScenePath { get; }
        OpenResult OpenSingle(string path);           // EditorSceneManager.OpenScene(path, Single)
        SaveResult SaveActive();                      // EditorSceneManager.SaveScene(activeScene)
        IReadOnlyList<DirtyScene> GetDirtyLoadedScenes();   // 所有已加载且 isDirty 的场景
    }
}
