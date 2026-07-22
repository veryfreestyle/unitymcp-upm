using UnityEditor;

namespace VeryFS.UnityMCP.Editor.Infrastructure
{
    public sealed class EditorSession
    {
        private const string SessionIdKey = "VeryFS.UnityMCP.EditorSessionId";

        private EditorSession()
        {
            EditorSessionId = SessionState.GetString(SessionIdKey, string.Empty);
            if (string.IsNullOrEmpty(EditorSessionId))
            {
                EditorSessionId = new UlidLikeIdGenerator().NewId("unity");
                SessionState.SetString(SessionIdKey, EditorSessionId);
            }
        }

        public static EditorSession Current { get; } = new EditorSession();

        public string EditorSessionId { get; }
    }
}
