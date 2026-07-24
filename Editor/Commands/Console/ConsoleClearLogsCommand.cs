using VeryFS.UnityMCP.Editor.Logs;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Console
{
    public sealed class ConsoleClearLogsCommand : IGroupedCommand
    {
        private readonly IConsoleLogReader reader;
        private readonly IEditorBusyState busy;

        public ConsoleClearLogsCommand(IConsoleLogReader reader, IEditorBusyState busy)
        {
            this.reader = reader;
            this.busy = busy;
        }

        public string Method => RpcMethods.ConsoleClearLogs;
        public string Group => "console";
        public string Action => "clear-logs";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "console-clear-logs",
            RpcMethod = RpcMethods.ConsoleClearLogs,
            Title = "Console / Clear Logs",
            Description = "Clear the Unity Console (native LogEntries buffer).",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(("type", "object"), ("additionalProperties", false))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            if (busy.IsCompiling || busy.IsUpdating)
            {
                return JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                    JsonRpcErrorCodes.EditorBusy, "Editor is busy.",
                    JsonRpcSerializer.Object(("errorCode", "editor_busy"))));
            }
            bool cleared = reader.Clear();
            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(("cleared", cleared)));
        }
    }
}
