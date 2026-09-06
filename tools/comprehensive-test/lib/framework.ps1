# framework.ps1 — test runner, assertions, reporting, status file.
# Consumed by run-full-tests.ps1 via dot-source. Uses $Script:RunState for
# shared state so phases can reference the same auth token / run counters.

# ═════════════════════════════════════════════════════════════════════════════
# Logging
# ═════════════════════════════════════════════════════════════════════════════

function Write-Log {
    param([string]$Message, [string]$Level = "INFO")
    $ts = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff")
    $line = "$ts [$Level] $Message"
    Write-Host $line
    Add-Content -Path $Script:LogPath -Value $line -Encoding utf8
}

# ═════════════════════════════════════════════════════════════════════════════
# Result recording (JSONL)
# ═════════════════════════════════════════════════════════════════════════════

function Write-TestResult {
    param(
        [string]$Phase,
        [string]$Name,
        [string]$Result,          # PASS / FAIL / SKIP / ERROR
        [double]$DurationMs,
        [string]$Expected = "",
        [string]$Actual = "",
        [string]$Notes = "",
        [string[]]$Artifacts = @()
    )
    $obj = [PSCustomObject]@{
        runId = $Script:RunState.RunId
        phase = $Phase
        test = $Name
        result = $Result
        startedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        durationMs = [Math]::Round($DurationMs, 1)
        expected = $Expected
        actual = $Actual
        notes = $Notes
        artifacts = $Artifacts
    }
    $json = $obj | ConvertTo-Json -Compress -Depth 6
    Add-Content -Path $Script:JsonlPath -Value $json -Encoding utf8

    switch ($Result) {
        "PASS"  { $Script:RunState.TestsPassed++ }
        "FAIL"  { $Script:RunState.TestsFailed++ }
        "SKIP"  { $Script:RunState.TestsSkipped++ }
        "ERROR" { $Script:RunState.TestsErrored++ }
    }
    $Script:RunState.TotalTests++
    Update-Status
}

# ═════════════════════════════════════════════════════════════════════════════
# Test invocation — Invoke-Test wraps a scriptblock with:
#   • timing
#   • try/catch → ERROR
#   • assertion failures → FAIL
#   • artifact snapshots on FAIL/ERROR
#   • HaltOnFailure honoring
# ═════════════════════════════════════════════════════════════════════════════

class TestAssertionFailedException : System.Exception {
    [string]$Expected
    [string]$Actual
    TestAssertionFailedException([string]$msg, [string]$exp, [string]$act) : base($msg) {
        $this.Expected = $exp
        $this.Actual = $act
    }
}

function Invoke-Test {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Block,
        [int]$TimeoutSec = 0,     # 0 = use default
        [string]$SkipReason = ""  # non-empty → skip without running the block
    )
    $phase = $Script:RunState.CurrentPhase
    $Script:RunState.CurrentTest = $Name
    Update-Status

    if ($SkipReason) {
        Write-Log "  ⊘ SKIP: $Name — $SkipReason"
        Write-TestResult -Phase $phase -Name $Name -Result "SKIP" -DurationMs 0 -Notes "$SkipReason"
        return
    }

    Write-Log "  ▶ TEST: $Name"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $Block
        $sw.Stop()
        Write-Log "  ✓ PASS: $Name  ($([Math]::Round($sw.Elapsed.TotalSeconds,2))s)"
        Write-TestResult -Phase $phase -Name $Name -Result "PASS" -DurationMs $sw.Elapsed.TotalMilliseconds
    } catch [TestAssertionFailedException] {
        $sw.Stop()
        Write-Log "  ✗ FAIL: $Name — $($_.Exception.Message)" "FAIL"
        Write-Log "       expected: $($_.Exception.Expected)" "FAIL"
        Write-Log "       actual:   $($_.Exception.Actual)" "FAIL"
        $artifacts = Save-FailureArtifacts $Name $_
        Write-TestResult -Phase $phase -Name $Name -Result "FAIL" -DurationMs $sw.Elapsed.TotalMilliseconds `
            -Expected $_.Exception.Expected -Actual $_.Exception.Actual -Notes $_.Exception.Message -Artifacts $artifacts
        if ($Script:RunState.HaltOnFailure) { throw "HaltOnFailure: FAIL in $Name" }
    } catch {
        $sw.Stop()
        Write-Log "  ⚠ ERROR: $Name — $($_.Exception.Message)" "ERROR"
        Write-Log "       $($_.ScriptStackTrace)" "ERROR"
        $artifacts = Save-FailureArtifacts $Name $_
        Write-TestResult -Phase $phase -Name $Name -Result "ERROR" -DurationMs $sw.Elapsed.TotalMilliseconds `
            -Notes ("{0}`n{1}" -f $_.Exception.Message, $_.ScriptStackTrace) -Artifacts $artifacts
        if ($Script:RunState.HaltOnFailure) { throw "HaltOnFailure: ERROR in $Name" }
    }
    $Script:RunState.CurrentTest = $null
}

