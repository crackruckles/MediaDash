# self-tests.ps1 - isolated harness truth-tests.
#
# These load the framework module directly and exercise its real accounting +
# exit-decision function (Get-RunVerdict) with stubbed external dependencies
# (browser, network, file operations). Never stub the assertion itself.
#
# Runs as a child process; nonzero exit = at least one self-test failed. The
# release run must invoke this before claiming the harness is trustworthy.
#
# Table (matches plan section 00-B):
#   1. Browser exit 1                      -> UI tests FAIL
#   2. Browser empty DOM                   -> assertion FAIL
#   3. Phase throws before first test      -> verdict ERROR
#   4. Unknown phase id                    -> run refused (exit 3)
#   5. Required phase all skipped          -> verdict FAIL (with reason)
#   6. Teardown throws after failing case  -> both failures retained, exit 1
#   7. Target is nonloopback / wrong port  -> refused before mutation
#   8. Clean successful stub run           -> verdict PASS

[CmdletBinding()] param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 1.0

$TestRoot = Split-Path -Parent $PSCommandPath
$PluginRoot = Split-Path -Parent (Split-Path -Parent $TestRoot)

# Which PowerShell host is available for child-process invocations? On Windows
# with only Windows PowerShell 5.1, "powershell" exists. On Linux/macOS and on
# modern Windows CI runners, "pwsh" (PS 7) exists. Prefer pwsh when both are
# present; the self-tests do not depend on any 5.1-only behavior.
$Script:PsChild = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }

# Minimal shared state so framework.ps1 helpers have somewhere to write.
# We DO NOT stub Get-RunVerdict, Record-PhaseException, Assert-SafeLiveTarget,
# Invoke-Test or Write-TestResult - those are the code under test.
function New-StubRunState {
    return [pscustomobject]@{
        RunId = 'self-test'
        StartedAtUtc = [DateTime]::UtcNow
        CurrentPhase = $null
        CurrentTest = $null
        PhasesRun = @()
        TestsPassed = 0
        TestsFailed = 0
        TestsSkipped = 0
        TestsErrored = 0
        TotalTests = 0
        PhaseTimings = @{}
        HaltOnFailure = $false
        JellyfinAuthHeader = $null
        JellyfinUrl = 'http://localhost:8099'
        PhaseExceptions = @()
        PhaseCounters = @{}
    }
}

function Initialize-SelfTestScope {
    $Script:RunState = New-StubRunState
    $Script:LogPath      = [IO.Path]::GetTempFileName()
    $Script:JsonlPath    = [IO.Path]::GetTempFileName()
    $Script:StatusPath   = [IO.Path]::GetTempFileName()
    $Script:FailuresDir  = [IO.Path]::Combine([IO.Path]::GetTempPath(), "self-test-fail-" + [Guid]::NewGuid())
    New-Item -ItemType Directory -Force -Path $Script:FailuresDir | Out-Null
    $Script:SummaryPath  = [IO.Path]::GetTempFileName()
}

. (Join-Path $TestRoot 'lib\framework.ps1')

$Script:SelfTestResults = @()
function Assert-Condition {
    param([string]$Name, [bool]$Condition, [string]$Detail = '')
    $Script:SelfTestResults += [pscustomobject]@{ Name = $Name; Passed = $Condition; Detail = $Detail }
    if ($Condition) {
        Write-Host "  [PASS] $Name"
    } else {
        Write-Host "  [FAIL] $Name -- $Detail"
    }
}

Write-Host "=== MediaDash harness self-tests ==="

# 1. Browser exit 1 (with success-looking output) -> the wrapper must throw.
Initialize-SelfTestScope
. (Join-Path $TestRoot 'phases\07-ui.ps1')

# Substitute Invoke-Browse for this test scope so no real browser is launched.
function Invoke-Browse {
    param([string]$Bin, [string[]]$BrowseArgs)
    return @{ Output = 'browser failed'; ExitCode = 1 }
}

$threw = $false; $msg = ''
try {
    Invoke-BrowseChecked -Bin 'stub' -BrowseArgs @('goto','http://x') | Out-Null
} catch [TestAssertionFailedException] {
    $threw = $true; $msg = $_.Exception.Message
} catch {
    $threw = $true
    $etype = $_.Exception.GetType().Name
    $emsg  = $_.Exception.Message
    $msg = "wrong exception type: $etype -- $emsg"
}
Assert-Condition '1a. Browser exit 1 -> Invoke-BrowseChecked throws' $threw $msg

