using System;
using LitJson;
using UnityEditor;
using VeryFS.UnityMCP.Editor.Infrastructure;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Testing
{
    // 键值存储 seam。生产走 SessionState (抗 domain reload, Editor 关闭即清),
    // 单测走内存字典 —— 避免测试互相污染真 SessionState。
    public interface ITestRunStateStore
    {
        string GetString(string key);

        void SetString(string key, string value);
    }

    public sealed class SessionStateTestRunStore : ITestRunStateStore
    {
        public string GetString(string key) => SessionState.GetString(key, string.Empty);

        public void SetString(string key, string value) => SessionState.SetString(key, value ?? string.Empty);
    }

    // 测试运行的易失状态: 运行标志 / 进度快照 / 最近一次结果。
    // 只保留最近一次 —— 推模型下没有 jobId, 超时复查只关心最近那次。
    public sealed class TestRunTracker
    {
        private const string KeyRunning = "VeryFS.UnityMCP.TestRun.Running";
        private const string KeyTestMode = "VeryFS.UnityMCP.TestRun.TestMode";
        private const string KeyStartedAt = "VeryFS.UnityMCP.TestRun.StartedAt";
        private const string KeyCaseStartedAt = "VeryFS.UnityMCP.TestRun.CaseStartedAt";
        private const string KeyProgressAt = "VeryFS.UnityMCP.TestRun.ProgressAt";
        private const string KeyProgress = "VeryFS.UnityMCP.TestRun.Progress";
        private const string KeyLastRun = "VeryFS.UnityMCP.TestRun.LastRun";

        private readonly ITestRunStateStore store;
        private readonly IClock clock;

        public TestRunTracker(ITestRunStateStore store, IClock clock)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public bool IsRunning => store.GetString(KeyRunning) == "1";

        // RunObserved: 是否已收到过至少一次进度回调。init 超时判据靠它。
        public bool RunObserved => !string.IsNullOrEmpty(store.GetString(KeyProgress));

        public string TestMode => store.GetString(KeyTestMode);

        public DateTimeOffset? StartedAt => ParseTime(store.GetString(KeyStartedAt));

        public DateTimeOffset? CurrentCaseStartedAt => ParseTime(store.GetString(KeyCaseStartedAt));

        // 最近一次进度回调的时刻。和 CurrentCaseStartedAt 分开存是刻意的: 后者只在用例名
        // 变化时刷新 (判"单个用例卡住"), 一个跑一分钟以上的用例在它眼里就是停滞; 这个每来
        // 一次回调都刷新, 才是"框架的运行还活着"的信号 —— TestRunCommand 拿它决定什么时候
        // 才敢放开 transport 闸门。
        public DateTimeOffset? LastProgressAt => ParseTime(store.GetString(KeyProgressAt));

        public void MarkStarted(string testMode)
        {
            store.SetString(KeyRunning, "1");
            store.SetString(KeyTestMode, testMode ?? "EditMode");
            store.SetString(KeyStartedAt, clock.UtcNow.ToString("O"));
            store.SetString(KeyCaseStartedAt, string.Empty);
            store.SetString(KeyProgressAt, string.Empty);
            store.SetString(KeyProgress, string.Empty);
        }

        public void UpdateProgress(TestProgress progress)
        {
            if (progress == null)
            {
                return;
            }

            string previousCase = null;
            var existing = ReadJson(KeyProgress);
            if (existing != null && existing.ContainsKey("currentTestFullName"))
            {
                previousCase = (string)existing["currentTestFullName"];
            }

            string currentCase = progress.CurrentTestFullName ?? string.Empty;
            if (previousCase != currentCase)
            {
                store.SetString(KeyCaseStartedAt, clock.UtcNow.ToString("O"));
            }

            // 每一次回调都刷 —— 哪怕用例名没变、计数没变。这是"运行还活着"的心跳。
            store.SetString(KeyProgressAt, clock.UtcNow.ToString("O"));

            var failures = TestResultPayload.BuildFailures(progress.FailuresSoFar, out bool capped);
            var payload = JsonRpcSerializer.Object(
                ("completed", progress.Completed),
                ("total", progress.Total),
                ("currentTestFullName", currentCase),
                ("failuresCapped", capped));
            payload["failuresSoFar"] = failures;
            store.SetString(KeyProgress, payload.ToJson());
        }

        public void MarkFinished(JsonData lastRunPayload)
        {
            var payload = lastRunPayload ?? JsonRpcSerializer.Object();
            payload["finishedAt"] = clock.UtcNow.ToString("O");
            store.SetString(KeyLastRun, payload.ToJson());
            store.SetString(KeyRunning, string.Empty);
            store.SetString(KeyProgress, string.Empty);
            store.SetString(KeyCaseStartedAt, string.Empty);
            store.SetString(KeyProgressAt, string.Empty);
        }

        public void Clear()
        {
            store.SetString(KeyRunning, string.Empty);
            store.SetString(KeyTestMode, string.Empty);
            store.SetString(KeyStartedAt, string.Empty);
            store.SetString(KeyCaseStartedAt, string.Empty);
            store.SetString(KeyProgressAt, string.Empty);
            store.SetString(KeyProgress, string.Empty);
            store.SetString(KeyLastRun, string.Empty);
        }

        public JsonData BuildProgressPayload() => ReadJson(KeyProgress);

        public JsonData LastRunPayload() => ReadJson(KeyLastRun);

        private JsonData ReadJson(string key)
        {
            string raw = store.GetString(key);
            if (string.IsNullOrEmpty(raw))
            {
                return null;
            }

            try
            {
                return JsonMapper.ToObject(raw);
            }
            catch
            {
                // 存坏了就当没有 —— 易失状态, 不值得让命令因此失败。
                return null;
            }
        }

        private static DateTimeOffset? ParseTime(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return null;
            }

            return DateTimeOffset.TryParse(raw, out var parsed) ? parsed : (DateTimeOffset?)null;
        }
    }
}
