using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LitJson;
using VeryFS.UnityMCP.Editor.Compilation;

namespace VeryFS.UnityMCP.Editor.Persistence
{
    public sealed class PendingRequestStore
    {
        private readonly string root;

        public PendingRequestStore(string root)
        {
            this.root = root;
        }

        public void Save(PendingRefreshRequest request)
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(PathFor(request.OriginRequestId), JsonMapper.ToJson(request));
        }

        public List<PendingRefreshRequest> LoadAll()
        {
            var requests = new List<PendingRefreshRequest>();
            if (!Directory.Exists(root))
            {
                return requests;
            }

            var paths = Directory.GetFiles(root, "*.json");
            Array.Sort(paths, StringComparer.Ordinal);
            foreach (var path in paths)
            {
                requests.Add(JsonMapper.ToObject<PendingRefreshRequest>(File.ReadAllText(path)));
            }

            return requests;
        }

        public void Delete(string originRequestId)
        {
            var path = PathFor(originRequestId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        public void AppendCompilerMessages(string originRequestId, IEnumerable<CompilerMessage> messages)
        {
            var path = PathFor(originRequestId);
            if (!File.Exists(path))
            {
                return;
            }

            var request = JsonMapper.ToObject<PendingRefreshRequest>(File.ReadAllText(path));
            foreach (var message in messages)
            {
                if (message.IsError)
                {
                    request.CompilerErrors.Add(message);
                }
                else
                {
                    request.CompilerWarnings.Add(message);
                }
            }

            Save(request);
        }

        private string PathFor(string originRequestId)
        {
            return Path.Combine(root, FileKeyFor(originRequestId) + ".json");
        }

        private static string FileKeyFor(string value)
        {
            using (var sha256 = SHA256.Create())
            {
                return Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(value)))
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_');
            }
        }
    }
}
