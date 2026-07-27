using System;
using System.Collections.Generic;
using System.IO;
using LitJson;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor.Infrastructure;
using VeryFS.UnityMCP.Editor.Commands;
using VeryFS.UnityMCP.Editor.Commands.AgentSkill;
using VeryFS.UnityMCP.Editor.Commands.Console;
using VeryFS.UnityMCP.Editor.Protocol;
using VeryFS.UnityMCP.Editor.Commands.Editor;
using VeryFS.UnityMCP.Editor.Commands.FairyGUI;
using VeryFS.UnityMCP.Editor.Commands.GameView;
using VeryFS.UnityMCP.Editor.Commands.Scene;
using VeryFS.UnityMCP.Editor.Commands.Screenshot;
using VeryFS.UnityMCP.Editor.Commands.Testing;
using VeryFS.UnityMCP.Editor.Infrastructure;
using VeryFS.UnityMCP.Editor.Lifecycle;
using VeryFS.UnityMCP.Editor.Logs;
using VeryFS.UnityMCP.Editor.Persistence;
using VeryFS.UnityMCP.Editor.Transport;

namespace VeryFS.UnityMCP.Editor
{
    [InitializeOnLoad]
    internal static class UnityMcpPlugin
    {
        private static readonly PendingRequestStore pendingRequestStore;
        private static readonly AssetsRefreshCommand assetsRefreshCommand;
        private static readonly RpcCommandRegistry registry;
        // Field retained to prevent GC of production connection loop
        private static RpcConnectionLoop productionLoop;
        private static TestRunTracker testRunTracker;
        private static TestRunCommand testRunCommand;
        // Main-thread mirror of testRunTracker.IsRunning, read by the transport thread's
        // test-run gate. The tracker is backed by SessionState, which throws
        // "can only be called from the main thread" off the main thread -- and that
        // exception would be swallowed in OnMessageReceived, leaving every inbound
        // request without a response. One frame of staleness is the acceptable price.
        private static volatile bool testRunInProgress;

