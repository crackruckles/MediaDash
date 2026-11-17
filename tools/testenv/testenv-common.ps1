#Requires -Version 7.0
# Shared helpers for the MediaDash test environment. Dot-source this file.
# Credentials live OUTSIDE the repo in %USERPROFILE%\.mediadash-testenv\*.env (never commit them).
#
# Errors: anything wrong with the test ENVIRONMENT (credentials, SSH, Proxmox, missing snapshots,
# a concurrent run) is thrown with New-EnvError and makes the runners exit 2. Anything else is a
# finding about MediaDash itself (exit 1). Agents: exit 2 means "report it, don't debug the plugin".

$script:PluginGuid = '38bdb090-b763-4294-934b-b54ade4d9d6d'
$script:CredDir    = if ($env:MDTEST_CRED_DIR) { $env:MDTEST_CRED_DIR } else { Join-Path $HOME '.mediadash-testenv' }
$script:SshKey     = Join-Path $HOME '.ssh\mediadash_test'
$script:RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:ArtifactDir = Join-Path $script:RepoRoot 'artifacts\testenv'
$OutputEncoding    = [Text.UTF8Encoding]::new($false)   # no BOM when piping to native commands

function Write-Step([string]$msg) { Write-Host "== $msg" -ForegroundColor Cyan }

# ------------------------------------------------------------------ error classification
function New-EnvError([string]$message) {
    $e = [System.Exception]::new($message)
    $e.Data['TestEnvKind'] = 'environment'
    $e
}
function Test-EnvError($errorRecordOrException) {
    $ex = if ($errorRecordOrException -is [System.Management.Automation.ErrorRecord]) { $errorRecordOrException.Exception } else { $errorRecordOrException }
    while ($ex) { if ($ex.Data -and $ex.Data['TestEnvKind'] -eq 'environment') { return $true }; $ex = $ex.InnerException }
    $false
}
function Get-LastLines([string]$text, [int]$n = 5) {
    (($text -split "`r?`n") | Where-Object { $_.Trim() } | Select-Object -Last $n) -join ' | '
}

# ------------------------------------------------------------------ credentials (loaded lazily)
$script:LoadProblems = [System.Collections.Generic.List[string]]::new()
function Read-EnvFile([string]$name) {
    $h = @{}
    $p = Join-Path $script:CredDir $name
    if (-not (Test-Path $p)) { $script:LoadProblems.Add("missing credential file $p"); return $h }
    foreach ($line in Get-Content $p) {
        if ($line -match '^\s*([A-Z0-9_]+)=(.*)$') { $h[$Matches[1]] = $Matches[2].Trim() }
    }
    $h
}
$script:Pve = Read-EnvFile 'pve.env'
$script:Jf  = Read-EnvFile 'jellyfin.env'

# One row per test host. MediaRoot is the path on the host; JfMediaRoot is the same folder as
# Jellyfin sees it (differs only inside Docker).
$script:Targets = [ordered]@{
    linux   = @{ Ssh = 'mdtest-linux';   Ip = '192.168.1.150'; Vmid = 111; Kind = 'lxc';  Os = 'linux';   Url = $Jf.JF_LINUX_URL;   Key = $Jf.JF_LINUX_API_KEY
                 PluginDir = '/var/lib/jellyfin/plugins'; DataDir = '/var/lib/jellyfin'; LogDir = '/var/log/jellyfin'
                 MediaRoot = '/srv/media'; JfMediaRoot = '/srv/media' }
    docker  = @{ Ssh = 'mdtest-docker';  Ip = '192.168.1.151'; Vmid = 112; Kind = 'lxc';  Os = 'linux';   Url = $Jf.JF_DOCKER_URL;  Key = $Jf.JF_DOCKER_API_KEY
                 PluginDir = '/srv/jellyfin/config/plugins'; DataDir = '/srv/jellyfin/config'; LogDir = '/srv/jellyfin/config/log'
                 MediaRoot = '/srv/media'; JfMediaRoot = '/media' }
    windows = @{ Ssh = 'mdtest-windows'; Ip = '192.168.1.152'; Vmid = 113; Kind = 'qemu'; Os = 'windows'; Url = $Jf.JF_WINDOWS_URL; Key = $Jf.JF_WINDOWS_API_KEY
                 PluginDir = 'C:\ProgramData\Jellyfin\Server\plugins'; DataDir = 'C:\ProgramData\Jellyfin\Server'; LogDir = 'C:\ProgramData\Jellyfin\Server\log'
                 MediaRoot = 'C:\mdtest\media'; JfMediaRoot = 'C:\mdtest\media' }
}

