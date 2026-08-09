using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace VeryFS.UnityMCP.Editor.Commands.Testing
{
    // ITestRunner 的生产实现。本文件是唯一接触 TestRunnerApi 与 ICallbacks 的地方,
    // 所以单测能靠替身完全绕开真 Test Runner (否则会在测试运行中再启动一次运行)。
    // 实现 IErrorCallbacks (继承自 ICallbacks) 而非只实现 ICallbacks —— PlayMode 的
    // prebuild-setup 失败或 job runner 内部异常时, CallbacksDelegator.RunFailed 只通知
    // OnError, 根本不会补发 RunFinished, 不接住就永远等不到终态。
    public sealed class UnityTestRunner : ITestRunner, IErrorCallbacks
    {
        private static readonly TimeSpan GroupRegexTimeout = TimeSpan.FromSeconds(1);

        private readonly TestRunnerApi api;
        private readonly List<TestCaseResult> leafResults = new List<TestCaseResult>();
        private Action<TestProgress> onProgress;
        private Action<TestRunOutcome> onFinished;
        private string currentTestFullName;
        private int totalTests;
        private bool domainReloadDisabled;
        // 本轮是不是 domain reload 之后认领回来的。与 domainReloadDisabled 并列汇报,
        // 让调用方知道这次结果的运行中途换过一次环境。
        private bool resumedAcrossReload;
        // EditMode 跑测不进 play mode, enterPlayModeOptions 对它的结果没有任何影响 ——
        // 记下本轮是不是 PlayMode, RunFinished 汇报 domainReloadDisabled 时按这个裁剪,
        // 避免同一会话里遗留的 PlayMode Restore 标记把下一次 EditMode 结果也带脏。
        private bool isPlayModeRun;
        // 本轮的过滤条件。RunStarted 数叶子时要用它筛一遍: 认领路径 (domain reload 之后)
        // 框架给的是整棵树, 不筛的话进度分母会把没选中的测试也算进去。
        private TestRunFilter activeFilter;
        // 回调注册在 [InitializeOnLoad] 单例上, 是进程级的: 用户从 Test Runner 窗口手工跑的
        // 运行、以及被上层放弃 (wall-clock 上限触发 request_timeout, 进度停滞后放开 gate) 但
        // 框架里还活着的旧运行, 回调都会打到这个对象上 —— TestRunnerApi 没有 Cancel, 放弃 ≠ 停止。
        // 单个 bool 闩认不出"这条回调属于哪一次运行", 所以用递增 runId 当身份判据:
        //   runIdSeq    只在 Execute 里 ++, 给每次请求发一个唯一 id;
        //   currentRunId 本次 Execute 在等的 id, 终结 (RunFinished/OnError) 后归 0;
        //   observedRunId RunStarted 把入站回调流绑定到哪个 id, 未绑定为 0。
        // 三者相等即"当前回调流是本次请求的", 见 Bound。
        private int runIdSeq;
        private int currentRunId;
        private int observedRunId;
        // 绑定时记下本轮该跑的叶子全名。runId 只能识别"跨 Execute 的旧账", 认不出同一次
        // Execute 期间从旁边那条还活着的运行插进来的 TestFinished —— 实测就栽在这里:
        // summary 是 EditMode 全量 (428/146.8s), failures[0] 却是 PlayMode 那条被取消的
        // SlowRunForInterruptSmoke。名字不在本轮清单里, 就是别人的账。
        private readonly HashSet<string> expectedLeafNames = new HashSet<string>();
        // 上面这条过滤有个前提: RunStarted 树里的 FullName 和 TestFinished 里的 FullName
        // 格式一致。万一某个模式下 (比如 PlayMode 走远端 adaptor) 不一致, 无条件过滤会把本轮
        // 自己的结果全丢掉 —— summary 报 Failed=1 而 failures[] 空的哑火比污染更糟。
        // 所以只有在本轮至少命中过一条 (证明格式对得上) 之后才开始丢弃。
        private bool leafNamesTrusted;
        // 静默丢弃看起来就像"这条测试没跑" —— 每轮吼一次 (只第一次, 别把 Console 刷爆),
        // 既能在真重叠时留下痕迹, 也是"清单判据有没有误伤自己人"的唯一可观测信号。
        private bool foreignLeafReported;

        // 见 ITestRunner.LastDiscardedFinishAt。丢弃终态时置位, 给闸门一个"框架里那次运行
        // 确实已经结束"的直接证据。
        private DateTimeOffset? lastDiscardedFinishAt;

        // 静默丢弃终态跟静默丢弃叶子一样看不出痕迹, 而它牵连的是闸门 —— 每轮吼一次。
        private bool discardedFinishReported;

        public DateTimeOffset? LastDiscardedFinishAt => lastDiscardedFinishAt;

        private bool Bound => currentRunId != 0 && observedRunId == currentRunId;

        public UnityTestRunner()
        {
            api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(this);
        }

        public void CountMatching(TestRunFilter filter, Action<int> onCounted)
        {
            var mode = ParseMode(filter.TestMode);
            api.RetrieveTestList(mode, root =>
            {
                int matched = 0;
                CountLeaves(root, filter, ref matched);
                onCounted(matched);
            });

            // 失焦时 Editor 可能不主动推进, 逼一帧确保回调被投递。
            EditorApplication.QueuePlayerLoopUpdate();
        }

        public void Execute(TestRunFilter filter, Action<TestProgress> progress, Action<TestRunOutcome> finished)
        {
            onProgress = progress;
            onFinished = finished;
            ResetForRun(filter);

            // 默认走正常 domain reload (语义与 CI 对齐); 只有调用方明确要速度时才压 override。
            if (isPlayModeRun && filter.DisableDomainReload)
            {
                domainReloadDisabled = PlayModeOptionsGuard.Apply();
            }

            var unityFilter = new Filter
            {
                testMode = ParseMode(filter.TestMode),
                assemblyNames = Empty(filter.AssemblyNames) ? null : filter.AssemblyNames,
                groupNames = Empty(filter.GroupNames) ? null : filter.GroupNames
            };

            api.Execute(new ExecutionSettings(unityFilter));
            EditorApplication.QueuePlayerLoopUpdate();
        }

        // domain reload 之后认领: 框架里那次运行还在跑, 这里只是重新挂上耳朵。
        // 不调 api.Execute (会变成第二次运行), 也不调 PlayModeOptionsGuard.Apply
        // (reload 都发生了, 谈不上禁用)。叶子清单留空, 由 reload 之后到达的 RunStarted 重建。
        public void Resume(TestRunFilter filter, Action<TestProgress> progress, Action<TestRunOutcome> finished)
        {
            onProgress = progress;
            onFinished = finished;
            ResetForRun(filter);
            resumedAcrossReload = true;

            EditorApplication.QueuePlayerLoopUpdate();
        }

        // Execute 与 Resume 共用的身份重置: 换一个新 runId, 旧运行的回调从此再也匹配不上。
        private void ResetForRun(TestRunFilter filter)
        {
            leafResults.Clear();
            currentTestFullName = null;
            totalTests = 0;

            currentRunId = ++runIdSeq;
            observedRunId = 0;
            expectedLeafNames.Clear();
            leafNamesTrusted = false;
            foreignLeafReported = false;
            discardedFinishReported = false;
            // 时间戳不清: 上一轮丢弃的那笔账对本轮闸门没有意义, 但闸门自己会拿它跟
            // tracker.StartedAt 比时间, 旧账天然出局 —— 这里清反而会丢掉 reload 之前
            // 那次丢弃的记录 (跨 reload 的对象是新的, 本来也就只剩这一份线索)。

            domainReloadDisabled = false;
            resumedAcrossReload = false;
            isPlayModeRun = ParseMode(filter.TestMode) == TestMode.PlayMode;
            activeFilter = filter;
        }

        public void RunStarted(ITestAdaptor testsToRun)
        {
            // 没有在等任何运行 (纯手工从 Test Runner 窗口跑的): 一个字都不记。
            if (currentRunId == 0)
            {
                return;
            }

            // 一次 Execute 只认第一个 RunStarted。之后再来的一定是别人的运行 —— 放它进来会
            // 清掉本轮已收集的 leafResults、把 totalTests 换成对方的规模, 进度直接报废。
            if (observedRunId == currentRunId)
            {
                return;
            }

            observedRunId = currentRunId;
            leafResults.Clear();
            expectedLeafNames.Clear();
            leafNamesTrusted = false;
            foreignLeafReported = false;
            totalTests = 0;
            CollectLeaves(testsToRun, activeFilter, expectedLeafNames, ref totalTests);
            EmitProgress();
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            // 还没绑定 (RunStarted 没来过) 就收到 RunFinished: 不是这次 Execute() 发起的运行,
            // 别把它的结果当成这次请求的结果上报, 也别碰下面的 Restore/onFinished。
            if (!Bound)
            {
                NoteDiscardedFinish("callback stream not bound to this request");
                return;
            }

            // 已绑定, 但收尾的这棵结果树和本轮该跑的叶子毫无交集 —— 那是旁边那条运行
            // (被放弃后才收尾, 或手工跑的那次) 在收尾, 它的 summary 不是本轮的账。
            if (leafNamesTrusted && result != null && !OverlapsExpectedLeaves(result.Test))
            {
                NoteDiscardedFinish("result tree does not overlap this request's leaves");
                return;
            }

            // 运行到这里说明这次 RunFinished 确实是本次 Execute() 的终态回调。立刻把身份注销,
            // 后面任何回调 (包括本轮自己迟到的、以及手工从 Test Runner 窗口发起的运行) 都不再
            // 匹配, 而不是被误当成这次结果的收尾。
            currentRunId = 0;
            observedRunId = 0;

            RestoreIfOwed();

            // NormalizeState 把 inconclusive 叶子归到 "Failed", 它们也会出现在 failures[]
            // 里 —— 汇总的 Failed/Total 如果不把 InconclusiveCount 折进来, 就会跟 failures[]
            // 自相矛盾 (失败列表里有条目, 但总数和失败数都不认它)。
            var summary = new TestRunSummary
            {
                Passed = result?.PassCount ?? 0,
                Failed = (result?.FailCount ?? 0) + (result?.InconclusiveCount ?? 0),
                Skipped = result?.SkipCount ?? 0,
                DurationSeconds = result?.Duration ?? 0d,
                ResultState = result?.ResultState ?? "Unknown"
            };
            summary.Total = summary.Passed + summary.Failed + summary.Skipped;

            var outcome = new TestRunOutcome
            {
                Summary = summary,
                Results = new List<TestCaseResult>(leafResults),
                DomainReloadDisabled = domainReloadDisabled,
                ResumedAcrossReload = resumedAcrossReload
            };

            var callback = onFinished;
            onFinished = null;
            onProgress = null;
            callback?.Invoke(outcome);
        }

        // IErrorCallbacks.OnError: PlayMode 的 prebuild-setup 失败或 job runner 内部异常时,
        // CallbacksDelegator.RunFailed 只会调这个方法, 不会再补发 RunFinished —— 不接住这次
        // 请求就没有终态可言, 只能靠 30s init 超时兜底, 如果进度已经到过一次连这根保险丝都断了。
        // 把它当成一次失败的终态收尾, 复用 RunFinished 同一套判重/Restore/清理逻辑。
        public void OnError(string message)
        {
            // 不能沿用"回调流是否已绑定"当判据 —— PlayMode 的 prebuild-setup 失败发生在
            // RunStarted 之前, 此时还没绑定, 这里直接 return 就会让这次请求永远等不到终态,
            // 刚好挡住了这个方法本来要接住的头号场景。改判 onFinished 是否非空: 只要这次
            // Execute() 还有一个终态回调没交付 (不管 RunStarted 是否跑过), 这次错误就该我接。
            if (onFinished == null)
            {
                NoteDiscardedFinish("no request is waiting for a terminal outcome");
                return;
            }

            // 代价: 如果用户此刻正在 Test Runner 窗口手工跑一次运行, 同时这次 MCP 请求也在
            // 等终态, 手工那次运行报错会被当成这次 MCP 请求的失败终态提前收尾。可接受 ——
            // 一次误判的失败调用方还能重试, 好过请求永远悬着, 把这个会话剩余时间的
            // transport gate 焊死。
            currentRunId = 0;
            observedRunId = 0;
            RestoreIfOwed();

            // 已经收集到的叶子结果 (比如 job runner 中途出错) 一并带上, 只在末尾追加这条
            // 错误本身作为一条失败用例, 而不是把已经真实跑完的结果一起丢掉。
            int failed = 1;
            int passed = 0;
            int skipped = 0;
            foreach (var leaf in leafResults)
            {
                if (leaf.State == "Passed")
                {
                    passed++;
                }
                else if (leaf.State == "Skipped")
                {
                    skipped++;
                }
                else
                {
                    failed++;
                }
            }

            var results = new List<TestCaseResult>(leafResults)
            {
                new TestCaseResult
                {
                    FullName = "<test-runner-error>",
                    State = "Failed",
                    Message = message
                }
            };

            var summary = new TestRunSummary
            {
                Passed = passed,
                Failed = failed,
                Skipped = skipped,
                DurationSeconds = 0d,
                ResultState = "Failed"
            };
            summary.Total = summary.Passed + summary.Failed + summary.Skipped;

            var outcome = new TestRunOutcome
            {
                Summary = summary,
                Results = results,
                DomainReloadDisabled = domainReloadDisabled,
                ResumedAcrossReload = resumedAcrossReload
            };

            var callback = onFinished;
            onFinished = null;
            onProgress = null;
            callback?.Invoke(outcome);
        }

        // 记下"框架投递了终态、但这笔账不是本对象在等的那次运行的"。两个用处:
        //   1. TestRunCommand 的闸门拿它当"框架里那次运行确实停了"的直接证据 (见
        //      ITestRunner.LastDiscardedFinishAt) —— 尤其是 domain reload 把槽位冲掉、
        //      回调随旧对象一起没了的那次, 否则只能靠 StuckThresholdMs 的停滞推断干等;
        //   2. 静默丢弃终态看起来就像"运行没结束", 留一条日志才有得查。每轮只吼一次,
        //      理由同 foreignLeafReported: 别把 Console 刷爆。
        private void NoteDiscardedFinish(string reason)
        {
            lastDiscardedFinishAt = DateTimeOffset.UtcNow;
            if (discardedFinishReported)
            {
                return;
            }

            discardedFinishReported = true;
            Debug.Log("Unity MCP: discarded a test run terminal callback — " + reason + ".");
        }

        // RunFinished 和 OnError 都要处理"这轮是否还欠一次 Restore", 抽成一个方法防止
        // 两处的判据/清理逻辑各写一份、日后改一处漏一处。
        private void RestoreIfOwed()
        {
            // Apply() 的重入检查在上一轮 Guard 泄漏时会返回 false —— 它把"EditorSettings
            // 已经是目标状态"等同于"这次没改", 但 override 其实还欠一次 Restore。用
            // IsPending 兜底, 否则第二轮 PlayMode 跑测既不清理遗留 override, 也不如实汇报
            // 本轮域重载状态 (override 明明还生效, 却报 domainReloadDisabled: false)。
            bool restoreOwed = domainReloadDisabled || PlayModeOptionsGuard.IsPending;
            if (!restoreOwed)
            {
                return;
            }

            // Restore() 无论如何都要做 —— 但汇报的 domainReloadDisabled 只在本轮确实是
            // PlayMode 时才置 true。EditMode 从不进 play mode, enterPlayModeOptions 影响不到
            // 它的结果; 这里的 restoreOwed 也可能只是在替上一轮泄漏的 PlayMode 运行擦屁股,
            // 不能算到这次 EditMode 结果头上。
            domainReloadDisabled = isPlayModeRun;
            try
            {
                PlayModeOptionsGuard.Restore();
            }
            catch (Exception exception)
            {
                // 恢复失败也必须把终态回调送出去 —— 否则这条 pending 请求会一直卡在
                // processing 直到客户端超时, 而跑测本身其实已经跑完了。
                Debug.LogWarning(
                    "Unity MCP: failed to restore play mode options after test run. " + exception.Message);
            }
        }

        public void TestStarted(ITestAdaptor test)
        {
            // 回调是进程级单例, 别人的运行 (手工跑的, 或被放弃后还活着的旧运行) 可能跟这次
            // Execute() 重叠。它的进度不属于这次请求 —— 转发了会污染 leafResults, 还会把
            // tracker.RunObserved 提前顶成 true, 熔掉 30s init 超时这根保险丝。跟 RunFinished
            // 用同一个绑定判据整体丢弃。
            if (!Bound)
            {
                return;
            }

            // suite/namespace/assembly 节点也会触发 TestStarted; 跟 TestFinished 一样只看叶子,
            // 否则 currentTestFullName 会被节点名 (比如 xxx.dll) 顶替, 每次顶替都重置
            // CurrentCaseStartedAt, 把 60s 卡死阈值不断顺延。
            if (test == null || test.HasChildren)
            {
                return;
            }

            string fullName = test.FullName ?? test.Name;
            if (IsForeignLeaf(fullName))
            {
                return;
            }

            currentTestFullName = fullName;
            EmitProgress();
        }

        public void TestFinished(ITestResultAdaptor result)
        {
            // 同 TestStarted: 不属于这次 Execute() 的运行, 整体丢弃。
            if (!Bound)
            {
                return;
            }

            if (result == null || result.HasChildren)
            {
                return;
            }

            string fullName = result.FullName ?? result.Test?.FullName;
            if (IsForeignLeaf(fullName))
            {
                return;
            }

            leafResults.Add(new TestCaseResult
            {
                FullName = fullName,
                State = NormalizeState(result.ResultState),
                DurationSeconds = result.Duration,
                Message = result.Message,
                StackTrace = result.StackTrace
            });
            EmitProgress();
        }

        private void EmitProgress()
        {
            if (onProgress == null)
            {
                return;
            }

            var failures = new List<TestCaseResult>();
            foreach (var result in leafResults)
            {
                if (result.State != "Passed" && result.State != "Skipped")
                {
                    failures.Add(result);
                }
            }

            onProgress(new TestProgress
            {
                Completed = leafResults.Count,
                Total = totalTests,
                CurrentTestFullName = currentTestFullName,
                FailuresSoFar = failures
            });
        }

        // ITestResultAdaptor.ResultState 是自由字符串 ("Passed" / "Failed" /
        // "Failed(Child)" / "Skipped:Ignored" ...), 归一成三档便于下游判定。
        private static string NormalizeState(string resultState)
        {
            if (string.IsNullOrEmpty(resultState))
            {
                return "Unknown";
            }

            string lowered = resultState.ToLowerInvariant();
            if (lowered.Contains("passed"))
            {
                return "Passed";
            }

            if (lowered.Contains("skipped") || lowered.Contains("ignored"))
            {
                return "Skipped";
            }

            return "Failed";
        }

        private static TestMode ParseMode(string testMode)
            => testMode == "PlayMode" ? TestMode.PlayMode : TestMode.EditMode;

        private static bool Empty(string[] values) => values == null || values.Length == 0;

        // 一趟走完既数叶子 (totalTests) 又记全名 (身份判据)。计数不用集合的 Count ——
        // 万一两条叶子全名撞了, 进度分母也不该少一个。
        private static void CollectLeaves(
            ITestAdaptor node, TestRunFilter filter, HashSet<string> names, ref int count)
        {
            if (node == null)
            {
                return;
            }

            if (!node.HasChildren)
            {
                // 认领路径下框架给的是整棵树 (实测: PlayMode 全量 RunStarted 报 55 个叶子,
                // 其中 3 个属于没选中的 DemoAcceptance, 而实际只跑 52 个)。不按本轮 filter
                // 筛一遍, 进度分母就永远比 completed 大, 看着像"卡在最后几个用例上"。
                // 复用预检那两个判据, 两处口径必须一致。
                if (filter != null &&
                    (!MatchesAssembly(node, filter.AssemblyNames) ||
                     !MatchesGroup(node.FullName ?? node.Name, filter.GroupNames)))
                {
                    return;
                }

                count++;
                string fullName = node.FullName ?? node.Name;
                if (!string.IsNullOrEmpty(fullName))
                {
                    names.Add(fullName);
                }

                return;
            }

            foreach (var child in node.Children)
            {
                CollectLeaves(child, filter, names, ref count);
            }
        }

        // "能证明是别人的账" 才丢: 本轮清单非空、格式已被验证对得上 (至少命中过一条)、
        // 而这条名字不在清单里。清单空或格式未验证时一律放行 —— 宁可放进一条脏数据,
        // 也不能因为名字格式不一致把本轮真实结果整体吞掉。
        private bool IsForeignLeaf(string fullName)
        {
            if (expectedLeafNames.Count == 0 || string.IsNullOrEmpty(fullName))
            {
                return false;
            }

            if (expectedLeafNames.Contains(fullName))
            {
                leafNamesTrusted = true;
                return false;
            }

            if (!leafNamesTrusted)
            {
                return false;
            }

            if (!foreignLeafReported)
            {
                foreignLeafReported = true;
                Debug.LogWarning(
                    "Unity MCP: ignoring test result from another test run (not in this run's test list): "
                    + fullName + ". Later ones are ignored silently.");
            }

            return true;
        }

        // RunFinished 的结果树是否属于本轮: 命中一条就够, 不用走完。
        private bool OverlapsExpectedLeaves(ITestAdaptor node)
        {
            if (node == null || expectedLeafNames.Count == 0)
            {
                return true;
            }

            if (!node.HasChildren)
            {
                string fullName = node.FullName ?? node.Name;
                return !string.IsNullOrEmpty(fullName) && expectedLeafNames.Contains(fullName);
            }

            foreach (var child in node.Children)
            {
                if (OverlapsExpectedLeaves(child))
                {
                    return true;
                }
            }

            return false;
        }

        // 预检匹配数。assemblyNames 大小写不敏感全等; groupNames 是非锚定正则
        // —— 与 Unity 自己的 UITestRunnerFilter 语义一致。
        private static void CountLeaves(ITestAdaptor node, TestRunFilter filter, ref int matched)
        {
            if (node == null)
            {
                return;
            }

            if (node.HasChildren)
            {
                foreach (var child in node.Children)
                {
                    CountLeaves(child, filter, ref matched);
                }

                return;
            }

            if (!MatchesAssembly(node, filter.AssemblyNames))
            {
                return;
            }

            if (!MatchesGroup(node.FullName, filter.GroupNames))
            {
                return;
            }

            matched++;
        }

        private static bool MatchesAssembly(ITestAdaptor leaf, string[] assemblyNames)
        {
            if (Empty(assemblyNames))
            {
                return true;
            }

            string assembly = null;
            var node = leaf;
            while (node != null && assembly == null)
            {
                if (!string.IsNullOrEmpty(node.Name) && node.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    assembly = node.Name.Substring(0, node.Name.Length - 4);
                }

                node = node.Parent;
            }

            if (assembly == null)
            {
                return false;
            }

            foreach (var candidate in assemblyNames)
            {
                if (string.Equals(candidate, assembly, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool MatchesGroup(string fullName, string[] groupNames)
        {
            if (Empty(groupNames))
            {
                return true;
            }

            foreach (var pattern in groupNames)
            {
                try
                {
                    // TestRunCommand 只校验正则能编译, 不校验回溯代价; 病态正则会锁死主线程,
                    // 必须带超时。超时按"没匹配上"处理 —— 宁可漏选这条测试也不能让 Editor 卡死。
                    if (Regex.IsMatch(fullName ?? string.Empty, pattern, RegexOptions.None, GroupRegexTimeout))
                    {
                        return true;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                }
            }

            return false;
        }
    }
}
