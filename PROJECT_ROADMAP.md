# Unity MCP Project Roadmap

日期：2026-07-13（最近更新：2026-07-27，新增 P18）

本文档是 Unity MCP 项目的全局路线图。它只描述项目级阶段、阶段目标和阶段之间的依赖关系；每个阶段的详细设计写入 `docs/superpowers/specs/`，实施计划写入 `docs/superpowers/plans/`。

每个阶段节首标注状态行，三态：`未开工` / `进行中（分支名）` / `已交付（YYYY-MM-DD）`。日期取自 git 提交时间线。

## P0 - Unity Plugin Foundation MVP

状态：已交付（2026-07-13）

目标：先完成 Unity plugin 的最小可靠编译反馈闭环。

范围：

- Unity 侧 JSON-RPC 2.0 over WebSocket client。
- `unity.register`、`unity.heartbeat`、`assets.refresh`、`requests.report`。
- Fake RPC server 作为 TDD/test helper。
- `AssetDatabase.Refresh`、compiler messages 收集、pending request 持久化。
- Domain Reload / reconnect 后重发未 ack terminal report。

对应 spec：

```text
docs/superpowers/specs/2026-07-13-unity-mcp-foundation-mvp-design.md
```

完成标准：

- Fake server 能驱动 invalid C# -> structured compiler errors。
- 修复 C# 后再次 refresh 能返回 succeeded。
- 并发第二个 refresh 返回 `editor_busy`。

## P1 - External Go Server Skeleton

状态：已交付（2026-07-14）

目标：建立外部 Go server 的进程和 Unity WebSocket server 基础，但仍不完整实现 MCP。

范围：

- Go server project skeleton。
- `/health`。
- `/unity` WebSocket endpoint。
- Unity registration/session state。
- `assets.refresh` JSON-RPC request forwarding。
- Fake or manual driver for server-to-Unity command tests。

完成标准：

- Unity plugin 能连接真实 Go server。
- Go server 能缓存 Unity ready/disconnected state。
- Go server 能向 Unity 发送 `assets.refresh` 并收到 `requests.report`。

## P2 - MCP Streamable HTTP Bridge

状态：已交付（2026-07-14）

目标：把 MCP tool call 桥接到 Unity RPC。

范围：

- `/mcp` Streamable HTTP。
- `health` tool。
- `assets-refresh` MCP tool -> `assets.refresh` Unity RPC。
- MCP call 等待 `requests.report` terminal result。
- Tool error mapping。

完成标准：

- MCP client 能调用 `health`。
- MCP client 能调用 `assets-refresh` 并收到 structured compiler result。
- Unity disconnected 时，`health` 以数据形式返回 disconnected state。

## P3 - Process Lifecycle and Discovery

状态：已交付（2026-07-14）

目标：让 Unity 自动启动、发现和监管 Go server。

范围：

- Unity 启动 Go server binary。
- Deterministic project port。
- `UnityMCP.json` discovery file。
- `UNITY_MCP_CLIENT_TOKEN` 和 `UNITY_MCP_UNITY_TOKEN`。
- Editor PID monitoring。
- Single-editor ownership。
- Normal shutdown cleanup。

完成标准：

- 打开 Unity project 后 server 自动可用。
- MCP client 能通过 `UnityMCP.json` 发现 server URL/token。
- 第二个同项目 Editor 不能接管已有 server。

## P4 - Core Editor Tools

状态：已交付（2026-07-15）

目标：补齐低风险 Editor control/read tools。

实际范围（P4 已交付）：

- dynamic registration（Unity 侧 `tools` descriptor 数组上报）。
- `editor.application.get-state`。
- `editor.application.set-state`。
- `console.get-logs`。
- `console.clear-logs`。
- `screenshot.game-view`（自 P6 提前引入）。

范围调整：

- `editor.selection.get` / `editor.selection.set` 推后到后续阶段。
- Screenshot / artifact 支持原属 P6，在 P4 提前落地 game-view 截图。

完成标准：

- Tools 通过 dynamic registration 暴露给 MCP client。
- Read/mutation command 遵守 main-thread dispatch 和 busy-state rules。
- Editor busy、compilation failed、object not found 等错误语义稳定。

## P5 - Hardening

状态：已交付（2026-07-15）

