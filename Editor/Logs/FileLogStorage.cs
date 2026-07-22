using System.Collections.Generic;
using System.IO;
using LitJson;
using VeryFS.UnityMCP.Editor.Infrastructure;

namespace VeryFS.UnityMCP.Editor.Logs
{
    public sealed class FileLogStorage : InMemoryLogStorage
    {
        private readonly string path;
        private bool dirty;

        public FileLogStorage(string path, IClock clock) : base(clock)
        {
            this.path = path;
            Load();
        }

        public override void Append(LogEntry entry)
        {
            base.Append(entry);
            dirty = true;
        }

        public override void Clear()
        {
            base.Clear();
            dirty = true;
        }

        public override void Flush()
        {
            if (!dirty)
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonMapper.ToJson(Snapshot()));
            dirty = false;
        }

        private void Load()
        {
            if (!File.Exists(path))
            {
                return;
            }
            try
            {
                var loaded = JsonMapper.ToObject<List<LogEntry>>(File.ReadAllText(path));
                if (loaded != null)
                {
                    ReplaceAll(loaded);
                }
            }
            catch
            {
                // Corrupt cache is non-fatal: start empty.
            }
        }
    }
}
