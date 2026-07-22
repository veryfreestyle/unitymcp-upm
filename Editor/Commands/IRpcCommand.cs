using Cysharp.Threading.Tasks;
using LitJson;
using VeryFS.UnityMCP.Editor.Persistence;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands
{
    // A tool command dispatched by rpcMethod. Handle runs on the Editor main
    // thread and returns either a terminal response (completion=response) or a
    // processing ack (completion=report).
    public interface IRpcCommand
    {
        string Method { get; }
        RpcToolDescriptor Descriptor { get; }
        JsonRpcResponse Handle(JsonRpcRequest request);
    }

    // A report-type command: after its ack is sent, ExecuteAccepted runs the
    // real work; the terminal outcome persists to the store and is reported by
    // the report loop. RecoverPending re-attaches after a domain reload.
    public interface ILongRunningCommand : IRpcCommand
    {
        void ExecuteAccepted(string requestId);
        void RecoverPending(PendingRefreshRequest entry);
        JsonData BuildReportParams(PendingRefreshRequest entry);
    }

    // 异步命令: 主线程执行, 内部可 await UniTask 跨帧 (B 层真实输入手势)。
    // 返回终响应 (completion=response 语义)。不入 store, 不跨 domain reload。
    public interface IAsyncRpcCommand : IRpcCommand
    {
        UniTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request);
    }
}
