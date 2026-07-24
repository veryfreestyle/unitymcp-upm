using System.Collections.Generic;

namespace VeryFS.UnityMCP.Editor.Logs
{
    // Reads the Unity Editor console. Backed by the native LogEntries buffer so it
    // returns exactly what the Console window shows -- including editor-internal /
    // native entries that never fire Application.logMessageReceived.
    public interface IConsoleLogReader
    {
        // Newest-first, filtered and capped to maxEntries. truncated is true when
        // more matching entries existed than were returned.
        List<ConsoleLogRecord> Read(int maxEntries, string logTypeFilter, bool includeStackTrace, out bool truncated);

        // Clears the native console buffer. Returns false when the native API is
        // unavailable (reflection failed).
        bool Clear();
    }
}
