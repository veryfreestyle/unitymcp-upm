using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;

namespace VeryFS.UnityMCP.Editor.Commands.Testing
{
    // test-list 的接缝: 把 TestRunnerApi.RetrieveTestList 的回调式查询包成 UniTask。
    // 生产实现 UnityTestListProvider 触碰真 API; 单测用替身, 避免在测试运行中再启动查询。
    public interface ITestListProvider
    {
        UniTask<IReadOnlyList<TestAssemblyInfo>> ListAssembliesAsync();
    }

    // ExtractAssemblies 遍历用的最小树接口。把庞大的 ITestAdaptor 收敛到 4 个成员,
    // 使遍历逻辑能用轻量替身单测, 而不必实现整个 ITestAdaptor。
    public interface ITestNode
    {
        string Name { get; }
        bool IsTestAssembly { get; }
        bool HasChildren { get; }
        IEnumerable<ITestNode> Children { get; }
    }

    public sealed class TestAssemblyInfo
    {
        public string Name { get; set; }
        public string TestMode { get; set; }
    }

    // ListAssembliesAsync 在超时时抛出, 由 TestListCommand 映射成 test_list_timeout。
    public sealed class TestListTimeoutException : Exception
    {
    }
}
