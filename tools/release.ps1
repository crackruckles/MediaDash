# Cut a MediaDash release end-to-end. The one guarantee this script gives:
# manifest.json's checksum for the new version equals the MD5 of the exact zip
# uploaded to GitHub Releases (self-verified by re-downloading and re-hashing).
#
# Usage:
#   ./tools/release.ps1 -Version 0.5.0 -Changelog "One-line summary of what's new."
#
# Requires: dotnet SDK, gh CLI (authenticated), PowerShell 5.1+.

#requires -Version 5.1
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Changelog
)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be X.Y.Z (got '$Version')" }
$ver4  = "$Version.0"
$tag   = "v$Version"
$zip   = "mediadash_${Version}.zip"
$stage = "_stage_v" + ($Version -replace '\.','')
$sourceUrl = "https://github.com/crackruckles/MediaDash/releases/download/$tag/$zip"
$timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

Write-Host "Publishing (Release)..."
# Stamp the version into the DLL. Without this the compiled AssemblyVersion stays at 0.0.0.0
# (its default when the csproj sets none), and Jellyfin's dashboard reads the DLL for its
# "My Plugins" version label - so the label reads 0.0.0.0 no matter what manifest.json says.
# /property:Version sets AssemblyVersion + FileVersion + InformationalVersion in one shot.
& dotnet publish --configuration Release "Jellyfin.Plugin.MediaDash.sln" /property:GenerateFullPaths=true /property:Version=$Version /consoleloggerparameters:NoSummary | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$publishDir = "Jellyfin.Plugin.MediaDash/bin/Release/net9.0/publish"
if (-not (Test-Path $publishDir)) { throw "publish output missing: $publishDir" }

# Guardrail against the v0.7.0 breakage: dotnet publish quietly emitted only the main dll (missing
# System.Diagnostics.PerformanceCounter.dll + siblings), the release script zipped what it saw, users
# installed a plugin that FileNotFoundExceptioned at load ("NotSupported" in the plugin list). The
# main assembly is Jellyfin.Plugin.MediaDash.dll — anything else at publish root is a copied NuGet
# runtime dep. A publish with zero of those is broken; abort before we upload a half-release.
$dependencyDlls = @(Get-ChildItem $publishDir -File -Filter '*.dll' | Where-Object { $_.Name -ne 'Jellyfin.Plugin.MediaDash.dll' })
if ($dependencyDlls.Count -eq 0) {
    throw "Publish output has no dependency DLLs beside the main assembly. CopyLocalLockFileAssemblies didn't run, or publish was incremental against a stale output. Delete Jellyfin.Plugin.MediaDash/bin and re-run."
}

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
Get-ChildItem $publishDir -File | Where-Object { $_.Extension -notin '.pdb','.xml' } | Copy-Item -Destination $stage

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage/*" -DestinationPath $zip

$md5 = (Get-FileHash $zip -Algorithm MD5).Hash.ToLower()
Write-Host "Zip MD5: $md5"

# Upload BEFORE writing manifest, so manifest never advertises a version that
# doesn't exist on Releases.
& gh release create $tag $zip --title $tag --notes $Changelog
if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }

# The one check that makes the drift impossible: re-download the asset gh just
# uploaded, hash it, and abort if it differs from what we're about to write.
$verifyPath = Join-Path $env:TEMP "release-verify-$Version.zip"
& gh release download $tag --pattern $zip --output $verifyPath --clobber | Out-Null
$verifyMd5 = (Get-FileHash $verifyPath -Algorithm MD5).Hash.ToLower()
if ($verifyMd5 -ne $md5) { throw "DRIFT: uploaded md5=$md5, downloaded md5=$verifyMd5" }
Write-Host "Verified: released zip MD5 == $md5"

# Prepend the new entry to manifest.json[0].versions via raw text insertion so
# existing formatting (2-space indent) is preserved.
# ponytail: string surgery, not parse+reserialize; a schema change to manifest.json breaks this script - update it then.
$changelogJson = $Changelog | ConvertTo-Json  # produces a JSON-safe quoted string
$newEntry = @"
      {
        "version": "$ver4",
        "changelog": $changelogJson,
        "targetAbi": "10.11.0.0",
        "sourceUrl": "$sourceUrl",
        "checksum": "$md5",
        "timestamp": "$timestamp"
      },
"@
$text = Get-Content manifest.json -Raw
$updated = [regex]::Replace($text, '("versions"\s*:\s*\[\s*\r?\n)', "`$1$newEntry`n", 1)
if ($updated -eq $text) { throw "Could not locate versions array in manifest.json" }
Set-Content manifest.json $updated -Encoding utf8

Write-Host ""
Write-Host "Done. Commit manifest.json + $zip, then push:"
Write-Host "  git add manifest.json $zip"
Write-Host "  git commit -m 'Release v$Version'"
Write-Host "  git push"
