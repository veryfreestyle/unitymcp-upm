using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Infrastructure;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    /// <summary>batch 入口用的钩子。legacy 装配下由 McpNullImplicitSessionHost 兜底。</summary>
    public interface IMcpImplicitSessionHost
    {
        IDisposable BeginImplicitSession();

        // 跟 BeginImplicitSession 配对的收尾: 批的 using 块不能直接 Dispose(那样同步),
        // 因为真正关闭前如果有按钮还按着, 必须先跑一次 ReleaseHeld 让业务收到 onTouchEnd
        // (review Important 4) —— 这一步要推帧, 只能是异步的。legacy 装配下
        // McpNullImplicitSessionHost 没有任何真实输入, 直接同步 Dispose 即可。
        UniTask EndImplicitSessionAsync(IDisposable scope);
    }

    public sealed class McpNullImplicitSessionHost : IMcpImplicitSessionHost
    {
        private sealed class NoOp : IDisposable { public void Dispose() { } }
        public IDisposable BeginImplicitSession() => new NoOp();

        public UniTask EndImplicitSessionAsync(IDisposable scope)
        {
            scope?.Dispose();
            return UniTask.CompletedTask;
        }
    }

    /// <summary>
    /// Acquire 的结果。Error 非 null 时 Player 为 null, 命令层直接把 Error 当 state 返回。
    /// </summary>
    public sealed class McpInputLease : IDisposable
    {
        private readonly McpStageInputSessionManager owner;

        internal McpInputLease(McpStageInputSessionManager owner,
            McpStageInputPlayer player, bool owning, string error)
        {
            this.owner = owner;
            Player = player;
            Owning = owning;
            Error = error;
        }

        public McpStageInputPlayer Player { get; }
        public bool Owning { get; }
        public string Error { get; }

        public void Dispose()
        {
            if (Owning) { owner.ReleaseOwned(); }
        }
    }

    /// <summary>
    /// Acquire 三态: 无显式 session 则独占接管、命令结束归还; 有显式 session 则租借、
    /// Dispose 是 no-op; batch 执行中由批入口开隐式 session。命令代码只写
    /// using var lease = sessions.Acquire("fgui.input.click"), 不判断自己在哪种模式。
    /// </summary>
    public sealed class McpStageInputSessionManager : IMcpImplicitSessionHost
    {
        private readonly IMcpStageInput input;
        private readonly string projectRoot;
        private readonly Func<double> clock;
        private readonly HashSet<int> pressedButtons = new HashSet<int>();

        private McpStageInputPlayer player;
        private string label;
        private double startedAt;
        private bool explicitSession;
        private int implicitDepth;

        // 进程内(= 本 domain 内)是否接管过。fork 的 ScriptedInputSource.mousePosition
        // 跨会话延续, 但第一次接管时它还是 (0,0), 不同步就会从屏幕角落划过整个界面。
        private bool everStarted;
        private bool visualizerApplied;

        public McpStageInputSessionManager(IMcpStageInput input, string projectRoot, Func<double> clock)
        {
            this.input = input ?? throw new ArgumentNullException(nameof(input));
            this.projectRoot = projectRoot;
            this.clock = clock ?? (() => EditorApplication.timeSinceStartup);
        }

        public bool HasSession => player != null && (explicitSession || implicitDepth > 0);
        public string SessionLabel => HasSession ? label : null;
        public double SessionAgeSeconds => HasSession ? clock() - startedAt : 0.0;

        public McpInputLease Acquire(string leaseLabel)
        {
            if (!input.IsPlaying)
            {
                return new McpInputLease(this, null, false, "not_playing");
            }
            if (player != null)
            {
                return new McpInputLease(this, player, false, null);
            }

            StartSession(leaseLabel);
            return new McpInputLease(this, player, true, null);
        }

        // 异步的理由跟 EndExplicit 一样: force 抢占要把上一个 session 关掉, 关掉之前必须先跑
        // 一次 ReleaseHeld 让业务收到 onTouchEnd。CloseSession 走的是 input.Dispose ->
        // fork 的 Restore()/ResetAll(), 那条路径按 fork 自己的说法只把 _upFrame 抹成 -1、
        // 不补写抬起帧, FairyGUI 永远读不到 GetMouseButtonUp —— 被抢占时手上还按着的按钮
        // 就此永久停在按下态。实测: press 一个 GButton 之后 begin-session force:true,
        // 该按钮的 button 控制器一直停在 "down", 移开指针、等若干帧都不恢复。
        //
        // ForceRelease 故意不做这件事: 它的两个调用点(退出 Play 模式、测试 teardown)都
        // 已经没有"业务状态机还要继续跑"的现场, 而且都是同步路径推不了帧。
        public async UniTask<string> BeginExplicit(string sessionLabel, bool force)
        {
            if (!input.IsPlaying) { return "not_playing"; }

            if (player != null)
            {
                if (!force) { return "conflict"; }
                Debug.LogWarning("[UnityMCP] fgui-input session '" + label
                    + "' was preempted by '" + sessionLabel + "' (force: true).");

                // explicitSession 必须在 CloseSession() 之前清掉, 不是之后 ——
                // CloseSession 本身也会抛(input.Dispose 抛出是真实路径, 不是假设:
                // 见 EndExplicit_WhenDisposeThrows... 的测试注释)。如果先调
                // CloseSession() 再清标志, 一旦它在这一步就抛出, 异常会在标志清掉
                // 之前就穿出 BeginExplicit —— 现场留下 player == null(CloseSession
                // 的 finally 已经摘掉了)但 explicitSession 还残留 true 的鬼状态,
                // 挡住后续 Acquire 在自己 Dispose 里的自助归还, 和"新 StartSession
                // 抛出"是同一个根因、只是换了触发点。这里镜像 ForceRelease() 的顺序:
                // 标志先清, 再做会抛的收尾动作。
                //
                // implicitDepth 故意不在这里清零(不管清零发生在哪一步): 它是"还有
                // 几层 using BeginImplicitSession() 没退出"的计数, 与当前 player 是
                // 谁无关 —— 被强占的若是一个 batch session, 外层 scope 还没走到自己
                // 的 using 块末尾, 它退出时仍要能把 depth 正确减到 0, 让 EndExplicit /
                // 后续治理判断保持准确; 提前清零会把这个计数冲成负数, 永远凑不回 0,
                // 反而让显式 session 结束时该关的 CloseSession 永远不触发。
                //
                // try/finally 的形状跟 EndExplicit 一致: ReleaseHeldBeforeCloseAsync 会抛是
                // 真实路径(binding.Run 的四道门), 抛出时 explicitSession 清零和 CloseSession()
                // 仍然要跑, 否则又回到"关闭动作被跳过"那个泄漏形状。异常原样穿出。
                try
                {
                    await ReleaseHeldBeforeCloseAsync();
                }
                finally
                {
                    explicitSession = false;
                    CloseSession();
                }
            }

            StartSession(sessionLabel);
            explicitSession = true;
            return null;
        }

        // 异步: 真正要关闭底层 session 之前(implicitDepth == 0, 没有别的批次还压着),
        // 如果还有按钮按着, 必须先跑一次 ReleaseHeld 序列让业务的拖拽状态机收到
        // onTouchEnd(review Important 4, 呼应 McpStageInputGateway.cs 里超时走 Cancel
        // 而不直接 Dispose 的同一条道理)。implicitDepth > 0 时这次调用不会真正关闭
        // (关闭动作让给 batch 的 EndImplicitSessionAsync), 不需要在这里释放。
        //
        // ReleaseHeldBeforeCloseAsync 是会抛的真实路径(input.RunAsync -> binding.Run 的
        // 四道门之一没过就同步抛, McpStageInputGateway.cs 的 XML doc 明说调用方必须
        // try/catch, 不能只看返回值)——最现实的触发: Play 模式退出到一半、批里还有按钮
        // 按着, "不在 Play 模式"这道门先炸。try/finally 保证即使它抛出, explicitSession
        // 清零和 CloseSession() 仍然跑, 不会让治理标志和底层 session 停在半路(Critical 1
        // 修的正是"关闭动作被跳过"这个形状, 这里不能在新 seam 上重新引入同一个洞)。
        // 异常本身原样穿出去, 不吞——调用方(FguiInputEndSessionCommand 的 Guarded)需要
        // 看到真正失败的原因。
        public async UniTask<bool> EndExplicit()
        {
            if (!explicitSession) { return false; }
            if (implicitDepth == 0)
            {
                try
                {
                    await ReleaseHeldBeforeCloseAsync();
                }
                finally
                {
                    explicitSession = false;
                    CloseSession();
                }
            }
            else
            {
                explicitSession = false;
            }
            return true;
        }

        public IDisposable BeginImplicitSession()
        {
            // 批开始时已有显式 session 就沿用, 不另开; 批尾关不关跟"是不是这次自己开的"
            // 无关, 见 ImplicitScope.Dispose 的注释。
            if (player == null && input.IsPlaying)
            {
                StartSession("batch-execute");
            }
            implicitDepth++;
            return new ImplicitScope(this);
        }

        // IMcpImplicitSessionHost.EndImplicitSessionAsync 的真实实现。跟 EndExplicit 用
        // 同一个"是否真的会关闭"判断口径: implicitDepth 减到这次 Dispose 之前的值为 1
        // (减完就是 0)且没有显式 session 挡着, 才是"这一步真的会调 CloseSession"——跟
        // ImplicitScope.Dispose 自己减完之后判断的是同一件事, 只是提前一步问, 好在
        // Dispose(同步)之前把要推帧的 ReleaseHeld 跑完。
        //
        // scope.Dispose() 必须放进 finally, 不能跟着 await 顺序往下写: ReleaseHeldBeforeCloseAsync
        // 会抛是真实路径(同 EndExplicit 顶上的注释), 抛出的话如果 Dispose() 在它后面顺序执行,
        // 就会被跳过——ImplicitScope.Dispose() 正是 implicitDepth-- 和最终 CloseSession() 发生的
        // 地方, 跳过它等于让 implicitDepth 永远不减、session 永远不关, 从新 seam 里重新长出
        // Critical 1 刚修掉的那个泄漏形状(唯一区别是这次连"批结束"这个动作本身都没有发生)。
        // 用 using 语句写不出"批开始时已有显式 session 沿用"这种带条件的收尾, 所以只能手写
        // try/finally 去复刻 using 本来就会给的保证。异常原样穿出, 不吞。
        public async UniTask EndImplicitSessionAsync(IDisposable scope)
        {
            try
            {
                if (implicitDepth == 1 && !explicitSession)
                {
                    await ReleaseHeldBeforeCloseAsync();
                }
            }
            finally
            {
                scope?.Dispose();
            }
        }

        // 尽力而为: 没有按钮按着或者没有 player 就什么都不做。真正调用 ReleaseHeld 需要
        // 推帧(IEnumerator 经 input.RunAsync 跑), 所以只能在能 await 的调用点用 ——
        // 这正是 review 划的两个seam(FguiInputEndSessionCommand / batch 的隐式 scope
        // 收尾), 都不是 lease 的 Dispose 路径(那条必须保持同步, 见 McpInputLease.Dispose)。
        private async UniTask ReleaseHeldBeforeCloseAsync()
        {
            if (player == null || pressedButtons.Count == 0) { return; }
            await input.RunAsync(player.ReleaseHeld());
        }

        public void ForceRelease(string reason)
        {
            if (player == null) { return; }
            Debug.LogWarning("[UnityMCP] fgui-input session '" + label + "' released: " + reason);
            explicitSession = false;
            implicitDepth = 0;
            CloseSession();
        }

        public bool IsButtonPressed(int button) => pressedButtons.Contains(button);
        public void NotePressed(int button) => pressedButtons.Add(button);
        public void NoteReleased(int button) => pressedButtons.Remove(button);
        public void ClearPressed() => pressedButtons.Clear();

        internal void ReleaseOwned()
        {
            if (explicitSession || implicitDepth > 0) { return; }
            CloseSession();
        }

        private void StartSession(string sessionLabel)
        {
            player = input.Start(sessionLabel, !everStarted);
            everStarted = true;
            label = sessionLabel;
            startedAt = clock();
            pressedButtons.Clear();

            // 面板缺省 on。只在本 domain 首次接管时应用一次 —— 每次接管都重设会
            // 把上一条 visualize 命令显式改过的样式静默清掉。
            if (!visualizerApplied)
            {
                visualizerApplied = true;
                if (FguiInputPreferences.LoadVisualizerEnabled(projectRoot))
                {
                    input.UseDefaultVisualizer(null);
                }
                else
                {
                    input.DisableVisualizer();
                }
            }

            // 样式只应用一次, 但标记每次接管都要清。visualizer 是跨会话持久的单例, 谁都不清
            // 它 —— 上一个会话留下的光标标记会一直挂在画面上, 攒够几十个会话(比如跑一遍
            // PlayMode 全量)整个 Game View 就是一地箭头。可视化存在的意义是"这次输入落在
            // 哪", 截图里混进上一次甚至上一个测试类留下的标记只会误导。
            //
            // 清的是已画的标记, 不是样式, 所以跟上面"只应用一次"那条不冲突; 同一个会话内
            // 的轨迹照常累积(那正是想看的)。ClearVisualizer 对没有 visualizer 的情况是
            // 明确的 no-op, 不用先判空。
            input.ClearVisualizer();
        }

        private void CloseSession()
        {
            if (player == null) { return; }
            McpStageInputPlayer closing = player;
            try
            {
                input.Dispose(closing);
            }
            finally
            {
                // 即使 input.Dispose 抛出(异常原样向上传, 不吞), 也要先把 player 摘掉 ——
                // 否则后续调用会把一个"已经出过异常、状态不明"的 player 当成还能续租的
                // 活 session, 把治理标志和 fork 侧真实状态进一步撕裂开。
                player = null;
                label = null;
                pressedButtons.Clear();
            }
        }

        private sealed class ImplicitScope : IDisposable
        {
            private readonly McpStageInputSessionManager owner;
            private bool disposed;

            public ImplicitScope(McpStageInputSessionManager owner)
            {
                this.owner = owner;
            }

            public void Dispose()
            {
                if (disposed) { return; }
                disposed = true;
                owner.implicitDepth--;
                // "opened" (是不是这次 BeginImplicitSession 自己开的 session) 故意不参与这个
                // 判断: EndExplicit() 在 implicitDepth > 0 时会把 explicitSession 清成 false
                // 但把关闭动作让给这里 —— 如果这里只在 opened==true 时才关, "批开始时已有
                // 显式 session 沿用(opened=false)、批内又把它 end 掉"这条合法调用序列就会让
                // player 永久留在现场却让 HasSession 报 false(review Critical 1): 后续
                // press/release/step 撞 session_required, begin-session 撞 conflict, 两头堵死,
                // 唯一出路是 force:true。这里只看"是不是最外层退出"和"有没有别的治理者
                // (显式 session)在挡着", 跟这次 scope 是不是自己开的无关。
                if (owner.implicitDepth == 0 && !owner.explicitSession)
                {
                    owner.CloseSession();
                }
            }
        }
    }
}
