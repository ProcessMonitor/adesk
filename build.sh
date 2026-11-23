#!/usr/bin/env bash
# Builds a self-contained adesk-pro for the current OS.
#
# Why self-contained: the published binary then needs no .NET runtime installed on the user's
# machine. The *build* still needs the SDK, so this script fetches a local copy into ./.dotnet
# when the machine does not already have one — nothing is installed system-wide, and
# `rm -rf .dotnet` undoes it.
set -euo pipefail

cd "$(cd "$(dirname "$0")" && pwd)"

OS="$(uname -s)"
case "$OS" in
  Darwin) RID="osx-$( [ "$(uname -m)" = "arm64" ] && echo arm64 || echo x64 )" ;;
  Linux)  RID="linux-x64" ;;
  MINGW*|MSYS*|CYGWIN*) RID="win-x64" ;;
  *) echo "unsupported OS: $OS" >&2; exit 1 ;;
esac

DOTNET="${DOTNET:-}"
if [ -z "$DOTNET" ]; then
  if command -v dotnet >/dev/null 2>&1; then
    DOTNET="$(command -v dotnet)"
  elif [ -x "./.dotnet/dotnet" ]; then
    DOTNET="./.dotnet/dotnet"
  else
    echo "No .NET SDK found; fetching a local copy into ./.dotnet (this does not touch your system)…"
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/adesk-dotnet-install.sh
    bash /tmp/adesk-dotnet-install.sh --channel 10.0 --install-dir "$PWD/.dotnet" >/dev/null
    rm -f /tmp/adesk-dotnet-install.sh
    DOTNET="$PWD/.dotnet/dotnet"
  fi
fi

PROJECT="src/AdeskPro/AdeskPro.csproj"
if [ "$OS" = "Darwin" ]; then PROJECT="src/AdeskPro.Mac.Cli/AdeskPro.Mac.Cli.csproj"; fi

echo "Building $PROJECT for $RID (self-contained)…"
"$DOTNET" publish "$PROJECT" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true \
  -p:EnableWindowsTargeting=true \
  -o publish

echo
echo "Done. Binary:"
ls -1 publish/adesk-pro* 2>/dev/null || ls -1 publish | head
echo
echo "Next:"
echo "  ./publish/adesk-pro doctor          # check permissions and visible windows"
echo "  ./publish/adesk-pro install-mcp     # print the MCP config for your agent"