# 2. Browser exit 0 but empty output -> -AllowEmpty:$false must fail.
function Invoke-Browse {
    param([string]$Bin, [string[]]$BrowseArgs)
    return @{ Output = ''; ExitCode = 0 }
}

$threw = $false
try {
    Invoke-BrowseChecked -Bin 'stub' -BrowseArgs @('js','x') | Out-Null
} catch [TestAssertionFailedException] {
    $threw = $true
}
Assert-Condition '2a. Empty browser output (default) -> assertion FAIL' $threw ''

$didNotThrow = $true
try {
    Invoke-BrowseChecked -Bin 'stub' -BrowseArgs @('goto','x') -AllowEmpty | Out-Null
} catch {
    $didNotThrow = $false
}
Assert-Condition '2b. Empty browser output with -AllowEmpty -> returns' $didNotThrow ''
function Invoke-Browse { return @{Output='NO';ExitCode=0} }
$refused = $false
try { Assert-BrowseVisible -Bin 'stub' -Selector '#hidden' } catch [TestAssertionFailedException] { $refused=$true }
Assert-Condition '2c. Hidden DOM cannot satisfy a visible UI assertion' $refused

# 3. Phase throws before any test recorded -> verdict ERROR.
Initialize-SelfTestScope
Record-PhaseException -Phase '02 scanners' -ErrorRecord (New-Object System.Management.Automation.ErrorRecord (
    (New-Object System.InvalidOperationException 'scanner crash'), 'stub', 'NotSpecified', $null))
$verdict = Get-RunVerdict -RunState $Script:RunState
Assert-Condition '3a. Phase throws -> verdict ERROR' ($verdict.Verdict -eq 'ERROR') "got $($verdict.Verdict) -- $($verdict.Reason)"
Assert-Condition '3b. Phase throws -> exit code 2' ($verdict.ExitCode -eq 2) "got $($verdict.ExitCode)"

# 4. Unknown phase id -> runner refuses (exit 3).
$runner = Join-Path $TestRoot 'run-full-tests.ps1'
$childOut = & $Script:PsChild -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $runner -RunId ('self-' + [guid]::NewGuid().ToString('N')) -OnlyPhases 'nope-does-not-exist' 2>&1 | Out-String
$childExit = $LASTEXITCODE
Assert-Condition '4a. Unknown phase id -> exit 3' ($childExit -eq 3) "child exit was $childExit"
Assert-Condition '4b. Unknown phase id -> clear message' ($childOut -match "unknown phase 'nope-does-not-exist'") "output: $($childOut.Substring(0,[Math]::Min(200,$childOut.Length)))"

# 5. Required phase produced no results -> verdict FAIL.
Initialize-SelfTestScope
$Script:RunState.PhasesRun = @('preflight','ui')
$Script:RunState.TotalTests = 1
$Script:RunState.TestsPassed = 1
$Script:RunState.PhaseCounters['preflight'] = 1
$Script:RunState.PhaseCounters['ui'] = 0
$verdict = Get-RunVerdict -RunState $Script:RunState -RequiredPhasesRun @('preflight','ui')
Assert-Condition '5a. Required phase with 0 results -> FAIL' ($verdict.Verdict -eq 'FAIL') "got $($verdict.Verdict) -- $($verdict.Reason)"
Assert-Condition '5b. Required phase FAIL reason mentions the phase name' ($verdict.Reason -match 'ui') $verdict.Reason

# 6. Teardown throws after a failing case -> both failures retained, exit 1.
Initialize-SelfTestScope
$Script:RunState.CurrentPhase = '02 scanners'
Write-TestResult -Phase '02 scanners' -Name 'stub-fail' -Result 'FAIL' -DurationMs 1 -Notes 'stub'
Record-PhaseException -Phase '99 teardown' -ErrorRecord (New-Object System.Management.Automation.ErrorRecord (
    (New-Object System.IO.IOException 'teardown crash'), 'stub', 'NotSpecified', $null))
Assert-Condition '6a. Teardown exception did not erase prior FAIL' ($Script:RunState.TestsFailed -ge 1) "TestsFailed=$($Script:RunState.TestsFailed)"
Assert-Condition '6b. Teardown exception recorded as ERROR' ($Script:RunState.TestsErrored -ge 1) "TestsErrored=$($Script:RunState.TestsErrored)"
$verdict = Get-RunVerdict -RunState $Script:RunState
Assert-Condition '6c. Prior FAIL + teardown ERROR -> verdict ERROR (higher severity wins)' ($verdict.Verdict -eq 'ERROR') "got $($verdict.Verdict) -- $($verdict.Reason)"