# ═════════════════════════════════════════════════════════════════════════════
# Assertions — throw TestAssertionFailedException on mismatch. Invoke-Test
# converts that to FAIL and records expected/actual.
# ═════════════════════════════════════════════════════════════════════════════

function Assert-Equal {
    param($Expected, $Actual, [string]$Message = "values should be equal")
    if ($Expected -ne $Actual) {
        throw [TestAssertionFailedException]::new($Message, "$Expected", "$Actual")
    }
}

function Assert-NotEqual {
    param($Expected, $Actual, [string]$Message = "values should differ")
    if ($Expected -eq $Actual) {
        throw [TestAssertionFailedException]::new($Message, "not: $Expected", "$Actual")
    }
}

function Assert-True {
    param([bool]$Condition, [string]$Message = "condition should be true")
    if (-not $Condition) {
        throw [TestAssertionFailedException]::new($Message, "true", "false")
    }
}

function Assert-False {
    param([bool]$Condition, [string]$Message = "condition should be false")
    if ($Condition) {
        throw [TestAssertionFailedException]::new($Message, "false", "true")
    }
}

function Assert-GreaterOrEqual {
    param($Threshold, $Actual, [string]$Message = "value should meet threshold")
    if ($Actual -lt $Threshold) {
        throw [TestAssertionFailedException]::new($Message, ">= $Threshold", "$Actual")
    }
}

function Assert-LessOrEqual {
    param($Threshold, $Actual, [string]$Message = "value should be within threshold")
    if ($Actual -gt $Threshold) {
        throw [TestAssertionFailedException]::new($Message, "<= $Threshold", "$Actual")
    }
}

function Assert-Contains {
    param($Collection, $Item, [string]$Message = "collection should contain item")
    if ($null -eq $Collection -or -not (@($Collection) -contains $Item)) {
        throw [TestAssertionFailedException]::new($Message, "contains: $Item", "$(@($Collection) -join ', ')")
    }
}

function Assert-NotContains {
    param($Collection, $Item, [string]$Message = "collection should not contain item")
    if ($null -ne $Collection -and (@($Collection) -contains $Item)) {
        throw [TestAssertionFailedException]::new($Message, "does not contain: $Item", "contains it")
    }
}

function Assert-FileExists {
    param([string]$Path, [string]$Message = "file should exist")
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw [TestAssertionFailedException]::new($Message, "exists: $Path", "not found")
    }
}

function Assert-FileMissing {
    param([string]$Path, [string]$Message = "file should not exist")
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        throw [TestAssertionFailedException]::new($Message, "not found: $Path", "exists")
    }
}

function Assert-Match {
    param([string]$Pattern, [string]$Value, [string]$Message = "value should match pattern")
    if ($Value -notmatch $Pattern) {
        throw [TestAssertionFailedException]::new($Message, "matches: $Pattern", "$Value")
    }
}

# ═════════════════════════════════════════════════════════════════════════════
# Wait-For — poll until predicate returns true, or timeout. Returns the
# last value predicate produced (or $null on timeout).
# ═════════════════════════════════════════════════════════════════════════════

