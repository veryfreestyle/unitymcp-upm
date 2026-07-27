using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace VeryFS.UnityMCP.Editor.Commands.Testing
{
    // ITestListProvider 生产实现。唯一接触 TestRunnerApi / ITestAdaptor 的地方,
    // 所以单测能靠替身完全绕开真 Test Runner。EditMode / PlayMode 各查一次,
    // 共用一个 30 秒超时窗口。RetrieveTestList 不进 play 模式, 两种 mode 开销对等。
    public sealed class UnityTestListProvider : ITestListProvider
    {
        private const int TimeoutMs = 30000;

        private readonly TestRunnerApi api;

        public UnityTestListProvider()
        {
            api = ScriptableObject.CreateInstance<TestRunnerApi>();
        }

        public UniTask<IReadOnlyList<TestAssemblyInfo>> ListAssembliesAsync()
        {
            var completion = new UniTaskCompletionSource<IReadOnlyList<TestAssemblyInfo>>();
            var results = new List<TestAssemblyInfo>();
            int pending = 2;
            // provider 不单测, 超时用真实时钟, 靠冒烟验证 (spec §8)。
            var deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMs);

            void OnList(ITestAdaptor root, string mode)
            {
                TestListPayload.ExtractAssemblies(new TestAdaptorNode(root), mode, results);
                pending--;
            }

            void Poll()
            {
                if (pending <= 0)
                {
                    EditorApplication.update -= Poll;
                    completion.TrySetResult(results);
                    return;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    EditorApplication.update -= Poll;
                    completion.TrySetException(new TestListTimeoutException());
                }
            }

            api.RetrieveTestList(TestMode.EditMode, root => OnList(root, "EditMode"));
            api.RetrieveTestList(TestMode.PlayMode, root => OnList(root, "PlayMode"));
            EditorApplication.update += Poll;
            // 失焦时 Editor 可能不主动推进, 逼一帧确保回调被投递。
            EditorApplication.QueuePlayerLoopUpdate();

            return completion.Task;
        }

        // 把 ITestAdaptor 薄包成 ITestNode, 只转发遍历需要的成员。
        private sealed class TestAdaptorNode : ITestNode
        {
            private readonly ITestAdaptor adaptor;

            public TestAdaptorNode(ITestAdaptor adaptor)
            {
                this.adaptor = adaptor;
            }

            public string Name => adaptor?.Name;
            public bool IsTestAssembly => adaptor != null && adaptor.IsTestAssembly;
            public bool HasChildren => adaptor != null && adaptor.HasChildren;

            public IEnumerable<ITestNode> Children
            {
                get
                {
                    if (adaptor == null || !adaptor.HasChildren)
                    {
                        yield break;
                    }

                    foreach (var child in adaptor.Children)
                    {
                        yield return new TestAdaptorNode(child);
                    }
                }
            }
        }
    }
}
