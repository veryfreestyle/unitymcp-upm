using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using LitJson;
using VeryFS.UnityMCP.Editor.Commands.Editor;
using VeryFS.UnityMCP.Editor.Commands.Scene;
using VeryFS.UnityMCP.Editor.Infrastructure;
using VeryFS.UnityMCP.Editor.Persistence;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Testing
{
    // test.run: 跑 Unity Test Runner, 走 ack + report 推模型。
    // 不自动 assets.refresh —— Unity 无 API 能检测"改了但未导入的 .cs",
    // 所以只拒能检测的三态, 由调用方负责先 refresh。
    public sealed class TestRunCommand : ILongRunningCommand
    {
        public const int InitTimeoutMs = 30000;
        public const int StuckThresholdMs = 60000;
        public const int DefaultTimeoutMs = 300000;
        // schema 边界。Go 侧 mcpbridge 用同一个区间, 但它是"越界即拒" (invalid_params),
        // 不是夹紧 —— 所以经 Go 转发过来的值一定在区间内, 而直连 Unity 的调用方 (或旧版本
        // 写下的 pending 记录) 仍可能越界, 本命令自己再夹一次。
        public const int MinTimeoutMs = 1000;
        public const int MaxTimeoutMs = 600000;

        private readonly ITestRunner runner;
        private readonly IEditorBusyState busy;
        private readonly IPlayModeController playMode;
        private readonly ISceneGateway scenes;
        private readonly TestRunTracker tracker;
        private readonly PendingRequestStore store;
        private readonly IClock clock;

        // 当前在飞的 requestId。只用于 Tick 的 init 超时检查, 不需要跨 domain reload
        // —— reload 后由 RecoverPending 接手。
        private string inFlightRequestId;

        // 本次运行的墙钟死线 (entry.StartedAt + timeoutMs)。timeoutMs 本身持久化在
        // entry.TargetState 里, 这里只缓存算好的时刻: Tick 每帧都跑, 而 Find() 要读磁盘,
        // 逐帧 LoadAll 太贵。缓存与 inFlightRequestId 同寿命, reload 后一起失效, 那之后
        // 由 RecoverPending 判中断, 不再需要这个死线。
        private DateTimeOffset? inFlightDeadline;

        // 这条在飞请求是否已被墙钟天花板判死。判死只终结调用方那一侧, 槽位仍留给运行本身:
        // TestRunnerApi 没有 Cancel API, 框架的运行还在跑, 闸门要等它真的停了才能放
        // (见 Abandon / ReleaseIfRunStopped)。
        private bool inFlightAbandoned;

        public TestRunCommand(
            ITestRunner runner,
            IEditorBusyState busy,
            IPlayModeController playMode,
            ISceneGateway scenes,
            TestRunTracker tracker,
            PendingRequestStore store,
            IClock clock)
        {
            this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
            this.busy = busy ?? throw new ArgumentNullException(nameof(busy));
            this.playMode = playMode ?? throw new ArgumentNullException(nameof(playMode));
            this.scenes = scenes ?? throw new ArgumentNullException(nameof(scenes));
            this.tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public string Method => RpcMethods.TestRun;

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "test-run",
            RpcMethod = RpcMethods.TestRun,
            Title = "Test / Run",
            Description =
                "Run Unity Test Runner tests for the given assemblies and wait for the terminal result. " +
                "Call assets-refresh first so tests run against the latest compiled assemblies. " +
                "Refused when the project has compilation errors, when the editor is compiling or importing, " +
                "when any loaded scene has unsaved changes, or when already in play mode. " +
                "While a run is in progress every tool except test-status, console (get-logs / clear-logs) and " +
                "screenshot-game-view returns editor_busy; the transport's own unity.heartbeat and " +
                "requests.report stay open so the run can report back. " +
                "timeoutMs is a wall-clock ceiling on the whole call: exceeding it answers with errorCode " +
                "request_timeout. Unity cannot cancel a running test run, so the tests keep going and other " +
                "tools stay refused with tests_running until the run actually stops; poll test-status. " +
                "Returns a summary plus every failing test; pass includeDetails to also get passing tests.",
            Completion = "report",
            FailureMode = "error",
            DefaultTimeoutMs = DefaultTimeoutMs,
            InputSchema = BuildInputSchema(),
            Annotations = JsonRpcSerializer.Object(("readOnlyHint", false))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            string[] assemblyNames = ReadStringArray(request.Params, "assemblyNames");
            if (assemblyNames == null || assemblyNames.Length == 0)
            {
                return Invalid(request.Id, "test.run requires a non-empty assemblyNames array.", null);
            }

            foreach (var assembly in assemblyNames)
            {
                if (IsExclusion(assembly))
                {
                    return Invalid(
                        request.Id, "assemblyNames does not support ! exclusion entries.", assembly);
                }
            }

            string testMode = ReadString(request.Params, "testMode") ?? "EditMode";
            if (testMode != "EditMode" && testMode != "PlayMode")
            {
                return Invalid(request.Id, "testMode must be EditMode or PlayMode.", null);
            }

            string[] groupNames = ReadStringArray(request.Params, "groupNames");
            if (groupNames != null)
            {
                foreach (var pattern in groupNames)
                {
                    if (IsExclusion(pattern))
                    {
                        return Invalid(
                            request.Id, "groupNames does not support ! exclusion entries.", pattern);
                    }

                    if (!IsValidRegex(pattern))
                    {
                        return Invalid(request.Id, "groupNames contains an invalid regular expression.", pattern);
                    }
                }
            }

            if (playMode.ScriptCompilationFailed)
            {
                return JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                    JsonRpcErrorCodes.CompilationFailed,
                    playMode.CompilationErrorDetails,
                    JsonRpcSerializer.Object(("errorCode", "compilation_failed"))));
            }

            if (busy.IsCompiling || busy.IsUpdating)
            {
                return JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                    JsonRpcErrorCodes.EditorBusy,
                    "Editor is busy compiling or importing assets.",
                    JsonRpcSerializer.Object(("errorCode", "editor_busy"))));
            }

            if (tracker.IsRunning)
            {
                return JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                    JsonRpcErrorCodes.EditorBusy,
                    "A Unity test run is already in progress.",
                    JsonRpcSerializer.Object(("errorCode", "tests_running"))));
            }

            var dirty = scenes.GetDirtyLoadedScenes();
            if (dirty != null && dirty.Count > 0)
            {
                // 必须在启动前拦: Test Runner 的 SaveModifiedSceneTask 会弹模态框,
                // 模态框阻塞 Editor 主线程 = 整个 MCP 挂住。
                var list = new JsonData();
                list.SetJsonType(JsonType.Array);
                foreach (var scene in dirty)
                {
                    list.Add(JsonRpcSerializer.Object(("name", scene.Name), ("path", scene.Path)));
                }

                var data = JsonRpcSerializer.Object(("errorCode", "unsaved_scenes"));
                data["scenes"] = list;
                return JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                    JsonRpcErrorCodes.InvalidEditorState,
                    "Save or discard scene changes before running tests.",
                    data));
            }

            // isPlayingOrWillChangePlaymode 必须一起判: editor-application-set-state 是长任务,
            // 它 ack 之后 play mode 转换还在路上, 此刻 IsPlaying 仍是 false 而 gate 也还没立起来
            // —— 只看 IsPlaying 的话这段窗口里的 test.run 会溜进去, 一边进 play mode 一边跑测试。
            if (playMode.IsPlaying || playMode.IsPlayingOrWillChangePlaymode)
            {
                return JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                    JsonRpcErrorCodes.InvalidEditorState,
                    "Cannot start a test run while in play mode.",
                    JsonRpcSerializer.Object(("errorCode", "invalid_editor_state"))));
            }

            var target = JsonRpcSerializer.Object(
                ("testMode", testMode),
                ("includeDetails", ReadBool(request.Params, "includeDetails")),
                // timeoutMs 跟着 pending 记录一起持久化 —— Tick 靠它给这次调用加墙钟天花板,
                // 没有天花板时"终态回调永不投递"(比如用户手工退出 play mode) 会让这条记录
                // 永远卡在 processing, 调用方等到自己超时也拿不到任何答复。
                ("timeoutMs", ReadTimeoutMs(request.Params)));
            target["assemblyNames"] = ToArray(assemblyNames);
            target["groupNames"] = ToArray(groupNames ?? new string[0]);

            store.Save(PendingRefreshRequest.StartLongRunning(
                request.Id, Method, clock.UtcNow.ToString("O"), JsonMapper.ToJson(target)));

            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                ("state", "processing"),
                ("requestId", request.Id)));
        }

        public void ExecuteAccepted(string requestId)
        {
            var entry = Find(requestId);
            if (entry == null || entry.State != "processing")
            {
                return;
            }

            // 并发 test.run: transport 是 fire-and-forget 派发 acceptance, 两个请求都可能在
            // tracker.IsRunning 变 true 之前就通过 Handle 的前置检查。这里必须抢先判重 ——
            // 否则第二个请求会直接顶掉 inFlightRequestId, 第一个请求的记录再没人碰得到, 永远卡在
            // processing。判重命中时只终结这条新请求自己, 不碰前一个请求的状态/tracker。
            if (!string.IsNullOrEmpty(inFlightRequestId))
            {
                // 同一个 requestId 重发到这里 (比如 transport 超时重试) 说明这次运行本来就已经
                // 在飞 —— 忽略即可, 不能顶掉/重跑自己; 只有确实是另一个请求才算真并发。
                if (inFlightRequestId != requestId)
                {
                    RejectConcurrent(requestId);
                }

                return;
            }

            if (tracker.IsRunning)
            {
                RejectConcurrent(requestId);
                return;
            }

            var target = JsonMapper.ToObject(entry.TargetState);
            var filter = new TestRunFilter
            {
                TestMode = (string)target["testMode"],
                AssemblyNames = ReadArray(target, "assemblyNames"),
                GroupNames = ReadArray(target, "groupNames")
            };
            bool includeDetails = target.ContainsKey("includeDetails") && (bool)target["includeDetails"];

            entry.ExecutionState = "counting";
            store.Save(entry);
            inFlightRequestId = requestId;
            // 死线按 ack 时刻 (entry.StartedAt) 起算, 不是这里的 now —— 调用方等待的是整个
            // 调用, 排队等派发的那段时间也算在它给的上限里。
            inFlightDeadline = DeadlineOf(entry, target);
            // 计数本身也走 TestRunnerApi 的异步回调, 同样可能因 domain reload 等原因卡住不回调
            // —— 从这里就开始计入 init 超时窗口, 而不是等到 matched>0 真正起跑才计时。
            tracker.MarkStarted(filter.TestMode);

            runner.CountMatching(filter, matched =>
            {
                if (matched > 0)
                {
                    StartRun(requestId, filter, includeDetails);
                    return;
                }

                // 防假绿: NUnit 在零匹配时照样报 Passed, 透传就是假绿。
                Fail(requestId, "no_tests_matched");
            });
        }

        // 生产由 EditorApplication.update 驱动; 单测手工调。
        public void Tick()
        {
            if (!tracker.IsRunning)
            {
                return;
            }

            // 运行标志立着, 但这次运行已经没有"等终态回调"这回事了 —— 两种来路:
            // (a) 墙钟天花板已经把调用方那条请求判死 (inFlightAbandoned);
            // (b) domain reload 把本对象的 in-flight 槽位冲掉了, 回调随旧对象一起没了。
            // 两种情形下闸门什么时候能放, 只能看框架的运行是不是真的停了。
            if (inFlightAbandoned || string.IsNullOrEmpty(inFlightRequestId))
            {
                ReleaseIfRunStopped();
                return;
            }

            // 墙钟天花板先判, 而且不看 RunObserved 也不豁免 compiling/importing: 进度回调
            // 来过一次就熔断了 init 超时那根保险丝, 之后 TestRunnerApi 若再不投递终态
            // (没有 Cancel API, 用户手工退出 play mode 就是这种情形), 只剩这一道能给出终态。
            // timeoutMs 是调用方自己给的等待上限, 拖过了就该收尾而不是继续无限等。
            if (inFlightDeadline.HasValue && clock.UtcNow > inFlightDeadline.Value)
            {
                Abandon(inFlightRequestId);
                return;
            }

            if (tracker.RunObserved)
            {
                return;
            }

            if (busy.IsCompiling || busy.IsUpdating)
            {
                return;
            }

            var startedAt = tracker.StartedAt;
            if (!startedAt.HasValue ||
                (clock.UtcNow - startedAt.Value).TotalMilliseconds <= InitTimeoutMs)
            {
                return;
            }

            Fail(inFlightRequestId, "test_init_timeout");
        }

        public void RecoverPending(PendingRefreshRequest entry)
        {
            if (entry == null || entry.State != "processing")
            {
                return;
            }

            // RecoverPending 不只在 domain reload 后被调 —— transport 每次重连都会遍历
            // 未终结的记录调它一遍 (RpcConnectionLoop.RecoverPendingRequestsAsync)。跑一次
            // 全量测试要一分钟, 期间掉线重连很常见, 那时本对象和 ICallbacks 都还活着,
            // 运行也还在跑, 判中断等于自己把好端端的运行打死。
            // 只有本对象已经不认这条请求 (in-flight 槽位为空或换了人) 才是真丢了回调 ——
            // reload 后新对象的槽位必然是空的, 正是这个判据。
            if (inFlightRequestId == entry.OriginRequestId)
            {
                return;
            }

            // ICallbacks 注册在已销毁的 C# 对象上, 恢复后拿不回结果 —— 直接判中断,
            // 让调用方重跑, 而不是留一条永远不终结的记录堵住后续调用。
            Fail(entry.OriginRequestId, "test_run_interrupted");
        }

        public JsonData BuildReportParams(PendingRefreshRequest entry)
        {
            var report = JsonRpcSerializer.Object(
                ("originRequestId", entry.OriginRequestId),
                ("method", entry.Method),
                ("state", entry.State),
                ("startedAt", entry.StartedAt),
                ("finishedAt", entry.FinishedAt));
            if (!string.IsNullOrEmpty(entry.ErrorCode))
            {
                report["errorCode"] = entry.ErrorCode;
            }

            if (!string.IsNullOrEmpty(entry.ResultPayload))
            {
                report["result"] = JsonMapper.ToObject(entry.ResultPayload);
            }

            return report;
        }

        private void StartRun(string requestId, TestRunFilter filter, bool includeDetails)
        {
            var entry = Find(requestId);
            if (entry == null || entry.State != "processing")
            {
                return;
            }

            entry.ExecutionState = "running";
            store.Save(entry);
            // 不再重新 MarkStarted —— init 超时窗口从 ExecuteAccepted 就已经开始计, 计数和执行算
            // 同一个 30s 窗口; TestMode 已记过, progress 此时也还是空, 重发一次只会把窗口拉长到 60s。

            runner.Execute(
                filter,
                progress =>
                {
                    // 被 test_init_timeout 判死的旧请求, Unity 的真 test runner 仍握着它的回调 ——
                    // 迟到的进度不属于当前在飞请求, 转发了会污染 tracker (含误压新请求的 RunObserved)。
                    if (requestId == inFlightRequestId)
                    {
                        tracker.UpdateProgress(progress);
                    }
                },
                outcome => Complete(requestId, outcome, includeDetails));
        }

        private void Complete(string requestId, TestRunOutcome outcome, bool includeDetails)
        {
            var entry = Find(requestId);
            if (entry == null || entry.State != "processing")
            {
                // 迟到的终态: 这条请求已经在墙钟天花板处被判死 (entry 终态, 也可能已被 report
                // 循环删掉)。绝不能改它的状态或事后补一份结果 —— 调用方收到的是 request_timeout,
                // 翻案只会自相矛盾。但"运行停了"这件事这里是最早能确认的地方, 直接放闸, 不必
                // 再干等 StuckThresholdMs 的停滞判据。PlayMode 的 override 在调到这里之前已由
                // UnityTestRunner 的 RunFinished / OnError (两者都走 RestoreIfOwed) 恢复过。
                if (inFlightAbandoned && inFlightRequestId == requestId)
                {
                    Release("request_timeout");
                }

                return;
            }

            // 防假绿兜底: 预检的 CountMatching 走本仓库自己的匹配器, 真正执行走框架的
            // RuntimeTestRunnerFilter, 两套语义并不等价 (框架把 "!Foo" 当排除项就是一例),
            // 所以"预检数出一堆、实际跑 0 个"是可能的。NUnit 在零测试时照样报
            // resultState: "Passed" 且各项计数全 0, 透传就是假绿 —— 终态里 total == 0
            // 一律判 no_tests_matched, 与预检零匹配同一个错误码。
            if (outcome?.Summary == null || outcome.Summary.Total == 0)
            {
                Fail(requestId, "no_tests_matched");
                return;
            }

            JsonData payload = null;
            try
            {
                // BuildRunResult 挪进 try —— 它抛异常同样得走 finally 清 tracker/槽位, 放在
                // try 外面就漏了。
                payload = TestResultPayload.BuildRunResult(outcome, includeDetails);
                entry.State = "succeeded";
                entry.ErrorCode = null;
                entry.FinishedAt = clock.UtcNow.ToString("O");
                // 必须先序列化再交给 tracker.MarkFinished —— 它会往同一个 payload 对象原地塞
                // finishedAt, 顺序反了这个字段就会漏进这次 RPC report 的 result 里 (report 自己
                // 已经有一个 finishedAt 兄弟字段, 不需要它)。
                entry.ResultPayload = payload.ToJson();
                store.Save(entry);
            }
            finally
            {
                // 就算 store.Save 抛异常也必须清 tracker 和 in-flight 槽位 —— 否则 SessionState
                // 的运行标志卡死, Task 6 的 transport gate 会挡住本次 Editor 会话剩余时间里的
                // 所有非白名单命令, 调用方也没有办法自己清掉。
                // 先清槽位再调 MarkFinished —— 后者抛异常不该连累前者也漏掉。
                inFlightRequestId = null;
                inFlightDeadline = null;
                inFlightAbandoned = false;
                tracker.MarkFinished(payload);
            }
        }

        private void Fail(string requestId, string errorCode)
        {
            var entry = Find(requestId);
            if (entry == null || entry.State != "processing")
            {
                return;
            }

            try
            {
                entry.State = "failed";
                entry.ErrorCode = errorCode;
                entry.FinishedAt = clock.UtcNow.ToString("O");
                store.Save(entry);
            }
            finally
            {
                // 同 Complete 的加固: store.Save 抛异常也不能漏清 —— test_run_interrupted 这条
                // 由 RecoverPending 触发, domain reload 后 inFlightRequestId 已经是 null, Tick
                // 的头一个 guard 直接返回, 一旦这里漏清 tracker 就再没人能清了。
                if (tracker.IsRunning)
                {
                    tracker.MarkFinished(JsonRpcSerializer.Object(("errorCode", errorCode)));
                }

                inFlightRequestId = null;
                inFlightDeadline = null;
                inFlightAbandoned = false;
            }
        }

        // 墙钟天花板到点: 终结调用方那一侧的请求, 但绝不放闸。
        // 为什么不能直接走 Fail: Fail 会清掉运行标志和槽位, 而 TestRunnerApi 没有 Cancel API
        // —— 框架的运行还在跑。此刻放闸有三个立刻可见的后果: assets-refresh (触发域重载) 和
        // editor-application-set-state 会打进一次还在跑的运行; 调用方重发的 test-run 会在旧运行
        // 仍在跑时再调一次 api.Execute, 把 UnityTestRunner 的 onFinished 和 runId 身份换掉,
        // 于是旧运行的 RunFinished 被身份判据丢弃, 它欠的 PlayMode override 恢复也一起
        // 丢掉, override 会一直留在被跟踪的 ProjectSettings 里直到 Editor 退出。
        private void Abandon(string requestId)
        {
            MarkEntryFailed(requestId, "request_timeout");
            // 天花板对这条请求不必再判第二次 —— entry 已经终态, 继续判只是每帧白读一遍磁盘。
            inFlightDeadline = null;
            // 槽位继续留给这次运行: 它迟到的进度还要靠 requestId == inFlightRequestId 这个判据
            // 转发进 tracker (那是下面判"还活着"的唯一信号), 并发判重也靠它把重发的 test.run
            // 挡在 tests_running 上。
            inFlightAbandoned = true;
        }

        // 闸门的释放判据。进度还在往前走 = 框架的运行还活着 = 标志必须继续压住; 停滞超过
        // StuckThresholdMs 才认定运行已经停了 —— 到那时已经没有回调可等, 再压住就是永久泄漏
        // (整个 Editor 会话里所有非白名单命令被挡死, 调用方自己解不开)。
        private void ReleaseIfRunStopped()
        {
            // 用 LastProgressAt 而不是 CurrentCaseStartedAt: 后者只在用例名变化时刷新, 一个跑
            // 一分钟以上的用例会被它误判成停滞, 于是在运行还活着时放闸 —— 正是要防的那件事。
            // 一次进度都没来过时退回 StartedAt, 让静默的运行同样有一个有限的等待窗口。
            var lastSignal = tracker.LastProgressAt ?? tracker.StartedAt;
            if (lastSignal.HasValue &&
                (clock.UtcNow - lastSignal.Value).TotalMilliseconds <= StuckThresholdMs)
            {
                return;
            }

            // 判死的那条请求收到的是 request_timeout, lastRun 跟它写同一个码; 槽位被 reload
            // 冲掉的那条则由 RecoverPending 报 test_run_interrupted, 这里对齐它, 免得
            // test.status 的 lastRun 跟 report 自相矛盾。
            Release(inFlightAbandoned ? "request_timeout" : "test_run_interrupted");
        }

        // 只放闸: 绝不碰 entry 的状态 —— 走到这里 entry 早已终态, 甚至已被 report 循环删掉。
        private void Release(string errorCode)
        {
            // 先清槽位再动 tracker, 理由同 Complete: MarkFinished 抛异常不该连累前者也漏掉。
            inFlightRequestId = null;
            inFlightDeadline = null;
            inFlightAbandoned = false;
            if (tracker.IsRunning)
            {
                tracker.MarkFinished(JsonRpcSerializer.Object(("errorCode", errorCode)));
            }
        }

        // 并发 test.run 命中判重时用: 只终结这条新请求自己的 entry, 绝不碰 tracker 或
        // inFlightRequestId —— 那两个属于先到的那次运行, 碰了就等于把真正在跑的那次腰斩。
        private void RejectConcurrent(string requestId)
        {
            MarkEntryFailed(requestId, "tests_running");
        }

        private bool MarkEntryFailed(string requestId, string errorCode)
        {
            var entry = Find(requestId);
            if (entry == null || entry.State != "processing")
            {
                return false;
            }

            entry.State = "failed";
            entry.ErrorCode = errorCode;
            entry.FinishedAt = clock.UtcNow.ToString("O");
            store.Save(entry);
            return true;
        }

        private PendingRefreshRequest Find(string requestId)
        {
            foreach (var entry in store.LoadAll())
            {
                if (entry.OriginRequestId == requestId)
                {
                    return entry;
                }
            }

            return null;
        }

        private static string[] ReadArray(JsonData target, string key)
        {
            if (target == null || !target.ContainsKey(key) || target[key] == null || !target[key].IsArray)
            {
                return new string[0];
            }

            var raw = target[key];
            var values = new string[raw.Count];
            for (int i = 0; i < raw.Count; i++)
            {
                values[i] = (string)raw[i];
            }

            return values;
        }

        private static JsonData BuildInputSchema()
        {
            var required = new JsonData();
            required.SetJsonType(JsonType.Array);
            required.Add("assemblyNames");

            var testModeEnum = new JsonData();
            testModeEnum.SetJsonType(JsonType.Array);
            testModeEnum.Add("EditMode");
            testModeEnum.Add("PlayMode");

            var schema = JsonRpcSerializer.Object(
                ("type", "object"),
                ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("testMode", JsonRpcSerializer.Object(
                        ("type", "string"),
                        ("description",
                            "EditMode (default) or PlayMode. PlayMode temporarily changes EditorSettings " +
                            "to disable domain reload and enters play mode, so pass it only when needed."))),
                    ("assemblyNames", JsonRpcSerializer.Object(
                        ("type", "array"),
                        ("minItems", 1),
                        ("items", JsonRpcSerializer.Object(("type", "string"))),
                        ("description",
                            "Required. Test assembly names without the .dll extension, case-insensitive " +
                            "exact match. Multiple entries are unioned. Leading ! (exclusion) is rejected."))),
                    ("groupNames", JsonRpcSerializer.Object(
                        ("type", "array"),
                        ("items", JsonRpcSerializer.Object(("type", "string"))),
                        ("description",
                            "Optional regular expressions matched (unanchored) against each test full name " +
                            "namespace.class.method. Example: \"Commands\" runs every test in that namespace. " +
                            "Leading ! (exclusion) is rejected."))),
                    ("includeDetails", JsonRpcSerializer.Object(
                        ("type", "boolean"),
                        ("description",
                            "Default false. When true also returns passing tests, which can be very large."))),
                    ("timeoutMs", JsonRpcSerializer.Object(
                        ("type", "integer"),
                        ("minimum", MinTimeoutMs),
                        ("maximum", MaxTimeoutMs),
                        ("description",
                            "Overall wall-clock ceiling for this call in milliseconds, default 300000. " +
                            "Exceeding it answers with errorCode request_timeout; the tests themselves " +
                            "cannot be cancelled and keep running."))))));
            schema["required"] = required;
            schema["properties"]["testMode"]["enum"] = testModeEnum;
            return schema;
        }

        // Unity 的 RuntimeTestRunnerFilter (com.unity.test-framework 1.1.33) 把 "!" 开头的
        // 条目解释成"排除项", 而预检用的 CountMatching 只会把它当普通名字/正则去匹配。
        // 语义一分叉就出现"预检数出 262 个、实跑 0 个"的假绿路径 —— 本命令不支持排除语义,
        // 就在入口明确拒掉, 而不是让两套匹配器各跑各的。
        private static bool IsExclusion(string entry)
            => !string.IsNullOrEmpty(entry) && entry[0] == '!';

        // 越界值按 schema 边界夹紧而不是报错: 经 Go 侧 mcpbridge 来的越界值已经被它拒成
        // invalid_params, 根本送不到这里; 能到这里的越界值只有直连调用方或旧记录, 对它们夹紧
        // 比多一条错误分支更有用 —— 这里报错也无处可去 (只有一个 ack 能返回)。
        private static int ReadTimeoutMs(JsonData @params)
        {
            if (@params == null || !@params.IsObject || !@params.ContainsKey("timeoutMs"))
            {
                return DefaultTimeoutMs;
            }

            var raw = @params["timeoutMs"];
            long value;
            if (raw != null && raw.IsInt)
            {
                value = (int)raw;
            }
            else if (raw != null && raw.IsLong)
            {
                value = (long)raw;
            }
            else if (raw != null && raw.IsDouble)
            {
                value = (long)(double)raw;
            }
            else
            {
                return DefaultTimeoutMs;
            }

            if (value < MinTimeoutMs)
            {
                return MinTimeoutMs;
            }

            return value > MaxTimeoutMs ? MaxTimeoutMs : (int)value;
        }

        private static DateTimeOffset? DeadlineOf(PendingRefreshRequest entry, JsonData target)
        {
            if (entry == null || string.IsNullOrEmpty(entry.StartedAt) ||
                !DateTimeOffset.TryParse(entry.StartedAt, out var startedAt))
            {
                return null;
            }

            int timeoutMs = DefaultTimeoutMs;
            if (target != null && target.IsObject && target.ContainsKey("timeoutMs") &&
                target["timeoutMs"] != null && target["timeoutMs"].IsInt)
            {
                // 记录里的值是 Handle 夹过的, 但记录也可能是旧版本写下的 —— 再夹一次,
                // 免得一个坏值把天花板变成"立刻超时"或"永不超时"。
                int persisted = (int)target["timeoutMs"];
                timeoutMs = persisted < MinTimeoutMs
                    ? MinTimeoutMs
                    : (persisted > MaxTimeoutMs ? MaxTimeoutMs : persisted);
            }

            return startedAt.AddMilliseconds(timeoutMs);
        }

        private static bool IsValidRegex(string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return false;
            }

            try
            {
                _ = new Regex(pattern);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static JsonRpcResponse Invalid(string requestId, string message, string pattern)
        {
            var data = JsonRpcSerializer.Object(("errorCode", "invalid_params"));
            if (pattern != null)
            {
                data["pattern"] = pattern;
            }

            return JsonRpcResponse.FromError(requestId, new JsonRpcError(
                JsonRpcErrorCodes.InvalidParams, message, data));
        }

        private static JsonData ToArray(IEnumerable<string> values)
        {
            var array = new JsonData();
            array.SetJsonType(JsonType.Array);
            foreach (var value in values)
            {
                array.Add(value);
            }

            return array;
        }

        private static string[] ReadStringArray(JsonData @params, string key)
        {
            if (@params == null || !@params.IsObject || !@params.ContainsKey(key))
            {
                return null;
            }

            var raw = @params[key];
            if (raw == null || !raw.IsArray)
            {
                return null;
            }

            var values = new string[raw.Count];
            for (int i = 0; i < raw.Count; i++)
            {
                values[i] = raw[i] != null && raw[i].IsString ? (string)raw[i] : string.Empty;
            }

            return values;
        }

        private static string ReadString(JsonData @params, string key)
        {
            if (@params == null || !@params.IsObject || !@params.ContainsKey(key))
            {
                return null;
            }

            return @params[key] != null && @params[key].IsString ? (string)@params[key] : null;
        }

        private static bool ReadBool(JsonData @params, string key)
        {
            return @params != null && @params.IsObject && @params.ContainsKey(key) &&
                   @params[key] != null && @params[key].IsBoolean && (bool)@params[key];
        }
    }
}
