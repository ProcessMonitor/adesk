# Installing adesk-pro

adesk-pro is a local executable that exposes an MCP server over stdio. Nothing is hosted
anywhere: the agent connects to a process on your machine.

## Prerequisites

**None at runtime.** The published binary is self-contained — no .NET runtime, no Xamarin/Maui
workload, no package manager. You build it once on the machine that will run it.

- Windows 10 1809+ (64-bit) or macOS 12+ (arm64 or x64)
- The app(s) you want the agent to drive, on the same machine

No admin rights are needed to build or run it.

## Build (one command)

```bash
git clone https://github.com/ProcessMonitor/adesk-pro.git
cd adesk-pro
./build.sh
```

`build.sh` detects the OS, and if the machine has no .NET SDK it downloads one into `./.dotnet`
first. That copy stays inside the repo and is not installed system-wide; delete it with
`rm -rf .dotnet`. The result is a single file at `publish/adesk-pro` (or `publish/adesk-pro.exe`
on Windows), about 35 MB, with no runtime dependency.

Quick check:

```
./publish/adesk-pro doctor
./publish/adesk-pro list-apps
```

### Build without the script

```bash
# Windows
dotnet publish src/AdeskPro/AdeskPro.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -o publish

# macOS (arm64; use osx-x64 on Intel)
dotnet publish src/AdeskPro.Mac.Cli/AdeskPro.Mac.Cli.csproj -c Release -r osx-arm64 \
  --self-contained true -p:PublishSingleFile=true -o publish
```

Dropping `--self-contained true` makes the output ~2 MB but then requires the **.NET 10 Desktop
Runtime** on the target machine — a worse trade for anyone who is not a .NET developer.

## Permissions

### Windows

The terminal running adesk-pro and the target app must be at the **same integrity level**. A
non-elevated process cannot read an elevated app's window; `doctor` reports this as an empty
window list.

### macOS

Two separate grants, both under System Settings › Privacy & Security:

- **Accessibility** — required for everything (reading and acting on windows).
- **Screen Recording** — required only for `screenshot`.

Add your terminal app, then restart it. `adesk-pro doctor` prints which one is missing:

```
./publish/adesk-pro doctor
```

## Point your agent at it

Let the binary write the config, so the absolute path is correct by construction:

```bash
./publish/adesk-pro install-mcp --client claude   # prints the exact `claude mcp add …` command
./publish/adesk-pro install-mcp                   # prints a JSON block for any MCP client
./publish/adesk-pro install-mcp --write ./mcp.json   # writes the block to a new file
```

Or paste it yourself — the `command` must be an **absolute path**:

```json
{
  "mcpServers": {
    "adesk-pro": {
      "command": "/absolute/path/to/publish/adesk-pro",
      "args": ["mcp"]
    }
  }
}
```

Restart the agent after editing the config.

### Claude Code

```bash
claude mcp add adesk-pro -- /absolute/path/to/publish/adesk-pro mcp
```

## First commands to try

```
./publish/adesk-pro doctor               # permissions + visible windows
./publish/adesk-pro list-apps            # what can the agent see?
./publish/adesk-pro snapshot --app Notepad -i      # Windows
./publish/adesk-pro snapshot --app TextEdit -i     # macOS
```

Then let the agent do the same through the MCP tools. The `skills` tool inside the server prints
the full usage guide, and every agent session can re-read it with a single tool call:

```
./publish/adesk-pro skills --name desktop
```

## Notes

- Everything runs locally; the accessibility tree never leaves your machine except into your own
  agent's context.
- The first `snapshot` against a UWP/WinUI app can be slower than later ones.
- If `list-apps` is empty, run `doctor`: it distinguishes "no permission" from "no matching app".
