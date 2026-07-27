# VeryFS UnityMCP

通过 [Model Context Protocol (MCP)](https://modelcontextprotocol.io) 暴露 Unity Editor 能力，让 AI 直接操作编辑器。完整工具链由 Unity Editor 插件和外部 Go server 两部分组成。

16 个工具：编译反馈、Console 日志、场景与 GameObject 查询、Game View 控制与截图、Test Runner 跑测、FairyGUI 交互闭环、批量执行。UPM 分发，Claude / Codex / OpenCode 一键接入。Unity 2021.3+，Mac / Windows 双平台。

## 设计理念

目标是让 AI 一次指令就跑完整个 TDD 循环：先写测试，确认它失败，再实现到它通过，中途不用人插手。跑一次测试能直接拿到结果概要和全部失败详情，编译错误也是可解析的结构，FairyGUI 界面同样能写断言。

没人在旁边复核，结果就得自己站得住。

**不允许假通过。** 筛选条件一个都没匹配上直接报错，不接受「跑了零个测试、零个失败」。测试没过不算调用出错，两件事分开返回。

**不靠等待猜时机。** 写操作做完立刻返回，不等动画播完。要确认结果就单独查一次状态，这些状态在动画开始时就定了。

**定位方式可复现。** 查找对象只认名字和路径，不读编辑器当前选中了什么。换时间、换机器跑，结果一样。

**出问题不能把自己卡死。** 启动没反应、总时长超限、断线丢回调，三种都有超时兜底，不会永远停在「正在跑测试」。断线重连后补发没送达的结果。

**会改东西的操作先划清边界。** 怎么定位、能不能撤销、失败会不会留半成品、拿什么确认，动手前先写清楚。说不清就不做。动态编译执行 C# 代码、读写删除脚本文件默认不提供。

**工具列表要短。** 列表越长越容易挑错，也更占上下文。同类操作合成一个，用参数区分动作，对外只留十几个。

## FairyGUI UI 自动化测试

FairyGUI 的界面元素是独立的 GObject 树，不挂 GameObject，通用 GameObject 查询无法覆盖。本项目提供专用通道，支持对 FairyGUI 界面做完整的 UI 自动化测试：

- **直接设置状态**：单帧同步设数值、选中项、滚动位置、控制器页码，用于构造断言前置条件。
- **模拟真实操作**：逐帧驱动 press → move → release，走完整事件派发，触发 `onChanged` / `onDragXxx` 等真实回调。
- **读取界面状态**：返回按钮选中态、列表选中项、滑块值、滚动位置等控件语义状态，供断言。

三者构成「构造 → 操作 → 断言」闭环。

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

## 在 CLAUDE.md / AGENTS.md 中提示 Agent

如果项目已有 `CLAUDE.md` 或 `AGENTS.md`，可以加入下面这段简短提示：

```markdown
## UnityMCP

When working on this Unity project, use the project-local `unitymcp` skill. Prefer UnityMCP MCP tools for Editor status, compilation, tests, logs, scene/Game View inspection, and Editor automation.

If the `unitymcp` skill is missing or stale, ask the user to open `Window/UnityMCP - VeryFS`, enable the desired Agent Clients, and click `Install Agent Skill`.
```

## MCP 工具一览

Server 通过 `tools/list` 暴露以下工具（含 `install-agent-skill` 自身；生成 skill 时默认排除该工具，避免 agent 递归安装）：

| Tool | Access | Completion | 用途 |
|---|---|---|---|
| `assets-refresh` | mutating | report | 用空参数触发 `AssetDatabase.Refresh()`，等待编译完成并返回最终报告。 |
| `batch-execute` | mutating | response | 在一次调用里串行执行一组 RPC 子命令，返回汇总结果。每项格式为 `{"tool": <rpcMethod>, "params": {...}}`；子命令按顺序执行，结果按下标对齐。`failFast`（默认 false）：遇到第一个失败的子命令（返回 error）就停止。伪命令 `wait` 用于步骤间暂停：`{"tool":"wait","params":{"ms":500}}` 或 `{"tool":"wait","params":{"frames":3}}`（`ms` 和 `frames` 互斥；无上限，用 `timeoutMs` 限制整批耗时）。不支持作为子命令的有：`assets.refresh`、`editor.application.set-state`、`test.run`（长耗时）、`batch.execute`（不可嵌套）。 |
| `console` | mutating | response | 读取或清空 Unity 控制台日志缓冲区。action: get-logs \| clear-logs。 |
| `editor-application-get-state` | read-only | response | 返回 EditorApplication 状态：playmode、paused、compilation 等相关标志位。 |
| `editor-application-set-state` | mutating | report | 启动/停止/暂停 playmode。项目有编译错误时拒绝执行。调用会阻塞直到 play mode 切换完成（跨越 domain reload），并返回切换后的状态。 |
| `fgui-input` | mutating | response | 通过真实输入管线驱动 FairyGUI 对象（异步，跨帧）。action: click \| double-click \| hover \| gesture。仅限 play mode。 |
| `fgui-query` | mutating | response | 检查实时 FairyGUI 层级结构。action: get-tree \| list-panels。 |
| `fgui-state` | mutating | response | 同步读写 FairyGUI 对象状态。action: set-text \| set-value \| set-controller \| set-selection \| scroll \| transition \| focus \| call-event。仅限 play mode。 |
| `game-view` | mutating | response | 查看或修改当前 Game View。action: get-state \| list-resolutions \| set-resolution \| set-maximized。 |
| `gameobject` | mutating | response | 在已打开的场景中定位 GameObject 并读取其组件。action: find \| component-get。 |
| `health` | read-only | response | 返回 Unity MCP server 状态和 Unity editor 连接状态。永不报错；editor 未连接时也作为正常数据返回。 |
| `install-agent-skill` | mutating | response | 为当前项目生成并安装 UnityMCP agent skill。 |
| `scene` | mutating | response | 查询或修改已打开的 Editor 场景。action: get \| open \| save。 |
| `screenshot-game-view` | read-only | response | 截取 Editor Game View 并以图片形式返回，供可视化检查。需要 Game View 已打开。 |
| `test-run` | mutating | report | 对指定 assembly 运行 Unity Test Runner 测试，并等待最终结果。跑测前先调用 `assets-refresh`，确保测试针对最新编译的程序集。以下情况会被拒绝：项目有编译错误、editor 正在 compiling/importing、任一已加载场景有未保存改动、或已处于 play mode。跑测期间，除 `test-status`、`console`（get-logs / clear-logs）和 `screenshot-game-view` 外，其余工具都返回 `editor_busy`；传输层自身的 `unity.heartbeat` 和 `requests.report` 保持开放，以便结果能回传。`timeoutMs` 是整个调用的墙钟耗时上限：超时返回 `errorCode` `request_timeout`。Unity 无法取消正在运行的测试，跑测会继续进行，其他工具在此期间以 `tests_running` 拒绝，需轮询 `test-status`。返回结果摘要及所有失败用例；传 `includeDetails` 可一并返回通过的用例。 |
| `test-status` | read-only | response | 读取当前跑测进度或最近一次已完成的跑测结果。跑测进行中也可调用。可在 `test-run` 调用超时后用它取回结果，或用来排查跑测为何卡住（`blockedReason`）。 |

---

## License

MIT