# ------------------------------------------------------------------ preflight
# Checks everything a run needs before touching any host. Prints one line per check and throws an
# environment error listing every failure (with how to fix it) if anything is wrong.
function Assert-TestEnv([string[]]$Hosts, [string]$Snapshot = 'golden', [switch]$NeedManifest, [switch]$NeedBuildTools) {
    $fail = [System.Collections.Generic.List[string]]::new()
    $ok   = { param($m) Write-Host "  ok    $m" -ForegroundColor DarkGreen }
    $bad  = { param($m, $fix) Write-Host "  FAIL  $m" -ForegroundColor Red; if ($fix) { Write-Host "        -> $fix" -ForegroundColor DarkYellow }; $fail.Add("$m$(if ($fix) { " (fix: $fix)" })") }
    Write-Step 'preflight'

    if ($PSVersionTable.PSVersion.Major -ge 7) { & $ok "PowerShell $($PSVersionTable.PSVersion)" }
    else { & $bad "PowerShell $($PSVersionTable.PSVersion)" 'run the scripts with pwsh (PowerShell 7), not powershell.exe' }

    $user = [Environment]::UserName
    if ($user -like 'CodexSandbox*') { & $bad "running as sandbox user '$user'" 'the SSH key and credentials live in the desktop user''s profile; run with Codex sandbox_mode = danger-full-access so commands run as that user' }
    else { & $ok "user $user" }

    $tools = @('ssh', 'scp', 'tar') + $(if ($NeedBuildTools) { @('dotnet') } else { @() })
    foreach ($tool in $tools) {
        if (Get-Command $tool -ErrorAction SilentlyContinue) { & $ok "$tool on PATH" } else { & $bad "$tool not on PATH" "install it or fix PATH for this shell" }
    }

    foreach ($p in $script:LoadProblems) { & $bad $p "credentials are created during test-env setup; restore $script:CredDir from backup or re-run setup" }
    foreach ($k in 'PVE_HOST', 'PVE_NODE', 'PVE_TOKEN_ID', 'PVE_TOKEN_SECRET') {
        if (-not $script:Pve[$k]) { & $bad "pve.env has no $k" "check $script:CredDir\pve.env" }
    }
    foreach ($h in $Hosts) {
        $t = $script:Targets[$h]
        if (-not $t.Url -or -not $t.Key) { & $bad "jellyfin.env has no URL/API key for $h" "expected JF_$($h.ToUpper())_URL and JF_$($h.ToUpper())_API_KEY in $script:CredDir\jellyfin.env" }
    }

    if (Test-Path $script:SshKey) { & $ok "SSH key $script:SshKey" } else { & $bad "SSH key $script:SshKey missing" 'the test env uses a dedicated key; it must exist in the profile of the user running the scripts' }
    foreach ($h in $Hosts) {
        $t = $script:Targets[$h]
        $cfg = (ssh -G $t.Ssh 2>$null) -join "`n"
        $hn = if ($cfg -match '(?m)^hostname (\S+)') { $Matches[1] } else { '' }
        if ($hn -eq $t.Ip -and $cfg -match 'mediadash_test') { & $ok "ssh alias $($t.Ssh) -> $hn" }
        else { & $bad "ssh alias $($t.Ssh) resolves to '$hn' (expected $($t.Ip) with the mediadash_test key)" "check the 'MediaDash test environment' block in ~/.ssh/config" }
    }

    if ($NeedManifest) {
        if (Test-Path (Join-Path $script:ArtifactDir 'manifest.json')) { & $ok 'fixture manifest' }
        else { & $bad "no $script:ArtifactDir\manifest.json" 'run ./tools/testenv/prepare-golden.ps1 once to build fixtures and snapshots' }
    }

    # Proxmox: reachable, token valid, every guest exists and has the snapshot we will roll back to.
    if ($script:Pve.PVE_TOKEN_SECRET) {
        try {
            $null = Invoke-Pve '/version'
            & $ok "Proxmox API at $($script:Pve.PVE_HOST):8006"
            foreach ($h in $Hosts) {
                $t = $script:Targets[$h]
                try {
                    $snaps = @(Invoke-Pve "/nodes/$($script:Pve.PVE_NODE)/$($t.Kind)/$($t.Vmid)/snapshot") | ForEach-Object { $_.name }
                    if ($snaps -contains $Snapshot) { & $ok "$h ($($t.Kind) $($t.Vmid)) has snapshot '$Snapshot'" }
                    else { & $bad "$h has no '$Snapshot' snapshot (has: $(($snaps | Where-Object { $_ -ne 'current' }) -join ', '))" $(if ($Snapshot -eq 'golden') { 'run ./tools/testenv/prepare-golden.ps1' } else { 'the base setup snapshots were removed; the host needs rebuilding' }) }
                } catch { & $bad "$h ($($t.Kind) $($t.Vmid)): $($_.Exception.Message)" '' }
            }
        } catch { & $bad "Proxmox API: $($_.Exception.Message)" '' }
    }

    if ($fail.Count) { throw (New-EnvError ("preflight failed:`n  - " + ($fail -join "`n  - "))) }
}

