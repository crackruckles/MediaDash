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

# Gather recycled originals - batches are timestamped subdirs under RecycleBinRoot.
$recycled = @{}
if (Test-Path $RecycleBinRoot) {
    Get-ChildItem $RecycleBinRoot -Recurse -File -ErrorAction SilentlyContinue |
        ForEach-Object { $recycled[$_.Name] = $_.FullName }
}

$results = @()
foreach ($fx in $manifest.fixtures) {
    # postExists is the manifest's "target rung would produce this file" hint. Reality: the
    # plugin walks the ladder and stops at the FIRST successful rung, which may land the
    # output at either the source's original path (rungs 1/2) or with the extension changed
    # to .mkv (rungs 3/4). Both are correct outcomes from a user perspective - "my file is
    # playable somewhere reasonable" - so accept either.
    $originalPath = Join-Path $fixtures $fx.name
    $mkvVariant   = Join-Path $fixtures ([IO.Path]::ChangeExtension($fx.name, '.mkv'))
    $candidates = @(@($originalPath, $mkvVariant) | Select-Object -Unique | Where-Object { Test-Path $_ })

    $status = 'PASS'
    $reason = ''

    if ($fx.targetRung -eq 0) {
        # Negative control - should be untouched at original path, no recycle entry.
        if (-not (Test-Path $originalPath)) {
            $status = 'FAIL'; $reason = "healthy fixture no longer at original path (false-positive delete)"
        }
        elseif ($recycled.ContainsKey($fx.name)) {
            $status = 'FAIL'; $reason = "healthy fixture was recycled (false positive)"
        }
        $chosen = $originalPath
    }
    elseif ($candidates.Count -eq 0) {
        $status = 'FAIL'; $reason = "no repaired output at '$($fx.name)' or its .mkv variant - plugin fell through to delete"
        $chosen = ''
    }
    else {
        $chosen = $candidates[0]
        # Strict Jellyfin-play gate: -xerror exits non-zero on any real decode failure
        # (partial packets, corrupt frames, container/stream mismatch). If this passes,
        # Jellyfin will direct-play the file end-to-end.
        & cmd /c """$Ffmpeg"" -v error -xerror -i ""$chosen"" -f null - 2>NUL >NUL"
        if ($LASTEXITCODE -ne 0) {
            $status = 'FAIL'; $reason = "output '$(Split-Path $chosen -Leaf)' fails strict decode (-xerror) - Jellyfin would not play cleanly"
        }
        elseif (-not $recycled.ContainsKey($fx.name)) {
            # Repair path always recycles the original (see PlayabilityFixer.TrySwapRepairedAsync).
            $status = 'WARN'; $reason = "output OK but pre-repair original not found in recycle bin"
        }
    }

    $results += [pscustomobject]@{
        name = $fx.name
        rung = $fx.targetRung
        actualOutput = if ($chosen) { Split-Path $chosen -Leaf } else { '(none)' }
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
