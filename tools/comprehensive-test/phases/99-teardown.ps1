# Invoke-Test records failures and continues. Never swallow cleanup errors.
function Invoke-Phase-Teardown {
    Start-Phase 'Teardown - restore settings and remove only resources owned by this run'
    if (-not $Script:RunState.JellyfinAuthHeader) { return }
    $tasks = @(Invoke-JfApi -Method GET -Path '/ScheduledTasks')
    foreach ($key in @('MediaDashFix','MediaDashScan','RefreshLibrary')) {
        $task = @($tasks | Where-Object Key -eq $key)
        if ($task.Count -ne 1 -or $task[0].State -ne 'Idle') {
            throw "Cleanup blocked: $key is not idle. Retaining fixture isolation; recover from config-before.json after tasks stop."
        }
    }
    Invoke-Test 'Remove only current-run fixture recycle entries' {
        if ($null -eq $Script:RunState.OriginalBinPaths) { return }
        $entries = @(Get-MediaDashRecycleBinItems | Where-Object {
            $_.BinPath -and $_.BinPath -notin $Script:RunState.OriginalBinPaths
        } | Where-Object {
            if (-not $_.OriginalPath) { return $false }
            if ($Script:RunState.PSObject.Properties['ArtworkFixturePath'] -and $_.OriginalPath -eq $Script:RunState.ArtworkFixturePath) { return $true }
            try {
                Assert-SafeLiveTarget -Url $Script:RunState.JellyfinUrl -AllowedFixturePaths @($Script:RunState.FixturesRoot) -FixturePath $_.OriginalPath
                $true
            } catch { $false }
        })
        foreach ($entry in $entries) {
            Invoke-JfApi -Method POST -Path '/MediaDash/RecycleBin/Items/Delete' -Body @{ BinPath = $entry.BinPath } | Out-Null
        }
        $remaining = @(Get-MediaDashRecycleBinItems | Where-Object BinPath -In @($entries.BinPath))
        Assert-Equal 0 $remaining.Count 'current-run recycle entries should be removed'
    }
    foreach ($libraryName in $Script:RunState.AddedLibraryIds) {
        Invoke-Test "Remove owned Jellyfin library: $libraryName" { Remove-JellyfinLibrary $libraryName }
    }
    Invoke-Test 'Dismiss only owned open issues and verify approvals are cleared' {
        $ownedIssues = @(Get-MediaDashIssues | Where-Object {
            if (-not $_.Path) { return $false }
            if ($Script:RunState.PSObject.Properties['ArtworkFixturePath'] -and $_.Path -eq $Script:RunState.ArtworkFixturePath) { return $true }
            try {
                Assert-SafeLiveTarget -Url $Script:RunState.JellyfinUrl -AllowedFixturePaths @($Script:RunState.FixturesRoot) -FixturePath $_.Path
                $true
            } catch { $false }
        })
        foreach ($ownedIssue in $ownedIssues) { Dismiss-MediaDashIssue $ownedIssue.Id }
        $remaining = @(Get-MediaDashIssues | Where-Object Id -In @($ownedIssues.Id))
        Assert-Equal 0 $remaining.Count 'owned open issues must be dismissed before settings restoration'
    }
    if ($Script:RunState.PSObject.Properties['ScratchRoot']) {
        Invoke-Test 'Remove scratch fixture directory' { Remove-ScratchFixtures }
    }
    Invoke-Test 'Record fixture integrity after the run' {
        if (-not $Script:RunState.PSObject.Properties['BackupRoot']) { return }
        $changes = @(Test-FixtureIntegrity -FixturesRoot $Script:RunState.FixturesRoot)
        $changes | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Script:ArtifactsDir 'fixture-changes.json') -Encoding utf8
        Write-Log "    $($changes.Count) base fixture(s) changed; see fixture-changes.json"
    }
    # Restoring configuration can start a rescan. Do it after all fixture I/O.
    Invoke-Test 'Restore MediaDash config to pre-run snapshot' { Restore-MediaDashConfigSnapshot }
    if ($Script:RunState.PSObject.Properties['OriginalTasks']) {
        foreach ($originalTask in $Script:RunState.OriginalTasks) {
            Invoke-Test "Restore scheduled triggers: $($originalTask.Key)" {
                $json = ConvertTo-Json -InputObject @($originalTask.Triggers) -Depth 10 -Compress
                Invoke-JfApi -Method POST -Path "/ScheduledTasks/$($originalTask.Id)/Triggers" -Body $json | Out-Null
                $actualTask = @(Invoke-JfApi -Method GET -Path '/ScheduledTasks' | Where-Object Key -EQ $originalTask.Key)
                Assert-Equal 1 $actualTask.Count 'original task must remain registered'
                Assert-Equal $json (ConvertTo-Json -InputObject @($actualTask[0].Triggers) -Depth 10 -Compress) 'original triggers must be restored'
            }
        }
    }
}