function Wait-For {
    param(
        [Parameter(Mandatory)][scriptblock]$Predicate,
        [int]$TimeoutSec = 60,
        [int]$IntervalMs = 500,
        [string]$Description = "condition"
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $last = $null
    while ((Get-Date) -lt $deadline) {
        try {
            $last = & $Predicate
            if ($last) { return $last }
        } catch {
            $last = $_.Exception.Message
        }
        Start-Sleep -Milliseconds $IntervalMs
    }
    throw [TestAssertionFailedException]::new("Wait-For timeout: $Description", "$Description within ${TimeoutSec}s", "still: $last")
}

# ═════════════════════════════════════════════════════════════════════════════
# Failure artifacts — copy plugin DB + config + diagnostics + Jellyfin log
# tail on any FAIL/ERROR so failures can be post-mortem'd.
# ═════════════════════════════════════════════════════════════════════════════

function Save-FailureArtifacts {
    param([string]$TestName, [object]$ErrorRecord)
    $safeName = ($TestName -replace '[^\w\-]+','_').Trim('_')
    $dir = Join-Path $Script:FailuresDir $safeName
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $collected = @()

    # 1. Error record + stack
    try {
        $errorInfo = [PSCustomObject]@{
            message = $ErrorRecord.Exception.Message
            type = $ErrorRecord.Exception.GetType().FullName
            stackTrace = $ErrorRecord.ScriptStackTrace
            capturedAt = (Get-Date).ToUniversalTime().ToString("o")
        }
        $errorInfo | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $dir "error.json") -Encoding utf8
        $collected += "error.json"
    } catch {}

    # 2. Plugin DB
    try {
        $dbPath = "$env:LOCALAPPDATA\jellyfin\data\mediadash\mediadash.db"
        if (Test-Path -LiteralPath $dbPath) {
            Copy-Item -LiteralPath $dbPath -Destination (Join-Path $dir "mediadash.db") -Force
            $collected += "mediadash.db"
        }
    } catch {}

    # 3. Plugin config XML
    try {
        $cfgPath = "$env:LOCALAPPDATA\jellyfin\plugins\configurations\Jellyfin.Plugin.MediaDash.xml"
        if (Test-Path -LiteralPath $cfgPath) {
            Copy-Item -LiteralPath $cfgPath -Destination (Join-Path $dir "plugin-config.xml") -Force
            $collected += "plugin-config.xml"
        }
    } catch {}

    # 4. Plugin diagnostics (via API — records live in memory)
    try {
        if ($Script:RunState.JellyfinAuthHeader) {
            $diag = Invoke-JfApi -Method GET -Path "/MediaDash/Diagnostics"
            $diag | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $dir "diagnostics.json") -Encoding utf8
            $collected += "diagnostics.json"
        }
    } catch {}

    # 5. Jellyfin log tail — last 300 lines of the newest log
    try {
        $logDir = "$env:LOCALAPPDATA\jellyfin\log"
        if (Test-Path $logDir) {
            $newest = Get-ChildItem -Path $logDir -Filter "*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($newest) {
                Get-Content -LiteralPath $newest.FullName -Tail 300 | Set-Content -Path (Join-Path $dir "jellyfin-tail.log") -Encoding utf8
                $collected += "jellyfin-tail.log"
            }
        }
    } catch {}

    # 6. MediaDash /Status snapshot
    try {
        if ($Script:RunState.JellyfinAuthHeader) {
            $status = Invoke-JfApi -Method GET -Path "/MediaDash/Status"
            $status | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $dir "mediadash-status.json") -Encoding utf8
            $collected += "mediadash-status.json"
        }
    } catch {}

    Write-Log "    ↳ artifacts saved to failures\$safeName ($($collected.Count) files)"
    return $collected
}

# ═════════════════════════════════════════════════════════════════════════════
# Status file — updated continuously so you can watch progress from another
# terminal. Read with: Get-Content status.json | ConvertFrom-Json
# ═════════════════════════════════════════════════════════════════════════════

function Update-Status {
    param([switch]$Final)
    try {
        $now = [DateTime]::UtcNow
        $elapsed = ($now - $Script:RunState.StartedAtUtc).TotalSeconds
        $obj = [PSCustomObject]@{
            runId = $Script:RunState.RunId
            startedAtUtc = $Script:RunState.StartedAtUtc.ToString("o")
            nowUtc = $now.ToString("o")
            elapsedSec = [Math]::Round($elapsed, 1)
            currentPhase = $Script:RunState.CurrentPhase
            currentTest = $Script:RunState.CurrentTest
            phasesRun = $Script:RunState.PhasesRun
            counters = [PSCustomObject]@{
                pass = $Script:RunState.TestsPassed
                fail = $Script:RunState.TestsFailed
                error = $Script:RunState.TestsErrored
                skip = $Script:RunState.TestsSkipped
                total = $Script:RunState.TotalTests
            }
            phaseTimings = $Script:RunState.PhaseTimings
            final = [bool]$Final
        }
        $obj | ConvertTo-Json -Depth 6 | Set-Content -Path $Script:StatusPath -Encoding utf8
    } catch {}
}

