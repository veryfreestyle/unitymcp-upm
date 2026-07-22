namespace VeryFS.UnityMCP.Editor.Protocol
{
    public static class RpcMethods
    {
        public const string UnityRegister = "unity.register";
        public const string UnityHeartbeat = "unity.heartbeat";
        public const string RequestsReport = "requests.report";
        public const string AssetsRefresh = "assets.refresh";
        public const string EditorApplicationGetState = "editor.application.get-state";
        public const string EditorApplicationSetState = "editor.application.set-state";
        public const string ConsoleGetLogs = "console.get-logs";
        public const string ConsoleClearLogs = "console.clear-logs";
        public const string ScreenshotGameView = "screenshot.game-view";
        public const string FairyGuiGetTree = "fgui.get-tree";
        public const string FairyGuiListPanels = "fgui.list-panels";
        public const string FairyGuiCallEvent = "fgui.call-event";
        public const string FairyGuiSetText = "fgui.set-text";
        public const string FairyGuiSetController = "fgui.set-controller";
        public const string FairyGuiSetValue = "fgui.set-value";
        public const string FairyGuiSetSelection = "fgui.set-selection";
        public const string FairyGuiScroll = "fgui.scroll";
        public const string FairyGuiTransition = "fgui.transition";
        public const string FairyGuiFocus = "fgui.focus";
        public const string FairyGuiClick = "fgui.click";
        public const string FairyGuiDoubleClick = "fgui.double-click";
        public const string FairyGuiGesture = "fgui.gesture";
        public const string FairyGuiHover = "fgui.hover";
        public const string GameObjectFind = "gameobject.find";
        public const string GameObjectComponentGet = "gameobject.component-get";
        public const string EditorSceneGet = "editor.scene.get";
        public const string EditorSceneOpen = "editor.scene.open";
        public const string EditorSceneSave = "editor.scene.save";
        public const string BatchExecute = "batch.execute";

        // P9 聚合组路由 key (一组一个 method)。
        public const string FairyGuiInputGroup = "fgui.input";
        public const string FairyGuiStateGroup = "fgui.state";
        public const string FairyGuiQueryGroup = "fgui.query";
        public const string ConsoleGroup = "console";
        public const string SceneGroup = "editor.scene";
        public const string GameObjectGroup = "gameobject";
    }
}