# ------------------------------------------------------------------ run lock (hosts are shared)
function Enter-TestEnvLock([string]$what) {
    New-Item -ItemType Directory -Force $script:ArtifactDir | Out-Null
    $lock = Join-Path $script:ArtifactDir 'testenv.lock'
    if (Test-Path $lock) {
        $info = try { Get-Content $lock -Raw | ConvertFrom-Json } catch { $null }
        if ($info -and (Get-Process -Id $info.Pid -ErrorAction SilentlyContinue)) {
            throw (New-EnvError "another test-env run is in progress ($($info.What), PID $($info.Pid), user $($info.User), started $($info.Started)). The hosts are shared: wait for it to finish. If that process is not really a test run, delete $lock.")
        }
        Remove-Item $lock -Force -ErrorAction SilentlyContinue   # stale lock from a killed run
    }
    try {
        $fs = [IO.File]::Open($lock, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        $bytes = [Text.Encoding]::UTF8.GetBytes((@{ Pid = $PID; What = $what; User = [Environment]::UserName; Started = (Get-Date -Format s) } | ConvertTo-Json -Compress))
        $fs.Write($bytes, 0, $bytes.Length); $fs.Close()
    } catch [System.IO.IOException] {
        throw (New-EnvError "another test-env run started at the same moment ($lock exists); try again")
    }
    $script:LockPath = $lock
}
function Exit-TestEnvLock {
    if ($script:LockPath -and (Test-Path $script:LockPath)) { Remove-Item $script:LockPath -Force -ErrorAction SilentlyContinue }
    $script:LockPath = $null
}

# ------------------------------------------------------------------ SSH
# Run a script on a test host and return its stdout lines. Linux: bash; Windows: PowerShell.
# Connection failures (ssh exit 255) are retried, then raised as environment errors; a failing
# remote command is raised with its exit code and the tail of its output.
function Invoke-Remote([string]$hostName, [string]$script, [int]$Retries = 2) {
    $t = $script:Targets[$hostName]
    if (-not $t) { throw "unknown host '$hostName'" }
    if ($t.Os -eq 'windows') {
        $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes("`$ProgressPreference='SilentlyContinue'; `$ErrorActionPreference='Stop'`n" + $script))
        $cmd = "powershell -NoProfile -NonInteractive -EncodedCommand $enc"
    } else {
        $b64 = [Convert]::ToBase64String([Text.UTF8Encoding]::new($false).GetBytes(($script -replace "`r", '') + "`n"))
        $cmd = "bash -c `"`$(echo $b64 | base64 -d)`" </dev/null"
    }
    for ($attempt = 0; ; $attempt++) {
        $raw = & ssh -o BatchMode=yes -o ConnectTimeout=10 -o ServerAliveInterval=15 $t.Ssh $cmd 2>&1
        $code = $LASTEXITCODE
        $stdout = @($raw | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
        $stderr = (@($raw | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" }) -join "`n")
        if ($code -eq 0) { return $stdout }
        if ($code -eq 255 -and $stderr -match 'Connection|timed out|refused|No route|Could not resolve|Permission denied|kex_exchange') {
            if ($attempt -lt $Retries) { Start-Sleep (5 * ($attempt + 1)); continue }
            $hint = if ($stderr -match 'Permission denied') { "the host rejected the mediadash_test key" } else { "is the guest running? (Proxmox $($t.Kind) $($t.Vmid))" }
            throw (New-EnvError "cannot SSH to $hostName ($($t.Ssh)): $(Get-LastLines $stderr 2) - $hint")
        }
        throw "command on $hostName failed (exit $code): $(Get-LastLines (($stdout -join "`n") + "`n" + $stderr) 6)"
    }
}

# ------------------------------------------------------------------ Jellyfin API
# GETs are retried on connection errors and 5xx (Jellyfin restarts after a deploy); writes are not.
# Output keeps Invoke-RestMethod's shape: a JSON array comes back as ONE object.
function Invoke-Jf([string]$hostName, [string]$path, [string]$method = 'GET', $body = $null, [int]$Retries = -1) {
    $t = $script:Targets[$hostName]
    if ($Retries -lt 0) { $Retries = if ($method -eq 'GET') { 3 } else { 0 } }
    $req = @{ Uri = "$($t.Url)$path"; Method = $method; Headers = @{ Authorization = "MediaBrowser Token=`"$($t.Key)`"" }; TimeoutSec = 60 }
    if ($null -ne $body) { $req.Body = ($body | ConvertTo-Json -Depth 20); $req.ContentType = 'application/json' }
    for ($attempt = 0; ; $attempt++) {
        try {
            $r = Invoke-RestMethod @req
            # Keep a JSON array as ONE object (callers expect that); leave scalars and objects alone.
            # (Write-Output -NoEnumerate would wrap scalars in a List in PowerShell 7.)
            if ($r -is [array]) { return , $r }
            return $r
        } catch {
            $status = try { [int]$_.Exception.Response.StatusCode } catch { 0 }
            $transient = ($status -eq 0) -or ($status -ge 500)
            if ($transient -and $attempt -lt $Retries) { Start-Sleep (2 * ($attempt + 1)); continue }
            $detail = if ($_.ErrorDetails.Message) { ' - ' + ($_.ErrorDetails.Message -replace '\s+', ' ').Substring(0, [Math]::Min(200, ($_.ErrorDetails.Message -replace '\s+', ' ').Length)) } else { '' }
            $what = if ($status) { "HTTP $status" } else { $_.Exception.Message }
            $msg = "Jellyfin on ${hostName}: $method $path failed: $what$detail"
            switch ($status) {
                401 { throw (New-EnvError "$msg (API key rejected: jellyfin.env may be out of date for $hostName)") }
                404 { if ($path -like '/MediaDash/*') { throw "$msg (MediaDash endpoint missing: did the plugin load? see /Plugins)" } }
                0   { throw (New-EnvError "$msg (Jellyfin unreachable at $($t.Url))") }
            }
            throw $msg
        }
    }
}

function Wait-JfReady([string]$hostName, [int]$timeoutSec = 180) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    $last = ''
    while ((Get-Date) -lt $deadline) {
        try { Invoke-Jf $hostName '/System/Info' 'GET' $null 0 | Out-Null; return }
        catch {
            $last = $_.Exception.Message
            if ($last -match 'HTTP 4\d\d') { throw }   # rejected key etc.: waiting will not help
            Start-Sleep 3
        }
    }
    throw (New-EnvError "Jellyfin on $hostName did not answer within $timeoutSec s (last error: $last)")
}

function Wait-PluginActive([string]$hostName, [int]$timeoutSec = 180) {
    Wait-JfReady $hostName $timeoutSec
    $p = (Invoke-Jf $hostName '/Plugins') | Where-Object { $_.Id -replace '-', '' -eq ($script:PluginGuid -replace '-', '') }
    if (-not $p) { return 'NotLoaded' }
    "$($p.Status) $($p.Version)"
}

# Recent MediaDash / plugin-loader lines from the host's Jellyfin log, for error messages.
function Get-JfPluginLogTail([string]$hostName, [int]$lines = 12) {
    $t = $script:Targets[$hostName]
    try {
        if ($t.Os -eq 'windows') {
            (Invoke-Remote $hostName "Get-ChildItem '$($t.LogDir)' -Filter 'log_*.log' | Sort-Object LastWriteTime | Select-Object -Last 1 | Get-Content | Select-String -Pattern 'MediaDash|PluginManager' | Select-Object -Last $lines | ForEach-Object { `$_.Line }" 0) -join "`n"
        } else {
            (Invoke-Remote $hostName "ls -t '$($t.LogDir)'/log_*.log 2>/dev/null | head -1 | xargs -r grep -aE 'MediaDash|PluginManager' | tail -$lines" 0) -join "`n"
        }
    } catch { "(could not read the Jellyfin log: $($_.Exception.Message))" }
}

# ------------------------------------------------------------------ Proxmox API
function Invoke-Pve([string]$path, [string]$method = 'GET', [hashtable]$form = $null, [int]$Retries = -1) {
    if (-not $script:Pve.PVE_TOKEN_SECRET) { throw (New-EnvError "no Proxmox credentials (see $script:CredDir\pve.env)") }
    if ($Retries -lt 0) { $Retries = if ($method -eq 'GET') { 2 } else { 0 } }
    $req = @{ Uri = "https://$($script:Pve.PVE_HOST):8006/api2/json$path"; Method = $method; SkipCertificateCheck = $true; SkipHeaderValidation = $true
              Headers = @{ Authorization = "PVEAPIToken=$($script:Pve.PVE_TOKEN_ID)=$($script:Pve.PVE_TOKEN_SECRET)" }; TimeoutSec = 60 }
    if ($form) { $req.Body = $form }
    for ($attempt = 0; ; $attempt++) {
        try { return (Invoke-RestMethod @req).data }
        catch {
            $status = try { [int]$_.Exception.Response.StatusCode } catch { 0 }
            if (($status -eq 0 -or $status -ge 500) -and $attempt -lt $Retries) { Start-Sleep (3 * ($attempt + 1)); continue }
            $detail = if ($_.ErrorDetails.Message) { ' - ' + (($_.ErrorDetails.Message -replace '\s+', ' ').Substring(0, [Math]::Min(200, ($_.ErrorDetails.Message -replace '\s+', ' ').Length))) } else { '' }
            $hint = switch ($status) {
                401     { 'token rejected: the secret in pve.env is wrong or the token was deleted' }
                403     { 'token lacks permission: it only covers pool mediadash-test (VM.Audit, VM.PowerMgmt, VM.Snapshot, VM.Snapshot.Rollback)' }
                0       { "cannot reach Proxmox at $($script:Pve.PVE_HOST):8006" }
                default { '' }
            }
            throw (New-EnvError "Proxmox $method $path failed: $(if ($status) { "HTTP $status" } else { $_.Exception.Message })$detail$(if ($hint) { " ($hint)" })")
        }
    }
}

function Wait-PveTask([string]$upid, [int]$timeoutSec = 300, [string]$what = 'Proxmox task') {
    $node = $script:Pve.PVE_NODE
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    $u = [uri]::EscapeDataString($upid)
    while ((Get-Date) -lt $deadline) {
        $s = Invoke-Pve "/nodes/$node/tasks/$u/status"
        if ($s.status -eq 'stopped') {
            if ($s.exitstatus -ne 'OK') {
                $log = try { (@(Invoke-Pve "/nodes/$node/tasks/$u/log?limit=200") | ForEach-Object { $_.t } | Select-Object -Last 4) -join ' | ' } catch { '' }
                throw (New-EnvError "$what failed: $($s.exitstatus)$(if ($log) { " - $log" })")
            }
            return
        }
        Start-Sleep 2
    }
    throw (New-EnvError "$what did not finish within $timeoutSec s ($upid)")
}

function Get-GuestStatus([string]$hostName) {
    $t = $script:Targets[$hostName]
    (Invoke-Pve "/nodes/$($script:Pve.PVE_NODE)/$($t.Kind)/$($t.Vmid)/status/current").status
}

function Start-Guest([string]$hostName) {
    $t = $script:Targets[$hostName]
    if ((Get-GuestStatus $hostName) -ne 'running') {
        Wait-PveTask (Invoke-Pve "/nodes/$($script:Pve.PVE_NODE)/$($t.Kind)/$($t.Vmid)/status/start" 'POST') 300 "starting $hostName"
    }
}

function Stop-Guest([string]$hostName) {
    $t = $script:Targets[$hostName]
    if ((Get-GuestStatus $hostName) -eq 'running') {
        Wait-PveTask (Invoke-Pve "/nodes/$($script:Pve.PVE_NODE)/$($t.Kind)/$($t.Vmid)/status/stop" 'POST') 300 "stopping $hostName"
    }
}

# Roll a guest back to a snapshot and start it. Containers must be stopped first; a VM rollback
# stops the VM itself.
function Restore-Guest([string]$hostName, [string]$snapshot = 'golden') {
    $t = $script:Targets[$hostName]
    $node = $script:Pve.PVE_NODE
    $snaps = @(Invoke-Pve "/nodes/$node/$($t.Kind)/$($t.Vmid)/snapshot") | ForEach-Object { $_.name }
    if ($snaps -notcontains $snapshot) {
        throw (New-EnvError "$hostName has no '$snapshot' snapshot$(if ($snapshot -eq 'golden') { ' - run ./tools/testenv/prepare-golden.ps1' })")
    }
    if ($t.Kind -eq 'lxc') { Stop-Guest $hostName }
    Wait-PveTask (Invoke-Pve "/nodes/$node/$($t.Kind)/$($t.Vmid)/snapshot/$snapshot/rollback" 'POST') 600 "rolling $hostName back to '$snapshot'"
    Start-Guest $hostName
}

# Best-effort step: run it, and on failure record a warning instead of aborting the run.
function Invoke-Optional([string]$what, [scriptblock]$block, [System.Collections.Generic.List[string]]$warnings) {
    try { & $block } catch {
        $m = "$what failed: $($_.Exception.Message)"
        if ($warnings) { $warnings.Add($m) }
        Write-Warning $m
    }
}

# ---------------------------------------------------------------------------------------------
# Jellyfin tasks, MediaDash issues, and comparison against the fixture manifest
# ---------------------------------------------------------------------------------------------

# Wait for a Jellyfin scheduled task (by Key) to finish a run that ended after $since.
function Wait-JfTask([string]$hostName, [string]$key, [datetime]$since, [int]$timeoutSec = 900) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep 3
        $task = (Invoke-Jf $hostName '/ScheduledTasks') | Where-Object Key -eq $key | Select-Object -First 1
        if (-not $task) { throw "scheduled task '$key' not found on $hostName (did MediaDash load?)" }
        $end = $task.LastExecutionResult.EndTimeUtc
        if ($task.State -eq 'Idle' -and $end -and ([datetime]$end).ToUniversalTime() -gt $since.ToUniversalTime()) {
            return $task.LastExecutionResult
        }
    }
    throw "scheduled task '$key' on $hostName did not finish within $timeoutSec s (hung? see the Jellyfin log)"
}

function Invoke-JfLibraryScan([string]$hostName) {
    $since = (Get-Date).ToUniversalTime().AddSeconds(-2)
    Invoke-Jf $hostName '/Library/Refresh' 'POST' | Out-Null
    Wait-JfTask $hostName 'RefreshLibrary' $since | Out-Null
}

function Invoke-MdScan([string]$hostName) {
    $since = (Get-Date).ToUniversalTime().AddSeconds(-2)
    Invoke-Jf $hostName '/MediaDash/Scan' 'POST' | Out-Null
    Wait-JfTask $hostName 'MediaDashScan' $since
}

function Invoke-MdFix([string]$hostName) {
    $since = (Get-Date).ToUniversalTime().AddSeconds(-2)
    Invoke-Jf $hostName '/MediaDash/Fix' 'POST' | Out-Null
    Wait-JfTask $hostName 'MediaDashFix' $since
}

function Get-MdIssues([string]$hostName, [string]$status = 'Detected') {
    # Invoke-RestMethod hands back a JSON array as ONE pipeline object; emit the issues one by one.
    $r = Invoke-Jf $hostName "/MediaDash/Issues?status=$status"
    foreach ($x in $r) { $x }
}

function Get-FixtureManifest {
    $p = Join-Path $script:ArtifactDir 'manifest.json'
    if (-not (Test-Path $p)) { throw (New-EnvError "no fixture manifest at $p - run ./tools/testenv/prepare-golden.ps1 first") }
    Get-Content $p -Raw -Encoding utf8 | ConvertFrom-Json
}

# Issue types that legitimately appear on many fixtures (every movie gets trickplay once
# extraction is on). They are reported but never counted as unexpected.
$script:AmbientTypes = @('LargeTrickplay')

# Jellyfin item id (32 hex, no dashes) for a fixture folder name such as "Artwork Target (2020)".
function Get-JfItemId([string]$hostName, [string]$name) {
    $term = ($name -replace '\s*\(\d{4}\)$', '')
    $items = (Invoke-Jf $hostName "/Items?recursive=true&searchTerm=$([uri]::EscapeDataString($term))").Items
    $hit = $items | Where-Object { $_.Name -eq $name -or $_.Name -eq $term } | Select-Object -First 1
    if ($hit) { ($hit.Id -replace '-', '').ToLower() }
}

# Compare detected issues with the manifest. Returns matched / missing / unexpected lists.
#   -Stages: which manifest stages must be present (pre-scan, post-scan, run-time).
function Compare-Issues([string]$hostName, $issues, $manifest, [string[]]$Stages = @('pre-scan', 'post-scan')) {
    $t = $script:Targets[$hostName]
    $root = $t.JfMediaRoot.TrimEnd('/', '\')
    $norm = { param($p) ($p -replace '\\', '/').TrimEnd('/') }
    $rel = foreach ($i in $issues) {
        $p = & $norm $i.Path
        $r = if ($p.StartsWith((& $norm $root) + '/', [StringComparison]::OrdinalIgnoreCase)) { $p.Substring($root.Length + 1) } else { $p }
        [pscustomobject]@{ Type = $i.Type; Rel = $r; Issue = $i }
    }
    $matchedIds = [System.Collections.Generic.HashSet[long]]::new()
    $matched = @(); $missing = @(); $known = @()
    foreach ($f in $manifest.fixtures) {
        if ($Stages -notcontains $f.stage) { continue }
        foreach ($type in $f.expect) {
            $hit = $null
            if ($f.path -match '^(metadata|trickplay):(.+)$') {
                $name = $Matches[2]
                if ($name -match '^[0-9a-f]{32}$') {
                    # A raw item id: match the folder of that name (orphan debris).
                    $hit = $rel | Where-Object { $_.Type -eq $type -and $_.Rel -like "*$name*" } | Select-Object -First 1
                } else {
                    # An item name: resolve its Jellyfin id and match issues raised against it.
                    $id = Get-JfItemId $hostName $name
                    $hit = $rel | Where-Object { $_.Type -eq $type -and $id -and (($_.Issue.ItemId -replace '-', '') -eq $id) } | Select-Object -First 1
                }
            } else {
                $free = { param($x) $x.Type -eq $type -and -not $matchedIds.Contains($x.Issue.Id) }
                # Exact path first, then an issue raised on something inside the fixture folder.
                $hit = $rel | Where-Object { (& $free $_) -and $_.Rel -eq $f.path } | Select-Object -First 1
                if (-not $hit) { $hit = $rel | Where-Object { (& $free $_) -and $_.Rel.StartsWith("$($f.path)/") } | Select-Object -First 1 }
                if (-not $hit) {
                    # Some scanners report the containing folder (e.g. an orphan sidecar's folder).
                    $parent = ($f.path -replace '/[^/]+$', '')
                    if ($parent -match '/') { $hit = $rel | Where-Object { $_.Type -eq $type -and $_.Rel -eq $parent -and -not $matchedIds.Contains($_.Issue.Id) } | Select-Object -First 1 }
                }
            }
            if ($hit) { [void]$matchedIds.Add($hit.Issue.Id); $matched += [pscustomobject]@{ Type = $type; Path = $f.path; IssueId = $hit.Issue.Id } }
            elseif ($f.note -like 'KNOWN GAP*') { $known += [pscustomobject]@{ Type = $type; Path = $f.path; Note = $f.note } }
            else      { $missing += [pscustomobject]@{ Type = $type; Path = $f.path; Note = $f.note } }
        }
    }
    $unexpected = @($rel | Where-Object { -not $matchedIds.Contains($_.Issue.Id) -and $script:AmbientTypes -notcontains $_.Type } |
        ForEach-Object { [pscustomobject]@{ Type = $_.Type; Path = $_.Rel; IssueId = $_.Issue.Id } })
    $ambient = @($rel | Where-Object { -not $matchedIds.Contains($_.Issue.Id) -and $script:AmbientTypes -contains $_.Type }).Count
    [pscustomobject]@{ Host = $hostName; Matched = $matched; Missing = $missing; Unexpected = $unexpected; Ambient = $ambient; KnownGaps = $known }
}

function Write-Comparison($c) {
    $color = if ($c.Missing.Count -eq 0 -and $c.Unexpected.Count -eq 0) { 'Green' } else { 'Yellow' }
    Write-Host ("  {0,-8} matched {1}, missing {2}, unexpected {3}, known gaps {4}, ambient {5}" -f $c.Host, $c.Matched.Count, $c.Missing.Count, $c.Unexpected.Count, @($c.KnownGaps).Count, $c.Ambient) -ForegroundColor $color
    foreach ($m in $c.Missing)    { Write-Host "      MISSING    $($m.Type)  $($m.Path)" }
    foreach ($u in $c.Unexpected) { Write-Host "      UNEXPECTED $($u.Type)  $($u.Path)" }
    foreach ($k in @($c.KnownGaps)) { Write-Host "      KNOWN GAP  $($k.Type)  $($k.Path)" -ForegroundColor DarkCyan }
}
