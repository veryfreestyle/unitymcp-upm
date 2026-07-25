# VeryFS UnityMCP

Exposes Unity Editor functionality via [Model Context Protocol (MCP)](https://modelcontextprotocol.io), enabling AI tools to interact with the Unity Editor directly.

## Requirements

- Unity 2021.3 LTS or later
- [FairyGUI](https://github.com/veryfreestyle/fairygui-upm)
- [LitJson](https://github.com/veryfreestyle/litjson-upm)
- [UniTask](https://github.com/Cysharp/UniTask)

## Installation

Add the following to your project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.veryfreestyle.unity.fairygui": "https://github.com/veryfreestyle/fairygui-upm.git",
    "com.veryfreestyle.unity.litjson": "https://github.com/veryfreestyle/litjson-upm.git",
    "com.cysharp.unitask": "https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask",
    "com.veryfreestyle.unitymcp": "https://github.com/veryfreestyle/unitymcp-upm.git"
  }
}
```

## Agent 提示词

粘下面这节进项目的 `CLAUDE.md`（或 `AGENTS.md`），让 agent 默认走 MCP 工具而不是命令行。替换 `<UNITY_VERSION>`、`<PROJECT_PATH>`、`<TEST_ASSEMBLY>`；不用代理就删代理那行。

---

### Unity 开发工作流

项目已接入 `very-unity-mcp`。**默认走 MCP 工具，不走命令行**，前提是有交互式 Editor 会话且 `health` 报 `unityConnected: true`。

- 编译反馈：`assets-refresh`（回传 `error CS`），第一选择。
- 跑测：`test-run`。`assemblyNames` 必填（如 `<TEST_ASSEMBLY>`）；`groupNames` 是非锚定正则，可按 namespace 收窄；`testMode` 取 `EditMode`（默认）/ `PlayMode`。
- 进度与终态：`test-status`。日志：`console`。其余 Editor 操作：`editor-application-set-state`、`scene`、`gameobject`、`screenshot-game-view`。

跑测约束：

- **先 `assets-refresh` 再 `test-run`**，否则跑的是上次编译的程序集。
- 单次 MCP 调用约 60 秒超时，套件要跑一两分钟。**超时不等于失败**：轮询 `test-status`，`running: false` 后从 `lastRun` 取终态。
- 跑测期间只放行 `test-status` / `console` / `screenshot-game-view`，其余返回 `tests_running`。
- 有未保存场景或已在 play mode 时直接拒（`unsaved_scenes` / `invalid_editor_state`）。
- 测试红不是 RPC 错误：`state` 仍是 `succeeded`，红绿看 `summary.failed`；零匹配报 `no_tests_matched`，不会假绿。
- `blockedReason: editor_unfocused` 是失焦导致的慢，不是卡死。
- PlayMode 跑测会临时改 `ProjectSettings/EditorSettings.asset`，还原刷盘时顺带落盘其它脏资源。commit 前查 `git status`，无关的 `ProjectSettings/` 改动 `git checkout --` 掉。

#### 交互式 Editor

```bash
HTTPS_PROXY=http://127.0.0.1:7892 NO_PROXY=127.0.0.1,localhost \
/Applications/Unity/Hub/Editor/<UNITY_VERSION>/Unity.app/Contents/MacOS/Unity \
  -projectPath <PROJECT_PATH> -logFile /tmp/unity-editor.log &
```

就绪要 15–25 秒（判据 `health`），关闭用 `pkill -f "Unity.app/Contents/MacOS/Unity"`。**同时只能有一个 Unity 实例**（License 冲突），启动前 `pgrep -fl "Unity.app/Contents/MacOS/Unity"`；有编译错误时会弹模态框卡住就绪，先用 batchmode 验证。

#### 后备：batchmode CLI

只在没有可用 Editor 会话时用。`<UNITY>` 同上路径，代理变量同样内联。

```bash
# 编译：看输出有无 error CS
<UNITY> -batchmode -nographics -projectPath <PROJECT_PATH> -logFile - -quit

# 跑测：不能带 -quit；红绿看 XML 头部 total/passed/failed，别 grep 日志
<UNITY> -batchmode -nographics -projectPath <PROJECT_PATH> \
  -runTests -testPlatform EditMode -assemblyNames <TEST_ASSEMBLY> \
  -testResults /tmp/editmode-results.xml -logFile -
```

---

## License

MIT
