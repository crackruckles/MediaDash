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
#   - PowerShell 7 or higher
#
# Usage:
#   .\run-full-tests.ps1 -ConfirmLive -DataDir <isolated-data-dir> # full run
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

#requires -Version 7.0
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
    [switch]$ConfirmLive,
    [string]$DataDir,
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
if ($RunId -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*$') { throw 'RunId must be a single directory name.' }
$Script:PluginRoot = Split-Path -Parent (Split-Path -Parent $Script:TestRoot)
$Script:ResultsDir = Join-Path $Script:TestRoot "test-results\$RunId"
$Script:LogPath    = Join-Path $Script:ResultsDir "run.log"
$Script:JsonlPath  = Join-Path $Script:ResultsDir "results.jsonl"
$Script:StatusPath = Join-Path $Script:ResultsDir "status.json"
$Script:FailuresDir = Join-Path $Script:ResultsDir "failures"
$Script:ArtifactsDir = Join-Path $Script:ResultsDir "artifacts"
$Script:SummaryPath = Join-Path $Script:ResultsDir "summary.md"

if (Test-Path -LiteralPath $Script:ResultsDir) { throw 'RunId already exists; use a new run ID to prevent stale evidence.' }
New-Item -ItemType Directory -Path $Script:ResultsDir | Out-Null
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
    OriginalBinPaths = $null
    OriginalTasks = @()
    DataDir = $DataDir

    # Timings
    PhaseTimings = @{}

    # Truth: phase-level unhandled exceptions and per-phase test counts. Consumed
    # by Get-RunVerdict so a phase that exploded before recording any test cannot
    # be silently interpreted as "PASS: 0 tests".
    PhaseExceptions = @()
    PhaseCounters = @{}
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
# Phase selection — validate that every user-supplied ID/name resolves to a
# real phase before we start. Silent typos have burned us: "-OnlyPhases 07"
# would resolve to zero phases and report "PASS: 0 tests" without ever hitting
# the UI phase the operator wanted.
# ─────────────────────────────────────────────────────────────────────────────
$AllPhaseKeys = @()
foreach ($p in $AllPhases) { $AllPhaseKeys += $p.Id; $AllPhaseKeys += $p.Name }
foreach ($k in ($OnlyPhases + $SkipPhases)) {
    if ($k -and $AllPhaseKeys -notcontains $k) {
        Write-Host "ERROR: unknown phase '$k'. Known: $($AllPhases.Id -join ',') or $($AllPhases.Name -join ',')" -ForegroundColor Red
        exit 3
    }
}

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
# Preflight is required unless the caller explicitly opted out via SkipPhases.
# If the caller narrowed to OnlyPhases and preflight is missing, that's an error
# rather than a silent skip: a live phase without preflight has no auth token.
if ($OnlyPhases.Count -gt 0 -and $PhasesToRun.Id -notcontains "00" -and "00" -notin $SkipPhases -and "preflight" -notin $SkipPhases) {
    Write-Host "ERROR: preflight (phase 00) is required when live phases are selected. Add it to -OnlyPhases or use -SkipPhases 00 to opt out explicitly." -ForegroundColor Red
    exit 3
}

# ─────────────────────────────────────────────────────────────────────────────
# Banner + main loop
# ─────────────────────────────────────────────────────────────────────────────
if (-not $ConfirmLive -or -not $DataDir) { throw 'Live runs require -ConfirmLive and -DataDir for a dedicated fixture-only Jellyfin instance.' }
Assert-SafeLiveTarget -Url $JellyfinUrl
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
    $preTotal = $Script:RunState.TotalTests
    $preFailures = $Script:RunState.TestsFailed + $Script:RunState.TestsErrored
    # Cleanup must continue to the next independent step even with HaltOnFailure.
    if ($phase.Id -eq '99') { $Script:RunState.HaltOnFailure = $false }
    Write-Log ""
    Write-Log "─── PHASE $($phase.Id) · $($phase.Name.ToUpper()) ─────────────────────────"
    Update-Status
    try {
        & $phase.Func
    } catch {
        Write-Log "PHASE ABORT: $($phase.Name) — $($_.Exception.Message)"
        Write-Log $_.ScriptStackTrace
        # Truth: an uncaught phase throw is recorded as an ERROR test result AND
        # captured on RunState.PhaseExceptions. Previously the exit-decision only
        # inspected TestsFailed/TestsErrored, so a phase that threw before any
        # test ran silently passed. Fixed by Record-PhaseException + Get-RunVerdict.
        Record-PhaseException -Phase "$($phase.Id) $($phase.Name)" -ErrorRecord $_
        if ($HaltOnFailure -or $phase.Id -eq "00" -or $phase.Id -eq "01") {
            $aborted = $true
            Write-Log "Halting further phases (except teardown)."
        }
    }
    $Script:RunState.PhaseCounters[$phase.Name] = $Script:RunState.TotalTests - $preTotal
    if ($phase.Id -in @('00','01') -and ($Script:RunState.TestsFailed + $Script:RunState.TestsErrored) -gt $preFailures) {
        $aborted = $true
        Write-Log 'Preflight/setup failed; halting dependent phases.'
    }
    $Script:RunState.PhaseTimings[$phase.Name] = ([DateTime]::UtcNow - $phaseStart).TotalSeconds
    Update-Status
}

# ─────────────────────────────────────────────────────────────────────────────
# Summary
# ─────────────────────────────────────────────────────────────────────────────
Write-Summary
Update-Status -Final

$verdict = Get-RunVerdict -RunState $Script:RunState -RequiredPhasesRun @($PhasesToRun.Name)

Write-Log ""
Write-Log "═══════════════════════════════════════════════════════════════════════"
Write-Log "  Run complete."
Write-Log "  PASS: $($Script:RunState.TestsPassed)"
Write-Log "  FAIL: $($Script:RunState.TestsFailed)"
Write-Log "  ERROR: $($Script:RunState.TestsErrored)"
Write-Log "  SKIP: $($Script:RunState.TestsSkipped)"
Write-Log "  Verdict:  $($verdict.Verdict) — $($verdict.Reason)"
Write-Log "  Summary:  $Script:SummaryPath"
Write-Log "  Failures: $Script:FailuresDir"
Write-Log "═══════════════════════════════════════════════════════════════════════"

exit $verdict.ExitCode
