# Publish an APPROVED MediaDash build to the beta channel.
#
# POLICY: only builds the maintainer has explicitly approved for wider testing go to beta.
# The beta branch is NOT a mirror of day-to-day development - see docs/BETA-TESTING.md.
# This script enforces it: it requires -Approved, must run on the `beta` branch, and needs a clean tree.
#
# Beta testers add this Jellyfin repository URL:
#   https://raw.githubusercontent.com/crackruckles/MediaDash/beta/manifest-beta.json
#
# Usage (from the beta branch, after fast-forwarding it to the approved commit):
#   ./tools/release-beta.ps1 -Version 1.0.8.3 -Changelog "What testers should try." -Approved
#
# Requires: dotnet SDK, gh CLI (authenticated), git, PowerShell 7+ (pwsh) or 5.1+.

#requires -Version 5.1
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Changelog,
    [switch]$Approved
)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

$BetaBranch   = 'beta'
$BetaManifest = 'manifest-beta.json'

if (-not $Approved) {
    throw "Refusing to publish: beta builds must be explicitly approved. Re-run with -Approved only for a build you have signed off for testers (docs/BETA-TESTING.md)."
}
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "Version must be X.Y.Z or X.Y.Z.W (got '$Version'). Jellyfin only accepts numeric versions." }
$ver4 = if ($Version -match '^\d+\.\d+\.\d+$') { "$Version.0" } else { $Version }

$branch = (& git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne $BetaBranch) { throw "Run this from the '$BetaBranch' branch (currently on '$branch'). Fast-forward beta to the approved commit first: git switch beta; git merge --ff-only <commit>" }
if (& git status --porcelain --untracked-files=normal) { throw "Working tree is not clean. Beta builds must come from a committed, approved source." }
if (-not (Test-Path $BetaManifest)) { throw "$BetaManifest not found on the beta branch." }

# Jellyfin only updates to a strictly higher version, so every beta needs a new number.
$existing = (Get-Content $BetaManifest -Raw | ConvertFrom-Json)[0].versions | ForEach-Object { [version]$_.version }
$highest = $existing | Sort-Object -Descending | Select-Object -First 1
if ($highest -and [version]$ver4 -le $highest) { throw "Version $ver4 is not higher than $highest already in $BetaManifest. Testers would never receive it - pick a higher number." }

$tag       = "v$Version-beta"
$zip       = "mediadash_${Version}-beta.zip"
$stage     = "_stage_beta_" + ($Version -replace '\.','')
$sourceUrl = "https://github.com/crackruckles/MediaDash/releases/download/$tag/$zip"
$timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$targetSha = (& git rev-parse HEAD).Trim()

# Beta builds skip the source-bound acceptance-evidence gate (tools/reported_issue_gate.py) - collecting
# that evidence is part of what beta testing is for - but the full offline suite must still pass.
# Stable releases (tools/release.ps1) and normal pushes keep the full gate.
Write-Host "Running offline test suite..."
& dotnet test Jellyfin.Plugin.MediaDash.sln --configuration Release --filter "Category!=ReportedIssueExternal" --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "Offline tests failed. Not publishing to beta." }

# Make sure the tag target exists on GitHub before gh creates the tag.
# --no-verify: the pre-push hook enforces the stable-release acceptance gate, which beta deliberately skips.
Write-Host "Pushing $BetaBranch ($targetSha)..."
& git push --no-verify origin "${BetaBranch}:${BetaBranch}"
if ($LASTEXITCODE -ne 0) { throw "git push of $BetaBranch failed" }

Write-Host "Publishing (Release) $Version..."
$publishDir = "Jellyfin.Plugin.MediaDash/bin/Release/net9.0/publish"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }   # never zip a stale incremental publish
& dotnet publish --configuration Release "Jellyfin.Plugin.MediaDash.sln" /property:GenerateFullPaths=true /property:Version=$Version /consoleloggerparameters:NoSummary | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
if (-not (Test-Path $publishDir)) { throw "publish output missing: $publishDir" }

# Same sidecar guard as release.ps1: every shippable PackageReference must be in publish/.
[xml]$csproj = Get-Content 'Jellyfin.Plugin.MediaDash/Jellyfin.Plugin.MediaDash.csproj'
$required = @()
foreach ($ref in $csproj.SelectNodes('//PackageReference')) {
    $name = $ref.GetAttribute('Include'); if (-not $name) { continue }
    if ($ref.GetAttribute('PrivateAssets') -eq 'All') { continue }
    if ($ref.GetAttribute('ExcludeAssets') -match 'runtime' -or $ref.ExcludeAssets -match 'runtime') { continue }
    $required += $name
}
$dlls = @(Get-ChildItem $publishDir -File -Filter '*.dll' | ForEach-Object { $_.BaseName })
$missing = @($required | Where-Object { $_ -notin $dlls })
if ($missing.Count -gt 0) { throw "Publish output is missing DLLs for: $($missing -join ', ')" }
Write-Host "Sidecar check OK: $($required.Count) shippable DLLs present."

if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
Get-ChildItem $publishDir -File | Where-Object { $_.Extension -notin '.pdb','.xml' } | Copy-Item -Destination $stage
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage/*" -DestinationPath $zip
$md5 = (Get-FileHash $zip -Algorithm MD5).Hash.ToLower()
Write-Host "Zip MD5: $md5"

$notesPath = Join-Path ([IO.Path]::GetTempPath()) "beta-notes-$Version.txt"
$notes = "**Beta build - approved for testing, not a stable release.**`n`nInstall via the beta repository: https://raw.githubusercontent.com/crackruckles/MediaDash/beta/manifest-beta.json (see docs/BETA-TESTING.md).`n`n$Changelog"
Set-Content -Path $notesPath -Value $notes -Encoding utf8
& gh release create $tag $zip --title "$tag" --notes-file $notesPath --target $targetSha --prerelease --latest=false
if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
Remove-Item $notesPath -ErrorAction SilentlyContinue

$verifyPath = Join-Path ([IO.Path]::GetTempPath()) "beta-verify-$Version.zip"
& gh release download $tag --pattern $zip --output $verifyPath --clobber | Out-Null
$verifyMd5 = (Get-FileHash $verifyPath -Algorithm MD5).Hash.ToLower()
if ($verifyMd5 -ne $md5) { throw "DRIFT: uploaded md5=$md5, downloaded md5=$verifyMd5" }
Write-Host "Verified: released zip MD5 == $md5"

$changelogJson = ("[BETA] " + $Changelog) | ConvertTo-Json
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
$text = Get-Content $BetaManifest -Raw
$updated = [regex]::Replace($text, '("versions"\s*:\s*\[\s*\r?\n)', "`$1$newEntry`n", 1)
if ($updated -eq $text) { throw "Could not locate versions array in $BetaManifest" }
$null = $updated | ConvertFrom-Json   # must still parse
[IO.File]::WriteAllText((Join-Path $PWD $BetaManifest), $updated, (New-Object Text.UTF8Encoding $false))

& git add $BetaManifest
& git commit -m "Beta $Version (approved): publish to beta channel"
if ($LASTEXITCODE -ne 0) { throw "git commit of $BetaManifest failed" }
& git push --no-verify origin "${BetaBranch}:${BetaBranch}"
if ($LASTEXITCODE -ne 0) { throw "git push of $BetaManifest failed" }

Write-Host ""
Write-Host "Beta $Version is live. Testers' Jellyfin servers will pick it up on the next 'Update Plugins' run."
Write-Host "Repo URL: https://raw.githubusercontent.com/crackruckles/MediaDash/beta/$BetaManifest"