目标：把 server 和 Unity RPC 做到可长期使用。P3/P4 已实现的安全机制在本阶段补齐测试覆盖，并补上被推后的 enforcement 与若干小 fix。

范围（8 项，均为小改动 + 测试）：

- WebSocket message size enforcement（Go 端 `SetReadLimit(8 MiB)` + oversize 主动断连；P4 仅落契约/常量，无主动 enforcement）。
- Host/Origin validation 测试覆盖（P3 已实现，补 invalid host/origin 拒绝测试）。
- Token authentication 测试覆盖（P3 已实现，补 invalid/missing token 拒绝测试）。
- Duplicate report idempotency 测试覆盖（`resolveCall` 已是 no-op，补第二条 terminal report 的回归测试）。
- Reconnect-during-call 的 Go 端测试（call 等待中 Unity 断连后重连）。
- InMemoryLogStorage 截断预算改用 UTF-8 字节数（当前用 UTF-16 `.Length`，CJK 载荷会溢出 8 MiB 预算）。
- `RpcCommandRegistry.Register` 重名 method guard。
- `ReplaceTools` 移到 ownership 检查通过之后（修 conflicting-editor 在 1008 拒绝前短暂覆盖 owner tool 集的 ordering 问题）。

范围裁剪：

- selection get/set：**彻底移除**。它读/写人类当前选中的对象，服务人机协作而非 AI 自动化；AI 看场景靠按名字/路径查询，不经 selection。需要时再单独评估。
- MCP conformance：不作为独立 task。go-sdk v1.6.1 已处理协议层（initialize/tools 列表/tools 调用）细节；conformance 由正常集成测试顺带覆盖。
- Windows server build：P5 阶段推后（Mac-only），已在 P10 补齐。

完成标准：

- Invalid tokens、oversize messages（Go 端主动断连）、duplicate reports、reconnect-during-call 均有测试覆盖。
- Go `go test ./...` 与 `go test -race ./...` 全绿；Unity EditMode 全绿。

## P6 - AI 看得见（FairyGUI 主轴）

状态：已交付（2026-07-15 ~ 2026-07-16）

目标：让 AI 能读取 Unity 运行/编辑期的 UI 与场景结构，从"黑盒看日志+截图"升级到"白盒看结构和状态"。

背景：目标项目以 UI 功能为主，主力 UI 框架是 **FairyGUI**。FairyGUI 的 UI 元素是 `GObject`/`GComponent` 树，不挂 GameObject，因此通用 GameObject 查询对它几乎无效——AI 需要专门的 FairyGUI 树读取能力。

范围：

- FairyGUI 树读取（主体）：遍历 `GRoot`/`GComponent` 树；读 `GObject` 的 name/type/text/visible/grayed/位置尺寸；按 name/path 查元素。
- 通用 scene 可见性（附带）：Scene 层级查询 + GameObject/Component 只读（uGUI 的结构作为通用 Component 读取的免费副产品）。

技术方向（P6 spec 已定）：

- 假定项目必装 FairyGUI，不做条件编译/可选。
- 直接引用 FairyGUI API，不用反射。
- 树序列化需要深度限制/按需展开，避免一次 dump 爆 8 MiB。

约束：本阶段纯只读，零副作用、零风险。

## P7 - AI 能测 / 能调试运行（FairyGUI 写 + editor-scene）

状态：已交付（2026-07-16）

目标：让 AI 在与运行中的 Unity 交互里不断调试运行，形成"跑起来 → 查 UI 树 → 触发 → 再查状态"的交互式闭环，以 UI 自动化测试为重点。

实际范围（9 个 tool，三条线合一个 spec）：

- 线1 — FairyGUI 写操作（运行期，play 模式）：事件触发（`fire-click`、`call-event` 8 事件白名单）、`set-text`、`set-controller`。本阶段主轴。
- 线2 — FairyGUI 只读增强：补 P6 冒烟确证的盲区——`GRoot.inst` 之外的 UIPanel / UIPainter 组件模式面板。
- 线3 — editor-scene 控制（编辑期文件写）：`editor.scene.get` / `editor.scene.open` / `editor.scene.save`，让 AI 能在不同场景跑测试。

