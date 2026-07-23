using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;

namespace VeryFS.UnityMCP.Editor.Compilation
{
    // Reads the project's CURRENT compile-error state directly from the Editor,
    // without triggering a recompile. A refresh that itself compiles nothing (no
    // script changed) would otherwise report a misleading "succeeded" even when
    // the project is already broken; this lets the terminal report surface those
    // pre-existing errors instead.
    internal interface ICurrentCompilerErrors
    {
        bool ProjectHasCompileErrors();

        List<CompilerMessage> ReadCompileErrors();
    }

    // Reflection-based reader over Unity's internal console API. Every reflective
    // access is guarded: on any failure it degrades to "no errors known" so a
    // refresh never crashes -- worst case it falls back to the prior behaviour.
    internal sealed class ConsoleCompilerErrors : ICurrentCompilerErrors
    {
        // Console entry mode bit that flags a script compile error (Unity 2022.3).
        private const int ScriptCompileErrorMode = 1 << 11;

        private const BindingFlags StaticFlags =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private const BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Regex CompileErrorMessage =
            new Regex(@"\(\d+,\d+\):\s*error\s+CS\d+", RegexOptions.Compiled);

        public bool ProjectHasCompileErrors()
        {
            try
            {
                var prop = typeof(EditorUtility).GetProperty("scriptCompilationFailed", StaticFlags);
                return prop != null && prop.GetValue(null) is bool failed && failed;
            }
            catch
            {
                return false;
            }
        }

        public List<CompilerMessage> ReadCompileErrors()
        {
            var result = new List<CompilerMessage>();
            try
            {
                var logEntries = Type.GetType("UnityEditor.LogEntries,UnityEditor");
                var logEntry = Type.GetType("UnityEditor.LogEntry,UnityEditor");
                if (logEntries == null || logEntry == null)
                {
                    return result;
                }

                var start = logEntries.GetMethod("StartGettingEntries", StaticFlags);
                var end = logEntries.GetMethod("EndGettingEntries", StaticFlags);
                var getEntry = logEntries.GetMethod("GetEntryInternal", StaticFlags);
                var fMessage = logEntry.GetField("message", InstanceFlags);
                var fFile = logEntry.GetField("file", InstanceFlags);
                var fLine = logEntry.GetField("line", InstanceFlags);
                var fMode = logEntry.GetField("mode", InstanceFlags);
                if (start == null || end == null || getEntry == null || fMessage == null || fMode == null)
                {
                    return result;
                }

                var seen = new HashSet<string>();
                var total = (int)start.Invoke(null, null);
                try
                {
                    var entry = Activator.CreateInstance(logEntry);
                    for (var i = 0; i < total; i++)
                    {
                        getEntry.Invoke(null, new object[] { i, entry });
                        var message = fMessage.GetValue(entry) as string ?? string.Empty;
                        var mode = fMode.GetValue(entry) is int m ? m : 0;
                        if ((mode & ScriptCompileErrorMode) == 0 && !CompileErrorMessage.IsMatch(message))
                        {
                            continue;
                        }

                        var file = fFile?.GetValue(entry) as string ?? string.Empty;
                        var line = fLine != null && fLine.GetValue(entry) is int l ? l : 0;
                        if (seen.Add(file + "|" + line + "|" + message))
                        {
                            result.Add(new CompilerMessage(string.Empty, file, line, 0, message, true));
                        }
                    }
                }
                finally
                {
                    end.Invoke(null, null);
                }
            }
            catch
            {
                result.Clear();
            }

            return result;
        }
    }
}
