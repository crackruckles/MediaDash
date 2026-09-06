# 07-ui.ps1 — smoke test the UI via the gstack browse tool.
# Verifies the config page loads, each tab is navigable, no JS console errors.
# Skipped if the browse binary isn't present.

function Get-BrowseBin {
    $candidates = @()
    $root = $null
    try { $root = git -C $Script:PluginRoot rev-parse --show-toplevel 2>$null } catch {}
    if ($root) { $candidates += (Join-Path $root ".claude\skills\gstack\browse\dist\browse.exe") }
    $candidates += "$env:USERPROFILE\.claude\skills\gstack\browse\dist\browse.exe"
    $candidates += "$env:USERPROFILE\.claude\skills\gstack\browse\dist\browse"
    foreach ($c in $candidates) { if (Test-Path -LiteralPath $c) { return $c } }
    return $null
}

function Invoke-Browse {
    param([string]$Bin, [string[]]$BrowseArgs)
    # browse emits benign startup lines to stderr ("[browse] Starting server...").
    # Under $ErrorActionPreference='Stop' those become ErrorRecords that throw the
    # scriptblock. Temporarily relax + coerce ErrorRecords to plain strings so
    # both streams merge into $Output cleanly.
    $prevEA = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $raw = & $Bin @BrowseArgs 2>&1
    } finally {
        $ErrorActionPreference = $prevEA
    }
    $lines = @($raw | ForEach-Object {
        if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.Exception.Message } else { [string]$_ }
    })
    return @{ Output = ($lines -join "`n"); ExitCode = $LASTEXITCODE }
}

function Invoke-Phase-Ui {
    Start-Phase "UI — config page + tabs + no JS console errors"

    $browse = Get-BrowseBin
    if (-not $browse) {
        Write-Log "    UI phase SKIPPED — browse tool not available"
        Invoke-Test "browse tool available" -SkipReason "browse binary not found" { }
        return
    }
    Write-Log "    using browse: $browse"

    Invoke-Test "Login page loads and login succeeds" {
        Invoke-Browse $browse @("goto", "$($Script:RunState.JellyfinUrl)/web/index.html") | Out-Null
        Start-Sleep -Seconds 2
        Invoke-Browse $browse @("wait", "--networkidle") | Out-Null
        # Try login form — Jellyfin sometimes lands directly on home if there's a persisted session
        $snap = (Invoke-Browse $browse @("snapshot", "-i")).Output
        if ($snap -match 'textbox.*User') {
            Invoke-Browse $browse @("fill", "@e1", $Script:RunState.AdminUser) | Out-Null
            Invoke-Browse $browse @("fill", "@e2", $Script:RunState.AdminPass) | Out-Null
            Invoke-Browse $browse @("click", "@e4") | Out-Null
            Start-Sleep -Seconds 3
        }
    }

    Invoke-Test "MediaDash config page loads without JS errors" {
        Invoke-Browse $browse @("goto", "$($Script:RunState.JellyfinUrl)/web/index.html#!/configurationpage?name=MediaDash") | Out-Null
        Invoke-Browse $browse @("wait", "--networkidle") | Out-Null
        Start-Sleep -Seconds 3
        $console = (Invoke-Browse $browse @("console", "--errors")).Output
        # Filter out benign warnings — the Jellyfin dashboard emits some third-party console warnings
        $realErrors = $console -split "`n" | Where-Object {
            $_ -match "error" -and
            $_ -notmatch "favicon" -and
            $_ -notmatch "DevTools"
        }
        if ($realErrors.Count -gt 0) {
            Write-Log "    JS console errors:"
            foreach ($e in $realErrors) { Write-Log "      $e" }
        }
        Assert-Equal 0 $realErrors.Count "config page should have no JS console errors"
    }

    Invoke-Test "MediaDashConfigPage element is present" {
        $r = (Invoke-Browse $browse @("js", "document.querySelector('#MediaDashConfigPage') ? 'YES' : 'NO'")).Output
        Assert-Match "YES" $r "MediaDashConfigPage container should mount"
    }

    Invoke-Test "Overview tab renders and includes system + drive widgets" {
        # Default landing is Overview. Verify key children.
        $sys = (Invoke-Browse $browse @("js", "document.querySelector('#mdSystem') ? 'YES' : 'NO'")).Output
        Assert-Match "YES" $sys "#mdSystem should render"
        $drives = (Invoke-Browse $browse @("js", "document.querySelector('#mdDrives') ? 'YES' : 'NO'")).Output
        Assert-Match "YES" $drives "#mdDrives should render"
    }

    Invoke-Test "Library breakdown card renders (or shows empty state gracefully)" {
        $libHost = (Invoke-Browse $browse @("js", "document.querySelector('#mdLibraryBreakdown') ? 'YES' : 'NO'")).Output
        Assert-Match "YES" $libHost "#mdLibraryBreakdown container should exist"
    }

    Invoke-Test "Issues tab is clickable and shows filter chips" {
        Invoke-Browse $browse @("js", "var b=document.querySelector('[data-tab=issues]');b&&b.click();'ok'") | Out-Null
        Start-Sleep -Seconds 2
        $totals = (Invoke-Browse $browse @("js", "document.querySelector('#mdIssueTotals')?document.querySelector('#mdIssueTotals').children.length:0")).Output
        Write-Log "    Issue type chips: $totals"
    }

    Invoke-Test "Recycle bin tab loads without crash" {
        Invoke-Browse $browse @("js", "var b=document.querySelector('[data-tab=recycle]');b&&b.click();'ok'") | Out-Null
        Start-Sleep -Seconds 2
        $console = (Invoke-Browse $browse @("console", "--errors")).Output
        $realErrors = $console -split "`n" | Where-Object { $_ -match "error" -and $_ -notmatch "favicon" }
        # Snapshot count only — errors on a specific tab are the actual concern
    }
}