安全模型分层：线1/线2 是 play 模式运行期操作（零磁盘副作用）；线3 是编辑期文件写（未保存改动 / 覆写磁盘不可逆）。

关键闭环设计：写操作同步执行完立即返回，不等后续动画/异步连锁。动画与状态判断由 AI 用 `fgui-get-tree` 独立完成——它读的是语义状态（`visible` / `text` / controller `selectedIndex` / transition `playing`），这些在动画开始（甚至之前）就已确定，天然绕开"动画何时结束"这个无法可靠归因的难题。写与查彻底分离。

依赖 P6 的只读通道作为地基。

## P8 - FairyGUI 全量自动化测试闭环

状态：已交付（2026-07-20）

目标：补齐 P7 遗留的"触发 → 查状态"两端盲区，让 AI 形成完整的"驱动交互 → 读专有状态断言"回路。

两层交互模型（核心设计，两类交互性质不同、不混）：

- A 层 — 即时状态操作（单帧同步）："arrange" 用途。直接把 UI 摆到某状态再断言，不关心输入管线。走现有 `IRpcCommand`。
- B 层 — 真实输入模拟（跨帧异步）："act" 用途。基于 `Stage.inst.SetCustomInput` 逐帧驱动，走完整 capture→bubble 派发，天然触发 onChanged / onGripTouchEnd / onDragXxx。新增 `IAsyncRpcCommand` + UniTask 地基。

范围：

- 读增强：`fgui-get-tree` 补控件专有状态（`GButton.selected`、`GList.selectedIndex`、`GSlider.value`、`GComboBox.value`、`GTextInput.promptText`、ScrollPane 位置等）。这是断言链原先断掉的地方。
- A 层命令：`set-value`、`set-selection`、`scroll`、`transition`、`focus`。
- B 层命令：`click`（吸收 P7 `fire-click`）、`double-click`、`gesture`（单指连续手势）、`hover`。
- 修 `fgui-call-event` 的 combobox bug。

关键技术约束（FairyGUI 5.2.0 源码核实，非文档）：

- `SetCustomInput` 单帧生效，处理当帧后立即复位。**独立 mouse-down / mouse-up 命令不可行**——B 层每个手势命令必须单命令自包含，内部逐帧跑完 press→move→release。
- `SetCustomInput` 只有左键通道，release 固定走 `onClick`，碰不到 `onRightClick`。
- 滚轮走 internal `HandleGUIEvents`（吃 Unity `Event` 对象），UnityMCP 程序集够不到，无法合成滚轮事件。

完成标准：

- 触发任一交互后能读到对应控件专有状态做断言，闭环不断链。
- EditMode 单测 + 交互式 Editor play 模式冒烟全绿。

## P9 - MCP 工具聚合与批量组合

状态：已交付（2026-07-21 ~ 2026-07-22）

目标：压缩对外工具面与 LLM 往返成本。P8 之后 FairyGUI 一族膨胀到 14 个工具、对外共 26 个，工具列表过长会稀释 LLM 注意力、增加 token 开销、降低选对工具的概率。

范围：

- 工具聚合：**内部保持细粒度命令（一命令一类、独立 schema、独立测试）不变，只在 MCP 边界（Unity 侧）把同族命令聚合成一个对外 tool**。对外工具 26 → 10。吸收参考项目 coplaydev 的"对外工具数少"，不吸收它的"巨型类"。
- 框架级聚合能力（非手写字典）：标记接口 `IGroupedCommand`（子命令声明 Group + Action + 本 action 的 schema 片段）+ 显式 `RpcGroupDefinition`（组级门面元数据）+ registry 自动合成扁平 schema 与 action 路由。加新组只需给子命令标注 Group/Action。不引入反射。
- 聚合分组（6 组）：`fgui-input` / `fgui-state` / `fgui-query` / `console` / `scene` / `gameobject`。FairyGUI 按交互性质切三组，Console 按对象聚合。
- 合并 schema：扁平 schema + `action` 枚举，参数平铺顶层，靠字段 description 引导 LLM。不用 `if/then` / `oneOf`（对 LLM 是弱约束）。
- `batch-execute`（2026-07-22）：横切工具，不进任何聚合组。一次调用串行执行多条子命令，聚合结果返回，把 FairyGUI 交互序列原子化以减少往返。支持 `wait` 伪指令（`ms` 或 `frames`，二选一）、`failFast` 开关（默认 `false`）、逐条结果 `{tool, ok, result}` / `{tool, ok:false, error}` 加 `successCount`/`failureCount`。拒绝长运行子命令（`assets.refresh`、`editor.application.set-state`）与嵌套 batch。

