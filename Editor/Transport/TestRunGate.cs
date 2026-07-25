using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Transport
{
    // 测试运行期间的方法白名单。测试跑动时 Editor 归测试独占, 每一条放行都对应
    // 一个跑测中必须仍然可达的理由:
    //   test.status        - 查询本次跑测进度/结果的唯一途径。
    //   console (组路由)   - console 现在是聚合工具, 客户端调用时 method 就是这个组路由
    //                         key, 不放行它 console-get-logs 实际上永远打不到 —— 卡死时
    //                         看控制台是唯一能判断"卡在哪"的诊断手段。组内 console-clear-logs
    //                         也随组放行: 它只清空控制台缓冲, 碰不到跑测本身, 无害。
    //   console.get-logs   - 独立方法名保留放行, 万一有调用方绕开组路由直接按老方法名调用。
    //   screenshot.game-view - 卡死时能直接看画面状态, 同属诊断手段。
    //   unity.heartbeat    - 必须放行, 否则测试跑几分钟就会被判定为连接失联。
    //   requests.report    - 长任务的终态推送走这个方法, 挡住就永远收不到 test.run 自己的结果。
    public static class TestRunGate
    {
        public static bool IsAllowedDuringTestRun(string method)
        {
            if (string.IsNullOrEmpty(method))
            {
                return false;
            }

            return method == RpcMethods.TestStatus
                || method == RpcMethods.ConsoleGroup
                || method == RpcMethods.ConsoleGetLogs
                || method == RpcMethods.ScreenshotGameView
                || method == RpcMethods.UnityHeartbeat
                || method == RpcMethods.RequestsReport;
        }
    }
}
