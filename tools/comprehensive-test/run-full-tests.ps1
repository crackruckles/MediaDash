# run-full-tests.ps1
# ─────────────────────────────────────────────────────────────────────────────
# MediaDash full-plugin test harness.
#
# Single entry point. Dot-sources framework + helpers + phase modules.
# Every phase, every test, per-test result written to results.jsonl.
# Failures snapshot the plugin DB, config, diagnostics, and Jellyfin log tail
# into per-test artifact folders so you can post-mortem without re-running.
#
# Prereqs (checked in phase 00 — script exits with clear error if any missing):
#   - Jellyfin 10.11.11 running at http://localhost:8099 with test/test admin
#   - MediaDash 1.0.7.5+ plugin loaded (fresh publish before running)
#   - C:\dev\mediadash-fixtures\* present (base fixtures from make-fixtures.sh)
#   - Jellyfin's bundled ffmpeg available (jellyfin\ffmpeg.exe)
#   - PowerShell 5.1 or higher
#
# Usage:
#   .\run-full-tests.ps1                          # full run
#   .\run-full-tests.ps1 -SkipPhases 07,08        # skip UI + edge case phases
#   .\run-full-tests.ps1 -OnlyPhases 02,03        # only scanners + fixers
#   .\run-full-tests.ps1 -RunId 2026-09-04-a      # named run (default: timestamp)
#   .\run-full-tests.ps1 -Quick                   # skip long-running edge cases
#   .\run-full-tests.ps1 -HaltOnFailure           # stop at first FAIL (default: continue)
#
# Monitoring while running:
#   Get-Content .\test-results\<runid>\run.log -Wait -Tail 20
#   Get-Content .\test-results\<runid>\status.json | ConvertFrom-Json
#
# Post-run:
#   test-results\<runid>\summary.md         — human-readable summary
#   test-results\<runid>\results.jsonl      — one JSON per test (grep/jq-able)
#   test-results\<runid>\failures\<test>\   — DB/config/log snapshot per failure

[CmdletBinding()]
param(
    [string]$JellyfinUrl = "http://localhost:8099",
    [string]$AdminUser   = "test",
    [string]$AdminPass   = "test",
    [string]$FixturesRoot = "C:\dev\mediadash-fixtures",
    [string]$RunId = (Get-Date -Format "yyyy-MM-dd-HHmmss"),
    [string[]]$SkipPhases = @(),
    [string[]]$OnlyPhases = @(),
    [switch]$Quick,
    [switch]$HaltOnFailure,
    [int]$DefaultTimeoutSec = 600
)

# Fail hard on any unhandled error — but individual tests catch their own so a
# single crash doesn't kill the whole run.
$ErrorActionPreference = "Stop"
# Version 1.0 only checks uninitialized variables — Version 2.0's non-existent
# property check kept cascading through helpers that legitimately return null
# or PSCustomObject with dynamic fields. 1.0 is sufficient for typo detection.
Set-StrictMode -Version 1.0

# ─────────────────────────────────────────────────────────────────────────────
# Paths + run directory
# ─────────────────────────────────────────────────────────────────────────────
$Script:TestRoot   = Split-Path -Parent $PSCommandPath
$Script:PluginRoot = Split-Path -Parent (Split-Path -Parent $Script:TestRoot)
$Script:ResultsDir = Join-Path $Script:TestRoot "test-results\$RunId"
$Script:LogPath    = Join-Path $Script:ResultsDir "run.log"
$Script:JsonlPath  = Join-Path $Script:ResultsDir "results.jsonl"
$Script:StatusPath = Join-Path $Script:ResultsDir "status.json"
$Script:FailuresDir = Join-Path $Script:ResultsDir "failures"
$Script:ArtifactsDir = Join-Path $Script:ResultsDir "artifacts"
$Script:SummaryPath = Join-Path $Script:ResultsDir "summary.md"

New-Item -ItemType Directory -Force -Path $Script:ResultsDir | Out-Null
New-Item -ItemType Directory -Force -Path $Script:FailuresDir | Out-Null
New-Item -ItemType Directory -Force -Path $Script:ArtifactsDir | Out-Null

# ─────────────────────────────────────────────────────────────────────────────
# Global run state (used by framework.ps1 helpers)
# ─────────────────────────────────────────────────────────────────────────────
$Script:RunState = [PSCustomObject]@{
    RunId = $RunId
    JellyfinUrl = $JellyfinUrl
    AdminUser = $AdminUser
    AdminPass = $AdminPass
    FixturesRoot = $FixturesRoot
    Quick = [bool]$Quick
    HaltOnFailure = [bool]$HaltOnFailure
    DefaultTimeoutSec = $DefaultTimeoutSec
    StartedAtUtc = [DateTime]::UtcNow

    # Populated by preflight
    JellyfinToken = $null
    JellyfinUserId = $null
    JellyfinAuthHeader = $null
    PluginId = "38bdb090-b763-4294-934b-b54ade4d9d6d"
    PluginVersion = $null
    JellyfinVersion = $null

    # Populated by test lifecycle
    CurrentPhase = $null
    CurrentTest = $null
    PhasesRun = @()
    TestsPassed = 0
    TestsFailed = 0
    TestsSkipped = 0
    TestsErrored = 0
    TotalTests = 0

    # Test-specific state that phases share (e.g. added library ids for teardown)
    AddedLibraryIds = @()
    CreatedFixturePaths = @()
    OriginalDryRun = $null
    OriginalConfigSnapshot = $null

    # Timings
    PhaseTimings = @{}
}

# ─────────────────────────────────────────────────────────────────────────────
# Dot-source lib + phases
# ─────────────────────────────────────────────────────────────────────────────
. (Join-Path $Script:TestRoot "lib\framework.ps1")
. (Join-Path $Script:TestRoot "lib\helpers.ps1")
. (Join-Path $Script:TestRoot "lib\fixtures.ps1")