范围划定：

- Go server 不改，保持纯转发。聚合落点在 Unity 侧。
- 不保留旧工具名，直接切换。本项目自研自用，`.mcp.json` 自维护，无第三方消费者。
- 参数校验：dispatch 只校验 `action` 合法性，具体参数由各子命令内部自校验。

完成标准：

- 对外工具 10 个，内部子命令源码逻辑与测试不因聚合而改。
- 新增聚合组只需标注 Group/Action + 登记 `RpcGroupDefinition`。
- batch 能把一条 FairyGUI 交互序列（操作 + 等待 + 验证）打包成一次 MCP 调用。

## P10 - 工程化与可运维

状态：已交付（2026-07-22 ~ 2026-07-25）

目标：让 plugin 可分发、可运维、跨平台，从"本仓库能跑"变成"任何 Unity 项目能用、出问题能看"。

范围：

- Windows server build：`pidmonitor.PidAlive` 分平台实现（`pidmonitor_unix.go` 用 `Signal(0)`，`pidmonitor_windows.go` 用 `OpenProcess` + `GetExitCodeProcess`），`win-x64` 交叉编译产出，已在 Windows 验证。补上 P5 推后的项。
- Server Monitor Window：Editor 窗口观察 server 进程状态（PID / 端口 / 心跳 / 连接态）并手动处置。
- Console 日志改读原生 `UnityEditor.LogEntries`（反射 `StartGettingEntries` → `GetEntryInternal` → `EndGettingEntries`），替代只挂 `Application.logMessageReceived` 的托管回调缓冲。收益是能抓编辑器内部/原生来源日志（UIElements 工厂 warning、`CreateGUI` 未捕获异常等），与 Console 窗口所见对齐；代价是 `LogEntry` 无时间戳，不支持按时间过滤。
- UPM 分发：包 `com.veryfreestyle.unitymcp`，源 `https://github.com/veryfreestyle/unitymcp-upm.git`，本仓库 `sync-unitymcp-upm.sh` 负责同步。消费方项目通过 manifest 引用即可用。

完成标准：

- Mac（`osx-arm64`）与 Windows（`win-x64`）双平台 server 可用。
- 其他 Unity 项目通过 UPM 引用即可拿到全部工具，已有真实消费方实跑。
- server 异常状态可从 Editor 窗口观察并处置。

## P11 - Unity Test Runner 接入 MCP

状态：已交付（2026-07-25 ~ 2026-07-26）

目标：让 AI 自主跑测试验收。此前 AI 改完代码只能靠 `assets-refresh` 看编译是否通过，或手工驱动一次 UI 看效果；跑仓库里的 EditMode / PlayMode 测试套件只能走命令行 batchmode（见 `AGENTS.md`），不在 MCP 通道内。

范围：

- `test-run`：长任务命令，一次 MCP 调用直接拿结果（复用现有 ack + report 推模型）。EditMode + PlayMode 双平台。载荷默认只回 summary + 全部失败详情，`includeDetails` 才回全量。测试红不是 RPC 错误——entry 仍是 `succeeded`，红绿在 `summary.failed` 里。
- `test-status`：只读命令，返回当前进度或最近一次结果（含 `testMode`、`blockedReason`、`stuckSuspected`），补上超时 / 卡死后的复查能力。
- 防假绿两道：预检 filter 零匹配报 `no_tests_matched`；终态 `summary.total == 0` 也判 `no_tests_matched`（预检匹配器与框架的 `RuntimeTestRunnerFilter` 语义不等价，`!` 前缀排除项在 `Handle` 直接拒掉）。
- PlayMode 靠 `EnterPlayModeOptions.DisableDomainReload` 保住 MCP 连接，`PlayModeOptionsGuard` 双层持久化（`SessionState` + `Library/` 标记）原值，落盘失败不清标记，第一帧 / 退出前均补恢复。
- transport 层集中互斥闸门：测试运行期间除白名单 6 项（`test.status`、`console` 组路由、`console.get-logs`、`screenshot.game-view`、`unity.heartbeat`、`requests.report`，见 `Transport/TestRunGate.cs`）外一律 `editor_busy` + `tests_running`。
- 运行超时三道：init 30 秒无 `RunStarted`、`timeoutMs` 墙钟上限（默认 300000）、重连后回调已丢失判 `test_run_interrupted`——任一条都保证运行标志不会泄漏把 MCP 面焊死。

