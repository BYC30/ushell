# ushell

`ushell` is a Unity UPM package that runs a local MCP server in an external .NET process and connects it to the Unity Editor through a Named Pipe Bridge. It exposes editor automation tools over `Streamable HTTP` so an MCP client can execute editor C# snippets, inspect logs, capture screenshots, toggle PlayMode, invoke runtime actions, refresh assets, and start builds without losing the MCP endpoint during Unity script compilation.

## Status

This repository now contains a v1 package scaffold with:

- External .NET HTTP MCP server process
- Unity Editor Named Pipe Bridge for main-thread tool execution
- Per-project settings and per-project Bridge pipe names, so multiple Unity projects can run separate MCP servers at the same time
- MCP methods for `initialize`, `ping`, `tools/list`, and `tools/call`
- Tool implementations for health checks, log access, PlayMode, code execution, screenshots, builds, and runtime invocation
- `refresh_assets` waits across Unity compile/domain reload and returns after Unity is idle again
- Runtime action registry with a few built-in actions
- Project settings UI for editor-side configuration

## Package Layout

```text
com.ushell/
  Editor/
  Runtime/
  Server/   # published executable only
  Server~/  # .NET MCP server source ignored by Unity
  package.json
```

## Installation

1. Add this repository as a local or git UPM package.
2. Open Unity `2019.4 LTS` or newer on Windows.
3. Open `Project Settings > Ushell`.
4. Confirm the HTTP port and allowed output paths.
5. Run `publish.ps1` once to build `Server/publish/Ushell.McpServer.exe` for the package.
6. Let Unity finish recompiling; the external MCP process and Unity Bridge auto-start after domain reload.

## Connecting An MCP Client

`ushell` hosts its MCP endpoint at:

```text
http://127.0.0.1:61337/mcp
```

The port is configurable in `Project Settings > Ushell`.
Unity starts the external MCP process from `Server/publish/Ushell.McpServer.exe`. If the executable is missing, the settings page reports a clear build instruction instead of trying to host MCP inside the Editor AppDomain.
When multiple Unity projects are open, each project keeps its own settings and Bridge pipe. If the preferred MCP port is already owned by another project, ushell starts this project's MCP process on the next available port and reports the actual endpoint in `health_check.serviceState.endpoint`.

The implementation currently supports:

- `initialize`
- `notifications/initialized`
- `ping`
- `tools/list`
- `tools/call`

## Tool Summary

- `health_check`
- `exec_expr`
- `get_logs` with optional type, sequence, keyword, regex filtering, and an optional clear-after-read switch
- `clear_logs`
- `capture_screenshot`
- `enter_playmode`
- `exit_playmode`
- `runtime_invoke`
- `build_project`
- `get_build_status`
- `refresh_assets` with optional `forceSynchronousImport` and `timeoutMs`
- `assign_task` with optional buttons, multiple automatic triggers, step handoffs, and an external-process-owned timeout
- `continue_task` to replace the controls of a `step_reached` task and wait for its next terminal state
- `list_tasks`
- `get_task`

All tool calls return a unified payload with:

- `success`
- `data`
- `logs`
- `warnings`
- `error`

### Task Semantics

- `assign_task` keeps the MCP request open while Unity remains interactive.
- Only logs matching `logKeyword` are retained in the task record.
- `autoTrigger` remains supported for one script; `autoTriggers` arms multiple one-shot keyword/script pairs.
- A configured `step` returns the task with `status=step_reached` when its keyword is observed, handing control back to the caller.
- `continue_task` reuses that task id, preserves prior captured logs and invocations, and replaces the next phase's filters, scripts, buttons, and steps.
- Completion by keyword, manual action, cancel, and timeout performs a final matching-log drain before committing the terminal state.
- Active task state, captured logs, invocation records, and the log sequence window survive Unity domain reload within the current Editor session.

## Notes

- v1 is Windows Editor only.
- The HTTP listener binds to localhost and is intended for local development workflows.
- The MCP HTTP listener runs outside Unity; Unity-side APIs are only accessed through the Editor Bridge.
- During script compilation/domain reload the Bridge may disconnect, but the MCP process keeps the HTTP endpoint alive.
- Long-running refresh and task requests are owned by the external MCP process and use short Bridge status polls instead of holding a pipe connection open.
- `exec_expr` now uses a bundled Mono evaluator session for shell-style execution and completions inside the Editor.
- Runtime invocation is intentionally constrained to registered actions rather than arbitrary runtime code execution.
- Tool execution is marshalled back onto the Unity main thread before it touches Editor APIs.
- The current transport implementation is POST-based JSON-RPC over the `/mcp` endpoint, with `GET` intentionally rejected for now.

## Publish

Run the publish script to build the external MCP server and sync the current package contents into the embedded Unity package location:

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```
