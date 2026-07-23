using System;

namespace VeryFS.UnityMCP.Editor.UI
{
    /// <summary>Chooses the pid to kill: the global holder when set, otherwise the
    /// discovery-file fallback.</summary>
    public static class ServerPidResolver
    {
        public static int Resolve(Func<int> holder, Func<int> discovery)
        {
            int fromHolder = holder();
            return fromHolder > 0 ? fromHolder : discovery();
        }
    }
}