对应 spec：

```text
docs/superpowers/specs/2026-07-25-p11-test-runner-design.md
```

完成标准：

- AI 能按程序集（可加 namespace 收窄）跑测试，一次调用拿到 summary + 全部失败详情。
- filter 匹配 0 个测试时报错，不产生假绿。
- PlayMode 测试跑完能回传结果，中断后不留脏的 `ProjectSettings` 改动。
- 卡死时 `test-status` 能给出 `blockedReason`。

## P12 - Game View 分辨率控制与高分辨率截图

状态：已交付（2026-07-26）

目标：让 AI 能读取并显式控制当前 Game View 的分辨率与 Maximize 状态，
从最终合成帧取得可预测的高分辨率截图。

范围：

- 新增 `game-view` 聚合工具：`get-state`、`list-resolutions`、
  `set-resolution`、`set-maximized`。
- 读取 Game View 当前分辨率、Maximize 状态和 `actualRenderTexture` 尺寸。
- 只按现有分辨率项的 `index` 切换，不创建或删除自定义项。
- 复用统一的 Game View 窗口选择规则，不自动打开窗口。
- set action 跨 Editor update 等待 RT 稳定，最长 3 秒。
- `screenshot-game-view` 默认 `maxEdge` 提升到 1920，保持只缩小、不放大。
- 不引入 Scene View、多视角、Camera 离屏渲染或 `ScreenCapture` supersize。

对应 spec：

```text
docs/superpowers/specs/2026-07-26-p12-game-view-control-design.md
```

完成标准：

- Unity 2022.3 相关 EditMode 测试与实际 MCP 冒烟通过。
- Unity 2021.3.45f2c1 临时副本编译通过，完整 EditMode 测试全绿。
- Full HD / 4K 分辨率切换后能从状态中读到对应 RT 尺寸，默认截图最长边为 1920。

## P13 - Install Agent Skill

状态：已交付（2026-07-26）

目标：把 UnityMCP 的 agent 使用说明从 README / 消费方项目 `CLAUDE.md` / `AGENTS.md` 的手工复制流程，迁移为包内模板驱动、可生成、可更新的 project-local agent skill。

范围：

- 新增 `install-agent-skill` MCP tool。
- 根据当前 UnityMCP runtime tool descriptors 生成真实工具清单与参数摘要。
- 新增包内英文 skill 模板，承载 UnityMCP agent 工作流语义。
- README 改为面向使用者说明如何安装 UnityMCP 和调用 `install-agent-skill` 安装 skill，不再内嵌完整 agent prompt。
- 写入 `.agents/skills/<name>/SKILL.md`，默认 `name=unitymcp`，默认不覆盖。
- 固定写入范围在项目根目录 `.agents/skills/` 下，拒绝路径穿越与误覆盖。

对应 spec：

```text
docs/superpowers/specs/2026-07-26-p13-install-agent-skill-design.md
```

完成标准：

- `install-agent-skill` 出现在 `tools/list`。
- 调用后生成英文 `.agents/skills/unitymcp/SKILL.md`。
- 生成 skill 结合包内 skill 模板与实际 tool descriptors，可替代原本粘贴进 `CLAUDE.md` / `AGENTS.md` 的 UnityMCP 使用说明。
- 路径安全、覆盖保护、模板缺失、未知 tool 名均有测试覆盖。

## P13.1 - Agent Client Integrations

状态：已交付（2026-07-26）

目标：把 P13 的 skill 安装与 MCP client config 生成扩展成项目级 Claude / Codex / OpenCode 集成，并在 Server Monitor Window 中提供可见状态和开关。

范围：

