<#
.SYNOPSIS
  (Re)builds the 'golden' snapshot that every test run rolls back to.
.DESCRIPTION
  For each host: roll back to 'configured' (fresh Jellyfin + admin + API key), copy in the
  fixture media, deploy the current MediaDash build, create the libraries, apply the test
  config, let Jellyfin scan, apply the post-scan fixtures (corrupt artwork, orphaned metadata,
  stale date), run a baseline MediaDash scan, compare it to the manifest, and snapshot 'golden'.
  Re-run whenever fixtures, Jellyfin versions or the test config change.
.EXAMPLE
  ./tools/testenv/prepare-golden.ps1
  ./tools/testenv/prepare-golden.ps1 -Hosts linux -SkipFixtureBuild
#>
param(
    [ValidateSet('linux', 'docker', 'windows')]
    [string[]]$Hosts = @('linux', 'docker', 'windows'),
    [switch]$SkipFixtureBuild,      # reuse artifacts\testenv\fixtures.tar.gz
    [switch]$NoSnapshot,            # stop before taking the 'golden' snapshot
    [switch]$Force                  # snapshot even if the baseline has missing/unexpected issues
)
# Checked here rather than with #Requires: under Windows PowerShell 5.1 a #Requires failure still
# exits 0, which an agent would read as success.
if ($PSVersionTable.PSVersion.Major -lt 7) {
    [Console]::Error.WriteLine("testenv: this script needs PowerShell 7. Run it with: pwsh -File $($MyInvocation.MyCommand.Path)")
    exit 2
}
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'testenv-common.ps1')
$exitCode = 0
$summary = @()
try {
Enter-TestEnvLock "prepare-golden.ps1 -Hosts $($Hosts -join ',')"
Assert-TestEnv -Hosts $Hosts -Snapshot 'configured' -NeedBuildTools
New-Item -ItemType Directory -Force $script:ArtifactDir | Out-Null
$tarball = Join-Path $script:ArtifactDir 'fixtures.tar.gz'

# The libraries MediaDash is tested against. Audiobooks live inside Books on purpose (MediaDash
# routes AudioBook items to the Books target).
$libraries = @(
    @{ Name = 'Movies';   Type = 'movies';     Dir = 'movies';   Trickplay = $true }
    @{ Name = 'TV';       Type = 'tvshows';    Dir = 'tv' }
    @{ Name = 'Music';    Type = 'music';      Dir = 'music' }
    @{ Name = 'Books';    Type = 'books';      Dir = 'books' }
    @{ Name = 'Comics';   Type = 'books';      Dir = 'comics' }
    @{ Name = 'Pictures'; Type = 'homevideos'; Dir = 'pictures' }
)

function Join-HostPath([hashtable]$t, [string]$a, [string]$b) {
    if ($t.Os -eq 'windows') { "$a\$b" } else { "$a/$b" }
}

# ------------------------------------------------------------------ 1. fixture media (once)
if (-not $SkipFixtureBuild) {
    Write-Step 'building fixtures on mdtest-linux'
    Start-Guest linux; Wait-JfReady linux
    scp -q -o BatchMode=yes (Join-Path $PSScriptRoot 'make-fixtures.sh') mdtest-linux:/root/make-fixtures.sh
    if ($LASTEXITCODE) { throw (New-EnvError 'could not copy make-fixtures.sh to mdtest-linux') }
    $comic = Join-Path $script:RepoRoot 'Jellyfin.Plugin.MediaDash.Tests/fixtures/formats/comic/sample.cbr'
    scp -q -o BatchMode=yes $comic mdtest-linux:/root/sample.cbr
    if ($LASTEXITCODE) { throw (New-EnvError 'could not copy sample.cbr to mdtest-linux') }
    Invoke-Remote linux @'
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive LC_ALL=C.UTF-8
command -v zip >/dev/null && command -v 7z >/dev/null && command -v python3 >/dev/null || apt-get install -y -qq zip p7zip-full python3 >/dev/null
sed -i 's/\r$//' /root/make-fixtures.sh
bash /root/make-fixtures.sh /root/fixture-build /usr/lib/jellyfin-ffmpeg/ffmpeg /root/sample.cbr | tail -2
tar -czf /root/fixtures.tar.gz -C /root/fixture-build media manifest.json
'@ | Out-Host
    scp -q -o BatchMode=yes mdtest-linux:/root/fixtures.tar.gz $tarball
    if ($LASTEXITCODE) { throw (New-EnvError 'could not copy fixtures.tar.gz back from mdtest-linux') }
    tar.exe -xzf $tarball -C $script:ArtifactDir manifest.json
}
if (-not (Test-Path $tarball)) { throw "no $tarball - run without -SkipFixtureBuild" }
$manifest = Get-FixtureManifest

foreach ($h in $Hosts) {
    $t = $script:Targets[$h]
    try {
    Write-Step "${h}: rolling back to 'configured'"
    Restore-Guest $h 'configured'
    Wait-JfReady $h 300

    # -------------------------------------------------------------- 2. media
    Write-Step "${h}: copying fixture media"
    if ($t.Os -eq 'windows') {
        scp -q -o BatchMode=yes $tarball "$($t.Ssh):C:/mdtest/fixtures.tar.gz"
        if ($LASTEXITCODE) { throw (New-EnvError "could not copy fixtures to $h") }
        Invoke-Remote $h @'
if (Test-Path C:\mdtest\media) { Remove-Item C:\mdtest\media -Recurse -Force }
tar.exe -xzf C:\mdtest\fixtures.tar.gz -C C:\mdtest
Remove-Item C:\mdtest\fixtures.tar.gz
icacls C:\mdtest\media /grant '*S-1-5-20:(OI)(CI)M' /T /Q | Out-Null
'@ | Out-Null
    } else {
        scp -q -o BatchMode=yes $tarball "$($t.Ssh):/srv/fixtures.tar.gz"
        if ($LASTEXITCODE) { throw (New-EnvError "could not copy fixtures to $h") }
        $own = if ($h -eq 'linux') { 'chown -R jellyfin:jellyfin /srv/media' } else { 'true' }
        Invoke-Remote $h "set -e; rm -rf /srv/media; tar -xzf /srv/fixtures.tar.gz -C /srv --no-same-owner; rm /srv/fixtures.tar.gz; $own" | Out-Null
    }

    # -------------------------------------------------------------- 3. plugin
    & (Join-Path $PSScriptRoot 'deploy-plugin.ps1') -Hosts $h -SkipBuild:($h -ne $Hosts[0])

    # -------------------------------------------------------------- 4. libraries
    Write-Step "${h}: creating libraries"
    foreach ($l in $libraries) {
        $path = Join-HostPath $t $t.JfMediaRoot $l.Dir
        $q = "name=$([uri]::EscapeDataString($l.Name))&collectionType=$($l.Type)&refreshLibrary=false&paths=$([uri]::EscapeDataString($path))"
        $opts = @{ LibraryOptions = @{ EnableRealtimeMonitor = $false
                                        EnableTrickplayImageExtraction = [bool]$l.Trickplay
                                        ExtractTrickplayImagesDuringLibraryScan = [bool]$l.Trickplay } }
        Invoke-Jf $h "/Library/VirtualFolders?$q" 'POST' $opts | Out-Null
    }

    # -------------------------------------------------------------- 5. Jellyfin scan
    # Must finish BEFORE MediaDash's first scan: saving the config triggers a MediaDash scan, and
    # MediaDash's one-time "content-aware defaults" sweep permanently switches off every video
    # scanner if it runs while Jellyfin has not indexed any video yet.
    Write-Step "${h}: Jellyfin library scan (includes trickplay extraction)"
    Invoke-JfLibraryScan $h

    # -------------------------------------------------------------- 6. MediaDash test config
    Write-Step "${h}: applying MediaDash test config"
    $cfg = Invoke-Jf $h "/Plugins/$($script:PluginGuid)/Configuration"
    $root = $t.JfMediaRoot
    $set = [ordered]@{
        AnalyticsEnabled = $false          # never report test runs to the public stats
        FirstRunDone = $true
        DryRun = $false                    # runs are rolled back, so fixes are real
        MinScanFileSizeMb = 1              # fixtures are tiny
        TrickplayMinSizeMb = 0
        AllowedAudioLanguages = @('eng')
        AllowedSubtitleLanguages = @('eng')
        StaleFixMode = 1                   # DetectOnly (Stale cannot fix anything)
        StaleThresholdDays = 30
        ConvertComics = $true
        ConvertImages = $true
        MoviesTargetPath   = Join-HostPath $t $root 'movies'
        TvTargetPath       = Join-HostPath $t $root 'tv'
        MusicTargetPath    = Join-HostPath $t $root 'music'
        BooksTargetPath    = Join-HostPath $t $root 'books'
        ComicsTargetPath   = Join-HostPath $t $root 'comics'
        PicturesTargetPath = Join-HostPath $t $root 'pictures'
    }
    # Every scanner on and reporting; nothing auto-fixes. The runner approves what it wants fixed.
    # (On first load MediaDash applies wizard defaults that switch nine scanners Off.)
    foreach ($prop in @($cfg.PSObject.Properties | Where-Object { $_.Name -like '*FixMode' -and $_.Name -ne 'StaleFixMode' })) {
        $set[$prop.Name] = 2                # ManualApprove
    }
    $enumNames = @{ FixMode = @('Off', 'DetectOnly', 'ManualApprove', 'Automatic'); MediaSortSource = @('JellyfinMetadata', 'FilenameHeuristic') }
    foreach ($k in $set.Keys) {
        if ($cfg.PSObject.Properties.Name -notcontains $k) { Write-Warning "config has no '$k' - skipped"; continue }
        $v = $set[$k]
        # Enums come back as names on some hosts and numbers on others: match the existing shape.
        if ($v -is [int] -and $cfg.$k -is [string]) {
            $names = if ($k -like '*FixMode') { $enumNames.FixMode } elseif ($enumNames.ContainsKey($k)) { $enumNames[$k] } else { $null }
            if ($names) { $v = $names[$v] }
        }
        $cfg.$k = $v
    }
    Invoke-Jf $h "/Plugins/$($script:PluginGuid)/Configuration" 'POST' $cfg | Out-Null
    # The save triggers a MediaDash scan (ConfigChangeRescanner). Let it finish before the post-scan
    # fixtures go in, then confirm every scanner survived the content-aware sweep.
    Start-Sleep 5
    $deadline = (Get-Date).AddMinutes(10)
    while (((Invoke-Jf $h '/ScheduledTasks') | Where-Object Key -eq 'MediaDashScan').State -ne 'Idle' -and (Get-Date) -lt $deadline) { Start-Sleep 3 }
    $after = Invoke-Jf $h "/Plugins/$($script:PluginGuid)/Configuration"
    $off = @($after.PSObject.Properties | Where-Object { $_.Name -like '*FixMode' -and $_.Name -ne 'StaleFixMode' -and "$($_.Value)" -notin @('ManualApprove', '2') } | ForEach-Object Name)
    if ($off) {
        Write-Warning "${h}: re-enabling scanners switched off after the config save: $($off -join ', ')"
        foreach ($n in $off) { $after.$n = if ($after.$n -is [string]) { 'ManualApprove' } else { 2 } }
        Invoke-Jf $h "/Plugins/$($script:PluginGuid)/Configuration" 'POST' $after | Out-Null
        Start-Sleep 5
        while (((Invoke-Jf $h '/ScheduledTasks') | Where-Object Key -eq 'MediaDashScan').State -ne 'Idle') { Start-Sleep 3 }
    }

    # -------------------------------------------------------------- 7. post-scan fixtures
    Write-Step "${h}: post-scan fixtures"
    $find = { param($name) (Invoke-Jf $h "/Items?recursive=true&includeItemTypes=Movie&searchTerm=$([uri]::EscapeDataString($name))").Items | Where-Object Name -like "$name*" | Select-Object -First 1 }
    $toHost = { param($p) if ($h -eq 'docker') { $p -replace '^/config/', '/srv/jellyfin/config/' } else { $p } }

    # CorruptArtwork: zero the Artwork Target's image inside Jellyfin's metadata folder.
    $art = & $find 'Artwork Target'
    $img = if ($art) { @(Invoke-Jf $h "/Items/$($art.Id)/Images") | Where-Object { $_.Path } | Select-Object -First 1 }
    if ($img) {
        $p = & $toHost $img.Path
        if ($t.Os -eq 'windows') { Invoke-Remote $h "[IO.File]::WriteAllBytes('$p', [byte[]]@())" | Out-Null }
        else { Invoke-Remote $h "truncate -s 0 '$p'" | Out-Null }
        Write-Host "  zeroed $p"
    } else { Write-Warning "${h}: no image found for 'Artwork Target' - CorruptArtwork fixture not created" }

    # Stale: backdate DateCreated through the item API.
    $stale = & $find 'Stale Test'
    if ($stale) {
        $uid = (@(Invoke-Jf $h '/Users') | Select-Object -First 1).Id
        $dto = Invoke-Jf $h "/Users/$uid/Items/$($stale.Id)"
        $dto.DateCreated = '2020-01-01T00:00:00.0000000Z'
        Invoke-Jf $h "/Items/$($stale.Id)" 'POST' $dto | Out-Null
        Write-Host '  backdated Stale Test to 2020-01-01'
    } else { Write-Warning "${h}: 'Stale Test' not found - Stale fixture not created" }

    # OrphanedDebris: a metadata folder for an item id Jellyfin has never heard of (debris left by a
    # removed item). Placed next to the Artwork Target's folder: <metadata>/library/<xx>/<id>/.
    # (Deleting a scanned video instead leaves the item in Jellyfin's DB, which MediaDash then
    # reports as Playability rather than debris.)
    if ($img) {
        $itemDir = Split-Path (& $toHost $img.Path) -Parent
        $libDir  = Split-Path (Split-Path $itemDir -Parent) -Parent
        if ($t.Os -eq 'windows') {
            Invoke-Remote $h "New-Item -ItemType Directory -Force '$libDir\0d\0dd0dd0d0dd0dd0d0dd0dd0d0dd0dd0d' | Out-Null; Set-Content '$libDir\0d\0dd0dd0d0dd0dd0d0dd0dd0d0dd0dd0d\poster.jpg' 'orphan'; icacls '$libDir\0d' /grant '*S-1-5-20:(OI)(CI)F' /T /Q | Out-Null" | Out-Null
        } else {
            $libDir = $libDir -replace '\\', '/'
            $own = if ($h -eq 'linux') { "chown -R jellyfin:jellyfin '$libDir/0d'" } else { 'true' }
            Invoke-Remote $h "mkdir -p '$libDir/0d/0dd0dd0d0dd0dd0d0dd0dd0d0dd0dd0d' && echo orphan > '$libDir/0d/0dd0dd0d0dd0dd0d0dd0dd0d0dd0dd0d/poster.jpg' && $own" | Out-Null
        }
        Write-Host "  added orphan metadata folder under $libDir"
    }

    # -------------------------------------------------------------- 8. baseline MediaDash scan
    Write-Step "${h}: baseline MediaDash scan"
    $res = Invoke-MdScan $h
    $issues = Get-MdIssues $h
    $cmp = Compare-Issues $h $issues $manifest
    Write-Comparison $cmp
    $issues | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 (Join-Path $script:ArtifactDir "golden-issues-$h.json")
    $summary += $cmp

    # The baseline scan's issues stay in the snapshot: runs start from a known issue list.
    # -------------------------------------------------------------- 9. snapshot
    # Never bake a wrong baseline into 'golden': every later run would compare against it.
    $dirty = $cmp.Missing.Count -or $cmp.Unexpected.Count
    if ($dirty -and -not $Force -and -not $NoSnapshot) {
        Write-Host "  ${h}: baseline has missing/unexpected issues - NOT snapshotting (the previous 'golden' is kept). Fix the fixtures/config, or pass -Force." -ForegroundColor Red
        $exitCode = [Math]::Max($exitCode, 1)
    }
    elseif (-not $NoSnapshot) {
        Write-Step "${h}: snapshot 'golden'"
        $node = $script:Pve.PVE_NODE
        $snaps = Invoke-Pve "/nodes/$node/$($t.Kind)/$($t.Vmid)/snapshot"
        if ($snaps.name -contains 'golden') { Wait-PveTask (Invoke-Pve "/nodes/$node/$($t.Kind)/$($t.Vmid)/snapshot/golden" 'DELETE') }
        $desc = "MediaDash golden $(Get-Date -Format s): fixtures + plugin + config + baseline scan"
        try { Wait-PveTask (Invoke-Pve "/nodes/$node/$($t.Kind)/$($t.Vmid)/snapshot" 'POST' @{ snapname = 'golden'; description = $desc }) 600 "snapshotting $h" }
        catch { throw (New-EnvError "$h now has NO 'golden' snapshot (the old one was deleted, the new one failed): $($_.Exception.Message). Re-run prepare-golden.ps1 -Hosts $h.") }
    }
    }
    catch {
        $env = Test-EnvError $_
        Write-Host "  ${h}: FAILED - $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "  ${h}: its existing 'golden' snapshot (if any) was left untouched unless stated above." -ForegroundColor DarkYellow
        $exitCode = [Math]::Max($exitCode, $(if ($env) { 2 } else { 1 }))
    }
}

}
catch {
    Write-Host "`n$($_.Exception.Message)" -ForegroundColor Red
    $exitCode = if (Test-EnvError $_) { 2 } else { 1 }
}
finally {
    Exit-TestEnvLock
}
if ($summary) {
    $summary | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 (Join-Path $script:ArtifactDir 'golden-summary.json')
    Write-Step 'golden baseline'
    $summary | ForEach-Object { Write-Comparison $_ }
}
exit $exitCode
