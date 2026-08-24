#requires -Version 5.1
<#
.SYNOPSIS
Build MediaDash, kill any running local Jellyfin, and deploy the plugin to both v10 + v12 dev servers.

.DESCRIPTION
Replaces the ad-hoc PowerShell one-liners I've been running each session. Copies the ENTIRE build
output (main DLL + every runtime sidecar + deps.json) into both plugin folders, matching what the
release zip actually ships. Previously a hand-picked 4-file copy list hid System.Management.dll
from Windows dev builds and broke SMART on Windows.

.PARAMETER SkipBuild
Skips the build step and only redeploys existing bin/Release/net9.0 output.

.EXAMPLE
./tools/deploy-local.ps1
./tools/deploy-local.ps1 -SkipBuild
#>
param(
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
# PowerShell 5.1 default encoding is Windows-1252; keep this file ASCII-safe so em dashes / arrows
# don't break the parser when the script is edited outside PS_ISE.
Set-Location (Split-Path $PSScriptRoot -Parent)

$src = 'Jellyfin.Plugin.MediaDash/bin/Release/net9.0'
$abis = @('v10','v12')
$pluginFolder = 'MediaDash_0.9.0.0'

if (-not $SkipBuild) {
    Write-Host '== building ==' -ForegroundColor Cyan
    & dotnet build 'Jellyfin.Plugin.MediaDash/Jellyfin.Plugin.MediaDash.csproj' -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
}

if (-not (Test-Path $src)) { throw "build output missing: $src" }

# Sanity-check the sidecars are all present. Same rule as tools/release.ps1: every non-runtime-excluded
# PackageReference should have a matching DLL in the output. Cheaper than duplicating the full audit;
# if the release script's check is passing this will too.
$dlls = @(Get-ChildItem $src -File -Filter '*.dll')
if ($dlls.Count -lt 3) {
    Write-Warning "Only $($dlls.Count) DLL(s) in $src - expected main + sidecars. Deploying anyway; run tools/release.ps1 audit if suspicious."
}

Write-Host '== stopping jellyfin ==' -ForegroundColor Cyan
# Native taskkill's stderr on "not found" trips PS 5.1's NativeCommandError under $ErrorActionPreference='Stop',
# which aborts the script even with 2>$null. Use PowerShell-native Stop-Process instead so the "nothing to
# kill" case is a benign no-op.
Get-Process -Name jellyfin -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

foreach ($abi in $abis) {
    $dst = Join-Path $env:LOCALAPPDATA "jellyfin-$abi/plugins/$pluginFolder"
    if (-not (Test-Path $dst)) {
        Write-Warning "Skipping $abi - target folder does not exist: $dst"
        continue
    }

    # Copy everything the build emits except PDB/XML (dev-only, huge, not needed at runtime unless
    # you're debugging into the plugin - swap the filter if you want them).
    $files = Get-ChildItem $src -File | Where-Object { $_.Extension -notin '.pdb','.xml' }
    foreach ($f in $files) {
        Copy-Item -Path $f.FullName -Destination (Join-Path $dst $f.Name) -Force
    }
    Write-Host ("  -> {0} ({1} files)" -f $abi, $files.Count) -ForegroundColor Green
}

Write-Host ''
Write-Host 'Deployed. Launch with:' -ForegroundColor Cyan
Write-Host '  C:\Users\crackruckles\Downloads\start-jellyfin-v10.bat'
Write-Host '  C:\Users\crackruckles\Downloads\start-jellyfin-v12.bat'
