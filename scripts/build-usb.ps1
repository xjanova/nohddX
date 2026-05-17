<#
.SYNOPSIS
    Builds the NoHddX USB boot image.

.DESCRIPTION
    Wraps `tools/iso-builder` with sane defaults. Output goes to
    `dist/nohddx-boot.img`. After this completes, flash the image to
    a USB stick with Rufus (Windows) or `dd` (Linux/macOS).

.PARAMETER ServerUrl
    Base URL of your NoHddX server, e.g. http://192.168.1.10:8080

.PARAMETER Output
    Output image path. Default: dist/nohddx-boot.img

.PARAMETER SizeMb
    Image size in MB. Default: 64.
#>
param(
    [Parameter(Mandatory)] [string]$ServerUrl,
    [string]$Output = "dist/nohddx-boot.img",
    [int]$SizeMb = 64
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet is not on PATH. Install the .NET 8 SDK first: https://dot.net"
}

$outFull = Join-Path $repoRoot $Output
$outDir = Split-Path -Parent $outFull
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

Write-Host "Building NoHddX USB image" -ForegroundColor Cyan
Write-Host "  server : $ServerUrl"
Write-Host "  output : $outFull"
Write-Host "  size   : $SizeMb MB"
Write-Host ""

dotnet run --project "$repoRoot/tools/iso-builder/NohddX.IsoBuilder.csproj" -- `
    --server-url $ServerUrl `
    --output $outFull `
    --size-mb $SizeMb `
    --cache "$repoRoot/tools/iso-builder/cache" `
    --ipxe-dir "$repoRoot/tools/ipxe"

if ($LASTEXITCODE -ne 0) { throw "iso-builder failed" }

Write-Host ""
Write-Host "Flash the image to a USB stick:" -ForegroundColor Green
Write-Host "  Windows: open Rufus, select '$outFull' in DD-Image mode."
Write-Host "  Linux  : sudo dd if=$outFull of=/dev/sdX bs=4M conv=fsync status=progress"
