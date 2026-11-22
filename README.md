# adesk-pro

**Desktop use for AI agents on Windows** — read and operate native apps through the OS accessibility tree. No screenshots, no pixel matching, no vision model in the loop.

English | [简体中文](README.zh-CN.md)

## Design

The agent loop is: snapshot an app window into a JSON tree with refs (`@snapshot:eN`), act on refs (click / type / set-value / press / scroll), verify by snapshotting again. adesk-pro is the tool; it has no model inside. The design stands on four decisions:

- **Managed UIA end to end.** `System.Windows.Automation` wraps the COM interfaces natively, so the entire core stays at ~2.6k lines of C# — tree walking, ref resolution, patterns, keyboard and mouse synthesis included.
- **The MCP server dispatches in-process.** Every tool call runs through the same command registry as the CLI — no subprocess per call, no global file lock. In MCP mode the ref store is plain server memory.
- **One command table.** The CLI parser, `--help` and the MCP tool schemas are all derived from a single registry; there is no second place where a command is defined.
- **Budgeted, honest walks.** Every tree walk runs under a budget (depth 30 / siblings 2000 / nodes 5000 / 5s deadline). Hitting a budget marks the snapshot `complete: false` instead of truncating silently.

## Demo (real session)

```
$ adesk-pro snapshot --app Notepad -i
{"ok":true,"data":{"snapshotId":"sn972n15auotk","refCount":18,"complete":true,
  "tree":{"role":"窗口","name":"无标题 - 记事本","ref":"@sn972n15auotk:e1","children":[
    {"role":"文档","name":"文本编辑器","ref":"@sn972n15auotk:e2","patterns":["scroll"]}, ...]}}}

$ adesk-pro type --ref @sn972n15auotk:e2 --text "adesk roundtrip 你好"
{"ok":true,"data":{"resolvedBy":"scope-path","how":"keyboard","delivered":true,"value":"adesk roundtrip 你好"}}

$ adesk-pro press --app 记事本 --keys ctrl+a
$ adesk-pro press --app 记事本 --keys ctrl+c
$ adesk-pro clipboard-get
{"ok":true,"data":{"text":"adesk roundtrip 你好"}}
```

## Install

Windows 10 1809+ with the .NET 10 runtime.

```
dotnet publish src/AdeskPro/AdeskPro.csproj -c Release -r win-x64 --self-contained false -o publish
```

## MCP

```json
{ "mcpServers": { "adesk-pro": { "command": "publish/adesk-pro.exe", "args": ["mcp"] } } }
```

Tool names match the CLI commands. Results return full `structuredContent` plus a one-line summary; screenshots come back as dedicated image blocks.

## CLI

30 commands:

- **observe**: `status` `list-apps` `list-windows` `snapshot` `find` `get` `screenshot`
- **act**: `click` `toggle` `expand` `select` `type` `set-value` `press` `scroll` `mouse-click` `hover` `drag`
- **apps and windows**: `launch` `close-app` `minimize` `maximize` `restore` `move-window` `resize-window` `focus`
- **sync and scripting**: `wait` `batch` `clipboard-get` `clipboard-set`

Highlights:

- `snapshot --root @snap:eN` re-drills one subtree of a previous snapshot; refs inside it are re-issued with composed scope paths, everything outside stays valid.
- `wait` polls until a window appears or a ref turns `enabled` / `visible` / `gone` — no fixed sleeps in agent scripts.
- `batch` runs several commands through one process, with `stop-on-error`.
- Every command prints one JSON envelope (`ok` + `data`/`error`). Errors carry a `disposition`: only `retry: safe` (nothing was delivered) may be retried.

## Ref semantics

Refs look like `@<snapshotId>:e<N>` and are re-identified live before any action, in three steps:

1. scope-path descent from the window
2. UIA **RuntimeId** pinning — repeated controls found via `find` stay addressable
3. identity match (pid + role + automationId + name + className); zero matches → `STALE_REF`, several → `AMBIGUOUS_TARGET`

A just-freed ref index is never reused after a drill, so an old ref cannot silently resolve to a different element. `STALE_REF` answers include a diagnostic comparing live vs. saved identity fields.

## Status

- Windows-only today. The `ISource` seam is where macOS (AX API) and Linux (AT-SPI) adapters would slot in.
- Sequential MCP dispatch (an agent loop is sequential anyway; parallel calls are on the roadmap).
- The managed UIA wrapper has no per-call timeout — a hung provider can stall a command. COM-level timeouts are on the roadmap.
- 39 unit tests; the walker runs against a fake source exercising the same branches as the UIA adapter.

## Roadmap

- [ ] element-relative headed clicks (click on a ref's coordinates when patterns are missing)
- [ ] screenshot diffing between two snapshots
- [ ] parallel MCP dispatch
- [ ] COM-level per-call timeouts
- [ ] macOS / Linux adapters

## License

MIT
