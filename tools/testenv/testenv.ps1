<#
.SYNOPSIS
  One MediaDash test run against the disposable test hosts. This is the command agents run.
.DESCRIPTION
  Builds the plugin from THIS working tree, then for each host in turn:
    1. rolls the host back to its 'golden' snapshot (fixtures + baseline already in place)
    2. deploys the build and waits for MediaDash to load
    3. seeds fresh Jellyfin transcode logs (HeavyTranscode / FailedTranscode fixtures age out)
    4. runs a MediaDash scan and compares the issues with artifacts\testenv\manifest.json
    5. unless -ScanOnly: approves issues, runs a Jellyfin library scan, runs the fix task,
       rescans, and records what was fixed, what failed, and what is still detected
    6. saves everything under artifacts\testenv\runs\<timestamp>\<host>\
    7. rolls the host back to 'golden' and stops it (unless -KeepState)
  Hosts run one after another so only one test guest uses RAM at a time.

  Exit codes:
    0  no problems
    1  MediaDash problems (missing/unexpected issues, failed fixes, build errors) - see summary.md
    2  the test ENVIRONMENT is broken (credentials, SSH, Proxmox, snapshots, another run active).
       Report it; do not try to fix MediaDash code for an exit-2 run.
.EXAMPLE
  ./tools/testenv/testenv.ps1                          # full run, all hosts
  ./tools/testenv/testenv.ps1 -Hosts linux -ScanOnly   # fast detection check on one host
  ./tools/testenv/testenv.ps1 -Types Playability,CorruptNfo
  ./tools/testenv/testenv.ps1 -Hosts windows -KeepState  # leave the host up to inspect it
  ./tools/testenv/testenv.ps1 -PreflightOnly           # just check the environment
#>
param(
    [ValidateSet('linux', 'docker', 'windows')]
    [string[]]$Hosts = @('linux', 'docker', 'windows'),
    [switch]$ScanOnly,          # detection only: no approvals, no fixes
    [string[]]$Types,           # approve only these issue types (default: everything approvable)
    [switch]$SkipBuild,         # reuse artifacts\testenv-publish from the last build
    [switch]$KeepState,         # do not roll back / stop the host at the end
    [switch]$PreflightOnly      # check the environment and exit
)
# Checked here rather than with #Requires: under Windows PowerShell 5.1 a #Requires failure still
# exits 0, which an agent would read as success.
if ($PSVersionTable.PSVersion.Major -lt 7) {
    [Console]::Error.WriteLine("testenv: this script needs PowerShell 7. Run it with: pwsh -File $($MyInvocation.MyCommand.Path)")
    exit 2
}
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'testenv-common.ps1')

$runId    = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir   = Join-Path $script:ArtifactDir "runs\$runId"
$started  = Get-Date
$summary  = [System.Collections.Generic.List[object]]::new()
$fatal    = $null          # a run-level failure (preflight, lock, build)
$fatalEnv = $false

function Save-Json($obj, [string]$dir, [string]$name) {
    try { $obj | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 (Join-Path $dir $name) }
    catch { Write-Warning "could not save ${name}: $($_.Exception.Message)" }
}

# Write two Jellyfin-style transcode logs naming fixture files: one clean (HeavyTranscode) and
# one ending in an ffmpeg failure marker (FailedTranscode). Fresh mtimes keep them in the lookback.
function Add-TranscodeLogs([string]$h) {
    $t = $script:Targets[$h]
    $sep = if ($t.Os -eq 'windows') { '\' } else { '/' }
    $heavy  = ($t.JfMediaRoot, 'movies', 'Heavy Transcode (2021)', 'Heavy Transcode (2021).mkv') -join $sep
    $failed = ($t.JfMediaRoot, 'movies', 'Failed Transcode (2021)', 'Failed Transcode (2021).mkv') -join $sep
    $stamp = Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'
    $logs = @(
        @{ Name = "FFmpeg.Transcode-$stamp`_mdtestheavy.log";  Path = $heavy;  Tail = 'video:1024kB audio:128kB subtitle:0kB other streams:0kB global headers:0kB muxing overhead: 1.2%' }
        @{ Name = "FFmpeg.Transcode-$stamp`_mdtestfailed.log"; Path = $failed; Tail = 'Error while decoding stream #0:0: Invalid data found when processing input' + "`n" + 'Conversion failed!' }
    )
    foreach ($l in $logs) {
        $header = (@{ Path = $l.Path; Protocol = 'File'; Container = 'mkv' } | ConvertTo-Json -Compress)
        $body = "$header`n`nffmpeg -i `"$($l.Path)`" -codec:v libx264 -f hls`n$($l.Tail)`n"
        $b64 = [Convert]::ToBase64String([Text.UTF8Encoding]::new($false).GetBytes($body))
        if ($t.Os -eq 'windows') {
            Invoke-Remote $h "[IO.File]::WriteAllBytes('$($t.LogDir)\$($l.Name)', [Convert]::FromBase64String('$b64'))" | Out-Null
        } else {
            $own = if ($h -eq 'linux') { "; chown jellyfin:jellyfin '$($t.LogDir)/$($l.Name)'" } else { '' }
            Invoke-Remote $h "echo $b64 | base64 -d > '$($t.LogDir)/$($l.Name)'$own" | Out-Null
        }
    }
}

# The most recent MediaDash lines from the host's Jellyfin log.
function Get-JfMediaDashLog([string]$h, [int]$lines = 400) {
    $t = $script:Targets[$h]
    if ($t.Os -eq 'windows') {
        Invoke-Remote $h "Get-ChildItem '$($t.LogDir)' -Filter 'log_*.log' | Sort-Object LastWriteTime | Select-Object -Last 1 | Get-Content | Select-String -SimpleMatch 'MediaDash' | Select-Object -Last $lines | ForEach-Object { `$_.Line }" 0
    } else {
        Invoke-Remote $h "ls -t '$($t.LogDir)'/log_*.log 2>/dev/null | head -1 | xargs -r grep -a MediaDash | tail -$lines" 0
    }
}