        static UnityMcpPlugin()
        {
            pendingRequestStore = new PendingRequestStore(Path.Combine(
                Directory.GetParent(Application.dataPath).FullName,
                "Library",
                "VeryFreestyle.UnityMcp",
                "requests"));
            assetsRefreshCommand = new AssetsRefreshCommand(
                new UnityAssetDatabase(),
                new UnityEditorBusyState(),
                pendingRequestStore,
                new SystemClock());
            registry = new RpcCommandRegistry();
            registry.Register(assetsRefreshCommand);
            var stateProvider = new UnityEditorStateProvider();
            registry.Register(new GetApplicationStateCommand(stateProvider));
            registry.Register(new SetApplicationStateCommand(
                new UnityPlayModeController(), stateProvider, new UnityEditorBusyState(),
                pendingRequestStore, new SystemClock()));
            // Console: read entries directly from the native Console buffer (UnityEditor.LogEntries)
            // so get-logs matches the Console window -- including editor-internal/native entries
            // that never fire Application.logMessageReceived. Wrapped so a wiring failure can never
            // break [InitializeOnLoad] (which also runs while the test domain loads).
            try
            {
                var consoleReader = new EditorConsoleLogReader();
                registry.RegisterGroup(new RpcGroupDefinition
                {
                    Group = RpcMethods.ConsoleGroup, ToolName = "console",
                    Title = "Console",
                    Description = "Read or clear the Unity console log buffer. action: get-logs | clear-logs."
                });
                registry.Register(new ConsoleGetLogsCommand(consoleReader));
                registry.Register(new ConsoleClearLogsCommand(consoleReader, new UnityEditorBusyState()));
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("Unity MCP: console wiring failed. " + ex.Message);
            }
            // Test Runner: 长任务命令 + 只读状态查询。两者都是独立工具, 不进聚合组
            // (P9 划定: 长任务不进组)。包在 try 里, 装配失败不能拖垮 [InitializeOnLoad]。
            try
            {
                var testTracker = new TestRunTracker(new SessionStateTestRunStore(), new SystemClock());
                var testCommand = new TestRunCommand(
                    new UnityTestRunner(),
                    new UnityEditorBusyState(),
                    new UnityPlayModeController(),
                    new UnitySceneGateway(),
                    testTracker,
                    pendingRequestStore,
                    new UnityEditorActivator(),
                    new SystemClock());
                // 字段赋值和 update 订阅必须排在两个 Register 前面: 万一下面
                // TestStatusCommand 的构造抛异常, test-run 依然会被注册成可调用命令,
                // 但 Tick() 的驱动和 gate 读的 testRunInProgress 镜像字段不能因此漏挂 ——
                // 否则 init 超时永远不触发, gate 标志也永远不刷新。
                testRunTracker = testTracker;
                testRunCommand = testCommand;
                EditorApplication.update += OnEditorUpdate;
                registry.Register(testCommand);
                registry.Register(new TestStatusCommand(
                    testTracker, new UnityEditorBusyState(), new UnityEditorFocusState(), new SystemClock()));
                registry.Register(new TestListCommand(
                    new UnityTestListProvider(), new UnityEditorBusyState(), new UnityPlayModeController()));
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("Unity MCP: test runner wiring failed. " + ex.Message);
            }
            // RpcConnectionLoop.StartAsync loads and recovers pending records after registration.

            // Ensure the project's server is up (auto-launch / reuse / refuse),
            // then connect only when we own it. Tokens survive Domain Reload.
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            var gameViewEnvironment = new UnityGameViewEnvironment();
            var gameViewSettler = new GameViewSettleWaiter(
                new UnityEditorUpdateAwaiter(), new SystemClock());
            registry.RegisterGroup(new RpcGroupDefinition
            {
                Group = RpcMethods.GameViewGroup,
                ToolName = "game-view",
                Title = "Game View",
                Description = "Inspect or change the current Game View. " +
                    "action: get-state | list-resolutions | set-resolution | set-maximized."
            });
            registry.Register(new GameViewGetStateCommand(gameViewEnvironment));
            registry.Register(new GameViewListResolutionsCommand(gameViewEnvironment));
            registry.Register(new GameViewSetResolutionCommand(
                gameViewEnvironment, gameViewSettler));
            registry.Register(new GameViewSetMaximizedCommand(
                gameViewEnvironment, gameViewSettler));
            registry.Register(new ScreenshotGameViewCommand(
                new UnityGameViewCapturer(gameViewEnvironment),
                Path.Combine(projectRoot, "Temp", "UnityMCP", "screenshots"),
                new UlidLikeIdGenerator()));
            var panelSource = new FairyGUIPanelSource();
            var stageInput = new UnityStageInput();
            var frameStepper = new UniTaskFrameStepper();
            registry.RegisterGroup(new RpcGroupDefinition
            {
                Group = RpcMethods.FairyGuiQueryGroup, ToolName = "fgui-query",
                Title = "FairyGUI / Query",
                Description = "Inspect the live FairyGUI hierarchy. action: get-tree | list-panels."
            });
            registry.Register(new FairyGUIGetTreeCommand(panelSource));
            registry.Register(new FairyGUIListPanelsCommand(panelSource));
            registry.RegisterGroup(new RpcGroupDefinition
            {
                Group = RpcMethods.FairyGuiStateGroup, ToolName = "fgui-state",
                Title = "FairyGUI / State",
                Description = "Read/write FairyGUI object state synchronously. action: set-text | set-value | " +
                    "set-controller | set-selection | scroll | transition | focus | call-event. Play mode only."
            });
            registry.Register(new FairyGUICallEventCommand(panelSource));
            registry.Register(new FairyGUISetTextCommand(panelSource));
            registry.Register(new FairyGUISetControllerCommand(panelSource));
            registry.Register(new FairyGUISetValueCommand(panelSource));
            registry.Register(new FairyGUISetSelectionCommand(panelSource));
            registry.Register(new FairyGUIScrollCommand(panelSource));
            registry.Register(new FairyGUITransitionCommand(panelSource));
            registry.Register(new FairyGUIFocusCommand(panelSource));
            registry.RegisterGroup(new RpcGroupDefinition
            {
                Group = RpcMethods.FairyGuiInputGroup, ToolName = "fgui-input",
                Title = "FairyGUI / Input",
                Description = "Drive FairyGUI objects through the real input pipeline (async, cross-frame). " +
                    "action: click | double-click | hover | gesture. Play mode only."
            });
            registry.Register(new FairyGUIClickCommand(panelSource, stageInput, frameStepper));
            registry.Register(new FairyGUIDoubleClickCommand(panelSource, stageInput, frameStepper));
            registry.Register(new FairyGUIGestureCommand(panelSource, stageInput, frameStepper));
            registry.Register(new FairyGUIHoverCommand(panelSource, stageInput, frameStepper));
            var sceneGateway = new UnitySceneGateway();
            registry.RegisterGroup(new RpcGroupDefinition
            {
                Group = RpcMethods.SceneGroup, ToolName = "scene",
                Title = "Scene",
                Description = "Query or mutate the open Editor scene(s). action: get | open | save."
            });
            registry.Register(new EditorSceneGetCommand(sceneGateway));
            registry.Register(new EditorSceneOpenCommand(sceneGateway));
            registry.Register(new EditorSceneSaveCommand(sceneGateway));
            registry.RegisterGroup(new RpcGroupDefinition
            {
                Group = RpcMethods.GameObjectGroup, ToolName = "gameobject",
                Title = "GameObject",
                Description = "Locate GameObjects and read their components in the open scene. action: find | component-get."
            });
            registry.Register(new GameObjectFindCommand(new UnityEditorBusyState(), new UnityGameObjectLocator()));
            registry.Register(new GameObjectComponentGetCommand(new UnityEditorBusyState(), new UnityGameObjectLocator()));
            registry.Register(new BatchExecuteCommand(
                registry, new UniTaskFrameStepper(), new UniTaskDelayProvider()));
            AgentSkillRegistration.Register(
                registry,
                projectRoot,
                Application.unityVersion);
            int port = ProjectPortCalculator.GetPort(projectRoot);
            ServerTokens tokens = TokenStore.GetOrCreate();
            int editorPid = System.Diagnostics.Process.GetCurrentProcess().Id;

            var launcher = ServerLauncher.CreateDefault();
            ServerLaunchResult launch = launcher.EnsureServer(
                projectRoot,
                EditorSession.Current.EditorSessionId,
                editorPid,
                port,
                tokens);
            if (launch.ShouldConnect && launch.ServerPid > 0)
            {
                VeryFS.UnityMCP.Editor.Infrastructure.ServerPidHolder.Set(launch.ServerPid);
            }
            if (!launch.ShouldConnect)
            {
                UnityEngine.Debug.LogWarning(
                    "Unity MCP: not connecting this Editor. " + launch.Reason);
                return;
            }

            string url = $"ws://127.0.0.1:{port}/unity";
            UnityEngine.Debug.Log($"Unity MCP: Connecting to {url} (port calculated from project path)");
            var productionDispatcher = new EditorMainThreadDispatcher();
            productionLoop = new RpcConnectionLoop(
                new Uri(url),
                registry,
                pendingRequestStore,
                productionDispatcher,
                EditorSession.Current,
                new UlidLikeIdGenerator(),
                true,
                tokens.UnityToken,
                ensureServerAlive: () =>
                {
                    var result = launcher.EnsureServer(
                        projectRoot,
                        EditorSession.Current.EditorSessionId,
                        editorPid,
                        port,
                        tokens);
                    if (result.ShouldConnect && result.ServerPid > 0)
                    {
                        VeryFS.UnityMCP.Editor.Infrastructure.ServerPidHolder.Set(result.ServerPid);
                    }
                    if (!result.ShouldConnect)
                    {
                        UnityEngine.Debug.LogWarning("Unity MCP: server re-launch refused, stopping reconnect. " + result.Reason);
                    }
                    return result.ShouldConnect;
                },
                testsRunning: () => testRunInProgress);
            _ = productionLoop.StartAsync();

            EditorApplication.quitting += OnEditorQuitting;
        }

