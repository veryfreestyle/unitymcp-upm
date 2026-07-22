using UnityEditor;

namespace VeryFS.UnityMCP.Editor.Commands
{
    public sealed class UnityEditorBusyState : IEditorBusyState
    {
        public bool IsCompiling => EditorApplication.isCompiling;

        public bool IsUpdating => EditorApplication.isUpdating;
    }
}
