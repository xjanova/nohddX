<#
.SYNOPSIS
    Starts the NoHddX server in development mode.

.DESCRIPTION
    Restores the solution, builds NohddX.Server, and runs it. The
    server listens on http://0.0.0.0:8080 by default and binds the
    DHCP-Proxy / TFTP / iSCSI / discovery services configured in
    src/NohddX.Server/appsettings.json.

    Note: TFTP (UDP/69), DHCP-Proxy (UDP/4011) and iSCSI (TCP/3260)
    require the server to be run from an elevated PowerShell on
    Windows because they bind to privileged ports / raw sockets.
#>
param(
    [string]$Configuration = "Debug",
    [switch]$NoRestore,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet is not on PATH. Install the .NET 8 SDK first: https://dot.net"
}

Write-Host "NoHddX server" -ForegroundColor Cyan
Write-Host "  repo : $repoRoot"
Write-Host "  conf : $Configuration"
Write-Host ""

if (-not $NoRestore) {
    Write-Host "Restoring..." -ForegroundColor Yellow
    dotnet restore "$repoRoot/NohddX.sln"
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }
}

if (-not $NoBuild) {
    Write-Host "Building NohddX.Server..." -ForegroundColor Yellow
    dotnet build "$repoRoot/src/NohddX.Server/NohddX.Server.csproj" -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
}

Write-Host ""
Write-Host "Starting NohddX.Server (Ctrl+C to stop)..." -ForegroundColor Green
dotnet run --no-build -c $Configuration --project "$repoRoot/src/NohddX.Server/NohddX.Server.csproj"
