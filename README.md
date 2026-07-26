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

---

## License

MIT