        // Main thread. Refreshes the test-run flag before Tick so an exception thrown out
        // of Tick can never starve the refresh and freeze the gate on a stale value; then
        // lets the test command check its own init timeout.
        private static void OnEditorUpdate()
        {
            testRunInProgress = testRunTracker != null && testRunTracker.IsRunning;
            testRunCommand?.Tick();
        }

        private static void OnEditorQuitting()
        {
            // Normal shutdown: drop our connection so the server's PID monitor and
            // /unity disconnect path can retire the owned server instead of leaving
            // an orphan. (The server also self-exits when this editor PID dies.)
            productionLoop?.Dispose();
        }

        internal static string InstallAgentSkillForMonitor(IReadOnlyList<string> clients, bool overwrite)
        {
            if (!registry.TryGet(RpcMethods.AgentSkillInstall, out var command))
            {
                throw new InvalidOperationException("install-agent-skill is not registered.");
            }

            var response = command.Handle(JsonRpcRequest.Create(
                "server-monitor-install-agent-skill",
                RpcMethods.AgentSkillInstall,
                JsonRpcSerializer.Object(
                    ("name", "unitymcp"),
                    ("clients", StringArray(clients)),
                    ("overwrite", overwrite))));

            if (response.Error != null)
            {
                throw new InvalidOperationException(response.Error.Message);
            }

            return "Installed UnityMCP agent skill.";
        }

        internal static RpcConnectionLoop StartForTests(string url)
        {
            var testDispatcher = new EditorMainThreadDispatcher();
            var loop = new RpcConnectionLoop(
                new Uri(url),
                registry,
                pendingRequestStore,
                testDispatcher,
                EditorSession.Current,
                new UlidLikeIdGenerator(),
                true,
                null);
            _ = loop.StartAsync();
            return loop;
        }

        private static JsonData StringArray(IReadOnlyList<string> values)
        {
            var result = new JsonData();
            result.SetJsonType(JsonType.Array);
            foreach (string value in values)
            {
                result.Add(value);
            }
            return result;
        }
    }
}
