# adesk-pro

**Desktop use for AI agents on Windows** — read and operate native apps through the OS accessibility tree. No screenshots, no pixel matching, no vision model in the loop.

English | [简体中文](README.zh-CN.md)

## Why

[vercel-labs/agent-desktop](https://github.com/vercel-labs/agent-desktop) proved the idea: hand the agent the accessibility tree as JSON, and it can drive desktop apps without screenshots. It ships as ~362k lines of Rust across three platform adapters.

I wanted to know how far a Windows-only core gets with different bets:

- **Managed UIA instead of hand-written COM interop.** `System.Windows.Automation` already wraps the hard parts. The whole core is ~2.2k lines of C#.
- **The MCP server dispatches in-process.** Every tool call runs through the same command registry as the CLI — no subprocess per call, no global file lock, and the ref store is plain server memory.
- **MCP responses carry the envelope once.** Full `structuredContent` plus a one-line text summary, not a second copy of the JSON.
- **One command table** derives the CLI parser, `--help` and the MCP tool schemas.

## Demo (real session)

```
$ adesk-pro snapshot --app Notepad -i
{"ok":true,"data":{"snapshotId":"sn972n15auotk","refCount":18,"complete":true,
  "tree":{"role":"窗口","name":"无标题 - 记事本","ref":"@sn972n15auotk:e1","children":[
    {"role":"文档","name":"文本编辑器","ref":"@sn972n15auotk:e2","patterns":["scroll"]}, ...]}}}

$ adesk-pro type --ref @sn972n15auotk:e2 --text "adesk-pro roundtrip 你好"
{"ok":true,"data":{"resolvedBy":"scope-path","how":"keyboard","delivered":true,"value":"adesk-pro roundtrip 你好"}}

$ adesk-pro press --app 记事本 --keys ctrl+a
$ adesk-pro press --app 记事本 --keys ctrl+c
$ adesk-pro clipboard-get
{"ok":true,"data":{"text":"adesk-pro roundtrip 你好"}}
```

Snapshot, act, verify — the loop lives in the calling agent; adesk-pro has no model inside.

## Install

Windows 10 1809+ with the .NET 10 runtime.

```
dotnet publish src/AdeskPro/AdeskPro.csproj -c Release -r win-x64 --self-contained false -o publish
```

## MCP

```json
{ "mcpServers": { "adesk-pro": { "command": "publish/adesk-pro.exe", "args": ["mcp"] } } }
```

Tool names match the CLI commands (`snapshot`, `click`, `type`, …). Results return full `structuredContent` plus a one-line summary as text.

## CLI

17 commands: `status list-apps list-windows snapshot find get click toggle expand select type set-value press scroll focus clipboard-get clipboard-set`, plus `mcp`.

Every command prints one JSON envelope (`ok` + `data`/`error`). Errors carry a `disposition`: only `retry: safe` (nothing was delivered) may be retried — anything else means the action may have landed, so read live state before acting.

## Refs

Refs look like `@<snapshotId>:e<N>` and are re-identified live before any action, in three steps:

1. scope-path descent from the window
2. UIA **RuntimeId** pinning — so repeated controls found via `find` stay addressable
3. identity match (pid + role + automationId + name + className); zero matches → `STALE_REF`, several → `AMBIGUOUS_TARGET`

Walks run under a budget (depth 30 / siblings 2000 / nodes 5000 / 5s deadline); hitting one marks the snapshot `complete: false` instead of truncating silently. `STALE_REF` answers include a diagnostic comparing live vs. saved identity fields.

## Status

- Windows-only today. The `ISource` seam is where macOS (AX API) and Linux (AT-SPI) adapters would slot in.
- 17 commands. Sequential MCP dispatch (an agent loop is sequential anyway; parallel calls are on the roadmap).
- The managed UIA wrapper has no per-call timeout — a hung provider can stall a command. COM-level timeouts are on the roadmap.
- 30 unit tests; the walker runs against a fake source exercising the same branches as the UIA adapter.

## Comparison with agent-desktop

| | agent-desktop | adesk-pro |
|---|---|---|
| Language | Rust (Windows layer ~77k lines of COM interop) | C# (~2.2k line core on managed UIA) |
| MCP call model | subprocess per tool call | in-process dispatch, memory-only state |
| MCP response | envelope duplicated in text + structuredContent | structuredContent + one-line summary |
| Twins on Windows | refs refused for identical elements | `find` targets them, resolution pins RuntimeId |
| Platforms | macOS / Windows / Linux | Windows |
| Commands | 57 | 17 (MVP) |

agent-desktop is the more complete tool; adesk-pro is a smaller bet with an architecture I find easier to reason about. Both are open source — take your pick.

## License

MIT
