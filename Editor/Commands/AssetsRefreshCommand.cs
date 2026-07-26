using System;
using System.Runtime.CompilerServices;
using LitJson;
using UnityEditor;
using VeryFS.UnityMCP.Editor.Compilation;
using VeryFS.UnityMCP.Editor.Infrastructure;
using VeryFS.UnityMCP.Editor.Persistence;
using VeryFS.UnityMCP.Editor.Protocol;

[assembly: InternalsVisibleTo("VeryFS.UnityMCP.Editor.Tests")]

namespace VeryFS.UnityMCP.Editor.Commands
{
    internal interface IAssetDatabase
    {
        void Refresh();
    }

    internal interface ICompilationTracker
    {
        void Configure(PendingRequestStore requestStore, IClock requestClock, ICurrentCompilerErrors requestCompilerErrors = null);

        void StartTracking(string originRequestId);

        void StopTracking(string originRequestId);

        void ScheduleCompletion();
    }

    public interface IEditorBusyState
    {
        bool IsCompiling { get; }

        bool IsUpdating { get; }
    }

    internal sealed class UnityAssetDatabase : IAssetDatabase
    {
        public void Refresh()
        {
            AssetDatabase.Refresh();
        }
    }

    internal sealed class UnityCompilationTracker : ICompilationTracker
    {
        public void Configure(PendingRequestStore requestStore, IClock requestClock, ICurrentCompilerErrors requestCompilerErrors = null)
        {
            CompilationTracker.Configure(requestStore, requestClock, requestCompilerErrors);
        }

        public void StartTracking(string originRequestId)
        {
            CompilationTracker.StartTracking(originRequestId);
        }

        public void StopTracking(string originRequestId)
        {
            CompilationTracker.StopTracking(originRequestId);
        }

        public void ScheduleCompletion()
        {
            CompilationTracker.ScheduleCompletion();
        }
    }

    internal sealed class AssetsRefreshCommand : ILongRunningCommand
    {
        private readonly IAssetDatabase assetDatabase;
        private readonly IEditorBusyState editorBusyState;
        private readonly PendingRequestStore store;
        private readonly IClock clock;
        private readonly ICompilationTracker compilationTracker;

        public AssetsRefreshCommand(
            IAssetDatabase assetDatabase,
            IEditorBusyState editorBusyState,
            PendingRequestStore store,
            IClock clock,
            ICompilationTracker compilationTracker = null)
        {
            this.assetDatabase = assetDatabase;
            this.editorBusyState = editorBusyState;
            this.store = store;
            this.clock = clock;
            this.compilationTracker = compilationTracker ?? new UnityCompilationTracker();
            ConfigureCompilationTracker();
        }

