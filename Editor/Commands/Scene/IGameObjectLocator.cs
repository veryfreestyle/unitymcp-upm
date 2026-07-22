using UnityEditor;
using UnityEngine;

namespace VeryFS.UnityMCP.Editor.Commands.Scene
{
    // Locates a GameObject by instanceId (preferred) or GameObject.Find path.
    public interface IGameObjectLocator
    {
        GameObject Locate(int? instanceId, string findPath);
    }

    public sealed class UnityGameObjectLocator : IGameObjectLocator
    {
        public GameObject Locate(int? instanceId, string findPath)
        {
            if (instanceId.HasValue)
            {
                return EditorUtility.InstanceIDToObject(instanceId.Value) as GameObject;
            }
            if (!string.IsNullOrEmpty(findPath))
            {
                return GameObject.Find(findPath); // active objects only, matches Unity API
            }
            return null;
        }
    }
}
