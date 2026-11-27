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

# Wraps Invoke-Browse so callers that plan to interpret the output are forced
# to first see a nonzero exit as an assertion failure. Previously a browse
# binary that exited 1 with body "browser failed" could produce misleading UI
# assertions because callers only touched .Output.
function Invoke-BrowseChecked {
    param([string]$Bin, [string[]]$BrowseArgs, [switch]$AllowEmpty)
    $r = Invoke-Browse -Bin $Bin -BrowseArgs $BrowseArgs
    if ($r.ExitCode -ne 0) {
        throw [TestAssertionFailedException]::new(
            "browse exited $($r.ExitCode)",
            "exit 0",
            "exit $($r.ExitCode); output: $($r.Output)")
    }
    if (-not $AllowEmpty -and [string]::IsNullOrWhiteSpace($r.Output)) {
        throw [TestAssertionFailedException]::new(
            "browse returned empty output",
            "non-empty output",
            "(empty)")
    }
    return $r
}

function Assert-BrowseVisible {
    param([string]$Bin, [string]$Selector)
    $selectorJson = ConvertTo-Json -InputObject $Selector -Compress
    $js = "(function(){var e=document.querySelector($selectorJson);if(!e)return 'NO';var s=getComputedStyle(e);return e.getClientRects().length>0&&s.display!=='none'&&s.visibility!=='hidden'?'YES':'NO';})()"
    $result = (Invoke-BrowseChecked $Bin @('js',$js)).Output
    Assert-Match 'YES' $result "visible element required: $Selector"
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
    Invoke-Test 'Prepare dashboard fixture beyond first-run walkthrough' {
        Set-MediaDashConfig @{FirstRunDone=$true}
    }

    Invoke-Test "Login page loads and login succeeds" {
        # Every browser interaction goes through Invoke-BrowseChecked so a broken
        # browser (exit 1) can't slip past unnoticed — the wrapper throws
        # TestAssertionFailedException and Invoke-Test records the FAIL.
        Invoke-BrowseChecked $browse @("goto", "$($Script:RunState.JellyfinUrl)/web/index.html") -AllowEmpty | Out-Null
        Start-Sleep -Seconds 2
        Invoke-BrowseChecked $browse @("wait", "--networkidle") -AllowEmpty | Out-Null
        # Try login form — Jellyfin sometimes lands directly on home if there's a persisted session
        $snap = (Invoke-BrowseChecked $browse @("snapshot", "-i")).Output
        if ($snap -match 'textbox.*User') {
            Invoke-BrowseChecked $browse @("fill", "@e1", $Script:RunState.AdminUser) -AllowEmpty | Out-Null
            Invoke-BrowseChecked $browse @("fill", "@e2", $Script:RunState.AdminPass) -AllowEmpty | Out-Null
            Invoke-BrowseChecked $browse @("click", "@e4") -AllowEmpty | Out-Null
            Start-Sleep -Seconds 3
        }
        # Assert we ended up on an authenticated page (something other than the login form).
        $post = (Invoke-BrowseChecked $browse @("snapshot", "-i")).Output
        Assert-True ($post -notmatch 'textbox.*User' -or $post -match 'button.*Sign\s*Out' -or $post -match 'link.*Dashboard') `
            "after login we should not still be looking at a fresh username textbox"
    }

    Invoke-Test "MediaDash config page loads without JS errors" {
        Invoke-BrowseChecked $browse @("goto", "$($Script:RunState.JellyfinUrl)/web/index.html#!/configurationpage?name=MediaDash") -AllowEmpty | Out-Null
        Invoke-BrowseChecked $browse @("wait", "--networkidle") -AllowEmpty | Out-Null
        Start-Sleep -Seconds 3
        $console = (Invoke-BrowseChecked $browse @("console", "--errors") -AllowEmpty).Output
        # Filter out benign warnings — the Jellyfin dashboard emits some third-party console warnings
        $realErrors = $console -split "`n" | Where-Object {
            $_ -match "error" -and
            $_ -notmatch "favicon" -and
            $_ -notmatch "DevTools" -and
            # Jellyfin core WebSocket handshake to /socket briefly 403s during page load in headless
            # Chrome (the token isn't accepted for the /socket subprotocol until the auth cookie is
            # fully populated). Not caused by MediaDash — same error appears on any Jellyfin plugin
            # config page in headless mode. Filter to keep the assertion trained on MediaDash bugs.
            $_ -notmatch "WebSocket connection to 'ws://[^']*/socket"
        }
        if ($realErrors.Count -gt 0) {
            Write-Log "    JS console errors:"
            foreach ($e in $realErrors) { Write-Log "      $e" }
        }
        Assert-Equal 0 $realErrors.Count "config page should have no JS console errors"
    }

    Invoke-Test "MediaDashConfigPage element is present" {
        $r = (Invoke-BrowseChecked $browse @("js", "document.querySelector('#MediaDashConfigPage') ? 'YES' : 'NO'")).Output
        Assert-Match "YES" $r "MediaDashConfigPage container should mount"
    }

    Invoke-Test "Overview tab renders and includes system + drive widgets" {
        Assert-BrowseVisible $browse '#mdTab-overview'
        # Default landing is Overview. Verify key children.
        $sys = (Invoke-BrowseChecked $browse @("js", "document.querySelector('#mdSystem') ? 'YES' : 'NO'")).Output
        Assert-Match "YES" $sys "#mdSystem should render"
        $drives = (Invoke-BrowseChecked $browse @("js", "document.querySelector('#mdDrives') ? 'YES' : 'NO'")).Output
        Assert-Match "YES" $drives "#mdDrives should render"
    }

    Invoke-Test "Library breakdown card renders (or shows empty state gracefully)" {
        $libHost = (Invoke-BrowseChecked $browse @("js", "document.querySelector('#mdLibraryBreakdown') ? 'YES' : 'NO'")).Output
        Assert-Match "YES" $libHost "#mdLibraryBreakdown container should exist"
    }

    Invoke-Test "Issues tab is clickable and shows filter chips" {
        Invoke-BrowseChecked $browse @("js", "var b=document.querySelector('[data-tab=issues]');b&&b.click();'ok'") | Out-Null
        Start-Sleep -Seconds 2
        Assert-BrowseVisible $browse '#mdTab-issues'
        $totals = (Invoke-BrowseChecked $browse @("js", "document.querySelector('#mdIssueTotals')?document.querySelector('#mdIssueTotals').children.length:0")).Output
        Write-Log "    Issue type chips: $totals"
        # Assert we actually saw a numeric count from the DOM (nonzero) — previously we
        # only logged and never asserted, so a browser that returned garbage still passed.
        Assert-Match '^\s*\d+' $totals "Issues tab should expose #mdIssueTotals children count"
    }

    Invoke-Test "Recycle bin tab loads without crash" {
        Invoke-BrowseChecked $browse @("js", "var b=document.querySelector('[data-tab=recycle]');b&&b.click();'ok'") | Out-Null
        Start-Sleep -Seconds 2
        Assert-BrowseVisible $browse '#mdTab-recycle'
        $console = (Invoke-BrowseChecked $browse @("console", "--errors") -AllowEmpty).Output
        $realErrors = $console -split "`n" | Where-Object {
            $_ -match "error" -and
            $_ -notmatch "favicon" -and
            $_ -notmatch "WebSocket connection to 'ws://[^']*/socket"
        }
        Assert-Equal 0 $realErrors.Count "Recycle bin tab should not raise JS console errors"
    }
    foreach ($tabName in @('overview','issues','history','files','recycle','errors','settings')) {
        Invoke-Test "Visible UI tab and screenshot: $tabName" {
            Invoke-BrowseChecked $browse @('js',"document.querySelector('[data-tab=$tabName]').click();'clicked'") | Out-Null
            Start-Sleep -Milliseconds 750
            Assert-BrowseVisible $browse "#mdTab-$tabName"
            $screenshot = Join-Path $Script:ArtifactsDir "ui-$tabName.png"
            Invoke-BrowseChecked $browse @('screenshot','--viewport',$screenshot) | Out-Null
            Assert-FileExists $screenshot 'browser screenshot must be saved'
        }
    }
}
