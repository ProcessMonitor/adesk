# adesk-pro (npm)

The launcher package for [adesk-pro](https://github.com/ProcessMonitor/adesk-pro). It contains
no automation logic — it resolves the platform binary that npm installed alongside it and hands
the process over.

## Why this package exists

The usual MCP install is one line of config. adesk-pro drives native apps through the OS
accessibility tree, which has to happen in a local native process, so there is always a binary
involved. This package hides it: npm installs the right binary for your OS and architecture, and
you configure the agent once.

## Use it

```bash
npx -y adesk-pro doctor      # check permissions and visible windows
npx -y adesk-pro list-apps   # what can the agent see?
```

MCP config — no absolute paths, no download step:

```json
{
  "mcpServers": {
    "adesk-pro": {
      "command": "npx",
      "args": ["-y", "adesk-pro", "mcp"]
    }
  }
}
```

Or generate it from the binary itself:

```bash
npx -y adesk-pro install-mcp --client claude
```

## Platforms

| Platform | Binary |
|---|---|
| macOS arm64 | `adesk-pro-darwin-arm64` |
| macOS x64 | `adesk-pro-darwin-x64` |
| Windows x64 | `adesk-pro-win32-x64` |

Each is a self-contained single-file binary — no .NET runtime, no build step. npm installs only
the one matching your machine.

macOS needs **Accessibility** permission (System Settings › Privacy & Security › Accessibility)
and, only for screenshots, **Screen Recording**. There is no notarised build, so a binary
downloaded outside npm may need `xattr -d com.apple.quarantine <file>` once. Installing through
npm does not set the quarantine flag.

## Environment override

Point the launcher at a binary you built yourself:

```bash
ADESK_PRO_BINARY=/path/to/adesk-pro npx -y adesk-pro status
```

## Build from source instead

See [INSTALL.md](https://github.com/ProcessMonitor/adesk-pro/blob/main/skills/adesk-pro/INSTALL.md).

MIT licensed.
