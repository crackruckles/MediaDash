# 02-scanners.ps1 — one test per scanner class. Each verifies that a known
# fixture surfaces the expected issue type, and that unrelated files are not
# flagged. This phase is READ-ONLY — no fixes are applied.

function Invoke-Phase-Scanners {
    Start-Phase "Scanner coverage — every IScanner detects its target issue on fixtures"

    # Precondition: initial scan from setup phase has populated the DB. Fetch once.
    $allIssues = @()
    Invoke-Test "Fetch all detected issues from the initial scan" {
        $allIssues = Get-MediaDashIssues
        $Script:RunState | Add-Member -MemberType NoteProperty -Name InitialIssues -Value $allIssues -Force
        Write-Log "    total issues: $($allIssues.Count)"
        Assert-GreaterOrEqual 1 $allIssues.Count "should have at least one detected issue"
    }

    # ─── One test per scanner ───

    Invoke-Test 'DuplicateScanner flags the two separately indexed Big Buck fixtures' {
        $duplicates = @($Script:RunState.InitialIssues | Where-Object { $_.Type -eq 'Duplicate' -and $_.Path -like '*Big Buck Test*' })
        Assert-GreaterOrEqual 1 $duplicates.Count 'local NFO title and year should group the duplicate fixtures'
    }

    Invoke-Test "QualityScanner flags Big Buck Test 4K high-bitrate" {
        $q = @($Script:RunState.InitialIssues | Where-Object { $_.Type -eq "Quality" -and $_.Path -like "*Big Buck Test 4K*" })
        Assert-GreaterOrEqual 1 $q.Count "expected Quality issue on Big Buck Test 4K"
    }

    Invoke-Test "PlayabilityScanner flags Truncated Movie (2021)" {
        $p = @($Script:RunState.InitialIssues | Where-Object { $_.Type -eq "Playability" -and $_.Path -like "*Truncated Movie*" })
        Assert-GreaterOrEqual 1 $p.Count "expected Playability issue on Truncated Movie"
    }

    Invoke-Test "AudioLanguageScanner flags Multi Audio (2022) fra/deu tracks" {
        # Depends on AllowedAudioLanguages config — need to set eng-only first if
        # the config didn't survive from setup. Ensure it's set.
        Set-MediaDashConfig @{ AllowedAudioLanguages = @("eng") }
        Start-MediaDashScan -Wait -TimeoutSec 600
        $issues = Get-MediaDashIssues
        $a = @($issues | Where-Object { $_.Type -eq "AudioLanguage" -and $_.Path -like "*Multi Audio*" })
        Assert-GreaterOrEqual 1 $a.Count "expected AudioLanguage issue on Multi Audio"
    }

    Invoke-Test "SubtitleLanguageScanner flags Sub Heavy (2023) fra/deu subs" {
        Set-MediaDashConfig @{ AllowedSubtitleLanguages = @("eng") }
        Start-MediaDashScan -Wait -TimeoutSec 600
        $issues = Get-MediaDashIssues
        $s = @($issues | Where-Object { $_.Type -eq "SubtitleLanguage" -and $_.Path -like "*Sub Heavy*" })
        Assert-GreaterOrEqual 1 $s.Count "expected SubtitleLanguage issue on Sub Heavy"
    }

    Invoke-Test "SuspiciousFileScanner flags the MZ .exe in movies folder" {
        $issues = Get-MediaDashIssues
        $m = @($issues | Where-Object { $_.Type -eq "MalwareRisk" -and $_.Path -like "*installer.exe" })
        Assert-GreaterOrEqual 1 $m.Count "expected MalwareRisk issue on installer.exe"
    }

    Invoke-Test "NfoScanner flags corrupt Broken Nfo (2020).nfo" {
        $issues = Get-MediaDashIssues
        $n = @($issues | Where-Object { $_.Type -eq "CorruptNfo" -and $_.Path -like "*Broken Nfo*" })
        # NfoScanner detects malformed XML — our fixture is truncated so it should trigger
        if ($n.Count -eq 0) {
            Write-Log "    NOTE: no CorruptNfo detected — may indicate scanner is stricter than fixture; investigate."
        }
        Assert-GreaterOrEqual 1 $n.Count "expected CorruptNfo issue on Broken Nfo fixture"
    }

    Invoke-Test 'ArtworkScanner flags only the owned managed poster' {
        $issues = @(Get-MediaDashIssues -Type 'CorruptArtwork')
        Assert-GreaterOrEqual 1 @($issues | Where-Object Path -EQ $Script:RunState.ArtworkFixturePath).Count 'managed zero-byte image must be detected'
        $sidecar = Join-Path (Split-Path -Parent $Script:RunState.ArtworkVideoPath) 'poster.jpg'
        Assert-Equal 0 @($issues | Where-Object Path -EQ $sidecar).Count 'user-placed sidecar artwork must be preserved'
    }

    Invoke-Test "OrphanCleanupScanner flags ghost.en.srt" {
        $issues = Get-MediaDashIssues
        $o = @($issues | Where-Object { $_.Type -eq "OrphanedDebris" -and $_.Path -like "*ghost.en.srt" })
        Assert-GreaterOrEqual 1 $o.Count "expected OrphanedDebris issue on ghost sidecar"
    }

    Invoke-Test "SubtitleFontScanner flags .ass with font ref (or skips gracefully)" {
        $issues = Get-MediaDashIssues
        $sf = @($issues | Where-Object { $_.Type -eq "SubtitleFonts" -and $_.Path -like "*Ass Sub Test*" })
        Assert-GreaterOrEqual 1 $sf.Count 'expected SubtitleFonts issue on the ASS fixture'
    }

    Invoke-Test 'TrickplayOptimizeScanner flags the padded .trickplay folder' {
        $issues = @(Get-MediaDashIssues -Type 'LargeTrickplay' | Where-Object Path -Like '*Trickplay Test*')
        Assert-GreaterOrEqual 1 $issues.Count 'SaveTrickplayWithMedia is enabled on generated libraries'
    }

    Invoke-Test "PlayabilityScanner does not flag Clean Movie (2024)" {
        $issues = Get-MediaDashIssues
        $bad = @($issues | Where-Object { $_.Type -eq "Playability" -and $_.Path -like "*Clean Movie*" })
        Assert-Equal 0 $bad.Count "Clean Movie should not be flagged as unplayable"
    }

    Invoke-Test "MediaGrouperScanner and MediaSorterScanner run without producing false positives" {
        # These scanners fire on misplaced / ungrouped files. Our fixtures are all
        # properly foldered, so we expect no misplaced/ungrouped issues.
        $issues = Get-MediaDashIssues
        $mp = @($issues | Where-Object { $_.Type -eq "Misplaced" -and $_.Path -notlike "*_orphans*" })
        $ug = @($issues | Where-Object { $_.Type -eq "Ungrouped" })
        Write-Log "    Misplaced=$($mp.Count) · Ungrouped=$($ug.Count)"
        Assert-Equal 0 $mp.Count 'properly foldered fixtures should not be misplaced'
        Assert-Equal 0 $ug.Count 'properly foldered fixtures should not be ungrouped'
    }

    Invoke-Test "StaleContentScanner runs without crashing (may find nothing on fresh library)" {
        $issues = Get-MediaDashIssues
        $st = @($issues | Where-Object { $_.Type -eq "Stale" })
        Write-Log "    Stale issues: $($st.Count) (0 expected on freshly-added fixtures)"
    }

    Invoke-Test "Scan diagnostics have no unexpected errors" {
        $diag = Get-MediaDashDiagnostics
        $errors = @($diag | Where-Object { $_.Category -like "Scan*Error" -or $_.Category -like "*Crash*" })
        if ($errors.Count -gt 0) {
            Write-Log "    Diagnostic errors from scan:"
            foreach ($e in $errors) { Write-Log "      $($e.Category): $($e.Message.Substring(0,[Math]::Min(120,$e.Message.Length)))" }
        }
        Assert-Equal 0 $errors.Count "no scan diagnostic errors expected on clean fixtures"
    }
}
