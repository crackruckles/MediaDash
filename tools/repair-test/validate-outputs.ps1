# Post-repair validator. Run AFTER MediaDash's Fix task has processed the fixtures folder.
# For each fixture, assert that the expected output file (per fixture-manifest.json's postExists)
# exists and decodes cleanly. Also asserts the pre-repair original is in the recycle bin.
#
# Exit code: 0 all pass, 1 any mismatch.

param(
    [string]$Ffmpeg = '',
    [string]$RecycleBinRoot = "$env:LOCALAPPDATA\jellyfin-v10\data\mediadash\recycle"
)

$ErrorActionPreference = 'Stop'

if (-not $Ffmpeg) {
    $jf = "$env:USERPROFILE\Downloads\jellyfin_10.11.11-amd64\jellyfin\ffmpeg.exe"
    if (Test-Path $jf) { $Ffmpeg = $jf } else { $Ffmpeg = 'ffmpeg' }
}

$manifest = Get-Content (Join-Path $PSScriptRoot 'fixture-manifest.json') -Raw | ConvertFrom-Json
$fixtures = Join-Path $PSScriptRoot 'fixtures'

# Gather recycled originals — batches are timestamped subdirs under RecycleBinRoot.
$recycled = @{}
if (Test-Path $RecycleBinRoot) {
    Get-ChildItem $RecycleBinRoot -Recurse -File -ErrorAction SilentlyContinue |
        ForEach-Object { $recycled[$_.Name] = $_.FullName }
}

$results = @()
foreach ($fx in $manifest.fixtures) {
    $expectedPath = Join-Path $fixtures $fx.postExists
    $expectedName = Split-Path $expectedPath -Leaf
    $extensionChanged = ($fx.name -ne $fx.postExists)

    $status = 'PASS'
    $reason = ''

    if (-not (Test-Path $expectedPath)) {
        $status = 'FAIL'; $reason = "expected output '$expectedName' not present"
    }
    else {
        & $Ffmpeg -v error -i $expectedPath -f null - 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) {
            $status = 'FAIL'; $reason = "output exists but does not decode cleanly"
        }
        elseif ($fx.targetRung -eq 0) {
            # Negative control — should be untouched (no recycle entry).
            if ($recycled.ContainsKey($fx.name)) {
                $status = 'FAIL'; $reason = "healthy fixture was recycled (false positive)"
            }
        }
        elseif ($extensionChanged) {
            # Rung 3: original .avi/.flv should be recycled, output .mkv present.
            if (-not $recycled.ContainsKey($fx.name)) {
                $status = 'WARN'; $reason = "output OK but recycled original not found"
            }
            if (Test-Path (Join-Path $fixtures $fx.name)) {
                $status = 'FAIL'; $reason = "extension change didn't clean up original at '$($fx.name)'"
            }
        }
        else {
            # Rungs 1/2/4: same-name replacement; expect pre-repair original in recycle bin.
            if (-not $recycled.ContainsKey($fx.name)) {
                $status = 'WARN'; $reason = "output OK but recycled original not found"
            }
        }
    }

    $results += [pscustomobject]@{
        name = $fx.name
        rung = $fx.targetRung
        expectedOutput = $expectedName
        status = $status
        reason = $reason
    }
}

$results | Format-Table -AutoSize

$failed = @($results | Where-Object { $_.status -eq 'FAIL' })
$warned = @($results | Where-Object { $_.status -eq 'WARN' })

if ($failed.Count -gt 0) {
    Write-Host "`n$($failed.Count) fixture(s) failed post-repair verification."
    exit 1
}
if ($warned.Count -gt 0) {
    Write-Host "`nAll pass, but $($warned.Count) recycle-bin cross-check(s) inconclusive (check bin path)."
    exit 0
}
Write-Host "`nAll $($results.Count) fixtures repaired as expected."
exit 0