function Test-OneHost([string]$h) {
    $t = $script:Targets[$h]
    $hostDir = Join-Path $runDir $h
    New-Item -ItemType Directory -Force $hostDir | Out-Null
    $row = [ordered]@{ Host = $h; Scan = $null; Fix = $null; Errors = 0; Problems = [System.Collections.Generic.List[string]]::new()
                       Warnings = [System.Collections.Generic.List[string]]::new(); EnvError = $null; Step = $null }
    $expectedRefusals = @()
    $step = { param($name) $row.Step = $name; Write-Step "${h}: $name" }
    try {
        & $step 'rolling back to golden'
        Restore-Guest $h 'golden'
        Wait-JfReady $h 300
        if ($t.Os -eq 'windows') { Invoke-Optional 'Windows clock resync' { Invoke-Remote $h 'w32tm /resync /force | Out-Null' | Out-Null } $row.Warnings }

        & $step 'deploying the build'
        & (Join-Path $PSScriptRoot 'deploy-plugin.ps1') -Hosts $h -SkipBuild
        Invoke-Optional 'clearing MediaDash diagnostics' { Invoke-Jf $h '/MediaDash/Errors/Clear' 'POST' | Out-Null } $row.Warnings

        & $step 'seeding transcode logs + scanning'
        Add-TranscodeLogs $h
        Invoke-MdScan $h | Out-Null
        $issues = @(Get-MdIssues $h)
        $cmp = Compare-Issues $h $issues $manifest @('pre-scan', 'post-scan', 'run-time')
        Write-Comparison $cmp
        Save-Json $issues $hostDir 'scan-issues.json'
        Save-Json $cmp $hostDir 'scan-compare.json'
        $row.Scan = [ordered]@{ Matched = $cmp.Matched.Count; Missing = $cmp.Missing.Count; Unexpected = $cmp.Unexpected.Count; KnownGaps = @($cmp.KnownGaps).Count }
        foreach ($m in $cmp.Missing)    { $row.Problems.Add("scan: MISSING $($m.Type) $($m.Path)") }
        foreach ($u in $cmp.Unexpected) { $row.Problems.Add("scan: UNEXPECTED $($u.Type) $($u.Path)") }

        if (-not $ScanOnly) {
            & $step 'approving + fixing'
            $queued = 0
            if ($Types) {
                foreach ($ty in $Types) {
                    try { $queued += [int](Invoke-Jf $h "/MediaDash/Issues/ApproveAll?type=$ty" 'POST') }
                    catch { $row.Problems.Add("approve ${ty}: $($_.Exception.Message)") }
                }
            } else {
                $queued = [int](Invoke-Jf $h '/MediaDash/Issues/ApproveAll' 'POST')
            }
            $stillDetected = @(Get-MdIssues $h 'Detected')    # blocked from bulk approval (consent rows, detect-only types)
            # MediaDash requires a Jellyfin library scan between detection and some fixes
            # (e.g. metadata-folder cleanup waits for one); users get one on Jellyfin's own schedule.
            Invoke-JfLibraryScan $h
            $fixRes = Invoke-MdFix $h

            & $step 'collecting fix results'
            $after = [ordered]@{}
            foreach ($s in 'Detected', 'Queued', 'Fixed', 'Dismissed') { $after[$s] = @(Get-MdIssues $h $s) }
            Save-Json $after $hostDir 'fix-issues-by-status.json'
            Invoke-Optional 'saving fix history' { Save-Json (Invoke-Jf $h '/MediaDash/History') $hostDir 'fix-history.json' } $row.Warnings
            Invoke-Optional 'saving recycle bin' { Save-Json (Invoke-Jf $h '/MediaDash/RecycleBin/Items') $hostDir 'fix-recycle-bin.json' } $row.Warnings

            $fixed  = @($after.Fixed)
            $allFailed = @($after.Detected + $after.Queued | Where-Object { $_.LastAttemptSuccess -eq $false })
            # Split failures into ones the manifest says are correct for this environment and real ones.
            $root = ($t.JfMediaRoot -replace '\\', '/').TrimEnd('/')
            $isExpected = {
                param($i)
                $rel = ($i.Path -replace '\\', '/'); if ($rel.StartsWith("$root/", [StringComparison]::OrdinalIgnoreCase)) { $rel = $rel.Substring($root.Length + 1) }
                @($manifest.fixExpectations | Where-Object { $_.type -eq $i.Type -and $_.path -eq $rel -and "$($i.LastAttemptMessage)" -like "*$($_.messageContains)*" }).Count -gt 0
            }
            $expectedFail = @($allFailed | Where-Object { & $isExpected $_ })
            $expectedRefusals = @($expectedFail | Where-Object Type -eq 'Playability' |
                ForEach-Object { "Refused fix for $($_.Path): $($_.LastAttemptMessage)" })
            $failed       = @($allFailed | Where-Object { -not (& $isExpected $_) })
            $notRun = @($after.Queued | Where-Object { $null -eq $_.LastAttemptSuccess })

            & $step 'rescanning to verify fixes'
            Invoke-MdScan $h | Out-Null
            $rescan = @(Get-MdIssues $h)
            Save-Json $rescan $hostDir 'rescan-issues.json'
            # A fix "stuck" if the same type is detected again on the same path.
            $redetected = @($fixed | Where-Object { $f = $_; $rescan | Where-Object { $_.Type -eq $f.Type -and $_.Path -eq $f.Path } })

            $row.Fix = [ordered]@{
                FixTask = $fixRes.Status; Queued = $queued; NotApprovable = $stillDetected.Count
                Fixed = $fixed.Count; Failed = $failed.Count; ExpectedFailures = $expectedFail.Count; NotAttempted = $notRun.Count; Redetected = $redetected.Count
                FixedByType  = [ordered]@{}; FailedByType = [ordered]@{}
            }
            $fixed  | Group-Object Type | ForEach-Object { $row.Fix.FixedByType[$_.Name]  = $_.Count }
            $failed | Group-Object Type | ForEach-Object { $row.Fix.FailedByType[$_.Name] = $_.Count }
            if ($fixRes.Status -and $fixRes.Status -ne 'Completed') { $row.Problems.Add("fix: the fix task ended with status '$($fixRes.Status)' $($fixRes.ErrorMessage)") }
            foreach ($f in $failed)     { $row.Problems.Add("fix: FAILED $($f.Type) $($f.Path) - $($f.LastAttemptMessage)") }
            foreach ($r in $redetected) { $row.Problems.Add("fix: STILL DETECTED after fix $($r.Type) $($r.Path)") }
            foreach ($n in $notRun)     { $row.Problems.Add("fix: queued but not attempted $($n.Type) $($n.Path)") }
            Write-Host ("  {0,-8} queued {1}, fixed {2}, failed {3} (+{4} expected), not attempted {5}, re-detected {6}" -f $h, $queued, $fixed.Count, $failed.Count, $expectedFail.Count, $notRun.Count, $redetected.Count)
        }

        & $step 'collecting diagnostics'
        Invoke-Optional 'reading MediaDash diagnostics' {
            $errors = @(foreach ($x in (Invoke-Jf $h '/MediaDash/Errors')) { $x })
            Save-Json $errors $hostDir 'mediadash-errors.json'
            $row.Errors = @($errors | Where-Object {
                $_.Source -ne 'PlayabilityFixer.RevalidateIndeterminate' -or $expectedRefusals -cnotcontains $_.Message
            }).Count
            if ($row.Errors) { $row.Problems.Add("MediaDash recorded $($row.Errors) diagnostic(s) - see $h\mediadash-errors.json") }
        } $row.Warnings
        $row.Step = 'done'
    }
    catch {
        $msg = "$($row.Step) failed: $($_.Exception.Message)"
        if (Test-EnvError $_) { $row.EnvError = $msg }
        else { $row.Problems.Add("RUN ERROR during $msg") }
        Write-Host "  ${h}: $msg" -ForegroundColor Red
        Invoke-Optional 'capturing the Jellyfin log tail' { Get-JfPluginLogTail $h 40 | Set-Content -Encoding utf8 (Join-Path $hostDir 'jellyfin-on-error.log') } $null
    }
    finally {
        Invoke-Optional 'saving the Jellyfin MediaDash log' { Get-JfMediaDashLog $h | Set-Content -Encoding utf8 (Join-Path $hostDir 'jellyfin-mediadash.log') } $row.Warnings
        if (-not $KeepState) {
            Write-Step "${h}: restoring golden and stopping"
            try { Restore-Guest $h 'golden'; Stop-Guest $h }
            catch {
                $m = "cleanup failed, $h may be left modified or running: $($_.Exception.Message). Fix: roll it back to 'golden' in Proxmox."
                if (-not $row.EnvError) { $row.EnvError = $m } else { $row.EnvError += " / $m" }
                Write-Host "  $m" -ForegroundColor Red
            }
        }
    }
    [pscustomobject]$row
}

