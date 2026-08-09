using System;
using System.Collections.Generic;

namespace VeryFS.UnityMCP.Editor.Commands.Testing
{
    // 一次运行的过滤条件。TestMode 用字符串而非 Unity 的 TestMode 枚举,
    // 这样本文件不依赖 com.unity.test-framework, 单测替身也不必引它。
    public sealed class TestRunFilter
    {
        public string TestMode { get; set; } = "EditMode";
        public string[] AssemblyNames { get; set; }
        public string[] GroupNames { get; set; }

        // true 时 PlayMode 跑测压 EnterPlayModeOptions.DisableDomainReload 换速度,
        // 代价是静态字段与静态构造不重置, 结果可能与 CI 不一致 (P11 spec §8 风险 1)。
        // 默认 false: 走正常 domain reload, 语义与 CI / 手工跑对齐。
        public bool DisableDomainReload { get; set; }
    }

    public sealed class TestCaseResult
    {
        public string FullName { get; set; }
        public string State { get; set; }
        public double DurationSeconds { get; set; }
        public string Message { get; set; }
        public string StackTrace { get; set; }
    }

    public sealed class TestRunSummary
    {
        public int Total { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public double DurationSeconds { get; set; }
        public string ResultState { get; set; }
    }

    public sealed class TestRunOutcome
    {
        public TestRunSummary Summary { get; set; }
        public List<TestCaseResult> Results { get; set; } = new List<TestCaseResult>();
        public bool DomainReloadDisabled { get; set; }

        // 这次结果是不是跨过至少一次 domain reload 收回来的 (运行中途环境换过一次)。
        // 与 DomainReloadDisabled 并列, 两者一起标注这次结果的适用范围。
        public bool ResumedAcrossReload { get; set; }
    }

    public sealed class TestProgress
    {
        public int Completed { get; set; }
        public int Total { get; set; }
        public string CurrentTestFullName { get; set; }
        public List<TestCaseResult> FailuresSoFar { get; set; } = new List<TestCaseResult>();
    }

    // 包住 Unity TestRunnerApi 的全部交互。单测必须用替身实现,
    // 直接调真 TestRunnerApi 会在测试运行中再启动一次测试运行而死锁。
    public interface ITestRunner
    {
        // RetrieveTestList 是回调式, 所以计数也是回调式。
        void CountMatching(TestRunFilter filter, Action<int> onCounted);

        void Execute(TestRunFilter filter, Action<TestProgress> onProgress, Action<TestRunOutcome> onFinished);

        // domain reload 之后重挂回调。框架里那次运行还在跑, 没了的只是持有回调的旧对象 ——
        // 与 Execute 的唯一区别就是不再发起运行。
        void Resume(TestRunFilter filter, Action<TestProgress> onProgress, Action<TestRunOutcome> onFinished);

        // 框架投递过终态、但身份判据判定"不是本对象在等的那次运行"因而整体丢弃的时刻。
        // 回调注册在进程级单例上, 被丢弃的来路有两条: 别人的运行 (手工从 Test Runner 窗口
        // 跑的), 以及 domain reload 把槽位冲掉、回调随旧对象一起没了的那次自己的运行。
        // 后者正是运行标志会泄漏的场合 —— 有这个时间戳, 闸门就有"运行确实停了"的直接证据,
        // 不必干等 StuckThresholdMs 的停滞推断。没有丢弃过则为 null。
        DateTimeOffset? LastDiscardedFinishAt { get; }
    }
}
