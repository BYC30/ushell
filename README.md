# ushell

`ushell` is a Unity UPM package that embeds a local MCP server directly inside the Unity Editor. It exposes editor automation tools over `Streamable HTTP` so an MCP client can execute editor C# snippets, inspect logs, capture screenshots, toggle PlayMode, invoke runtime actions, and start builds.

## Status

This repository now contains a v1 package scaffold with:

- Embedded Editor-side HTTP MCP server
- MCP methods for `initialize`, `ping`, `tools/list`, and `tools/call`
- Tool implementations for health checks, log access, PlayMode, code execution, screenshots, builds, and runtime invocation
- Runtime action registry with a few built-in actions
- Project settings UI for editor-side configuration

## Package Layout

```text
com.ushell/
  Editor/
  Runtime/
  package.json
```

## Installation

1. Add this repository as a local or git UPM package.
2. Open Unity `2022.3 LTS` or newer on Windows.
3. Open `Project Settings > Ushell`.
4. Confirm the HTTP port and allowed output paths.
5. Let Unity finish recompiling; the server auto-starts after domain reload.

## Connecting An MCP Client

`ushell` hosts its MCP endpoint at:

```text
http://127.0.0.1:61337/mcp
```

The port is configurable in `Project Settings > Ushell`.

The implementation currently supports:

- `initialize`
- `notifications/initialized`
- `ping`
- `tools/list`
- `tools/call`

## Tool Summary

- `health_check`
- `exec_expr`
- `get_logs` with optional type, sequence, keyword, and regex filtering
- `clear_logs`
- `capture_screenshot`
- `enter_playmode`
- `exit_playmode`
- `runtime_invoke`
- `build_project`
- `get_build_status`

All tool calls return a unified payload with:

- `success`
- `data`
- `logs`
- `warnings`
- `error`

## Notes

- v1 is Windows Editor only.
- The HTTP listener binds to localhost and is intended for local development workflows.
- `exec_expr` now uses a bundled Mono evaluator session for shell-style execution and completions inside the Editor.
- Runtime invocation is intentionally constrained to registered actions rather than arbitrary runtime code execution.
- Tool execution is marshalled back onto the Unity main thread before it touches Editor APIs.
- The current transport implementation is POST-based JSON-RPC over the `/mcp` endpoint, with `GET` intentionally rejected for now.

## Publish

Run the publish script to sync the current package contents into the embedded Unity package location:

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```
