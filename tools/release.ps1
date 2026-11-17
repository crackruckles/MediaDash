# Cut a MediaDash release end-to-end. The one guarantee this script gives:
# manifest.json's checksum for the new version equals the MD5 of the exact zip
# uploaded to GitHub Releases (self-verified by re-downloading and re-hashing).
#
# Usage:
#   ./tools/release.ps1 -Version 0.5.0 -Changelog "One-line summary of what's new."
#
# Requires: committed clean source, Python 3.11+, dotnet SDK, gh CLI (authenticated), PowerShell 5.1+.

#requires -Version 5.1
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Changelog
)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

# Version schema: X.Y.Z for regular releases; X.Y.Z.W for hotfixes (locked 2026-08-07).
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "Version must be X.Y.Z or X.Y.Z.W (got '$Version')" }
# AssemblyVersion accepts max 4 parts. Pad a 3-part release with .0 so the DLL FileVersion
# lines up with manifest.json; leave a 4-part hotfix alone.
$ver4  = if ($Version -match '^\d+\.\d+\.\d+$') { "$Version.0" } else { $Version }
$tag   = "v$Version"
$zip   = "mediadash_${Version}.zip"
$stage = "_stage_v" + ($Version -replace '\.','')
$sourceUrl = "https://github.com/crackruckles/MediaDash/releases/download/$tag/$zip"
$timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

function Assert-ReleaseSource {
    param([string]$ExpectedSha)
    $sha = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or -not $sha) { throw "Could not resolve release HEAD SHA" }
    $sha = $sha.Trim()
    $changes = & git status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect release source status" }
    if ($changes) { throw "Release source has uncommitted or untracked files. Commit the intended release changes before publishing." }
    if ($ExpectedSha -and $sha -ne $ExpectedSha) { throw "HEAD changed while building the release. Rebuild from the intended commit." }
    return $sha
}

$targetSha = Assert-ReleaseSource

Write-Host "Checking reported-issue regressions and source-bound acceptance evidence..."
& python -B (Join-Path $PSScriptRoot 'reported_issue_gate.py') --require-clean --expected-revision $targetSha
if ($LASTEXITCODE -ne 0) { throw "Reported-issue gate failed. No release was published." }

Write-Host "Publishing (Release)..."
# Stamp the version into the DLL. Without this the compiled AssemblyVersion stays at 0.0.0.0
# (its default when the csproj sets none), and Jellyfin's dashboard reads the DLL for its
# "My Plugins" version label - so the label reads 0.0.0.0 no matter what manifest.json says.
# /property:Version sets AssemblyVersion + FileVersion + InformationalVersion in one shot.
& dotnet publish --configuration Release "Jellyfin.Plugin.MediaDash.sln" /property:GenerateFullPaths=true /property:Version=$Version /consoleloggerparameters:NoSummary | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$publishDir = "Jellyfin.Plugin.MediaDash/bin/Release/net9.0/publish"
if (-not (Test-Path $publishDir)) { throw "publish output missing: $publishDir" }

# Guardrail against the v0.7.0 / v0.9.9.1 / v1.0.6 sidecar breakages: dotnet publish sometimes emits
# only the main dll (missing System.Diagnostics.PerformanceCounter.dll + siblings), the release script
# zipped what it saw, users installed a plugin that FileNotFoundExceptioned at load ("NotSupported" in
# the plugin list). Same failure mode surfaced 2026-08-23 when System.Management.dll was missing from
# dev deploys, breaking Windows SMART.
#
# Fix: assert every PackageReference that DOESN'T carry <ExcludeAssets>runtime</ExcludeAssets> in the
# csproj is present in publish/ as a .dll. Package refs marked ExcludeAssets=runtime (Jellyfin.Controller,
# Jellyfin.Model, Microsoft.Data.Sqlite - provided by the host) are expected to NOT ship. This catches
# the "dropped a package reference" and "incremental publish went stale" cases explicitly, not by count.
$csprojPath = 'Jellyfin.Plugin.MediaDash/Jellyfin.Plugin.MediaDash.csproj'
[xml]$csproj = Get-Content $csprojPath
$requiredPackages = @()
foreach ($ref in $csproj.SelectNodes('//PackageReference')) {
    $name = $ref.GetAttribute('Include')
    if (-not $name) { continue }
    # PrivateAssets=All → analyzer packages, never shipped.
    $private = $ref.GetAttribute('PrivateAssets')
    if ($private -eq 'All') { continue }
    # ExcludeAssets>runtime</ExcludeAssets> → provided by the host.
    $excludeInline = $ref.GetAttribute('ExcludeAssets')
    $excludeChild = $ref.ExcludeAssets
    if ($excludeInline -match 'runtime' -or $excludeChild -match 'runtime') { continue }
    $requiredPackages += $name
}
if ($requiredPackages.Count -eq 0) {
    throw "csproj audit found no shippable PackageReference entries. That's not right - check $csprojPath."
}