- `install-agent-skill` 新增 `clients` 参数，支持 `claude` / `codex` / `opencode`。
- Claude/Codex 写 `.agents/skills/<name>/SKILL.md`，OpenCode 写 `.opencode/skills/<name>/SKILL.md`，不使用 symlink。
- `.mcp.json` 改为 merge / managed-entry 更新，不覆盖其它配置。
- 新增 `.opencode/opencode.json` merge / managed-entry 更新，remote MCP entry 使用 Bearer header 与 `oauth: false`。
- Server Monitor Window 新增 Claude/Codex/OpenCode toggle，每行显示 config 和 skill 状态。
- 关闭 toggle 时移除对应 MCP config entry，并只删除 UnityMCP generated marker 命中的 skill。
- README 改为中文 UI 流程，不再说明手工调用 `install-agent-skill` tool。

对应 spec：

```text
docs/superpowers/specs/2026-07-26-p13-1-agent-client-integrations-design.md
```

完成标准：

- 三个 client target 的 config 与 skill 状态能在 Server Monitor Window 中看到。
- 按 toggle 生成/移除 `.mcp.json`、`.codex/config.toml`、`.opencode/opencode.json` 中的 UnityMCP entry。
- `Install Agent Skill` 按当前 toggle 覆写安装对应 client 的 skill。
- 单元测试覆盖 config merge/removal、skill 双写/安全删除、`clients` 参数与 UI controller 行为。

## P14 - test-run 启动前激活 Unity Editor

状态：已交付（2026-07-26）

目标：`test-run` 启动 Unity Test Runner 前主动把 Editor 带到前台，降低后台失焦导致测试逐帧推进变慢、`test-status` 报 `blockedReason: "editor_unfocused"` 的概率。

范围：

- 在 `TestRunCommand.ExecuteAccepted()` 内、进入 `CountMatching` 前激活一次，同时覆盖 EditMode 与 PlayMode。
- 激活是 best-effort：失败只写 warning，不阻断测试，不改变现有错误语义。
- 新增 `IEditorActivator` seam，单测验证调用时机；生产实现处理平台细节。
- macOS 走进程内 AppKit `NSApplication.activateIgnoringOtherApps:`；Windows 走主窗口句柄 `ShowWindow(SW_RESTORE)` + `SetForegroundWindow`；其他平台 no-op。
- 不改 throttle / `EditorPrefs`——那是用户级全局副作用，P11 已明确排除。

对应 spec：

```text
docs/superpowers/specs/2026-07-26-p14-activate-editor-before-test-run-design.md
```

完成标准：

- 后台运行的 Editor 在 `test-run` 后被带到前台，`editor_unfocused` 不再是常态。
- 激活失败不影响测试结果与错误语义。

## P15 - GameObject 写操作

状态：未开工

目标：补上"读得到但改不了"的第一层盲区。当前 `gameobject` 族全只读，AI 无法建/删/改 GameObject，搭不了非 UI 的测试夹具或初始状态。

技术方向（开工前写 spec 细化）：为现有 `gameobject` group 增加 create / modify / duplicate / delete。难点不在技术而在安全边界——定位歧义（同名对象、路径 vs instance id）、scene dirty 语义、删除保护、Prefab instance 上的行为、`Undo` 边界。P7 线3（`editor.scene.open` / `save`）已踩过编辑期文件写这条线，有先例可循。

## P16 - Component 写操作

状态：未开工

目标：让 AI 能改 Component 字段，把对象摆到指定初始状态。

技术方向（开工前写 spec 细化）：类型化 add / remove 加 serialized field patch。优先 `SerializedObject` / `SerializedProperty`，不用反射直写；不引入任意方法调用（已列高风险候选）；对不可序列化 property 建立显式边界与错误语义。依赖 P15 的对象定位。

## P17 - Prefab 写操作

状态：未开工

目标：让 AI 能把搭好的对象固化成 Prefab，或修改已有 Prefab。

技术方向（开工前写 spec 细化）：只读铺垫（`asset` 组的 `get-info` / `find` / `component-get`）已由 P18 交付，P17 直接做 create-from-gameobject 与 headless modify；交互式 Prefab Stage 流程后置。磁盘写入不可逆，需单独定义 GUID、variant、nested prefab、保存失败与回滚语义。依赖 P15 / P16。

## P18 - 低风险只读补齐（Asset 查询与测试发现）

状态：已交付（2026-07-28）

