using System;
using VeryFS.UnityMCP.Editor.Logs;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Console
{
    public sealed class ConsoleClearLogsCommand : IGroupedCommand
    {
        private readonly ILogStorage storage;
        private readonly IEditorBusyState busy;
        private readonly Action clearConsole;

        public ConsoleClearLogsCommand(ILogStorage storage, IEditorBusyState busy, Action clearConsole)
        {
            this.storage = storage;
            this.busy = busy;
            this.clearConsole = clearConsole;
        }

        public string Method => RpcMethods.ConsoleClearLogs;
        public string Group => "console";
        public string Action => "clear-logs";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "console-clear-logs",
            RpcMethod = RpcMethods.ConsoleClearLogs,
            Title = "Console / Clear Logs",
            Description = "Clear the collected console logs.",
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
            storage.Clear();
            try
            {
                clearConsole?.Invoke();
            }
            catch
            {
                // Best-effort native console clear.
            }
            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(("cleared", true)));
        }
    }
}
