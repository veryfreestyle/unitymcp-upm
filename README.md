# VeryFS UnityMCP

通过 [Model Context Protocol (MCP)](https://modelcontextprotocol.io) 暴露 Unity Editor 能力，让 AI 直接操作编辑器。完整工具链由 Unity Editor 插件和外部 Go server 两部分组成。

17 个工具：编译反馈、Console 日志、场景与 GameObject 查询、Asset 只读查询、Game View 控制与截图、Test Runner 跑测、FairyGUI 交互闭环、批量执行。UPM 分发，Claude / Codex / OpenCode 一键接入。Unity 2021.3+，Mac / Windows 双平台。

## 设计理念

目标是让 AI 在一次指令内完成完整的 TDD 循环：先写测试，确认失败，再实现到通过，全程无需人工介入。一次跑测调用即可取得结果概要与全部失败详情，编译错误以结构化形式返回，FairyGUI 界面同样可写断言。

没有人工复核，每个返回结果都必须能独立采信。

**不产生假通过。** 筛选条件未命中任何用例时直接报错，不接受「零用例、零失败」的结果。测试未通过不等同于调用失败，两者分开返回。

**不以等待推测时机。** 写操作执行完毕即返回，不等待界面动画结束。结果确认由独立的状态查询完成，这些状态在动画开始时即已确定。

**定位方式可复现。** 对象定位只接受名字与路径，不读取编辑器当前选中项。同一指令在不同时间、不同机器上执行，结果一致。

**异常不得导致自锁。** 启动无响应、总时长超限、重连后回调丢失，三种情形均有超时收尾，不会让工具长期停留在「正在跑测试」。断线重连后补发未送达的最终结果。

**写操作先划定边界。** 目标如何定位、能否撤销、失败是否留下半成品、返回后凭什么确认，均需在实现前写明，无法明确者不实现。动态编译执行 C# 代码、读写删除脚本文件默认不提供。

**工具列表保持精简。** 列表越长，选错工具的概率越高，上下文占用也越大。同族操作合并为一个工具，以参数区分具体动作，对外仅暴露十余个。

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

## 配置 MCP 客户端

打开 Unity 项目后，UnityMCP 会启动项目本地 MCP server，并按勾选的客户端写入 MCP 配置。

1. 打开 `Window/UnityMCP - VeryFS`，确认 MCP Server 状态为 `Connected`。
2. 在 `MCP Clients` 区域勾选需要配置的客户端。默认启用 `Claude` 和 `Codex`，`OpenCode` 默认关闭，需要时手动开启。

![UnityMCP window](monitor.jpg)

UI 会按勾选项维护这些项目内文件，Unity 每次启动都会重写它们：

- `.mcp.json`（Claude）
- `.codex/config.toml`（Codex）
- `.opencode/opencode.json`（OpenCode）

取消勾选会移除对应文件里的 UnityMCP entry，不动其它配置。这些文件含 client token，应保持 git-ignored。

## MCP 工具一览

Server 通过 `tools/list` 暴露 17 个工具；精确参数、schema 与错误语义以运行时 `tools/list` 为准。

| 分组 | 工具 | 用途 |
|---|---|---|
| 连接与 Editor | `health`、`editor-get-state`、`editor-set-state` | 查看 MCP / Editor 状态，切换 play mode。 |
| 编译与日志 | `assets-refresh`、`console` | 触发 AssetDatabase refresh、读取或清空 Console。 |
| 场景与资源 | `scene`、`gameobject`、`asset` | 打开/保存场景，查询场景对象、组件、AssetDatabase 与 prefab 内容。 |
| Game View | `game-view`、`screenshot-game-view` | 查看/设置 Game View 分辨率、最大化状态并截图。 |
| Test Runner | `test-list`、`test-run`、`test-status` | 发现测试程序集、运行 EditMode / PlayMode 测试、查询运行进度或最终结果。 |
| FairyGUI | `fgui-query`、`fgui-state`、`fgui-input` | 查询 FairyGUI 层级，设置控件状态，走真实输入管线执行 click / gesture 等交互。 |
| 编排 | `batch-execute` | 串行执行多条普通 RPC 子命令，减少多步 UI 自动化往返。 |

常用测试流程：先 `assets-refresh`，再用 `test-list` 取程序集名，然后调用 `test-run`。`test-run` 超时只表示 MCP 调用等到上限，不代表测试失败；继续用 `test-status` 读取最终结果。

---

## License

MIT
