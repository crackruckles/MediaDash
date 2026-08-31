# Reset the jellyfin-v10 datadir to a clean state, preserving the
# MediaDash plugin + its config.
#
# Fixes F-019 by nuking legacy item/DB state. Leaves fixture library
# untouched.
#
# Manual step at the end: complete the browser wizard.

$ErrorActionPreference = "Stop"

$root = "$env:LOCALAPPDATA\jellyfin-v10"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backup = "$env:LOCALAPPDATA\jellyfin-v10-preresetOF-$stamp"

Write-Host "Stopping Jellyfin ..." -ForegroundColor Cyan
taskkill /f /im jellyfin.exe 2>&1 | Out-Null
Start-Sleep -Seconds 3

Write-Host "Renaming datadir → $backup ..." -ForegroundColor Cyan
Rename-Item $root $backup

Write-Host "Creating fresh datadir ..." -ForegroundColor Cyan
New-Item -ItemType Directory $root -Force | Out-Null
New-Item -ItemType File "$root\.jellyfin-data" -Force | Out-Null

# Preserve the MediaDash plugin binary + its config
Write-Host "Copying MediaDash plugin from backup ..." -ForegroundColor Cyan
$srcPlugin = "$backup\plugins\MediaDash_0.9.0.0"
$dstPlugin = "$root\plugins\MediaDash_0.9.0.0"
New-Item -ItemType Directory "$root\plugins" -Force | Out-Null
Copy-Item $srcPlugin $dstPlugin -Recurse -Force

# Preserve plugin config so DryRun/Automatic settings survive
Write-Host "Copying plugin config ..." -ForegroundColor Cyan
$srcCfg = "$backup\plugins\configurations\Jellyfin.Plugin.MediaDash.xml"
$dstCfgDir = "$root\plugins\configurations"
New-Item -ItemType Directory $dstCfgDir -Force | Out-Null
if (Test-Path $srcCfg) { Copy-Item $srcCfg "$dstCfgDir\" -Force }

# Preserve the recycle bin contents (they're on disk, not in the DB)
Write-Host "Preserving recycle bin ..." -ForegroundColor Cyan
$srcBin = "$backup\data\mediadash"
if (Test-Path $srcBin) {
    New-Item -ItemType Directory "$root\data" -Force | Out-Null
    Copy-Item $srcBin "$root\data\" -Recurse -Force
}

Write-Host ""
Write-Host "Datadir reset. Old datadir kept at $backup" -ForegroundColor Green
Write-Host ""
Write-Host "Now starting Jellyfin ..." -ForegroundColor Cyan
Start-Process -FilePath "$env:USERPROFILE\Downloads\jellyfin_10.11.11-amd64\jellyfin\jellyfin.exe" `
    -ArgumentList "--datadir","`"$root`""

Write-Host ""
Write-Host "=== NEXT STEPS (in browser) ===" -ForegroundColor Yellow
Write-Host "1. Wait ~10 s for Kestrel to start."
Write-Host "2. Open http://localhost:8099/  → complete the Startup Wizard:"
Write-Host "     - Language: English"
Write-Host "     - Create admin user:  username = test  password = test"
Write-Host "     - Skip 'Add media library' — I'll wire it up via the API."
Write-Host "     - Skip metadata language."
Write-Host "     - Accept remote-access prompt (default is fine)."
Write-Host "3. When you see 'You're all set', run:"
Write-Host "     .\configure-libraries.ps1"
Write-Host ""
Write-Host "That's it — Jellyfin will re-index the fixture library from scratch." -ForegroundColor Green