# 7. Target-safety guard refuses non-loopback / wrong port / escape.
$refused = $false
try { Assert-SafeLiveTarget -Url 'http://192.168.1.117:8096' } catch { $refused = $true }
Assert-Condition '7a. Non-loopback target refused' $refused ''

$refused = $false
try { Assert-SafeLiveTarget -Url 'http://localhost:8096' } catch { $refused = $true }
Assert-Condition '7b. Loopback but wrong port refused (8096 is prod)' $refused ''

$refused = $false
try { Assert-SafeLiveTarget -Url 'http://localhost:8099' -AllowedFixturePaths @('C:\dev\mediadash-fixtures') -FixturePath 'C:\Windows\notepad.exe' } catch { $refused = $true }
Assert-Condition '7c. Loopback OK but fixture outside allowlist refused' $refused ''

$didNotThrow = $true
try { Assert-SafeLiveTarget -Url 'http://localhost:8099' -AllowedFixturePaths @([IO.Path]::GetTempPath()) -FixturePath ([IO.Path]::Combine([IO.Path]::GetTempPath(),'x.mkv')) } catch { $didNotThrow = $false }
Assert-Condition '7d. Loopback + fixture inside allowlist accepted' $didNotThrow ''

# 8. Clean stub run -> verdict PASS.
Initialize-SelfTestScope
$Script:RunState.CurrentPhase = '02 scanners'
Write-TestResult -Phase '02 scanners' -Name 'stub-pass' -Result 'PASS' -DurationMs 1
$verdict = Get-RunVerdict -RunState $Script:RunState
Assert-Condition '8a. All PASS -> verdict PASS' ($verdict.Verdict -eq 'PASS') "got $($verdict.Verdict) -- $($verdict.Reason)"
Assert-Condition '8b. All PASS -> exit 0' ($verdict.ExitCode -eq 0) "got $($verdict.ExitCode)"

# Missing coverage must never be a green result, even alongside passing tests.
Initialize-SelfTestScope
Write-TestResult -Phase '00 preflight' -Name 'ready' -Result 'PASS' -DurationMs 0
Write-TestResult -Phase '07 ui' -Name 'browser absent' -Result 'SKIP' -DurationMs 0
$verdict = Get-RunVerdict -RunState $Script:RunState
Assert-Condition '9a. Skipped coverage produces INCOMPLETE, not PASS' ($verdict.Verdict -eq 'INCOMPLETE' -and $verdict.ExitCode -ne 0) $verdict.Reason

foreach ($url in @('ftp://localhost:8099','http://user:secret@localhost:8099','http://localhost:8099?token=x','http://localhost:8099/#fragment')) {
    $refused = $false
    try { Assert-SafeLiveTarget -Url $url } catch { $refused = $true }
    Assert-Condition "9b. Unsafe URL shape refused: $url" $refused
}
$allowed = Join-Path ([IO.Path]::GetTempPath()) 'fixture-root'
$refused = $false
try { Assert-SafeLiveTarget -Url 'http://localhost:8099' -AllowedFixturePaths @($allowed) -FixturePath ($allowed + '-foreign/file.mkv') } catch { $refused = $true }
Assert-Condition '9c. Sibling with same prefix is outside fixture root' $refused

# Exercise real teardown with stubbed I/O. A cleanup failure must be recorded,
# and libraries not created by this run must never be removed.
Initialize-SelfTestScope
$Script:RunState | Add-Member AddedLibraryIds @('MediaDash TV Scratch')
$Script:RunState | Add-Member OriginalConfigSnapshot $null
$Script:RunState | Add-Member OriginalBinPaths @('old-bin')
$Script:RunState | Add-Member FixturesRoot ([IO.Path]::GetTempPath())
$Script:RunState.JellyfinAuthHeader = 'stub'
. (Join-Path $TestRoot 'phases/99-teardown.ps1')
function Restore-MediaDashConfigSnapshot { throw 'simulated restore failure' }
function Save-FailureArtifacts { return @() }
function Invoke-JfApi { return @([pscustomobject]@{Key='MediaDashFix'; State='Idle'}, [pscustomobject]@{Key='MediaDashScan'; State='Idle'}, [pscustomobject]@{Key='RefreshLibrary'; State='Idle'}) }
function Get-MediaDashRecycleBinItems { return @() }
$Script:RemovedLibraries = @()
function Remove-JellyfinLibrary { param($Name) $Script:RemovedLibraries += $Name }
function Remove-ScratchFixtures { }
function Test-FixtureIntegrity { return @() }
function Get-MediaDashIssues { return @() }
Invoke-Phase-Teardown
Assert-Condition '10a. Config restore failure is retained as ERROR' ($Script:RunState.TestsErrored -gt 0)
Assert-Condition '10b. Teardown removes the owned TV library only' (($Script:RemovedLibraries -join ',') -eq 'MediaDash TV Scratch') ($Script:RemovedLibraries -join ',')

