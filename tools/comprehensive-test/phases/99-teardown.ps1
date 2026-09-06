# 99-teardown.ps1 — clean up after the run so the box is in a known state.
# Always runs (even if earlier phases errored). Best-effort — every step
# swallows its own errors so one broken cleanup step doesn't hide others.

function Invoke-Phase-Teardown {
    Start-Phase "Teardown — restore config, empty test recycle bin, remove test libraries"

    Invoke-Test "Restore MediaDash config to pre-run snapshot" {
        try {
            Restore-MediaDashConfigSnapshot
        } catch {
            Write-Log "    warn: config restore failed — $($_.Exception.Message)"
        }
    }

    Invoke-Test "Empty the recycle bin of any test-created entries" {
        try {
            $items = Get-MediaDashRecycleBinItems
            $testEntries = @($items | Where-Object {
                $_.OriginalPath -like "*mediadash-fixtures*" -or
                $_.OriginalPath -like "*_test-scratch*"
            })
            if ($testEntries.Count -gt 0) {
                foreach ($e in $testEntries) {
                    Invoke-JfApi -Method POST -Path "/MediaDash/RecycleBin/Items/Delete" -Body @{ HistoryIds = @($e.HistoryId) } -IgnoreErrors | Out-Null
                }
                Write-Log "    cleared $($testEntries.Count) test recycle-bin entries"
            }
        } catch {
            Write-Log "    warn: recycle bin cleanup failed — $($_.Exception.Message)"
        }
    }

    Invoke-Test "Remove test Jellyfin libraries added during setup" {
        foreach ($libName in @("MediaDash Test", "MediaDash Scratch")) {
            try {
                Remove-JellyfinLibrary $libName
            } catch {
                Write-Log "    warn: could not remove $libName — $($_.Exception.Message)"
            }
        }
    }

    Invoke-Test "Remove scratch fixture directory" {
        try {
            Remove-ScratchFixtures
        } catch {
            Write-Log "    warn: scratch cleanup failed — $($_.Exception.Message)"
        }
    }

    Invoke-Test "Fixture integrity check — report what the run modified" {
        try {
            $changes = Test-FixtureIntegrity -FixturesRoot $Script:RunState.FixturesRoot
            if ($changes.Count -eq 0) {
                Write-Log "    all fixture files match backup manifest (no modifications)"
            } else {
                Write-Log "    $($changes.Count) fixture file(s) changed during the run:"
                foreach ($c in $changes) { Write-Log "      [$($c.kind)] $($c.path)" }
                # Also write to an artifact so it's in the summary
                $artifactPath = Join-Path $Script:ArtifactsDir "fixture-changes.json"
                $changes | ConvertTo-Json -Depth 6 | Set-Content -Path $artifactPath -Encoding utf8
                Write-Log "    → $artifactPath"
            }
        } catch {
            Write-Log "    warn: integrity check failed — $($_.Exception.Message)"
        }
    }

    Invoke-Test "Verify no MediaDash issues remain in DB (informational)" {
        try {
            $issues = Get-MediaDashIssues
            Write-Log "    issues remaining in DB after teardown: $($issues.Count) (informational only)"
        } catch {
            Write-Log "    warn: issue count check failed — $($_.Exception.Message)"
        }
    }
}
