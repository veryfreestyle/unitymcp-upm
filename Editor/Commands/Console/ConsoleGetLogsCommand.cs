using LitJson;
using VeryFS.UnityMCP.Editor.Infrastructure;
using VeryFS.UnityMCP.Editor.Logs;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Console
{
    public sealed class ConsoleGetLogsCommand : IGroupedCommand
    {
        private readonly ILogStorage storage;
        private readonly IClock clock;

        public ConsoleGetLogsCommand(ILogStorage storage, IClock clock)
        {
            this.storage = storage;
            this.clock = clock;
        }

        public string Method => RpcMethods.ConsoleGetLogs;
        public string Group => "console";
        public string Action => "get-logs";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "console-get-logs",
            RpcMethod = RpcMethods.ConsoleGetLogs,
            Title = "Console / Get Logs",
            Description = "Return collected Unity console logs, newest first, with type/time/count filters.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"),
                ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("maxEntries", JsonRpcSerializer.Object(("type", "integer"), ("minimum", 1), ("maximum", 500))),
                    ("logTypeFilter", JsonRpcSerializer.Object(("type", "string"))),
                    ("includeStackTrace", JsonRpcSerializer.Object(("type", "boolean"))),
                    ("lastMinutes", JsonRpcSerializer.Object(("type", "integer"), ("minimum", 0)))))),
            Annotations = JsonRpcSerializer.Object(("readOnlyHint", true))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            int maxEntries = ReadInt(request.Params, "maxEntries", 100);
            int lastMinutes = ReadInt(request.Params, "lastMinutes", 0);
            bool includeStackTrace = ReadBool(request.Params, "includeStackTrace");
            string logTypeFilter = ReadString(request.Params, "logTypeFilter");

            if (maxEntries < 1 || maxEntries > 500 || lastMinutes < 0)
            {
                return JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                    JsonRpcErrorCodes.InvalidParams, "invalid params",
                    JsonRpcSerializer.Object(("errorCode", "invalid_params"))));
            }

            var logs = storage.Query(maxEntries, logTypeFilter, includeStackTrace, lastMinutes, clock.UtcNow, out var truncated);
            var entries = new JsonData();
            entries.SetJsonType(JsonType.Array);
            foreach (var e in logs)
            {
                var obj = JsonRpcSerializer.Object(
                    ("timestampUtc", e.TimestampUtc), ("type", e.Type), ("message", e.Message));
                if (includeStackTrace)
                {
                    obj["stackTrace"] = e.StackTrace ?? string.Empty;
                }
                entries.Add(obj);
            }

            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                ("entries", entries), ("returnedCount", logs.Count), ("truncated", truncated)));
        }

        private static int ReadInt(JsonData p, string key, int fallback)
        {
            if (p != null && p.IsObject && p.ContainsKey(key) && p[key].IsInt)
            {
                return (int)p[key];
            }
            return fallback;
        }

        private static bool ReadBool(JsonData p, string key)
        {
            return p != null && p.IsObject && p.ContainsKey(key) && p[key].IsBoolean && (bool)p[key];
        }

        private static string ReadString(JsonData p, string key)
        {
            return p != null && p.IsObject && p.ContainsKey(key) && p[key].IsString ? (string)p[key] : null;
        }
    }
}