$Script:DismissedIds = @()
$Script:CleanupIssues = @(
    [pscustomobject]@{Id=7;Path=(Join-Path ([IO.Path]::GetTempPath()) 'owned.mkv')},
    [pscustomobject]@{Id=8;Path=([IO.Path]::Combine([IO.Path]::GetPathRoot([IO.Path]::GetTempPath()), 'foreign.mkv'))},
    [pscustomobject]@{Id=9;Path=''}
)
function Get-MediaDashIssues { return @($Script:CleanupIssues | Where-Object Id -NotIn $Script:DismissedIds) }
function Dismiss-MediaDashIssue { param($IssueId) $Script:DismissedIds += $IssueId }
Invoke-Phase-Teardown
Assert-Condition '10d. Cleanup dismisses owned approvals and preserves foreign or unscoped rows' (($Script:DismissedIds -join ',') -eq '7') ($Script:DismissedIds -join ',')

# A running task must retain isolation instead of removing its inputs/settings.
$Script:RemovedLibraries = @()
function Invoke-JfApi { return @([pscustomobject]@{Key='MediaDashFix';State='Running'}) }
$refused = $false
try { Invoke-Phase-Teardown } catch { $refused = $true }
Assert-Condition '10c. Busy teardown refuses cleanup and retains isolation' ($refused -and $Script:RemovedLibraries.Count -eq 0)

. (Join-Path $TestRoot 'lib/helpers.ps1')
$Script:RunState | Add-Member LastFixHistoryId 10 -Force
function Get-MediaDashHistory { return [pscustomobject]@{Items=@([pscustomobject]@{Id=9;FileName='movie.mkv';Type='Playability';Success=$true})} }
$refused = $false
try { Get-CurrentFixHistory -FileName 'movie.mkv' -Type 'Playability' | Out-Null } catch [TestAssertionFailedException] { $refused = $true }
Assert-Condition '11a. Old successful history cannot satisfy a current fix' $refused
function Get-MediaDashHistory { return [pscustomobject]@{Items=@([pscustomobject]@{Id=11;FileName='movie.mkv';Type='Playability';Success=$false})} }
$refused = $false
try { Get-CurrentFixHistory -FileName 'movie.mkv' -Type 'Playability' | Out-Null } catch [TestAssertionFailedException] { $refused = $true }
Assert-Condition '11b. Current failed fix cannot pass by having history' $refused
function Get-MediaDashHistory { return [pscustomobject]@{Items=@([pscustomobject]@{Id=11;FileName='movie.mkv';Type='Playability';Success=$true})} }
$row = Get-CurrentFixHistory -FileName 'movie.mkv' -Type 'Playability'
Assert-Condition '11c. Current matching successful history is accepted' ($row.Id -eq 11)

. (Join-Path $TestRoot 'lib/fixtures.ps1')
$fixtureScope = Join-Path ([IO.Path]::GetTempPath()) ('fixture-integrity-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureScope | Out-Null
try {
    $refused = $false
    try { Test-FixtureIntegrity -FixturesRoot (Join-Path $fixtureScope 'absent') | Out-Null } catch { $refused = $true }
    Assert-Condition '12a. Missing backup manifest cannot pass integrity' $refused
    $backupScope = Join-Path $fixtureScope 'backup'
    New-Item -ItemType Directory -Path $backupScope | Out-Null
    '{"files":[]}' | Set-Content -LiteralPath (Join-Path $backupScope 'manifest.json')
    $refused = $false
    try { Test-FixtureIntegrity -FixturesRoot (Join-Path $fixtureScope 'source') -BackupRoot $backupScope | Out-Null } catch { $refused = $true }
    Assert-Condition '12b. Empty backup manifest cannot pass integrity' $refused
    'changed backup' | Set-Content -LiteralPath (Join-Path $backupScope 'movie.mkv')
    @{files=@(@{path='movie.mkv';sha256=('0' * 64)})} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $backupScope 'manifest.json')
    $refused = $false
    try { Test-FixtureIntegrity -FixturesRoot (Join-Path $fixtureScope 'source') -BackupRoot $backupScope | Out-Null } catch { $refused = $true }
    Assert-Condition '12c. Damaged backup cannot be used as a restoration baseline' $refused
} finally {
    if ((Resolve-Path -LiteralPath $fixtureScope).Path -ne [IO.Path]::GetFullPath($fixtureScope)) { throw 'Unexpected self-test temporary root.' }
    Remove-Item -LiteralPath $fixtureScope -Recurse -Force
}

