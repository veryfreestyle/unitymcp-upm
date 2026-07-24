using System;
using System.Collections.Generic;
using System.Reflection;

namespace VeryFS.UnityMCP.Editor.Logs
{
    // Reflection-based reader over Unity's internal UnityEditor.LogEntries console
    // buffer. Modelled on CoplayDev unity-mcp's ReadConsole.cs (see AGENTS.md
    // "参考实现"). Every reflective access is guarded: on any failure it degrades to
    // "no entries" / "cannot clear" so a query never crashes the RPC loop.
    public sealed class EditorConsoleLogReader : IConsoleLogReader
    {
        private const BindingFlags StaticFlags =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private const BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly Type logEntryType;
        private readonly MethodInfo startGettingEntries;
        private readonly MethodInfo endGettingEntries;
        private readonly MethodInfo clear;
        private readonly FieldInfo fMode;
        private readonly FieldInfo fMessage;
        private readonly FieldInfo fFile;
        private readonly FieldInfo fLine;
        private readonly MethodInfo getEntryInternal;
        private readonly bool ready;

        public EditorConsoleLogReader()
        {
            try
            {
                var logEntriesType = Type.GetType("UnityEditor.LogEntries,UnityEditor");
                logEntryType = Type.GetType("UnityEditor.LogEntry,UnityEditor");
                if (logEntriesType == null || logEntryType == null)
                {
                    return;
                }

                startGettingEntries = logEntriesType.GetMethod("StartGettingEntries", StaticFlags);
                endGettingEntries = logEntriesType.GetMethod("EndGettingEntries", StaticFlags);
                clear = logEntriesType.GetMethod("Clear", StaticFlags);
                getEntryInternal = logEntriesType.GetMethod("GetEntryInternal", StaticFlags);
                fMode = logEntryType.GetField("mode", InstanceFlags);
                fMessage = logEntryType.GetField("message", InstanceFlags);
                fFile = logEntryType.GetField("file", InstanceFlags);
                fLine = logEntryType.GetField("line", InstanceFlags);

                ready = startGettingEntries != null && endGettingEntries != null
                    && getEntryInternal != null && fMode != null && fMessage != null;
            }
            catch
            {
                ready = false;
            }
        }

        public List<ConsoleLogRecord> Read(int maxEntries, string logTypeFilter, bool includeStackTrace, out bool truncated)
        {
            truncated = false;
            if (!ready)
            {
                return new List<ConsoleLogRecord>();
            }

            var raw = new List<ConsoleLogShaping.RawEntry>();
            try
            {
                var total = (int)startGettingEntries.Invoke(null, null);
                try
                {
                    var entry = Activator.CreateInstance(logEntryType);
                    for (var i = 0; i < total; i++)
                    {
                        getEntryInternal.Invoke(null, new object[] { i, entry });
                        raw.Add(new ConsoleLogShaping.RawEntry
                        {
                            Mode = fMode.GetValue(entry) is int m ? m : 0,
                            Message = fMessage.GetValue(entry) as string ?? string.Empty,
                            File = fFile?.GetValue(entry) as string ?? string.Empty,
                            Line = fLine != null && fLine.GetValue(entry) is int l ? l : 0
                        });
                    }
                }
                finally
                {
                    endGettingEntries.Invoke(null, null);
                }
            }
            catch
            {
                return new List<ConsoleLogRecord>();
            }

            return ConsoleLogShaping.Shape(raw, maxEntries, logTypeFilter, includeStackTrace, out truncated);
        }

        public bool Clear()
        {
            if (clear == null)
            {
                return false;
            }
            try
            {
                clear.Invoke(null, null);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
