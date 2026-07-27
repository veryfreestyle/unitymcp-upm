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

## 安装 Agent Skill

打开 Unity 项目后，UnityMCP 会启动项目本地 MCP server。通过 Unity UI 可以同时完成 MCP 配置写入和 Agent Skill 安装。

1. 打开 `Window/UnityMCP - VeryFS`，确认 MCP Server 状态为 `Connected`。
2. 在 `Agent Clients` 区域勾选需要配置的客户端。默认启用 `Claude` 和 `Codex`，`OpenCode` 默认关闭，需要时手动开启。
3. 点击 `Install Agent Skill`，为已启用客户端安装或更新 `unitymcp` skill。

![UnityMCP window](monitor.jpg)

UI 会按勾选项维护这些项目内文件：

- MCP 配置：`.mcp.json`、`.codex/config.toml`、`.opencode/opencode.json`
- Agent Skill：`.agents/skills/unitymcp/SKILL.md`、`.opencode/skills/unitymcp/SKILL.md`

升级 UnityMCP 后，再次点击 `Install Agent Skill` 即可更新生成内容。如果 UI 提示存在用户维护的 `Custom` skill，确认后才会覆盖。

## MCP 工具一览

Server 通过 `tools/list` 暴露以下工具（含 `install-agent-skill` 自身；生成 skill 时默认排除该工具，避免 agent 递归安装）：

| Tool | Access | Completion | Purpose |
|---|---|---|---|
| `assets-refresh` | mutating | report | Trigger AssetDatabase.Refresh() with empty params and wait for the terminal compilation report. |
| `batch-execute` | mutating | response | Run a sequence of RPC sub-commands serially in one call and return their aggregated results. Each entry is {"tool": <rpcMethod>, "params": {...}}. Sub-commands run in order; results align by index. failFast (default false): stop after the first failing sub-command (a sub-command fails when it returns an error). Pseudo-command "wait" pauses between steps: {"tool":"wait","params":{"ms":500}} or {"tool":"wait","params":{"frames":3}} (ms and frames are mutually exclusive; no upper bound — bound the whole batch via timeoutMs). NOT supported as sub-commands: assets.refresh, editor.application.set-state, test.run (long-running), and batch.execute (no nesting). |
| `console` | mutating | response | Read or clear the Unity console log buffer. action: get-logs \| clear-logs. |
| `editor-application-get-state` | read-only | response | Return EditorApplication state: playmode, paused, compilation, and related flags. |
| `editor-application-set-state` | mutating | report | Start/stop/pause playmode. Refuses when the project has compilation errors. Blocks until the play-mode transition completes (survives domain reload) and returns the post-transition state. |
| `fgui-input` | mutating | response | Drive FairyGUI objects through the real input pipeline (async, cross-frame). action: click \| double-click \| hover \| gesture. Play mode only. |
| `fgui-query` | mutating | response | Inspect the live FairyGUI hierarchy. action: get-tree \| list-panels. |
| `fgui-state` | mutating | response | Read/write FairyGUI object state synchronously. action: set-text \| set-value \| set-controller \| set-selection \| scroll \| transition \| focus \| call-event. Play mode only. |
| `game-view` | mutating | response | Inspect or change the current Game View. action: get-state \| list-resolutions \| set-resolution \| set-maximized. |
| `gameobject` | mutating | response | Locate GameObjects and read their components in the open scene. action: find \| component-get. |
| `health` | read-only | response | Return the Unity MCP server status and Unity editor connection state. Never errors; a disconnected editor is reported as data. |
| `install-agent-skill` | mutating | response | Generate and install a UnityMCP agent skill for the current project. |
| `scene` | mutating | response | Query or mutate the open Editor scene(s). action: get \| open \| save. |
| `screenshot-game-view` | read-only | response | Capture the Editor Game View and return it as an image for visual inspection. Requires an open Game View. |
| `test-run` | mutating | report | Run Unity Test Runner tests for the given assemblies and wait for the terminal result. Call assets-refresh first so tests run against the latest compiled assemblies. Refused when the project has compilation errors, when the editor is compiling or importing, when any loaded scene has unsaved changes, or when already in play mode. While a run is in progress every tool except test-status, console (get-logs / clear-logs) and screenshot-game-view returns editor_busy; the transport's own unity.heartbeat and requests.report stay open so the run can report back. timeoutMs is a wall-clock ceiling on the whole call: exceeding it answers with errorCode request_timeout. Unity cannot cancel a running test run, so the tests keep going and other tools stay refused with tests_running until the run actually stops; poll test-status. Returns a summary plus every failing test; pass includeDetails to also get passing tests. |
| `test-status` | read-only | response | Read the current test run progress or the most recent finished run. Allowed while a test run is in progress. Use it after a test-run call times out to recover the result, or to see why a run appears stuck (blockedReason). |

## 在 CLAUDE.md / AGENTS.md 中提示 Agent

如果项目已有 `CLAUDE.md` 或 `AGENTS.md`，可以加入下面这段简短提示：

```markdown
## UnityMCP

When working on this Unity project, use the project-local `unitymcp` skill. Prefer UnityMCP MCP tools for Editor status, compilation, tests, logs, scene/Game View inspection, and Editor automation.

If the `unitymcp` skill is missing or stale, ask the user to open `Window/UnityMCP - VeryFS`, enable the desired Agent Clients, and click `Install Agent Skill`.
```

---

## License

MIT