目标：补齐两处只读盲区——AI 读不到 asset 本身，也发现不了可跑的测试。两条线互不
依赖，一个阶段两份 spec。

范围（两条线 + 一处命名规整）：

- 线1 — `asset` 只读组（新工具）：`search` / `get-info` / `find` / `component-get`。
  search 走结构化条件（`nameContains` / `typeName` / `labels` / `folders` /
  `searchInPackages`）+ 上限截断，默认只搜 `Assets/`，结果按 path 升序保证可复现；
  `get-info` 按 path 或 guid 查，`details` 按主对象类型特化（Material 的 shader
  解析与 fallback 判定、GameObject 仅 `prefabAssetType`），加新类型只加分支不加
  action；`find` / `component-get` 对齐 `gameobject` 组同名 action 的设计——
  `find` 用 `childPath` 定位节点、支持深度受限的层级展开与浅层组件类型名，
  `component-get` 给指定节点的 Component 完整字段，两者复用同一套字节预算截断，
  覆盖原本归 P17 的 prefab 深层结构查询。
- 线2 — 独立 `test-list` 工具（新增，与 `test-run` / `test-status` 成 `test-` 前缀族，
  不进组）。零入参纯 assembly 枚举器，返回 `{ assemblies: [{ name, testMode }] }` 直接
  喂 `test-run.assemblyNames`。实际跑测按 assembly 就够，method 级下钻（full name /
  `groupNames` 正则）不做。`test-run` / `test-status` 均保持独立工具、一行不改。
- 命名规整：`editor-application-get-state` / `editor-application-set-state` →
  `editor-get-state` / `editor-set-state`，只改对外 `Descriptor.Name`，
  内部 `RpcMethods` 常量不动（pending 记录按 method 回查，改了会让跨版本升级的
  终态推送落空）。

对外工具 16 → 18（净增 `asset` 与 `test-list` 各一个）。

关键约束：

- 只经 `AssetDatabase` 查询，不绕过它做文件遍历 / grep `.meta` / 手写 YAML 解析。
  Editor 读不出来的（断裂引用里存的原始 guid）如实报解析不开，不猜。
- **asset preview 缩略图永久排除。** `AssetPreview` 首次返回 null、需跨帧轮询、
  多数类型没有 preview、128×128 对 LLM 信息量极低；读原图像素是另一套机制。两者都不做。
- `test-list` 独立实现 `IAsyncRpcCommand`（`RetrieveTestList` 回调跨帧，30s 超时报
  `test_list_timeout`），不入组、不入 store、不跨 domain reload。因独立注册，
  `test-status` 与 `RpcCommandRegistry` 的全组同步/异步约束都无关，一行不改。
- 跑测期间 `test-list` 由**现有** `TestRunGate` 自动拒（`test.list` 不在白名单 →
  `tests_running`），零 gate 改动；`test-status` 仍在白名单，可达。
- 编译失败时 `test-list` 拒答（`compilation_failed`），与 `test-run` 行为一致——
  它不跑测试本无假绿风险，但保持 test 类工具行为统一、且编译失败本就该先修。

对应 spec：

```text
docs/superpowers/specs/2026-07-27-p18-asset-readonly-design.md
docs/superpowers/specs/2026-07-27-p18-test-discovery-design.md
```

完成标准：

- `asset` 组能按结构化条件搜到资源，能按 path / guid 读到类型化信息。
- 用 `Example 21 - Curve UI` 那个坏 material 能直接读出 `shaderResolved: false`。
- `find` 能读出 prefab 的层级结构（含深度 / 字节预算截断），`component-get`
  能读出指定节点的 Component 完整字段。
- `test-list` 的输出（assembly 名 + testMode）能直接填 `test-run` 的 `assemblyNames`。
- 跑测期间 `test-status` 可达、`test-list` 被现有闸门拒（`tests_running`）。
- Unity 2022.3 EditMode 全绿 + 真实 Editor 冒烟通过；Unity 2021.3 编译通过。

## P19 - PlayMode 跑测跨 domain reload 续跑

状态：进行中（`feat/p19-test-run-domain-reload`）

