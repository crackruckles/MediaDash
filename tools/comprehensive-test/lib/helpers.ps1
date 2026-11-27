# helpers.ps1 — Jellyfin auth, HTTP wrappers, MediaDash-specific helpers.
# Consumed via dot-source from run-full-tests.ps1.

# ═════════════════════════════════════════════════════════════════════════════
# Auth
# ═════════════════════════════════════════════════════════════════════════════

function Connect-JellyfinAdmin {
    param()
    $url = $Script:RunState.JellyfinUrl
    Assert-SafeLiveTarget -Url $url
    $body = @{ Username = $Script:RunState.AdminUser; Pw = $Script:RunState.AdminPass } | ConvertTo-Json -Compress
    $bootstrapHeader = 'MediaBrowser Client="mdtest", Device="ci", DeviceId="ci-run", Version="1"'
    $r = Invoke-RestMethod -Method Post -Uri "$url/Users/AuthenticateByName" -ContentType application/json `
        -Body $body -Headers @{ Authorization = $bootstrapHeader } -TimeoutSec 30 -MaximumRedirection 0 -NoProxy
    $token = $r.AccessToken
    $userId = $r.User.Id
    $Script:RunState.JellyfinToken = $token
    $Script:RunState.JellyfinUserId = $userId
    $Script:RunState.JellyfinAuthHeader = 'MediaBrowser Token="' + $token + '", Client="mdtest", Device="ci", DeviceId="ci-run", Version="1"'
    Write-Log "Auth OK — user $userId"
}

# ═════════════════════════════════════════════════════════════════════════════
# Invoke-JfApi — universal Jellyfin/MediaDash API wrapper.
# ═════════════════════════════════════════════════════════════════════════════

function Invoke-JfApi {
    param(
        [Parameter(Mandatory)][ValidateSet("GET","POST","PUT","DELETE","PATCH")][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        [object]$Body = $null,
        [string]$ContentType = "application/json",
        [int]$TimeoutSec = 30,
        [switch]$Raw,
        [switch]$IgnoreErrors
    )
    $url = ($Script:RunState.JellyfinUrl.TrimEnd('/')) + $Path
    Assert-SafeLiveTarget -Url $Script:RunState.JellyfinUrl
    if (-not $Path.StartsWith('/') -or $Path.StartsWith('//')) { throw 'API path must be relative to the selected server.' }
    $headers = @{}
    if ($Script:RunState.JellyfinAuthHeader) {
        $headers.Authorization = $Script:RunState.JellyfinAuthHeader
    }
    $argList = @{
        Method = $Method
        Uri = $url
        Headers = $headers
        TimeoutSec = $TimeoutSec
        MaximumRedirection = 0
        NoProxy = $true
    }
    if ($null -ne $Body) {
        if ($Body -is [string]) { $argList.Body = $Body } else { $argList.Body = ($Body | ConvertTo-Json -Depth 10 -Compress) }
        $argList.ContentType = $ContentType
    }
    try {
        if ($Raw) {
            return Invoke-WebRequest @argList -UseBasicParsing
        } else {
            $response = Invoke-RestMethod @argList
            return $response
        }
    } catch {
        if ($IgnoreErrors) { return $null }
        throw
    }
}

# ═════════════════════════════════════════════════════════════════════════════
# Library management
# ═════════════════════════════════════════════════════════════════════════════

function Add-JellyfinLibrary {
    param([string]$Name, [string]$Type, [string]$Path)
    if (@(Get-JellyfinLibraries | Where-Object Name -EQ $Name).Count) { throw "Library name already exists: $Name" }
    $q = "name=$([Uri]::EscapeDataString($Name))&collectionType=$Type&paths=$([Uri]::EscapeDataString($Path))&refreshLibrary=false"
    $body = @{ LibraryOptions = @{ EnableRealtimeMonitor=$false; EnableInternetProviders=$false; SaveTrickplayWithMedia=$true } } | ConvertTo-Json -Compress
    # Track before POST so a lost response does not orphan a created library.
    $Script:RunState.AddedLibraryIds += $Name
    Invoke-JfApi -Method POST -Path "/Library/VirtualFolders?$q" -Body $body | Out-Null
    Write-Log "Added library: $Name ($Type) -> $Path"
}

function Remove-JellyfinLibrary {
    param([string]$Name)
    if (-not @(Get-JellyfinLibraries | Where-Object Name -EQ $Name).Count) { return }
    $q = "name=$([Uri]::EscapeDataString($Name))&refreshLibrary=false"
    Invoke-JfApi -Method DELETE -Path "/Library/VirtualFolders?$q" | Out-Null
    Assert-Equal 0 @(Get-JellyfinLibraries | Where-Object Name -EQ $Name).Count 'owned library must be removed'
    Write-Log "Removed library: $Name"
}

function Get-JellyfinLibraries {
    Invoke-JfApi -Method GET -Path "/Library/VirtualFolders"
}

function Wait-ForJellyfinScan {
    param([int]$TimeoutSec = 300)
    Start-MediaDashTask -Key 'RefreshLibrary' -Route '/Library/Refresh' -Wait -TimeoutSec $TimeoutSec
}

function Get-JellyfinItemCount {
    param([string]$IncludeItemTypes = "Movie")
    $q = "?Recursive=true&IncludeItemTypes=$IncludeItemTypes"
    $r = Invoke-JfApi -Method GET -Path "/Items$q"
    return [int]$r.TotalRecordCount
}

# ═════════════════════════════════════════════════════════════════════════════
# MediaDash plugin control
# ═════════════════════════════════════════════════════════════════════════════

function Get-MediaDashConfig {
    Invoke-JfApi -Method GET -Path "/Plugins/$($Script:RunState.PluginId.Replace('-',''))/Configuration"
}

function Set-MediaDashConfig {
    param([hashtable]$Patch)
    $cfg = Get-MediaDashConfig
    foreach ($k in $Patch.Keys) { $cfg | Add-Member -MemberType NoteProperty -Name $k -Value $Patch[$k] -Force }
    $json = $cfg | ConvertTo-Json -Depth 10 -Compress
    Invoke-JfApi -Method POST -Path "/Plugins/$($Script:RunState.PluginId.Replace('-',''))/Configuration" -Body $json | Out-Null
    # ConfigChangeRescanner has a 750ms debounce. Drain it before a subsequent
    # Reset/manual scan so that a queued config scan cannot invalidate the test.
    Start-Sleep -Milliseconds 1500
    Wait-For -TimeoutSec 600 -Description 'configuration rescan to drain' -Predicate {
        $tasks = Invoke-JfApi -Method GET -Path '/ScheduledTasks'
        $worker = $tasks | Where-Object Key -eq 'MediaDashScan' | Select-Object -First 1
        return ($worker -and $worker.State -eq 'Idle')
    } | Out-Null
}

function Save-MediaDashConfigSnapshot {
    if (-not $Script:RunState.OriginalConfigSnapshot) {
        $Script:RunState.OriginalConfigSnapshot = Get-MediaDashConfig
        $Script:RunState.OriginalConfigSnapshot | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Script:ResultsDir 'config-before.json') -Encoding utf8
        Write-Log "Snapshotted original MediaDash config"
    }
}

function Restore-MediaDashConfigSnapshot {
    if ($Script:RunState.OriginalConfigSnapshot) {
        $json = $Script:RunState.OriginalConfigSnapshot | ConvertTo-Json -Depth 10 -Compress
        Invoke-JfApi -Method POST -Path "/Plugins/$($Script:RunState.PluginId.Replace('-',''))/Configuration" -Body $json | Out-Null
        $actual = Get-MediaDashConfig | ConvertTo-Json -Depth 10 -Compress
        Assert-Equal $json $actual 'original configuration must be restored exactly'
        Write-Log "Restored original MediaDash config"
    }
}

function Start-MediaDashTask {
    param([string]$Key, [string]$Route, [switch]$Wait, [int]$TimeoutSec)
    $before = $null
    if ($Wait) {
        $before = Wait-For -TimeoutSec $TimeoutSec -Description "$Key to be idle before starting" -Predicate {
            $tasks = Invoke-JfApi -Method GET -Path '/ScheduledTasks'
            $worker = $tasks | Where-Object Key -eq $Key | Select-Object -First 1
            if ($worker -and $worker.State -eq 'Idle') { return $worker }
            return $false
        }
    }
    Invoke-JfApi -Method POST -Path $Route | Out-Null
    if ($Wait) {
        # A fast task may finish between polls. A changed execution timestamp proves it ran;
        # observing Idle alone does not. Rejected requests and failed executions are failures.
        $finished = Wait-For -TimeoutSec $TimeoutSec -Description "$Key to complete a new execution" -Predicate {
            $tasks = Invoke-JfApi -Method GET -Path '/ScheduledTasks'
            $worker = $tasks | Where-Object Key -eq $Key | Select-Object -First 1
            if ($worker -and $worker.State -eq 'Idle' -and $worker.LastExecutionResult -and
                $worker.LastExecutionResult.EndTimeUtc -ne $before.LastExecutionResult.EndTimeUtc) { return $worker }
            return $false
        }
        if ($finished.LastExecutionResult.Status -ne 'Completed') {
            throw "$Key execution ended with $($finished.LastExecutionResult.Status)"
        }
    }
}

function Start-MediaDashScan {
    param([switch]$Wait, [int]$TimeoutSec = 600)
    Start-MediaDashTask -Key 'MediaDashScan' -Route '/MediaDash/Scan' -Wait:$Wait -TimeoutSec $TimeoutSec
}

function Start-MediaDashFix {
    param([switch]$Wait, [int]$TimeoutSec = 1800)
    foreach ($queuedIssue in @(Get-MediaDashIssues -Status 'Queued')) {
        if (-not (Test-OwnedFixturePath $queuedIssue.Path)) { throw 'Refusing global Fix: an approval is outside this run.' }
    }
    $baseline = @((Get-MediaDashHistory).Items | Measure-Object -Property Id -Maximum)[0].Maximum
    $Script:RunState | Add-Member -NotePropertyName LastFixHistoryId -NotePropertyValue ([long]$baseline) -Force
    Start-MediaDashTask -Key 'MediaDashFix' -Route '/MediaDash/Fix' -Wait:$Wait -TimeoutSec $TimeoutSec
}

function Test-OwnedFixturePath {
    param([string]$Path)
    if (-not $Path) { return $false }
    if ($Script:RunState.PSObject.Properties['ArtworkFixturePath'] -and $Path -eq $Script:RunState.ArtworkFixturePath) { return $true }
    try {
        Assert-SafeLiveTarget -Url $Script:RunState.JellyfinUrl -AllowedFixturePaths @($Script:RunState.FixturesRoot) -FixturePath $Path
        return $true
    } catch { return $false }
}

function Clear-OwnedFixtureApprovals {
    $task = @(Invoke-JfApi -Method GET -Path '/ScheduledTasks' | Where-Object Key -EQ 'MediaDashFix')
    Assert-Equal 1 $task.Count 'fix task must be registered'
    Assert-Equal 'Idle' $task[0].State 'do not alter approvals under a running task'
    foreach ($queuedIssue in @(Get-MediaDashIssues -Status 'Queued')) {
        if (-not (Test-OwnedFixturePath $queuedIssue.Path)) { throw 'Unexpected foreign approval; cleanup refused.' }
        Invoke-JfApi -Method POST -Path "/MediaDash/Issues/$($queuedIssue.Id)/Revert" | Out-Null
    }
}

function Get-CurrentFixHistory {
    param([string]$FileName, [string]$Type)
    if (-not $Script:RunState.PSObject.Properties['LastFixHistoryId']) { throw 'No fix-run baseline was captured.' }
    $rows = @((Get-MediaDashHistory).Items | Where-Object {
        $_.Id -gt $Script:RunState.LastFixHistoryId -and $_.FileName -eq $FileName -and $_.Type -eq $Type
    })
    Assert-Equal 1 $rows.Count 'exactly one new history row should describe this issue'
    Assert-True $rows[0].Success 'the current fix must succeed'
    return $rows[0]
}
function Get-MediaDashIssues {
    param([string]$Type = $null, [string]$Status = $null)
    $q = @()
    if ($Type) { $q += "type=$Type" }
    if ($Status) { $q += "status=$Status" }
    else { $q += 'openOnly=true' }
    $qs = if ($q.Count -gt 0) { "?" + ($q -join "&") } else { "" }
    $r = Invoke-JfApi -Method GET -Path "/MediaDash/Issues$qs"
    # Stream rows so direct property filters work. Callers requiring an array
    # for an empty response should collect with @(...).
    $arr = if ($null -eq $r) { @() } else { @($r) }
    return $arr
}

function Approve-MediaDashIssue {
    param([long]$IssueId)
    Invoke-JfApi -Method POST -Path "/MediaDash/Issues/$IssueId/Approve" | Out-Null
}

function Dismiss-MediaDashIssue {
    param([long]$IssueId)
    Invoke-JfApi -Method POST -Path "/MediaDash/Issues/$IssueId/Dismiss" | Out-Null
}

function Get-MediaDashRecycleBinSummary {
    # Returns { FileCount, SizeBytes, IsEmptying, EmptyingDone, EmptyingTotal }
    Invoke-JfApi -Method GET -Path "/MediaDash/RecycleBin"
}

function Get-MediaDashRecycleBinItems {
    # Returns bare array of recycle-bin entries.
    $r = Invoke-JfApi -Method GET -Path "/MediaDash/RecycleBin/Items"
    $arr = if ($null -eq $r) { @() } else { @($r) }
    return $arr
}

# Backward-compat alias — callers used $bin.Items before. Now returns the items array directly.
# For summary access, callers should switch to Get-MediaDashRecycleBinSummary.
function Get-MediaDashRecycleBin {
    return [PSCustomObject]@{
        Items = Get-MediaDashRecycleBinItems
        Summary = Get-MediaDashRecycleBinSummary
    }
}

function Get-MediaDashHistory {
    param([int]$Limit = 500)
    $r = Invoke-JfApi -Method GET -Path "/MediaDash/History?limit=$Limit"
    if ($null -eq $r) { return [PSCustomObject]@{ Items = @() } }
    # Return a wrapper so existing $history.Items call sites keep working
    return [PSCustomObject]@{ Items = @($r) }
}

function Get-MediaDashDiagnostics {
    # Real route is /MediaDash/Errors — /Diagnostics doesn't exist. Returns bare array.
    $r = Invoke-JfApi -Method GET -Path "/MediaDash/Errors"
    $arr = if ($null -eq $r) { @() } else { @($r) }
    return $arr
}

function Get-MediaDashStatus {
    Invoke-JfApi -Method GET -Path "/MediaDash/Status"
}

# ═════════════════════════════════════════════════════════════════════════════
# File helpers
# ═════════════════════════════════════════════════════════════════════════════

function Copy-FixtureFile {
    param([string]$Src, [string]$Dst)
    $dir = Split-Path -Parent $Dst
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Copy-Item -LiteralPath $Src -Destination $Dst -Force
    $Script:RunState.CreatedFixturePaths += $Dst
}

function Get-JellyfinFfmpegPath {
    # The bundled ffmpeg that MediaDash's FfmpegExecutor uses at runtime.
    $candidates = @(
        "$env:USERPROFILE\Downloads\jellyfin_10.11.11-amd64\jellyfin\ffmpeg.exe",
        "$env:LOCALAPPDATA\jellyfin\ffmpeg\jellyfin-ffmpeg.exe",
        "$env:ProgramFiles\Jellyfin\Server\ffmpeg.exe"
    )
    foreach ($c in $candidates) { if (Test-Path -LiteralPath $c) { return $c } }
    return $null
}