# ------------------------------------------------------------------ run
try {
    Enter-TestEnvLock "testenv.ps1 -Hosts $($Hosts -join ',')"
    Assert-TestEnv -Hosts $Hosts -Snapshot 'golden' -NeedManifest -NeedBuildTools:(-not $SkipBuild)
    if ($PreflightOnly) { Write-Step 'preflight passed'; exit 0 }
    New-Item -ItemType Directory -Force $runDir | Out-Null
    $manifest = Get-FixtureManifest
    if (-not $SkipBuild) {
        # Build once from the working tree; each host deploy below reuses it.
        & (Join-Path $PSScriptRoot 'deploy-plugin.ps1') -BuildOnly
    }
    foreach ($h in $Hosts) { $summary.Add((Test-OneHost $h)) }
}
catch {
    $fatal = $_.Exception.Message
    $fatalEnv = Test-EnvError $_
    Write-Host "`n$fatal" -ForegroundColor Red
}
finally {
    Exit-TestEnvLock
}

# ------------------------------------------------------------------ summary (always written)
New-Item -ItemType Directory -Force $runDir | Out-Null
$envErrors = @($summary | Where-Object EnvError)
$problems  = @($summary | ForEach-Object { $_.Problems }).Count
$exitCode = if ($fatalEnv -or $envErrors.Count) { 2 } elseif ($fatal -or $problems) { 1 } else { 0 }
Save-Json ([ordered]@{ RunId = $runId; ExitCode = $exitCode; Fatal = $fatal; FatalIsEnvironment = $fatalEnv; Hosts = $summary }) $runDir 'summary.json'

