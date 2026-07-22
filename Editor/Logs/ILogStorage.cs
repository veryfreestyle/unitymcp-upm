using System;
using System.Collections.Generic;

namespace VeryFS.UnityMCP.Editor.Logs
{
    public interface ILogStorage
    {
        void Append(LogEntry entry);
        void Clear();
        List<LogEntry> Query(int maxEntries, string logTypeFilter, bool includeStackTrace, int lastMinutes, DateTimeOffset now, out bool truncated);
        void Flush();
    }
}