目标：撤掉 P11 为保住 MCP 连接而设的 `EnterPlayModeOptions.DisableDomainReload`
默认行为，让 PlayMode 跑测走正常 domain reload——语义与 CI / 手工跑对齐，同时消掉
P11 spec §8 记的风险 1（静态状态不重置导致测试互相污染）与风险 2（被跟踪的
`ProjectSettings/EditorSettings.asset` 留脏 diff）。

范围：

- `test-run` 新增可选参数 `disableDomainReload`（默认 `false`）。默认走正常 reload；
  显式传 `true` 才回到 P11 的 `PlayModeOptionsGuard` 路径换速度。
- 跨 reload 续跑：composition root 装配后调一次 `TestRunCommand.ResumeAfterReload()`，
  按 pending 记录的 `ExecutionState` 认领（`running` 认领，`counting` 判中断），
  `UnityTestRunner.Resume()` 只重挂回调、不重发 `api.Execute`。
- 存活判据：认领后 60 秒内无任何回调判 `test_run_interrupted`，不让请求干等墙钟上限。
- 返回体与 `test-status.lastRun` 增加 `resumedAcrossReload`，标注这次结果跨过 reload。

对应 spec：

```text
docs/superpowers/specs/2026-07-28-p19-test-run-domain-reload-design.md
```

完成标准：

- PlayMode 跑测默认走正常 domain reload 且能拿回终态结果，全程不碰
  `ProjectSettings/EditorSettings.asset`。
- `disableDomainReload: true` 仍走老路径，跑完 override 已还原。
- 认领失手（框架里其实没有运行）时 60 秒内给出 `test_run_interrupted`，闸门不焊死。
- Unity 2022.3 全量三盘绿 + 真实 Editor 冒烟通过；Unity 2021.3 编译 + EditMode
  全量 + PlayMode 默认路径各实跑一次。

## 候选范围（未排期）

以下方向已识别但未排期，开工前各自单独写 spec 并评估得失。标「高风险」的需先明确安全边界。参考实现的逐工具评估见 `docs/superpowers/reference/2026-07-26-coplaydev-unity-mcp-tool-survey.md`。

### 低风险只读（有明确用例）

- Project metadata：`get-project-info`、`get-tags`、`get-layers`。实现便宜，且 tags / layers 是类型化 GameObject patch 的输入约束。
- Profiler 只读子集：`get-frame-timing`、`get-counters`、`get-object-memory`。Profiler session mutation、Memory Snapshot、Frame Debugger 不在内。

### 待重估 / 有副作用

- uGUI / UI Toolkit 的 UI 测试 adapter（按 P8 定的两层交互接口补齐）。实测消费方用量偏低（某消费方 95 个 cs 文件中 uGUI 3 / UIElements 3，FairyGUI 15），价值待重估。
- Package 管理工具（P15 / P16 / P17 只覆盖 GameObject / Component / Prefab 写入，Package 不在其范围内）。
- Menu 查询 + allowlist 执行：查询可以先做；执行只允许仓库配置的精确 allowlist。
- 给 `Editor/Integration`、`Editor/Acceptance` 这类横切测试补 `[Category]` 标注，让分类维度从"目录与 namespace 隐含"变成显式（当前全仓库零 `[Category]`，故 `test-run` 未暴露 `categoryNames` 参数）。

### 高风险

- Component 方法调用（反射调用，任意副作用，参数/返回值序列化复杂）。
- OS 级输入模拟（脆弱、时序 flake；EventSystem / 框架级事件模拟优先）。

## 规则

- 每组工具单独写 spec。
- 高风险工具必须明确安全边界。
- 继续禁止 script write/delete/execute 作为默认能力，除非后续 spec 明确重新评估。
- 候选范围内的方向只是草图，开工前各写 spec 细化；roadmap 随认知更新。

## Roadmap Rules

- 本 roadmap 是项目级方向，不替代 spec 或 plan。
- 新阶段开 spec 前，先在本 roadmap 建节占号，状态写 `进行中（分支名）`；P 号只从这里领，不在 spec 里自行造号。
- 每个 P 阶段开始前，先写或更新对应 spec。
- Spec 批准后，再用 Superpowers `writing-plans` 生成实施计划。
- Roadmap 可以随项目认知更新，但不应放入具体 task checklist。
- 阶段交付后回填状态行为 `已交付（YYYY-MM-DD）`；不留未来时表述。
