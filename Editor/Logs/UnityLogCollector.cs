using System;
using System.Text.RegularExpressions;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Infrastructure;

namespace VeryFS.UnityMCP.Editor.Logs
{
    public sealed class UnityLogCollector : IDisposable
    {
        private static readonly Regex RichText = new Regex(
            @"</?(b|i|size|color|material|quad|a)\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly ILogStorage storage;
        private readonly IClock clock;
        private bool disposed;

        public UnityLogCollector(ILogStorage storage, IClock clock)
        {
            this.storage = storage;
            this.clock = clock;
            Application.logMessageReceivedThreaded += OnLog;
        }

        public static string StripRichText(string message)
        {
            return message == null ? null : RichText.Replace(message, string.Empty);
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            try
            {
                storage.Append(new LogEntry
                {
                    TimestampUtc = clock.UtcNow.ToString("O"),
                    Type = type.ToString(),
                    Message = StripRichText(condition),
                    StackTrace = stackTrace
                });
            }
            catch
            {
                // Never let logging recurse or throw on the logging thread.
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            Application.logMessageReceivedThreaded -= OnLog;
        }
    }
}
