using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace VeryFS.UnityMCP.Editor.Commands.Scene
{
    public sealed class UnitySceneGateway : ISceneGateway
    {
        public bool IsPlaying => EditorApplication.isPlaying;

        public SceneSnapshot GetActiveScene()
        {
            var s = EditorSceneManager.GetActiveScene();
            return new SceneSnapshot(s.path ?? string.Empty, s.name ?? string.Empty, s.isDirty, s.isLoaded);
        }

        public IReadOnlyList<SceneEntry> GetAllScenes()
        {
            var buildMap = new Dictionary<string, bool>();
            foreach (var b in EditorBuildSettings.scenes)
            {
                buildMap[b.path] = b.enabled;
            }
            var list = new List<SceneEntry>();
            foreach (var guid in AssetDatabase.FindAssets("t:Scene"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string name = Path.GetFileNameWithoutExtension(path);
                bool inBuild = buildMap.TryGetValue(path, out bool enabled);
                list.Add(new SceneEntry(path, name, inBuild, inBuild && enabled));
            }
            return list;
        }

        public bool ActiveSceneDirty => EditorSceneManager.GetActiveScene().isDirty;
        public string ActiveScenePath => EditorSceneManager.GetActiveScene().path ?? string.Empty;

        public OpenResult OpenSingle(string path)
        {
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            return new OpenResult(scene.IsValid());
        }

        public SaveResult SaveActive()
        {
            bool ok = EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            return new SaveResult(ok);
        }

        public IReadOnlyList<DirtyScene> GetDirtyLoadedScenes()
        {
            var dirty = new List<DirtyScene>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty)
                {
                    dirty.Add(new DirtyScene(scene.name, scene.path));
                }
            }

            return dirty;
        }
    }
}
