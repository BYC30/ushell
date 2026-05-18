# AGENTS.md

## Project Architecture

UShell is a Unity Editor package that exposes MCP tools for controlling and inspecting a Unity project.

The current architecture intentionally separates the MCP protocol server from the Unity Editor AppDomain:

- `Server~/Ushell.McpServer` contains the source for the external .NET MCP process.
- `Server/publish/Ushell.McpServer.exe` is the generated runtime artifact used by Unity.
- Unity Editor code owns all Unity state and tool implementations.
- The external MCP process owns HTTP JSON-RPC, MCP protocol stability, tool schema caching, timeouts, and Bridge forwarding.
- Unity and the external process communicate through a project-scoped Named Pipe Bridge.

Do not reintroduce an HTTP MCP server inside the Unity Editor AppDomain. Unity script compilation and domain reload must not terminate the MCP HTTP endpoint.

## Ownership Boundaries

### External MCP Process

The external server owns:

- `http://127.0.0.1:{port}/mcp`
- JSON-RPC methods such as `initialize`, `ping`, `tools/list`, `tools/call`, `ushell/status`, and `ushell/shutdown`
- tool schema cache fallback while Unity is unavailable
- short health/status timeouts
- long-running `refresh_assets` request ownership while Unity compiles or reloads
- conversion of Bridge errors into MCP tool envelopes

The external server must not reference Unity APIs.

### Unity Editor Side

Unity owns:

- `UshellToolRegistry`
- all tool handlers
- `EditorApplication`, `AssetDatabase`, PlayMode, builds, logs, screenshots, runtime actions
- Bridge lifecycle
- external MCP process supervision
- project-specific settings and pipe naming

Only Unity code may touch Unity Editor state.

### Bridge

`UshellEditorBridgeServer` is the only boundary through which the external MCP process invokes Unity behavior.

Bridge requests are project-scoped by `UshellPaths.BridgePipeName`. The pipe name must be stable for a project path and distinct across different Unity projects.

Bridge execution is serialized by the external `BridgeClient`. Preserve that invariant unless the Unity-side Bridge is redesigned to be safely concurrent.

## Multi-Unity / Multi-MCP Rules

Multiple Unity projects may be open at the same time.

The invariant is:

- each Unity project has a unique Bridge pipe name
- each Unity project has at most one active external MCP process
- preferred port comes from project-scoped settings
- if the preferred port is occupied by another project, supervisor selects an alternate port
- after domain reload, supervisor must reuse the existing MCP process for the same project instead of starting another one

Use `ushell/status` for fast process ownership checks. Do not use `health_check` for supervisor process detection because `health_check` may depend on Bridge availability during compile/reload.

## Refresh / Compile Invariant

`refresh_assets` is explicitly cross-domain-reload safe.

Expected flow:

1. MCP receives `refresh_assets`.
2. Unity Bridge validates arguments and schedules the refresh.
3. Unity returns an accepted response with `refreshRequestId`.
4. MCP keeps the original MCP request alive.
5. Unity calls `AssetDatabase.Refresh` through `UshellRefreshTracker`.
6. If compile/domain reload interrupts Bridge, MCP keeps polling.
7. After Bridge returns and Unity is idle, MCP returns success.
8. On timeout, MCP returns `REFRESH_TIMEOUT` and keeps the server process alive.

`UshellRefreshTracker` owns refresh state. Do not leave refresh in a half-updated state such as `scheduled` without a reliable update-loop path to execute it.

`refresh_assets.timeoutMs` defaults to `120000`.

## Bootstrap / Domain Reload Rules

On domain reload:

- stop Unity Bridge before reload
- do not stop the external MCP process
- restart Bridge after reload
- make service startup resilient with an update-loop pending start path, not only `delayCall`
- reuse the existing project MCP process when possible

On Editor quit:

- stop Bridge
- shut down the active external MCP process for that project

## Publish Rules

The external MCP executable is generated, not source.

Use `publish.ps1` to build and publish:

- source project: `Server~/Ushell.McpServer/Ushell.McpServer.csproj`
- output artifact: `Server/publish/Ushell.McpServer.exe`
- release target: `win-x64`, self-contained, single file

Do not commit generated `bin`, `obj`, or published exe artifacts unless explicitly requested.

If Unity reports a missing MCP executable, run the publish script instead of changing Unity startup logic.

## Public Interfaces

MCP endpoint:

- `http://127.0.0.1:{port}/mcp`

Important MCP methods:

- `ping`: must not depend on Unity Bridge
- `tools/list`: should use Unity Bridge when available and cached schema otherwise
- `tools/call`: invokes Unity tools through Bridge
- `ushell/status`: external-process-only status for supervisor detection
- `ushell/shutdown`: graceful external process shutdown

Important tool schema:

- `refresh_assets.timeoutMs?: number`

`health_check` must return:

- external MCP service state
- Bridge state
- refresh state
- Unity state when Bridge is available

When Unity Bridge is unavailable, `health_check` should return a successful MCP response with a clear disconnected Bridge state, not hang the HTTP request.

## Testing Checklist

Before considering architecture changes complete, verify:

- `dotnet build Server~/Ushell.McpServer/Ushell.McpServer.csproj`
- `dotnet publish Server~/Ushell.McpServer -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true`
- `publish.ps1 -Destination <UnityProject>/Packages/com.ushell`
- Unity imports without compile errors
- external MCP process starts automatically
- `ping` works without Bridge
- `tools/list` works
- `health_check` works with Bridge connected
- `exec_expr` returns the target Unity project path
- `refresh_assets(timeoutMs=...)` survives script compile/domain reload
- `ping` still responds during refresh/compile
- `health_check` degrades quickly while Bridge is disconnected
- Bridge reconnects after compile
- only one MCP process remains for the same Unity project after domain reload

## Design Discipline

Keep complexity behind the owning boundary.

Do not make callers remember hidden ordering such as "call refresh, then manually poll, then restart Bridge." The module that owns the state must expose a complete semantic operation and keep its own invariants consistent.

Prefer minimal, verifiable changes. Avoid unrelated refactors while working on process supervision, Bridge IPC, or refresh state.
