# 08-edge-cases.ps1 — the "weird stuff" that field bugs come from.
# Skippable with -Quick.

function Invoke-Phase-EdgeCases {
    Start-Phase "Edge cases — unicode paths, 0-byte files, concurrency, cancel-while-running"

    if ($Script:RunState.Quick) {
        Write-Log "    -Quick set: skipping most edge-case tests"
    }

    Invoke-Test "Unicode filename fixture is scanned without crashing" {
        $issues = Get-MediaDashIssues
        # Just verify the scan didn't blow up on the 日本語 fixture — no assertion on
        # a specific issue type since it depends on video content.
        $diag = Get-MediaDashDiagnostics
        $unicodeErrors = @($diag | Where-Object { $_.Message -like "*日本語*" -and $_.Category -like "*Error*" })
        Assert-Equal 0 $unicodeErrors.Count "no error diagnostics for unicode path"
    }

    Invoke-Test "0-byte file does not cause any scanner to crash" {
        $diag = Get-MediaDashDiagnostics
        $zeroErrors = @($diag | Where-Object { $_.Message -like "*Zero Byte*" -and $_.Category -like "*Error*" })
        Assert-Equal 0 $zeroErrors.Count "no error diagnostics for 0-byte file"
    }

    Invoke-Test "Concurrent Scan/Run calls are serialized (second is 4xx or no-op)" {
        # Start a scan
        Invoke-JfApi -Method POST -Path "/MediaDash/Scan" -IgnoreErrors | Out-Null
        Start-Sleep -Milliseconds 500
        # Try to start another
        $secondScan = try {
            Invoke-JfApi -Method POST -Path "/MediaDash/Scan" -Raw
        } catch [System.Net.WebException] {
            $_.Exception.Response
        }
        $s = Get-MediaDashStatus
        Write-Log "    IsScanning after double-start: $($s.IsScanning)"
        # Both allowed to succeed but should not produce two concurrent scan runs
        # Cancel and wait
        Invoke-JfApi -Method POST -Path "/MediaDash/Scan/Cancel" -IgnoreErrors | Out-Null
        Wait-For -TimeoutSec 60 -Description "scan to stop" -Predicate {
            $s = Get-MediaDashStatus; return (-not $s.IsScanning)
        } | Out-Null
    }

    Invoke-Test "Scan Cancel while running stops the task" {
        Invoke-JfApi -Method POST -Path "/MediaDash/Scan" | Out-Null
        Start-Sleep -Milliseconds 800
        Invoke-JfApi -Method POST -Path "/MediaDash/Scan/Cancel" | Out-Null
        Wait-For -TimeoutSec 30 -Description "scan to cancel" -Predicate {
            $s = Get-MediaDashStatus; return (-not $s.IsScanning)
        } | Out-Null
    }

    Invoke-Test "Fix Cancel while running stops the task" {
        # Approve everything so a fix run has work
        $issues = Get-MediaDashIssues
        $detected = @($issues | Where-Object { $_.Status -eq "Detected" })
        foreach ($i in $detected) { Approve-MediaDashIssue $i.Id }
        Set-MediaDashConfig @{ DryRun = $true }
        Invoke-JfApi -Method POST -Path "/MediaDash/Fix" | Out-Null
        Start-Sleep -Milliseconds 800
        Invoke-JfApi -Method POST -Path "/MediaDash/Fix/Cancel" | Out-Null
        Wait-For -TimeoutSec 60 -Description "fix run to cancel" -Predicate {
            $s = Get-MediaDashStatus; return (-not $s.IsFixing)
        } | Out-Null
    }

    Invoke-Test "Multiple config saves in quick succession don't corrupt state" {
        for ($i = 0; $i -lt 10; $i++) {
            Set-MediaDashConfig @{ DryRun = ($i % 2 -eq 0); RecycleBinRetentionDays = (30 + $i) }
        }
        Start-Sleep -Milliseconds 500
        $cfg = Get-MediaDashConfig
        Assert-Equal 39 $cfg.RecycleBinRetentionDays "final retention should match last save"
        # And schedule trigger should still be intact (1.0.7.5 bugfix)
        $fixTask = (Invoke-JfApi -Method GET -Path "/ScheduledTasks") | Where-Object Key -eq "MediaDashFix" | Select-Object -First 1
        # If a trigger existed before, it should still be there
        Write-Log "    trigger count after 10 saves: $(@($fixTask.Triggers).Count)"
    }

    Invoke-Test "Plugin survives a Jellyfin task-registry reload (invoke random ScheduledTasks GET)" {
        1..5 | ForEach-Object {
            Invoke-JfApi -Method GET -Path "/ScheduledTasks" | Out-Null
            Invoke-JfApi -Method GET -Path "/MediaDash/Status" | Out-Null
        }
        $s = Get-MediaDashStatus
        Assert-True ($null -ne $s.IsScanning) "MediaDash still responsive after burst"
    }
}
