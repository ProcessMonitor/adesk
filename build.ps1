#Requires -Version 5.1
<#
.SYNOPSIS
    Builds a self-contained adesk-pro.exe for Windows.

.DESCRIPTION
    The published binary needs no .NET runtime on the user's machine. The build still needs the
    SDK, so this script installs a copy under .\.dotnet when the machine has none — nothing is
    installed system-wide, and removing that folder undoes it.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Runtime win-arm64
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [string]$Output = 'publish'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Set-Location -LiteralPath $PSScriptRoot

function Find-Dotnet {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $local = Join-Path $PSScriptRoot '.dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $local) { return $local }

    Write-Host 'No .NET SDK found; installing a local copy into .\.dotnet (not system-wide)...'
    $installer = Join-Path $env:TEMP 'adesk-dotnet-install.ps1'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
    try {
        & $installer -Channel '10.0' -InstallDir (Join-Path $PSScriptRoot '.dotnet') | Out-Null
    }
    finally {
        Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue
    }

    if (-not (Test-Path -LiteralPath $local)) {
        throw 'The .NET SDK install did not produce .\.dotnet\dotnet.exe'
    }
    return $local
}

$dotnet = Find-Dotnet

Write-Host "Building adesk-pro for $Runtime (self-contained, single file)..."
& $dotnet publish 'src\AdeskPro\AdeskPro.csproj' `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --output $Output

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $Output 'adesk-pro.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Expected $exe, but it was not produced" }

$sizeMb = [math]::Round((Get-Item -LiteralPath $exe).Length / 1MB, 1)
Write-Host ''
Write-Host "Done: $exe ($sizeMb MB, no .NET runtime required)"
Write-Host ''
Write-Host 'Next:'
Write-Host "  .\$Output\adesk-pro.exe doctor        # check permissions and visible windows"
Write-Host "  .\$Output\adesk-pro.exe install-mcp   # print the MCP config for your agent"