$md = @("# MediaDash test run $runId", '', "Build: working tree at $script:RepoRoot  ", "Duration: $([int]((Get-Date) - $started).TotalMinutes) min  ",
        "Mode: $(if ($ScanOnly) { 'scan only' } else { 'scan + fix' })$(if ($Types) { " (types: $($Types -join ', '))" })  ",
        "Result: **exit $exitCode** - $(switch ($exitCode) { 0 { 'no problems' } 1 { 'MediaDash problems below' } 2 { 'TEST ENVIRONMENT problem - not a MediaDash finding' } })", '')
if ($fatal) { $md += "## Run stopped before testing$(if ($fatalEnv) { ' (environment)' })"; $md += ''; $md += '```'; $md += $fatal; $md += '```'; $md += '' }
if ($summary.Count) {
    $md += '| Host | Scan matched | Missing | Unexpected | Known gaps | Fixed | Failed | Expected failures | Re-detected | Errors |'
    $md += '|---|---|---|---|---|---|---|---|---|---|'
    foreach ($s in $summary) {
        $md += "| $($s.Host) | $($s.Scan.Matched) | $($s.Scan.Missing) | $($s.Scan.Unexpected) | $($s.Scan.KnownGaps) | $($s.Fix.Fixed) | $($s.Fix.Failed) | $($s.Fix.ExpectedFailures) | $($s.Fix.Redetected) | $($s.Errors) |"
    }
}
foreach ($s in $summary | Where-Object { $_.EnvError }) { $md += ''; $md += "## $($s.Host): environment problem"; $md += "- $($s.EnvError)" }
foreach ($s in $summary | Where-Object { $_.Problems.Count }) { $md += ''; $md += "## $($s.Host)"; $md += ($s.Problems | ForEach-Object { "- $_" }) }
foreach ($s in $summary | Where-Object { $_.Warnings.Count }) { $md += ''; $md += "## $($s.Host): warnings (did not affect the result)"; $md += ($s.Warnings | ForEach-Object { "- $_" }) }
$md | Set-Content -Encoding utf8 (Join-Path $runDir 'summary.md')

Write-Step "run $runId finished with exit $exitCode - $runDir"
$md | Select-Object -Skip 6 | Out-Host
exit $exitCode
