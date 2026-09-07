# Pre-repair validator. For each fixture, assert that:
#   preBroken = true  → ffmpeg decode returns non-zero (file is genuinely broken)
#   preBroken = false → ffmpeg decode returns zero (file is genuinely healthy)
# If any expectation flips, the fixture regeneration is wrong and repair-ladder tests
# would run against the wrong baseline.
#
# Exit code: 0 all pass, 1 any mismatch.

param([string]$Ffmpeg = '')

$ErrorActionPreference = 'Continue'

if (-not $Ffmpeg) {
    $jf = "$env:USERPROFILE\Downloads\jellyfin_10.11.11-amd64\jellyfin\ffmpeg.exe"
    if (Test-Path $jf) { $Ffmpeg = $jf } else { $Ffmpeg = 'ffmpeg' }
}

$manifest = Get-Content (Join-Path $PSScriptRoot 'fixture-manifest.json') -Raw | ConvertFrom-Json
$fixtures = Join-Path $PSScriptRoot 'fixtures'

$results = @()
foreach ($fx in $manifest.fixtures) {
    $path = Join-Path $fixtures $fx.name
    $expected = if ($fx.preBroken) { 'broken' } else { 'healthy' }
    if (-not (Test-Path $path)) {
        $results += [pscustomobject]@{ name = $fx.name; expected = $expected; actual = 'MISSING'; pass = $false }
        continue
    }
    # Full-file decode to /null with -err_detect explode + -xerror to match the strictness
    # PlayabilityScanner's DecodeCheckAsync uses. cmd /c swallows stderr so PS 5.1 doesn't
    # wrap it into NativeCommandError.
    & cmd /c """$Ffmpeg"" -v error -xerror -err_detect explode -i ""$path"" -f null - 2>NUL >NUL"
    $decoded = ($LASTEXITCODE -eq 0)
    $actual = if ($decoded) { 'healthy' } else { 'broken' }
    $results += [pscustomobject]@{ name = $fx.name; expected = $expected; actual = $actual; pass = ($expected -eq $actual) }
}

$results | Format-Table -AutoSize

$failed = @($results | Where-Object { -not $_.pass })
if ($failed.Count -gt 0) {
    Write-Host "`n$($failed.Count) fixture(s) do not match manifest expectations. Re-run regenerate.ps1 or update the manifest."
    exit 1
}
Write-Host "`nAll $($results.Count) fixtures match their pre-repair expectations."
exit 0
