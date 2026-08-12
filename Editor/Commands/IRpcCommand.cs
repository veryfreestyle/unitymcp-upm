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
    //
    // 内部 await 必须是 UniTask, 不许裸 await Task。UniTask 的续体由 PlayerLoop / 编辑态的
    // EditorApplication.update 驱动, 是原生 pump; 裸 Task 的续体交给 await 那一刻的
    // SynchronizationContext.Current, 而宿主项目可以把主线程的 Current 换成自己的实现
    // (框架式 async 库常这么做)。换掉之后 Unity 的 UnitySynchronizationContext.ExecuteTasks
    // 会因为 Current 不再是它而直接返回, 队列里的续体在本 domain 内永不执行 —— 命令就静默挂死,
    // 且只在那类宿主项目里复现。transport 层为此把两个循环搬到了线程池, 命令层靠这条约束。
    public interface IAsyncRpcCommand : IRpcCommand
    {
        UniTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request);
    }
}
