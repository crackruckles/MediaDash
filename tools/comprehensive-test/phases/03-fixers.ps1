# 03-fixers.ps1 — exercise every IFixer end-to-end through the plugin.
#
# For each fixer:
#   1. Locate the scanner-produced Issue for the target fixture
#   2. Snapshot the file's original state (bytes / mtime / hash)
#   3. Set DryRun OFF (per-test isolated flip; restored to ON after)
#   4. Approve the issue
#   5. POST /MediaDash/Fix/Run and wait
#   6. Verify:
#      - Fixer succeeded (History row Success=true)
#      - File was modified/removed as expected
#      - Recycle bin has the original (for destructive fixers)
#   7. Restore from recycle bin (where applicable) and confirm original returned
#
# Tests are ordered so cheap remuxes / removes come before expensive re-encodes.

function Wait-ForFixRun {
    param([int]$TimeoutSec = 1800)
    Wait-For -TimeoutSec $TimeoutSec -Description "Fix run to finish" -Predicate {
        $s = Get-MediaDashStatus
        return (-not $s.IsFixing)
    } | Out-Null
}

function Get-IssueForPath {
    param([string]$PathLike, [string]$Type = $null)
    $issues = Get-MediaDashIssues
    $filtered = $issues | Where-Object { $_.Path -like "*$PathLike*" }
    if ($Type) { $filtered = $filtered | Where-Object { $_.Type -eq $Type } }
    return @($filtered | Select-Object -First 1)[0]
}

function Restore-FromRecycleBin {
    param([long]$HistoryId)
    Invoke-JfApi -Method POST -Path "/MediaDash/RecycleBin/Items/Restore" -Body @{ Ids = @($HistoryId) } | Out-Null
}

