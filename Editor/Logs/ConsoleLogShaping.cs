using System;
using System.Collections.Generic;
using System.Linq;

namespace VeryFS.UnityMCP.Editor.Logs
{
    // Pure, Editor-free shaping logic for native console entries. Mirrors the
    // classification/filtering approach of CoplayDev unity-mcp's ReadConsole.cs
    // (see AGENTS.md "参考实现"), extracted so it can be unit tested without a
    // live Editor. EditorConsoleLogReader supplies the raw entries via reflection.
    internal static class ConsoleLogShaping
    {
        // Raw native LogEntry data, as read (oldest-first) from UnityEditor.LogEntries.
        internal struct RawEntry
        {
            public int Mode;
            public string Message;
            public string File;
            public int Line;
        }

        // LogEntry.mode bits (Unity 2022.3; may vary by version -- content inference
        // is preferred, these are the fallback).
        private const int ModeBitError = 1 << 0;
        private const int ModeBitAssert = 1 << 1;
        private const int ModeBitWarning = 1 << 2;
        private const int ModeBitException = 1 << 4;
        private const int ModeBitScriptingError = 1 << 9;
        private const int ModeBitScriptingWarning = 1 << 10;
        private const int ModeBitScriptingException = 1 << 18;
        private const int ModeBitScriptingAssertion = 1 << 22;

        // oldest-first raw in -> newest-first ConsoleLogRecord out, filtered and capped.
        public static List<ConsoleLogRecord> Shape(
            IReadOnlyList<RawEntry> raw,
            int maxEntries,
            string logTypeFilter,
            bool includeStackTrace,
            out bool truncated)
        {
            var matches = new List<ConsoleLogRecord>();
            if (raw != null)
            {
                foreach (var e in raw)
                {
                    if (string.IsNullOrEmpty(e.Message))
                    {
                        continue;
                    }
                    var type = ClassifyType(e.Mode, e.Message);
                    if (!MatchesFilter(type, logTypeFilter))
                    {
                        continue;
                    }
                    var (body, stack) = SplitMessageAndStackTrace(e.Message);
                    matches.Add(new ConsoleLogRecord
                    {
                        Type = type,
                        Message = body,
                        File = e.File ?? string.Empty,
                        Line = e.Line,
                        StackTrace = includeStackTrace ? stack : null
                    });
                }
            }

            // matches is oldest-first; keep the newest maxEntries, newest-first.
            truncated = matches.Count > maxEntries;
            var newestFirst = new List<ConsoleLogRecord>(Math.Min(maxEntries, matches.Count));
            for (int i = matches.Count - 1; i >= 0 && newestFirst.Count < maxEntries; i--)
            {
                newestFirst.Add(matches[i]);
            }
            return newestFirst;
        }

        // Severity classification: prefer message/stacktrace content (stable across
        // Unity versions), fall back to mode bits for native entries with no hint.
        public static string ClassifyType(int mode, string message)
        {
            var inferred = InferTypeFromMessage(message);
            if (inferred != "Log")
            {
                return inferred;
            }
            return GetTypeFromMode(mode);
        }

        // null/empty filter matches all. Case-insensitive. The "error" bucket mirrors
        // the Unity Console error toggle: it also includes Exception and Assert.
        public static bool MatchesFilter(string type, string logTypeFilter)
        {
            if (string.IsNullOrEmpty(logTypeFilter))
            {
                return true;
            }
            var filter = logTypeFilter.ToLowerInvariant();
            switch (type)
            {
                case "Exception":
                    return filter == "error" || filter == "exception";
                case "Assert":
                    return filter == "error" || filter == "assert";
                default:
                    return filter == type.ToLowerInvariant();
            }
        }

        public static (string body, string stackTrace) SplitMessageAndStackTrace(string fullMessage)
        {
            if (string.IsNullOrEmpty(fullMessage))
            {
                return (fullMessage, null);
            }
            var lines = fullMessage.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (lines.Length <= 1)
            {
                return (fullMessage, null);
            }
            int stackStart = FindStackStartIndex(lines);
            if (stackStart <= 0)
            {
                return (string.Join("\n", lines), null);
            }
            return (
                string.Join("\n", lines.Take(stackStart)),
                string.Join("\n", lines.Skip(stackStart)));
        }

        private static string InferTypeFromMessage(string fullMessage)
        {
            if (string.IsNullOrEmpty(fullMessage))
            {
                return "Log";
            }
            if (fullMessage.IndexOf("LogError", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Error";
            }
            if (fullMessage.IndexOf("LogWarning", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Warning";
            }
            if (fullMessage.IndexOf(" warning CS", StringComparison.OrdinalIgnoreCase) >= 0
                || fullMessage.IndexOf(": warning CS", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Warning";
            }
            if (fullMessage.IndexOf(" error CS", StringComparison.OrdinalIgnoreCase) >= 0
                || fullMessage.IndexOf(": error CS", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Error";
            }
            if (fullMessage.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Exception";
            }
            if (fullMessage.IndexOf("Assertion", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Assert";
            }
            return "Log";
        }

        private static string GetTypeFromMode(int mode)
        {
            if ((mode & (ModeBitException | ModeBitScriptingException)) != 0)
            {
                return "Exception";
            }
            if ((mode & (ModeBitError | ModeBitScriptingError)) != 0)
            {
                return "Error";
            }
            if ((mode & (ModeBitAssert | ModeBitScriptingAssertion)) != 0)
            {
                return "Assert";
            }
            if ((mode & (ModeBitWarning | ModeBitScriptingWarning)) != 0)
            {
                return "Warning";
            }
            return "Log";
        }

        private static int FindStackStartIndex(string[] lines)
        {
            for (int i = 1; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("at ")
                    || trimmed.StartsWith("UnityEngine.")
                    || trimmed.StartsWith("UnityEditor.")
                    || trimmed.Contains("(at ")
                    || (trimmed.Length > 0 && char.IsUpper(trimmed[0]) && trimmed.Contains('.')))
                {
                    return i;
                }
            }
            return -1;
        }
    }
}
