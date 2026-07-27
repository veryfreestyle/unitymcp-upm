using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using VeryFS.UnityMCP.Editor.Commands.Editor;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Testing
{
    // test-list: 零入参, 枚举全部测试程序集 (name + testMode), 直接喂 test-run.assemblyNames。
    // 独立工具, 不进聚合组 (spec §3.1)。异步: RetrieveTestList 回调跨帧。
    public sealed class TestListCommand : IAsyncRpcCommand
    {
        private readonly ITestListProvider provider;
        private readonly IEditorBusyState busy;
        private readonly IPlayModeController playMode;

        public TestListCommand(ITestListProvider provider, IEditorBusyState busy, IPlayModeController playMode)
        {
            this.provider = provider;
            this.busy = busy;
            this.playMode = playMode;
        }

        public string Method => RpcMethods.TestList;

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "test-list",
            RpcMethod = RpcMethods.TestList,
            Title = "Test / List",
            Description =
                "List every test assembly the Unity Test Runner sees, each with its testMode " +
                "(EditMode / PlayMode). Feed a returned name into test-run.assemblyNames and its " +
                "testMode into test-run.testMode. Running tests is a separate tool, test-run. " +
                "Refused when the project has compilation errors or the editor is compiling or importing.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"),
                ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object())),
            Annotations = JsonRpcSerializer.Object(("readOnlyHint", true))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
            => JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                JsonRpcErrorCodes.InternalError, "async command; use HandleAsync",
                JsonRpcSerializer.Object(("errorCode", "async_command"))));

        public async UniTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request)
        {
            // 前置检查顺序与 TestRunCommand 一致: 先编译失败, 再 editor 忙。
            if (playMode.ScriptCompilationFailed)
            {
                return Error(request.Id, JsonRpcErrorCodes.CompilationFailed,
                    playMode.CompilationErrorDetails, "compilation_failed");
            }

            if (busy.IsCompiling || busy.IsUpdating)
            {
                return Error(request.Id, JsonRpcErrorCodes.EditorBusy,
                    "Editor is busy compiling or importing assets.", "editor_busy");
            }

            IReadOnlyList<TestAssemblyInfo> assemblies;
            try
            {
                assemblies = await provider.ListAssembliesAsync();
            }
            catch (TestListTimeoutException)
            {
                return Error(request.Id, JsonRpcErrorCodes.RequestTimeout,
                    "Timed out retrieving the test list.", "test_list_timeout");
            }

            return JsonRpcResponse.FromSuccess(request.Id, TestListPayload.BuildResponse(assemblies));
        }

        private static JsonRpcResponse Error(string id, int code, string message, string errorCode)
            => JsonRpcResponse.FromError(id, new JsonRpcError(
                code, message ?? string.Empty,
                JsonRpcSerializer.Object(("errorCode", errorCode))));
    }
}