foreach ($entryPoint in @('tools/live-recycle-tests.ps1','tools/repair-test/torture-test.ps1','tools/seed-subtitle-test.ps1')) {
    $output = & $Script:PsChild -NoProfile -NonInteractive -File (Join-Path $PluginRoot $entryPoint) 2>&1 | Out-String
    Assert-Condition "13. $entryPoint refuses without live opt-in" ($LASTEXITCODE -ne 0 -and $output -match 'ConfirmLive') $output
}

# Invoke-RestMethod emits a JSON array as one pipeline object. The API wrapper
# must enumerate it before consumers filter tasks/issues by properties.
function Invoke-RestMethod {
    return ,@(
        [pscustomobject]@{Key='MediaDashFix';Id=1;Type='Playability'},
        [pscustomobject]@{Key='MediaDashScan';Id=2;Type='Quality'},
        [pscustomobject]@{Key='RefreshLibrary';Id=3;Type='Quality'}
    )
}
$workers = @(Invoke-JfApi -Method GET -Path '/ScheduledTasks' | Where-Object Key -In @('MediaDashFix','MediaDashScan','RefreshLibrary'))
Assert-Condition '14a. REST task arrays can be filtered directly' ($workers.Count -eq 3) "count=$($workers.Count)"
$filtered = @(Get-MediaDashIssues | Where-Object Id -EQ 2)
Assert-Condition '14b. Issue helpers stream individual issue rows' ($filtered.Count -eq 1 -and $filtered[0].Id -eq 2) "count=$($filtered.Count)"
$now = [DateTimeOffset]'2026-10-20T14:00:00Z'
Assert-Condition '15a. Recycle timestamp comparison uses instants across time zones' (Test-RecentUtcTimestamp '2026-10-20T22:00:00+08:00' -Now $now)
Assert-Condition '15b. Stale recycle timestamp is refused' (-not (Test-RecentUtcTimestamp '2026-10-20T13:00:00Z' -Now $now))
Assert-Condition '15c. Future recycle timestamp is refused' (-not (Test-RecentUtcTimestamp '2026-10-20T14:10:00Z' -Now $now))
Assert-Condition '15d. Invalid recycle timestamp is refused' (-not (Test-RecentUtcTimestamp 'not a timestamp' -Now $now))

$Script:RunState = [pscustomobject]@{JellyfinUrl='http://127.0.0.1:8099'; FixturesRoot=[IO.Path]::GetTempPath()}
Assert-Condition '16a. Empty approval paths cannot belong to the run' (-not (Test-OwnedFixturePath ''))
Assert-Condition '16b. Fixture child paths belong to the run' (Test-OwnedFixturePath (Join-Path $Script:RunState.FixturesRoot 'owned-approval.mkv'))
Assert-Condition '16c. Approval outside the fixture root is refused' (-not (Test-OwnedFixturePath ([IO.Path]::Combine([IO.Path]::GetPathRoot($Script:RunState.FixturesRoot), 'foreign-approval.mkv'))))
function Get-MediaDashIssues { return [pscustomobject]@{Path=''} }
$refused = $false
try { Start-MediaDashFix } catch { $refused = $_.Exception.Message -like '*outside this run*' }
Assert-Condition '16d. Global Fix refuses an unowned approval before starting work' $refused

# Report
$totalFail = @($Script:SelfTestResults | Where-Object { -not $_.Passed }).Count
$totalPass = @($Script:SelfTestResults | Where-Object { $_.Passed }).Count
Write-Host ""
Write-Host "Self-test summary: $totalPass passed, $totalFail failed"
if ($totalFail -gt 0) { exit 1 } else { exit 0 }