        public string Method => RpcMethods.AssetsRefresh;

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "assets-refresh",
            RpcMethod = RpcMethods.AssetsRefresh,
            Title = "Assets / Refresh",
            Description = "Trigger AssetDatabase.Refresh() with empty params and wait for the terminal compilation report.",
            Completion = "report",
            FailureMode = "data",
            DefaultTimeoutMs = 120000,
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"),
                ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object()))
        };

        public JsonData BuildReportParams(PendingRefreshRequest entry)
        {
            return RefreshResultBuilder.BuildReportParams(entry);
        }

        public void RecoverPending(PendingRefreshRequest entry)
        {
            if (entry == null || entry.State != "processing")
            {
                return;
            }

            if (entry.ExecutionState == "accepted")
            {
                FailAcceptedWithoutExecution(entry.OriginRequestId);
                return;
            }

            if (entry.State == "processing" &&
                (entry.ExecutionState == "refresh_started" || string.IsNullOrEmpty(entry.ExecutionState)))
            {
                // Fail requests that are older than the Go-side tool timeout (DefaultTimeoutMs).
                // This prevents a stale orphan (left over when a previous Unity session crashed
                // before reporting) from permanently blocking new assets-refresh calls.
                if (IsStaleRequest(entry))
                {
                    RefreshResultBuilder.MarkRefreshFailed(entry, clock.UtcNow.ToString("O"), "timeout");
                    store.Save(entry);
                    return;
                }

                ConfigureCompilationTracker();
                compilationTracker.StartTracking(entry.OriginRequestId);
                // Resume via the same idle-poll path used by ExecuteAccepted. We must NOT
                // build the terminal report immediately even when the editor looks idle:
                // a recovered refresh may have triggered no compilation at all (e.g. no
                // script change), and CompleteWhenIdle's two-stable-idle-checks gate is
                // what distinguishes "genuinely nothing to compile" from "compilation is
                // about to start". If compilation did run before the reload, the persisted
                // CompilationTriggered flag makes CompleteWhenIdle settle on the next tick.
                compilationTracker.ScheduleCompletion();
            }
        }

        private bool IsStaleRequest(PendingRefreshRequest entry)
        {
            if (string.IsNullOrEmpty(entry.StartedAt))
            {
                return false;
            }

            if (!DateTimeOffset.TryParse(entry.StartedAt, out var startedAt))
            {
                return false;
            }

            var elapsedMs = (clock.UtcNow - startedAt).TotalMilliseconds;
            return elapsedMs > Descriptor.DefaultTimeoutMs;
        }

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            if (!HasEmptyObjectParams(request.Params))
            {
                return JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                    JsonRpcErrorCodes.InvalidParams,
                    "assets.refresh requires empty object params.",
                    JsonRpcSerializer.Object(("errorCode", "invalid_params"))));
            }

            var currentActiveRequestId = ActiveRequestId();
            if (editorBusyState.IsCompiling || editorBusyState.IsUpdating || !string.IsNullOrEmpty(currentActiveRequestId))
            {
                var data = JsonRpcSerializer.Object(("errorCode", "editor_busy"));
                if (!string.IsNullOrEmpty(currentActiveRequestId))
                {
                    data["activeRequestId"] = currentActiveRequestId;
                }

                return JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                    JsonRpcErrorCodes.EditorBusy,
                    "Editor is busy with an active assets.refresh request.",
                    data));
            }

            store.Save(PendingRefreshRequest.Start(request.Id, clock.UtcNow.ToString("O")));

            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                ("state", "processing"),
                ("requestId", request.Id)));
        }

        public void ExecuteAccepted(string requestId)
        {
            if (!IsProcessing(requestId))
            {
                return;
            }

            try
            {
                var request = FindRequest(requestId);
                if (request == null || request.State != "processing")
                {
                    return;
                }

                request.ExecutionState = "refresh_started";
                store.Save(request);
                ConfigureCompilationTracker();
                compilationTracker.StartTracking(requestId);
                compilationTracker.ScheduleCompletion();
                assetDatabase.Refresh();
            }
            catch
            {
                var request = FindRequest(requestId);
                if (request == null || request.State != "processing")
                {
                    throw;
                }

                RefreshResultBuilder.MarkRefreshFailed(request, clock.UtcNow.ToString("O"));
                store.Save(request);
                compilationTracker.StopTracking(requestId);
            }
        }

        public void FailAcceptedWithoutExecution(string requestId)
        {
            var request = FindRequest(requestId);
            if (request == null || request.State != "processing" || request.ExecutionState != "accepted")
            {
                return;
            }

            RefreshResultBuilder.MarkRefreshFailed(request, clock.UtcNow.ToString("O"), "refresh_not_executed");
            store.Save(request);
            ConfigureCompilationTracker();
            compilationTracker.StopTracking(requestId);
        }

        private void ConfigureCompilationTracker()
        {
            compilationTracker.Configure(store, clock, new ConsoleCompilerErrors());
        }

        private static bool HasEmptyObjectParams(JsonData @params)
        {
            return @params != null && @params.IsObject && @params.Count == 0;
        }

        private string ActiveRequestId()
        {
            foreach (var request in store.LoadAll())
            {
                if (request.State == "processing")
                {
                    return request.OriginRequestId;
                }
            }

            return null;
        }

        private bool IsProcessing(string requestId)
        {
            var request = FindRequest(requestId);
            return request != null && request.State == "processing";
        }

        private PendingRefreshRequest FindRequest(string requestId)
        {
            foreach (var request in store.LoadAll())
            {
                if (request.OriginRequestId == requestId)
                {
                    return request;
                }
            }

            return null;
        }
    }
}
