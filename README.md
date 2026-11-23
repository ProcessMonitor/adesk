# adesk-pro

Also listed on [Agensi](https://www.agensi.io/skills/adesk-pro) — the agent skill marketplace.
**Desktop use for AI agents** — read and operate native apps through the OS accessibility tree. No screenshots, no pixel matching, no vision model in the loop.

Works on **Windows** (UI Automation) and **macOS** (Accessibility / AX API).

English | [简体中文](README.zh-CN.md)

## Design

The agent loop is: snapshot an app window into a JSON tree with refs (`@snapshot:eN`), act on refs (click / type / set-value / press / scroll), verify by snapshotting again. adesk-pro is the tool; it has no model inside. The design stands on four decisions:

- **Native accessibility end to end.** Windows uses managed UIA; macOS uses the AX API through hand-written P/Invoke (no Xamarin/Maui workload to install). Tree walking, ref resolution, patterns, keyboard and mouse synthesis live in one core of ~3k lines of C#, split from thin per-OS adapters.
- **The MCP server dispatches in-process.** Every tool call runs through the same command registry as the CLI — no subprocess per call, no global file lock. In MCP mode the ref store is plain server memory.
- **One command table.** The CLI parser, `--help` and the MCP tool schemas are all derived from a single registry; there is no second place where a command is defined.
- **Budgeted, honest walks.** Every tree walk runs under a budget (depth 30 / siblings 2000 / nodes 5000 / 5s deadline). Hitting a budget marks the snapshot `complete: false` instead of truncating silently.

## Install

### npm (recommended — nothing to download or build)

```bash
npx -y adesk-pro doctor      # check permissions and visible windows
npx -y adesk-pro list-apps
```

npm installs the right self-contained binary for your OS and architecture as an optional
dependency, so there is no runtime to install and no build step. Add it to your agent:

```json
{ "mcpServers": { "adesk-pro": { "command": "npx", "args": ["-y", "adesk-pro", "mcp"] } } }
```

### From source (no npm, or a platform that is not packaged)

```bash
git clone https://github.com/ProcessMonitor/adesk-pro.git
cd adesk-pro
./build.sh                      # macOS/Linux; fetches a local SDK into ./.dotnet if you have none
.\build.ps1                     # Windows (PowerShell)
```

Binaries are self-contained single files with no runtime dependency: ~35 MB on macOS, ~68 MB on
Windows (WPF's native libraries cannot be trimmed). Neither script installs anything system-wide —
`rm -rf .dotnet` undoes the SDK it may have fetched.

Raw commands, if you want them:

```bash
dotnet publish src/AdeskPro/AdeskPro.csproj  -c Release -r win-x64   --self-contained true -p:PublishSingleFile=true -o publish   # Windows
dotnet publish src/AdeskPro.Mac.Cli/AdeskPro.Mac.Cli.csproj -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -o publish  # macOS
```

### Permissions

- **Windows**: the terminal and the target app must run at the same integrity level (a non-elevated tool cannot read an elevated app).
- **macOS**: grant **Accessibility** to your terminal (System Settings › Privacy & Security › Accessibility); add **Screen Recording** only if you want `screenshot`. `adesk-pro doctor` tells you which one is missing.

Only commands that actually read or act on the accessibility tree ask for permission. `--help`,
`--version`, `status`, `doctor` and `list-apps` never prompt.

## MCP

Let the tool write the config, so no path is hand-typed:

```bash
npx -y adesk-pro install-mcp --client claude   # prints the `claude mcp add …` line
npx -y adesk-pro install-mcp                   # prints the JSON block for any client
```

When run through npm it emits the `npx` form (stable across upgrades); a binary run directly
emits its absolute path.

Tool names match the CLI commands. Results return full `structuredContent` plus a one-line summary; screenshots come back as dedicated image blocks.

## Demo (real session)

```
$ adesk-pro snapshot --app Notepad -i
{"ok":true,"data":{"snapshotId":"sn972n15auotk","refCount":18,"complete":true,
  "tree":{"role":"窗口","name":"无标题 - 记事本","ref":"@sn972n15auotk:e1","children":[
    {"role":"文档","name":"文本编辑器","ref":"@sn972n15auotk:e2","patterns":["scroll"]}, ...]}}}

$ adesk-pro type @sn972n15auotk:e2 "adesk roundtrip 你好"
{"ok":true,"data":{"resolvedBy":"scope-path","how":"keyboard","delivered":true,"value":"adesk roundtrip 你好"}}

$ adesk-pro press --app 记事本 --keys ctrl+a
$ adesk-pro press --app 记事本 --keys ctrl+c
$ adesk-pro clipboard-get
{"ok":true,"data":{"text":"adesk roundtrip 你好"}}
```

The same session on macOS (verified against TextEdit):

```
$ adesk-pro snapshot --app TextEdit -i        → 26 refs, complete: true
$ adesk-pro set-value @s1trh18tk4e7qn:e2 "round-2 验证 中文内容"
$ adesk-pro get @s1trh18tk4e7qn:e2              → resolves by scope-path, value reads back
```

## CLI

33 commands. Flags are the canonical form; the shorthand in the examples works too (`click @sid:eN` == `click --ref @sid:eN`).

- **observe**: `status` `doctor` `list-apps` `list-windows` `snapshot` `find` `get` `screenshot`
- **act**: `click` `toggle` `expand` `select` `type` `set-value` `press` `scroll` `mouse-click` `hover` `drag`
- **apps and windows**: `launch` `close-app` `minimize` `maximize` `restore` `move-window` `resize-window` `focus`
- **sync and scripting**: `wait` `batch` `clipboard-get` `clipboard-set`
- **setup and guidance**: `install-mcp` `skills`

Highlights:

- `click` on an element without clickable patterns falls back to an element-center mouse click.
- `snapshot --root @snap:eN` re-drills one subtree of a previous snapshot; refs inside it are re-issued with composed scope paths, everything outside stays valid.
- `wait` polls until a window appears or a ref turns `enabled` / `visible` / `gone` — no fixed sleeps in agent scripts. **A `wait` timeout is an error (`TIMEOUT`), never `ok:true`.**
- `batch` runs several commands through one process, with `stop-on-error`.
- Every command prints one JSON envelope (`ok` + `data`/`error`). Errors carry a `disposition`: only `retry: safe` (nothing was delivered) may be retried. An action interrupted after input was synthesised reports `retry: unsafe`.
- `doctor` reports platform, permissions and visible windows — run it first when a snapshot comes back empty.

## Ref semantics

Refs look like `@<snapshotId>:e<N>` and are re-identified live before any action, in three steps:

1. scope-path descent from the window
2. **RuntimeId** pinning on Windows (repeated controls found via `find` stay addressable)
3. identity match (pid + stable role id + automationId + name + className); zero matches → `STALE_REF`, several → `AMBIGUOUS_TARGET`

Identity uses the **stable role id** (`ControlType.Button`, `AXButton`), never the localised role label, so a snapshot keeps working after the OS language changes. A widget whose name *is* its value (a text field) does not require a name match, so editing text does not invalidate the ref that edited it.

A just-freed ref index is never reused after a drill, so an old ref cannot silently resolve to a different element. `STALE_REF` answers include a diagnostic comparing live vs. saved identity fields.

## Status

- Windows and macOS. Linux (AT-SPI) slots into the same `ISource`/`IPlatform` seam.
- Sequential MCP dispatch (an agent loop is sequential anyway; parallel calls are on the roadmap).
- Windows: the managed UIA wrapper has no per-call timeout. macOS: `AXUIElementSetMessagingTimeout` is set to 2s per element, so a hung app fails the call instead of stalling the agent.
- 66 unit tests; the walker runs against a fake source exercising the same branches as the real adapters. The macOS adapter is additionally exercised by hand against live apps (TextEdit, Finder).

## Roadmap

- [ ] COM-level per-call timeouts on Windows
- [ ] parallel MCP dispatch
- [ ] screenshot diffing between two snapshots
- [ ] Linux adapter

## License

MIT
