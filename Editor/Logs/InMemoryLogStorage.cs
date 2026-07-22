using System;
using System.Collections.Generic;
using System.Text;
using LitJson;
using VeryFS.UnityMCP.Editor.Infrastructure;

namespace VeryFS.UnityMCP.Editor.Logs
{
    public class InMemoryLogStorage : ILogStorage
    {
        private readonly object sync = new object();
        private readonly LinkedList<LogEntry> entries = new LinkedList<LogEntry>();
        private readonly int capacity;
        private readonly int byteBudget;

        public InMemoryLogStorage(IClock clock, int capacity = 2000, int byteBudget = 6 * 1024 * 1024)
        {
            this.capacity = capacity;
            this.byteBudget = byteBudget;
        }

        public virtual void Append(LogEntry entry)
        {
            lock (sync)
            {
                entries.AddLast(entry);
                while (entries.Count > capacity)
                {
                    entries.RemoveFirst();
                }
            }
        }

        public virtual void Clear()
        {
            lock (sync)
            {
                entries.Clear();
            }
        }

        public List<LogEntry> Query(int maxEntries, string logTypeFilter, bool includeStackTrace, int lastMinutes, DateTimeOffset now, out bool truncated)
        {
            var result = new List<LogEntry>();
            truncated = false;
            var cutoff = lastMinutes > 0 ? now.AddMinutes(-lastMinutes) : DateTimeOffset.MinValue;
            long budget = byteBudget;

            lock (sync)
            {
                for (var node = entries.Last; node != null; node = node.Previous)
                {
                    var e = node.Value;
                    if (logTypeFilter != null && e.Type != logTypeFilter)
                    {
                        continue;
                    }
                    if (lastMinutes > 0 && DateTimeOffset.Parse(e.TimestampUtc) < cutoff)
                    {
                        continue;
                    }
                    if (result.Count >= maxEntries)
                    {
                        truncated = true;
                        break;
                    }

                    var shaped = new LogEntry
                    {
                        TimestampUtc = e.TimestampUtc,
                        Type = e.Type,
                        Message = e.Message,
                        StackTrace = includeStackTrace ? e.StackTrace : null
                    };
                    var size = EstimateSize(shaped);
                    if (result.Count > 0 && budget - size < 0)
                    {
                        truncated = true;
                        break;
                    }
                    budget -= size;
                    result.Add(shaped);
                }
            }
            return result;
        }

        public virtual void Flush()
        {
        }

        protected List<LogEntry> Snapshot()
        {
            lock (sync)
            {
                return new List<LogEntry>(entries);
            }
        }

        protected void ReplaceAll(IEnumerable<LogEntry> loaded)
        {
            lock (sync)
            {
                entries.Clear();
                foreach (var e in loaded)
                {
                    entries.AddLast(e);
                    while (entries.Count > capacity)
                    {
                        entries.RemoveFirst();
                    }
                }
            }
        }

        private static long EstimateSize(LogEntry e)
        {
            long n = 64; // envelope overhead per entry
            n += Encoding.UTF8.GetByteCount(e.Message ?? string.Empty)
               + Encoding.UTF8.GetByteCount(e.StackTrace ?? string.Empty)
               + Encoding.UTF8.GetByteCount(e.Type ?? string.Empty)
               + Encoding.UTF8.GetByteCount(e.TimestampUtc ?? string.Empty);
            return n;
        }
    }
}