$AllPhases = @(
    @{ Id = "00"; Name = "preflight";      File = "phases\00-preflight.ps1";      Func = "Invoke-Phase-Preflight" },
    @{ Id = "01"; Name = "setup";          File = "phases\01-setup.ps1";          Func = "Invoke-Phase-Setup" },
    @{ Id = "02"; Name = "scanners";       File = "phases\02-scanners.ps1";       Func = "Invoke-Phase-Scanners" },
    @{ Id = "03"; Name = "fixers";         File = "phases\03-fixers.ps1";         Func = "Invoke-Phase-Fixers" },
    @{ Id = "04"; Name = "combined-pass";  File = "phases\04-combined-pass.ps1";  Func = "Invoke-Phase-CombinedPass" },
    @{ Id = "05"; Name = "config";         File = "phases\05-config.ps1";         Func = "Invoke-Phase-Config" },
    @{ Id = "06"; Name = "endpoints";      File = "phases\06-endpoints.ps1";      Func = "Invoke-Phase-Endpoints" },
    @{ Id = "07"; Name = "ui";             File = "phases\07-ui.ps1";             Func = "Invoke-Phase-Ui" },
    @{ Id = "08"; Name = "edge-cases";     File = "phases\08-edge-cases.ps1";     Func = "Invoke-Phase-EdgeCases" },
    @{ Id = "09"; Name = "reported-bugs";  File = "phases\09-reported-bugs.ps1";  Func = "Invoke-Phase-ReportedBugs" },
    @{ Id = "99"; Name = "teardown";       File = "phases\99-teardown.ps1";       Func = "Invoke-Phase-Teardown" }
)

foreach ($p in $AllPhases) {
    . (Join-Path $Script:TestRoot $p.File)
}

# ─────────────────────────────────────────────────────────────────────────────
# Phase selection
# ─────────────────────────────────────────────────────────────────────────────
$PhasesToRun = $AllPhases
if ($OnlyPhases.Count -gt 0) {
    $PhasesToRun = $AllPhases | Where-Object { $_.Id -in $OnlyPhases -or $_.Name -in $OnlyPhases }
    Write-Log "Filter: OnlyPhases -> $($PhasesToRun.Name -join ', ')"
}
if ($SkipPhases.Count -gt 0) {
    $PhasesToRun = $PhasesToRun | Where-Object { $_.Id -notin $SkipPhases -and $_.Name -notin $SkipPhases }
    Write-Log "Filter: SkipPhases -> $($PhasesToRun.Name -join ', ')"
}
# Teardown always runs last unless explicitly skipped
if ($PhasesToRun.Id -notcontains "99" -and "99" -notin $SkipPhases -and "teardown" -notin $SkipPhases) {
    $PhasesToRun += ($AllPhases | Where-Object { $_.Id -eq "99" })
}

# ─────────────────────────────────────────────────────────────────────────────
# Banner + main loop
# ─────────────────────────────────────────────────────────────────────────────
Write-Log ""
Write-Log "═══════════════════════════════════════════════════════════════════════"
Write-Log "  MediaDash comprehensive test — Run $RunId"
Write-Log "  Jellyfin: $JellyfinUrl   Fixtures: $FixturesRoot"
Write-Log "  Results:  $Script:ResultsDir"
Write-Log "  Phases:   $($PhasesToRun.Name -join ' > ')"
Write-Log "  Quick=$Quick  HaltOnFailure=$HaltOnFailure  Timeout=${DefaultTimeoutSec}s"
Write-Log "═══════════════════════════════════════════════════════════════════════"
Write-Log ""

Update-Status

$aborted = $false
foreach ($phase in $PhasesToRun) {
    if ($aborted -and $phase.Id -ne "99") {
        Write-Log "SKIP phase $($phase.Id) $($phase.Name) — run was aborted"
        continue
    }
    $Script:RunState.CurrentPhase = "$($phase.Id) $($phase.Name)"
    $Script:RunState.PhasesRun += $phase.Name
    $phaseStart = [DateTime]::UtcNow
    Write-Log ""
    Write-Log "─── PHASE $($phase.Id) · $($phase.Name.ToUpper()) ─────────────────────────"
    Update-Status
    try {
        & $phase.Func
    } catch {
        Write-Log "PHASE ABORT: $($phase.Name) — $($_.Exception.Message)"
        Write-Log $_.ScriptStackTrace
        if ($HaltOnFailure -or $phase.Id -eq "00" -or $phase.Id -eq "01") {
            $aborted = $true
            Write-Log "Halting further phases (except teardown)."
        }
    }
    $Script:RunState.PhaseTimings[$phase.Name] = ([DateTime]::UtcNow - $phaseStart).TotalSeconds
    Update-Status
}

# ─────────────────────────────────────────────────────────────────────────────
# Summary
# ─────────────────────────────────────────────────────────────────────────────
Write-Summary
Update-Status -Final

Write-Log ""
Write-Log "═══════════════════════════════════════════════════════════════════════"
Write-Log "  Run complete."
Write-Log "  PASS: $($Script:RunState.TestsPassed)"
Write-Log "  FAIL: $($Script:RunState.TestsFailed)"
Write-Log "  ERROR: $($Script:RunState.TestsErrored)"
Write-Log "  SKIP: $($Script:RunState.TestsSkipped)"
Write-Log "  Summary:  $Script:SummaryPath"
Write-Log "  Failures: $Script:FailuresDir"
Write-Log "═══════════════════════════════════════════════════════════════════════"

if ($Script:RunState.TestsFailed -gt 0 -or $Script:RunState.TestsErrored -gt 0) {
    exit 1
}
exit 0