# ═════════════════════════════════════════════════════════════════════════════
# Summary — final markdown report
# ═════════════════════════════════════════════════════════════════════════════

function Write-Summary {
    $now = [DateTime]::UtcNow
    $elapsed = ($now - $Script:RunState.StartedAtUtc).TotalSeconds

    # Read all results back for grouping
    $all = @()
    if (Test-Path $Script:JsonlPath) {
        $all = Get-Content -LiteralPath $Script:JsonlPath | ForEach-Object { $_ | ConvertFrom-Json }
    }
    $byPhase = $all | Group-Object phase | Sort-Object Name

    $md = @()
    $md += "# MediaDash Test Run — $($Script:RunState.RunId)"
    $md += ""
    $md += "- **Started:** $($Script:RunState.StartedAtUtc.ToString('o'))"
    $md += "- **Duration:** $([Math]::Round($elapsed / 60, 1)) minutes"
    $md += "- **Jellyfin:** $($Script:RunState.JellyfinUrl)"
    $md += "- **Plugin version:** $($Script:RunState.PluginVersion)"
    $md += "- **Jellyfin version:** $($Script:RunState.JellyfinVersion)"
    $md += ""
    $md += "## Totals"
    $md += ""
    $md += "| Result | Count |"
    $md += "|--------|-------|"
    $md += "| PASS   | $($Script:RunState.TestsPassed) |"
    $md += "| FAIL   | $($Script:RunState.TestsFailed) |"
    $md += "| ERROR  | $($Script:RunState.TestsErrored) |"
    $md += "| SKIP   | $($Script:RunState.TestsSkipped) |"
    $md += "| Total  | $($Script:RunState.TotalTests) |"
    $md += ""

    $md += "## Per-phase"
    $md += ""
    $md += "| Phase | Tests | Pass | Fail | Error | Skip | Duration |"
    $md += "|-------|-------|------|------|-------|------|----------|"
    foreach ($grp in $byPhase) {
        $pass = @($grp.Group | Where-Object result -eq PASS).Count
        $fail = @($grp.Group | Where-Object result -eq FAIL).Count
        $err  = @($grp.Group | Where-Object result -eq ERROR).Count
        $skip = @($grp.Group | Where-Object result -eq SKIP).Count
        $phaseName = $grp.Name -replace '^\d+\s+', ''
        $dur = if ($Script:RunState.PhaseTimings.ContainsKey($phaseName)) { [Math]::Round($Script:RunState.PhaseTimings[$phaseName],1).ToString() + "s" } else { "-" }
        $md += "| $($grp.Name) | $($grp.Group.Count) | $pass | $fail | $err | $skip | $dur |"
    }
    $md += ""

    # Failures + errors detailed
    $failed = $all | Where-Object { $_.result -in @("FAIL","ERROR") }
    if ($failed) {
        $md += "## Failures ($($failed.Count))"
        $md += ""
        foreach ($f in $failed) {
            $md += "### [$($f.result)] $($f.test)"
            $md += ""
            $md += "- Phase: ``$($f.phase)``"
            $md += "- Duration: $($f.durationMs) ms"
            if ($f.expected) { $md += "- Expected: ``$($f.expected)``" }
            if ($f.actual)   { $md += "- Actual:   ``$($f.actual)``" }
            if ($f.notes)    {
                $md += "- Notes:"
                $md += '```'
                $md += $f.notes
                $md += '```'
            }
            if ($f.artifacts.Count -gt 0) {
                $md += "- Artifacts: $($f.artifacts -join ', ')"
            }
            $md += ""
        }
    }

    $md += "## Environment"
    $md += ""
    $md += '```'
    $md += "RunId:       $($Script:RunState.RunId)"
    $md += "OS:          $([Environment]::OSVersion)"
    $md += "PS version:  $($PSVersionTable.PSVersion)"
    $md += "Jellyfin:    $($Script:RunState.JellyfinUrl)"
    $md += "Plugin:      $($Script:RunState.PluginVersion)"
    $md += '```'

    $md -join "`n" | Set-Content -Path $Script:SummaryPath -Encoding utf8
    Write-Log "Summary written: $Script:SummaryPath"
}

# ═════════════════════════════════════════════════════════════════════════════
# Convenience wrappers used by every phase
# ═════════════════════════════════════════════════════════════════════════════

function Start-Phase { param([string]$Description) Write-Log "◆ $Description" }