$publishedDlls = @(Get-ChildItem $publishDir -File -Filter '*.dll' | ForEach-Object { $_.BaseName })
$missing = @()
foreach ($pkg in $requiredPackages) {
    # Package name usually equals the assembly name (SharpCompress → SharpCompress.dll,
    # System.Management → System.Management.dll). If a package ever ships an assembly under a
    # different name we'll need to map explicitly; today it's 1:1.
    if ($pkg -notin $publishedDlls) {
        $missing += $pkg
    }
}
if ($missing.Count -gt 0) {
    throw "Publish output is missing DLLs for: $($missing -join ', '). Delete Jellyfin.Plugin.MediaDash/bin and re-run - the publish likely went incremental against a stale output, or a package reference was dropped from the csproj."
}
Write-Host "Sidecar check OK: $($requiredPackages.Count) shippable DLLs present."

$stagePath = [IO.Path]::GetFullPath((Join-Path $PWD $stage))
if ([IO.Path]::GetDirectoryName($stagePath) -ne $PWD.Path) { throw "Release staging path is outside the repository" }
if ((Test-Path -LiteralPath $stagePath) -and ((Get-Item -LiteralPath $stagePath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Release staging path is a link" }
if (Test-Path -LiteralPath $stagePath) { Remove-Item -LiteralPath $stagePath -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
Get-ChildItem $publishDir -File | Where-Object { $_.Extension -notin '.pdb','.xml' } | Copy-Item -Destination $stage

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage/*" -DestinationPath $zip

$md5 = (Get-FileHash $zip -Algorithm MD5).Hash.ToLower()
Write-Host "Zip MD5: $md5"

# Upload BEFORE writing manifest, so manifest never advertises a version that
# doesn't exist on Releases. Pass notes via a temp file: --notes with an inline
# string breaks whenever the changelog contains characters PowerShell splits on
# ("," "(" ">" quotes...), and shell-safe changelogs aren't the point.
$notesPath = Join-Path $env:TEMP "release-notes-$Version.txt"
Set-Content -Path $notesPath -Value $Changelog -Encoding utf8
# --target pins the tag to the exact local HEAD SHA. Previously gh created the tag
# on the remote's default-branch head, which drifted when local commits weren't pushed
# yet at release time — v1.0.7's tag landed on the PR#11 merge instead of the release
# commit for exactly this reason. The pushed-commit assumption is documented in the
# CONTRIBUTING notes: run this only after git push.
$null = Assert-ReleaseSource -ExpectedSha $targetSha
& gh release create $tag $zip --title $tag --notes-file $notesPath --target $targetSha
if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
Remove-Item $notesPath -ErrorAction SilentlyContinue

# The one check that makes the drift impossible: re-download the asset gh just
# uploaded, hash it, and abort if it differs from what we're about to write.
$verifyPath = Join-Path $env:TEMP "release-verify-$Version.zip"
& gh release download $tag --pattern $zip --output $verifyPath --clobber | Out-Null
$verifyMd5 = (Get-FileHash $verifyPath -Algorithm MD5).Hash.ToLower()
if ($verifyMd5 -ne $md5) { throw "DRIFT: uploaded md5=$md5, downloaded md5=$verifyMd5" }
Write-Host "Verified: released zip MD5 == $md5"

# Prepend TWO new entries to manifest.json[0].versions (same zip, different targetAbi)
# so both the 10.11 and 12.0 host lines see this version as installable. Raw text
# insertion preserves existing 2-space indent.
# ponytail: string surgery, not parse+reserialize; a schema change to manifest.json breaks this script - update it then.
$changelogJson = $Changelog | ConvertTo-Json  # produces a JSON-safe quoted string
$newEntry = @"
      {
        "version": "$ver4",
        "changelog": $changelogJson,
        "targetAbi": "12.0.0.0",
        "sourceUrl": "$sourceUrl",
        "checksum": "$md5",
        "timestamp": "$timestamp"
      },
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

# MD-013: keep build.yaml's version in lockstep with the manifest's top entry so downstream
# consumers of the build descriptor (packaging tooling, catalog scrapers, `jprm` runs) don't
# label artifacts with a stale version. Single-source-ish: this script IS the release entry
# point, so writing both files here means neither can drift as long as releases go through
# release.ps1 (per the "hand-editing manifest.json is banned" rule).
$buildYaml = Get-Content build.yaml -Raw
$buildYamlUpdated = [regex]::Replace($buildYaml, '(?m)^(version:\s*)"[^"]*"', "`$1`"$Version`"", 1)
# Idempotent: if the regex returns the file unchanged, that's either (a) already at $Version
# — fine, nothing to do — or (b) the version: line is genuinely missing. Distinguish by
# checking whether the target string is present. The pre-fix line threw on both cases, failing
# a release whose only "problem" was that build.yaml was pre-stamped (common when release.ps1
# runs after a dev build already bumped it).
if ($buildYamlUpdated -eq $buildYaml -and $buildYaml -notmatch "(?m)^version:\s*`"$([regex]::Escape($Version))`"\s*$") {
    throw "Could not locate version: line in build.yaml"
}
Set-Content build.yaml $buildYamlUpdated -Encoding utf8

Write-Host ""
Write-Host "Done. Commit manifest.json + build.yaml + $zip, then push:"
Write-Host "  git add manifest.json build.yaml $zip"
Write-Host "  git commit -m 'Release v$Version'"
Write-Host "  git push"
