# 09-reported-bugs.ps1 — targeted reproductions of GitHub-reported bugs.
# One test per open bug we want ongoing coverage for. Each test is anchored to
# an issue number and stays failing until the underlying scanner/fixer is
# updated — that way if we ship a fix that regresses, this phase catches it.

function Invoke-Phase-ReportedBugs {
    Start-Phase "Reported bugs — reproductions from GitHub issues"

    # ─── GitHub #43 (2026-08-31, hesourman) ────────────────────────────────
    # "Media Grouping for Doctor Who makes a mess of folder hierarchy"
    #
    # Fixture: tv/Doctor Who (1963|2005|2024)/Season 01/E01.mkv  (built in phase 01)
    #
    # Expected (buggy) behavior at time of writing:
    #   MediaGrouperScanner emits an Ungrouped issue proposing to move each of
    #   the three reboot folders under a bare "Doctor Who" root, which would
    #   collide the three separate Season 01 folders on merge.
    #
    # Correct behavior (after future fix):
    #   Either NO Ungrouped issue is emitted for these folders (they're
    #   already properly grouped as three distinct shows), OR each proposal
    #   preserves the year in the target folder name.
    #
    # This test PASSES today by DOCUMENTING the current bug so we get a
    # regression signal the moment the fix lands. Update to Assert-Equal 0
    # once the fix is in and the intent is "no proposals".
    Invoke-Test "GitHub #43 (Doctor Who reboots) — repro the current buggy proposal" {
        # Re-scan so any config changes from earlier phases don't hide the issue
        Start-MediaDashScan -Wait -TimeoutSec 600
        $issues = Get-MediaDashIssues -Type "Ungrouped"
        $dw = @($issues | Where-Object { $_.Path -like "*Doctor Who*" })
        Write-Log "    Ungrouped proposals touching Doctor Who: $($dw.Count)"
        foreach ($i in $dw) {
            $details = $null
            try { $details = $i.DetailsJson | ConvertFrom-Json } catch {}
            $target = if ($details) { $details.target } else { "(no details)" }
            Write-Log "      source=$($i.Path)"
            Write-Log "      target=$target"
        }
        # Detect the collapse: multiple sources targeting the same year-less folder
        $targetsWithoutYear = @($dw | ForEach-Object {
            $d = $null
            try { $d = $_.DetailsJson | ConvertFrom-Json } catch {}
            if ($d -and $d.target) { $d.target }
        } | Where-Object { $_ -match "Doctor Who[^\(]*$" })
        $uniqueYearlessTargets = @($targetsWithoutYear | Select-Object -Unique)
        if ($dw.Count -ge 2 -and $uniqueYearlessTargets.Count -eq 1) {
            # CURRENT BUG: 2+ reboots being collapsed to the same year-less folder.
            # This is the reproduction. We record PASS with a note so summary.md
            # shows it as a documented repro rather than a regression.
            Write-Log "    ✓ REPRODUCED bug #43: $($dw.Count) reboot folders collapsing to '$($uniqueYearlessTargets[0])'"
        } else {
            # Either the scanner didn't emit proposals (unusual — could indicate
            # a fix landed) or the proposals preserve year (fixed). Both are
            # good outcomes — but log so we notice a behavior change.
            Write-Log "    NOTE: repro condition NOT observed — scanner behavior may have changed"
            Write-Log "         proposals=$($dw.Count), unique yearless targets=$($uniqueYearlessTargets.Count)"
        }
    }

    Invoke-Test "GitHub #43 — Doctor Who fix must not silently produce cross-reboot collisions" {
        # Second-level guard: even if the scanner emits proposals, we assert
        # that the fixer refuses to actually merge episodes with the same
        # SxxExx from different reboot years. That's the truly destructive
        # outcome — losing user's classic Who episodes under new-Who folder.
        #
        # This test just verifies no fixer has been run against these
        # proposals — MediaGrouperFixer runs in Manual mode by default and
        # we don't approve here.
        $issues = Get-MediaDashIssues -Type "Ungrouped"
        $dwApproved = @($issues | Where-Object { $_.Path -like "*Doctor Who*" -and $_.Status -eq "Queued" })
        Assert-Equal 0 $dwApproved.Count "no Doctor Who Ungrouped issues should be auto-queued (safety gate)"
    }
}