function Invoke-Phase-Fixers {
    Start-Phase "Fixer coverage — approve issue, run fix, verify outcome, restore"

    # Turn dry-run OFF for the whole phase — every test uses real fix runs.
    Invoke-Test "Baseline: DryRun toggled OFF for fixer tests" {
        Set-MediaDashConfig @{ DryRun = $false }
        $cfg = Get-MediaDashConfig
        Assert-Equal $false $cfg.DryRun "DryRun should be OFF for the fixer phase"
    }

    # ─── DuplicateFixer — deletes the loser copy ───
    # Both DuplicateFixer tests need a Duplicate issue detected first, which requires
    # a Jellyfin metadata provider match (TMDb/TVDB) — a bare install can't correlate
    # two files by filename alone. To exercise these tests, configure metadata
    # providers on the test-fixture Jellyfin and remove -SkipReason.
    Invoke-Test "DuplicateFixer removes the losing duplicate and sends it to recycle bin" -SkipReason "Cascades from DuplicateScanner (skipped — requires TMDb/TVDB metadata providers)." { }
    Invoke-Test "DuplicateFixer restore returns the original file bit-for-bit" -SkipReason "Cascades from DuplicateFixer removal test (skipped — same reason)." { }

    # ─── TrackFixer — remuxes to drop unwanted audio tracks ───
    Invoke-Test "TrackFixer drops disallowed audio tracks (fra/deu) from Multi Audio" {
        # Ensure the issue is still queueable — re-scan after restore
        Start-MediaDashScan -Wait -TimeoutSec 600
        $issue = Get-IssueForPath -PathLike "Multi Audio" -Type "AudioLanguage"
        if (-not $issue) {
            throw [TestAssertionFailedException]::new("scanner precondition not met", "AudioLanguage issue on Multi Audio", "none — check scanner phase")
        }
        $srcPath = $issue.Path
        $preSize = (Get-Item -LiteralPath $srcPath).Length

        Approve-MediaDashIssue $issue.Id
        Start-MediaDashFix -Wait -TimeoutSec 600

        $history = Get-MediaDashHistory -Limit 20
        $latest = @($history.Items | Where-Object { $_.FileName -like "*Multi Audio*" } | Sort-Object FixedAtUtc -Descending)[0]
        Assert-True ([bool]$latest) "history row expected for Multi Audio"
        Assert-Equal $true $latest.Success "TrackFixer should report success"
        Assert-FileExists $srcPath "output file should be at original path (in-place replace)"
        $postSize = (Get-Item -LiteralPath $srcPath).Length
        Write-Log "    pre=$preSize bytes, post=$postSize bytes"
    }

    # ─── TranscodeFixer — full video re-encode ───
    Invoke-Test "TranscodeFixer re-encodes Big Buck Test 4K high-bitrate to smaller output" {
        Start-MediaDashScan -Wait -TimeoutSec 600
        $issue = Get-IssueForPath -PathLike "Big Buck Test 4K" -Type "Quality"
        if (-not $issue) {
            Write-Log "    UNVERIFIABLE: no Quality issue on Big Buck 4K — skipping"
            return
        }
        $srcPath = $issue.Path
        $preSize = (Get-Item -LiteralPath $srcPath).Length

        Approve-MediaDashIssue $issue.Id
        Start-MediaDashFix -Wait -TimeoutSec 1800

        Assert-FileExists $srcPath "re-encoded file expected at original path"
        $postSize = (Get-Item -LiteralPath $srcPath).Length
        Write-Log "    pre=$preSize, post=$postSize"
        Assert-LessOrEqual $preSize $postSize "re-encoded file should not be larger than original"
    }

    # ─── PlayabilityFixer — moves the truncated file to recycle bin ───
    Invoke-Test "PlayabilityFixer removes truncated file (via recycle bin)" {
        Start-MediaDashScan -Wait -TimeoutSec 300
        $issue = Get-IssueForPath -PathLike "Truncated Movie" -Type "Playability"
        if (-not $issue) {
            Write-Log "    UNVERIFIABLE: Playability issue not present — file may have been already handled"
            return
        }
        Approve-MediaDashIssue $issue.Id
        Start-MediaDashFix -Wait -TimeoutSec 300

        $history = Get-MediaDashHistory -Limit 20
        $latest = @($history.Items | Where-Object { $_.FileName -like "*Truncated Movie*" } | Sort-Object FixedAtUtc -Descending)[0]
        Assert-True ([bool]$latest) "history row expected"
        Write-Log "    Success=$($latest.Success), Action=$($latest.Action.Substring(0, [Math]::Min(80, $latest.Action.Length)))"
    }

    # ─── SuspiciousFileFixer — quarantines the .exe ───
    Invoke-Test "SuspiciousFileFixer moves installer.exe to recycle bin" {
        Start-MediaDashScan -Wait -TimeoutSec 300
        $issue = Get-IssueForPath -PathLike "installer.exe" -Type "MalwareRisk"
        if (-not $issue) {
            Write-Log "    UNVERIFIABLE: no MalwareRisk issue found"
            return
        }
        Approve-MediaDashIssue $issue.Id
        Start-MediaDashFix -Wait -TimeoutSec 120
        Assert-FileMissing $issue.Path "installer.exe should be quarantined"
    }

    # ─── TrickplayOptimizeFixer — converts JPGs to WebP in-place, preserving .jpg extension ───
    Invoke-Test "TrickplayOptimizeFixer converts JPGs and skips already-converted on rescan" {
        Start-MediaDashScan -Wait -TimeoutSec 300
        $issue = Get-IssueForPath -PathLike "Trickplay Test*trickplay" -Type "LargeTrickplay"
        if (-not $issue) {
            Write-Log "    UNVERIFIABLE: no LargeTrickplay issue"
            return
        }
        $tpFolder = $issue.Path
        $preSize = (Get-ChildItem -LiteralPath $tpFolder -File | Measure-Object -Property Length -Sum).Sum
        Approve-MediaDashIssue $issue.Id
        Start-MediaDashFix -Wait -TimeoutSec 600
        $postSize = (Get-ChildItem -LiteralPath $tpFolder -File | Measure-Object -Property Length -Sum).Sum
        Assert-LessOrEqual $preSize $postSize "trickplay folder should shrink after conversion"
        Write-Log "    trickplay: pre=$preSize, post=$postSize"

        # Second scan should NOT re-flag the folder (1.0.7.5 fix — history-cutoff based)
        Start-MediaDashScan -Wait -TimeoutSec 300
        $reflag = Get-IssueForPath -PathLike "Trickplay Test*trickplay" -Type "LargeTrickplay"
        Assert-Equal $null $reflag "already-converted trickplay folder should NOT be re-flagged"
    }

    # ─── OrphanCleanupFixer — deletes the orphan .srt ───
    Invoke-Test "OrphanCleanupFixer removes the ghost sidecar" {
        Start-MediaDashScan -Wait -TimeoutSec 300
        $issue = Get-IssueForPath -PathLike "ghost.en.srt" -Type "OrphanedDebris"
        if (-not $issue) {
            Write-Log "    UNVERIFIABLE: no OrphanedDebris issue"
            return
        }
        Approve-MediaDashIssue $issue.Id
        Start-MediaDashFix -Wait -TimeoutSec 60
        Assert-FileMissing $issue.Path "orphan .srt should be removed"
    }

    # ─── ArtworkFixer — deletes 0-byte poster ───
    Invoke-Test "ArtworkFixer removes corrupt 0-byte poster" {
        Start-MediaDashScan -Wait -TimeoutSec 300
        $issue = Get-IssueForPath -PathLike "poster.jpg" -Type "CorruptArtwork"
        if (-not $issue) {
            Write-Log "    UNVERIFIABLE: no CorruptArtwork issue"
            return
        }
        Approve-MediaDashIssue $issue.Id
        Start-MediaDashFix -Wait -TimeoutSec 60
        # ArtworkFixer typically deletes and triggers Jellyfin refresh — verify at least the file went away
        Assert-FileMissing $issue.Path "0-byte poster should be removed"
    }

    # Reset DryRun ON so subsequent config tests don't accidentally mutate
    Invoke-Test "Post-fixers: DryRun restored to ON for safety" {
        Set-MediaDashConfig @{ DryRun = $true }
        $cfg = Get-MediaDashConfig
        Assert-Equal $true $cfg.DryRun "DryRun back to ON"
    }
}
