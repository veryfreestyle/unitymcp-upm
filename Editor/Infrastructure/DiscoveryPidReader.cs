using System;
using System.IO;
using LitJson;

namespace VeryFS.UnityMCP.Editor.Infrastructure
{
    /// <summary>Reads server.pid from the project's UnityMCP.json discovery file.
    /// Fallback pid source for the kill button when the in-memory holder is empty.
    /// Returns 0 on any problem (missing file/field, parse error).</summary>
    public static class DiscoveryPidReader
    {
        public static int ReadServerPid(string projectRoot)
        {
            try
            {
                var path = Path.Combine(projectRoot, DiscoveryFileWriter.FileName);
                if (!File.Exists(path))
                {
                    return 0;
                }

                var root = JsonMapper.ToObject(File.ReadAllText(path));
                if (!root.ContainsKey("server")) { return 0; }
                var server = root["server"];
                if (server == null || !server.ContainsKey("pid") || !server["pid"].IsInt)
                {
                    return 0;
                }

                return (int)server["pid"];
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
