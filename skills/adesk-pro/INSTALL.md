# Installing adesk-pro

adesk-pro is a local Windows executable that exposes an MCP server over stdio.
Nothing is hosted anywhere: the agent connects to a process on your machine.

## Prerequisites

- Windows 10 1809+ or Windows 11
- [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (the desktop runtime is fine)
- The app(s) you want the agent to drive, on the same machine

No admin rights are needed to build or run it.

## Build

```
git clone https://github.com/ProcessMonitor/adesk-pro.git
cd adesk-pro
dotnet publish src/AdeskPro/AdeskPro.csproj -c Release -r win-x64 --self-contained false -o publish
```

The binary ends up at `publish/adesk-pro.exe`. Quick check:

```
adesk-pro status
adesk-pro list-apps
```

## Point your agent at it

### Claude Code

```
claude mcp add adesk-pro -- publish\adesk-pro.exe mcp
```

or edit `.mcp.json` / `mcpServers` config:

### Generic MCP config (Cursor, Claude Desktop, ZCode, …)

```json
{
  "mcpServers": {
    "adesk-pro": {
      "command": "C:\\path\\to\\publish\\adesk-pro.exe",
      "args": ["mcp"]
    }
  }
}
```

Use forward slashes or escaped backslashes for the path, and restart the agent after editing the config.

## First commands to try

```
adesk-pro snapshot --app Notepad -i     # JSON tree of Notepad, interactive elements carry refs
adesk-pro find --app Notepad --name Save
adesk-pro screenshot --app Notepad --out shot.png
```

Then let the agent do the same through the MCP tools — the `skills` tool inside the
server prints the full usage guide (`adesk-pro skills get desktop`), and every agent
session can re-read it with a single tool call.

## Notes

- Everything runs locally; the accessibility tree never leaves your machine except
  into your own agent's context.
- The first `snapshot` against a UWP/WinUI app can be slower than later ones.
- If `status` reports nothing, make sure the terminal and the target app run in the
  same session (no elevated/admin mismatch).
