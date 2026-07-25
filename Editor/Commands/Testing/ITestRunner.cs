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
    }
}
