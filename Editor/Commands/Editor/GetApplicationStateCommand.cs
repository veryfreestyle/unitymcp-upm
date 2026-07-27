using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Editor
{
    public sealed class GetApplicationStateCommand : IRpcCommand
    {
        private readonly IEditorStateProvider provider;

        public GetApplicationStateCommand(IEditorStateProvider provider)
        {
            this.provider = provider;
        }

        public string Method => RpcMethods.EditorApplicationGetState;

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "editor-get-state",
            RpcMethod = RpcMethods.EditorApplicationGetState,
            Title = "Editor / Application / Get State",
            Description = "Return EditorApplication state: playmode, paused, compilation, and related flags.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(("type", "object"), ("additionalProperties", false)),
            Annotations = JsonRpcSerializer.Object(("readOnlyHint", true), ("idempotentHint", true))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            return JsonRpcResponse.FromSuccess(request.Id, EditorStateData.ToJson(provider));
        }
    }
}
