---
name: {{SKILL_NAME}}
description: Use when working on a Unity project connected through VeryFS UnityMCP; prefer MCP tools for compilation, testing, scene/UI inspection, and Editor automation.
---

# UnityMCP Workflow

## When to Use This Skill

Use this skill for Unity Editor work in {{PROJECT_PATH}} with Unity {{UNITY_VERSION}}.

## Connection Check

Before the first Unity MCP call in a task, call `health` when the connection state is unknown. Prefer the interactive MCP workflow when `unityConnected` is `true`.

Do not call `health` before every MCP tool. Re-check only after Unity/server restarts, connection errors, long-running tests, or other signs that the Editor session may have changed.

## Testing and Verification

Call `assets-refresh` before `test-run`. A timed-out `test-run` call does not mean the tests failed: poll `test-status` until `running` is `false`, then inspect `lastRun.summary.failed`. Treat `no_tests_matched` as an error. During a test run, use only `test-status`, `console`, and `screenshot-game-view`.

{{TEST_ASSEMBLY_GUIDANCE}}

## Editor Session

Do not open a second Unity instance when {{PROJECT_PATH}}/Temp/UnityLockfile exists. If no lock exists and `health` is disconnected, start the Editor:

{{UNITY_LAUNCH_GUIDANCE}}

Wait 15–25 seconds, then call `health` again. A script-compilation-error modal can prevent MCP readiness; use the batchmode compile fallback below to diagnose it. Close only the Editor instance opened for this project.

Dirty scenes and an existing PlayMode state are rejected rather than saved automatically. `blockedReason: editor_unfocused` means the Editor is progressing slowly while unfocused. PlayMode tests temporarily change `ProjectSettings/EditorSettings.asset`; after tests, inspect `git status` because restoring settings can serialize unrelated dirty project settings.

## Batchmode Fallback

Use batchmode only when no usable interactive Editor session exists. Do not pass `-quit` when using `-runTests`; determine pass/fail from the test-results XML counts rather than grepping logs.

{{UNITY_EXECUTABLE_GUIDANCE}}

## Available MCP Tools

{{TOOL_SUMMARY}}

## Generated From

{{GENERATED_FROM}}
