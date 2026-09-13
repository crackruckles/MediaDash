# Real-file torture test: take a Big Buck Bunny source and break it every common way
# a real user file breaks (interrupted downloads, bit-rot, corrupt containers, wrong
# extensions), then run the whole MediaDash Playability repair ladder against each
# variant end-to-end through a live Jellyfin and report the outcome matrix.
#
# Prerequisites:
#   - Local Jellyfin v10 running at localhost:8099 with admin user test/test
#   - MediaDash plugin deployed (deploy-local.ps1)
#   - BBB source at torture/bbb-source.mp4 (downloaded on first run if missing)
#
# Exit code: 0 = every variant handled correctly (repaired-and-plays OR safely-recycled),
# 1 = at least one variant produced a broken output or unexpected outcome.

param(
    [string]$Ffmpeg = '',
    [string]$JellyfinUrl = 'http://localhost:8099',
    [string]$AdminUser = 'test',
    [string]$AdminPass = 'test'
)

$ErrorActionPreference = 'Stop'
if (-not $Ffmpeg) {
    $j = "$env:USERPROFILE\Downloads\jellyfin_10.11.11-amd64\jellyfin\ffmpeg.exe"
    if (Test-Path $j) { $Ffmpeg = $j } else { $Ffmpeg = 'ffmpeg' }
}
$Ffprobe = $Ffmpeg -replace 'ffmpeg\.exe$','ffprobe.exe'

$torture = Join-Path $PSScriptRoot 'torture'
$variants = Join-Path $torture 'variants'
$src = Join-Path $torture 'bbb-source.mp4'
if (-not (Test-Path $src)) {
    Write-Host "Downloading BBB source..."
    New-Item -ItemType Directory $torture -Force | Out-Null
    Invoke-WebRequest 'https://test-videos.co.uk/vids/bigbuckbunny/mp4/h264/720/Big_Buck_Bunny_720_10s_5MB.mp4' -OutFile $src
}

Remove-Item $variants -Recurse -Force -EA 0
New-Item -ItemType Directory $variants -Force | Out-Null

# ─── Damage recipes ─────────────────────────────────────────────────────────
# Each recipe writes a broken variant next to $src. Function receives ($out) — the target
# path — and produces the damaged file. Naming convention: NN-<kind>.<ext>. Extension is
# preserved from the recipe so scanner + fixer see realistic extensions.

function Xor-Region($path, [double]$fs, [double]$fl, $seed) {
    $b = [System.IO.File]::ReadAllBytes($path)
    $s = [int]($b.Length * $fs); $l = [int]($b.Length * $fl)
    $r = New-Object System.Random $seed
    for ($i = 0; $i -lt $l; $i++) { $b[$s + $i] = $b[$s + $i] -bxor $r.Next(1, 255) }
    [System.IO.File]::WriteAllBytes($path, $b)
}
function Truncate-Tail($p, $n) {
    $f = [System.IO.File]::OpenWrite($p); $f.SetLength([Math]::Max(0, $f.Length - $n)); $f.Close()
}
function Truncate-Head($p, $n) {
    $b = [System.IO.File]::ReadAllBytes($p)
    if ($n -ge $b.Length) { [System.IO.File]::WriteAllBytes($p, @()); return }
    [System.IO.File]::WriteAllBytes($p, $b[$n..($b.Length - 1)])
}
function Zero-Region($p, [double]$fs, [double]$fl) {
    $b = [System.IO.File]::ReadAllBytes($p)
    $s = [int]($b.Length * $fs); $l = [int]($b.Length * $fl)
    for ($i = 0; $i -lt $l; $i++) { $b[$s + $i] = 0 }
    [System.IO.File]::WriteAllBytes($p, $b)
}
function Ff($argsStr) { & cmd /c """$Ffmpeg"" $argsStr 2>NUL >NUL"; return $LASTEXITCODE }
function StrictDecode($p) {
    if (-not (Test-Path $p)) { return $false }
    & cmd /c """$Ffmpeg"" -v error -xerror -i ""$p"" -f null - 2>NUL >NUL"
    return ($LASTEXITCODE -eq 0)
}
function ScannerBroken($p) {
    # Mirror plugin's DecodeCheckAsync: broken if exit non-zero OR decoded < 90% of expected.
    & cmd /c """$Ffmpeg"" -v error -xerror -err_detect explode -i ""$p"" -f null - 2>NUL >NUL"
    if ($LASTEXITCODE -ne 0) { return $true }
    $d = (& $Ffprobe -v error -show_entries format=duration -of default=nw=1:nk=1 $p).Trim()
    if (-not $d) { return $true }
    $dur = [double]$d; if ($dur -le 0) { return $true }
    $expected = [Math]::Min(30.0, $dur)
    $out = & cmd /c """$Ffmpeg"" -threads 4 -xerror -v error -stats -i ""$p"" -t 30 -f null - 2>&1"
    $m = ($out | Select-String -Pattern 'time=(\d\d):(\d\d):([\d.]+)' | Select-Object -Last 1)
    if (-not $m) { return $true }
    $g = $m.Matches[0].Groups
    $decoded = [int]$g[1].Value * 3600 + [int]$g[2].Value * 60 + [double]$g[3].Value
    return ($decoded -lt $expected * 0.9)
}

$recipes = @(
    @{ n = '01-tail-trunc-tiny.mp4';         desc = 'MP4 tail trunc 10 KB (partial network drop)'; build = { param($o); Copy-Item $src $o; Truncate-Tail $o 10240 } }
    @{ n = '02-tail-trunc-medium.mp4';       desc = 'MP4 tail trunc 500 KB (interrupted download)'; build = { param($o); Copy-Item $src $o; Truncate-Tail $o 512000 } }
    @{ n = '03-tail-trunc-large.mp4';        desc = 'MP4 tail trunc 2 MB (half the file gone)';     build = { param($o); Copy-Item $src $o; Truncate-Tail $o 2097152 } }
    @{ n = '04-remuxed-mkv-tail-trunc.mkv';  desc = 'MKV remux then 500 KB tail trunc';             build = { param($o); Ff "-y -v error -i ""$src"" -c copy ""$o""" | Out-Null; Truncate-Tail $o 512000 } }
    @{ n = '05-remuxed-mkv-noheadtrim.mkv';  desc = 'MKV remux head-truncated 512 bytes (EBML damaged)'; build = { param($o); Ff "-y -v error -i ""$src"" -c copy ""$o""" | Out-Null; Truncate-Head $o 512 } }
    @{ n = '06-mid-xor-tiny.mp4';            desc = 'MP4 mid-file XOR 0.5% (localised bit-rot)';    build = { param($o); Copy-Item $src $o; Xor-Region $o 0.5 0.005 11 } }
    @{ n = '07-mid-xor-small.mp4';           desc = 'MP4 mid-file XOR 2% (moderate bit-rot)';       build = { param($o); Copy-Item $src $o; Xor-Region $o 0.4 0.02 22 } }
    @{ n = '08-mid-xor-large.mp4';           desc = 'MP4 mid-file XOR 8% (heavy bit-rot)';          build = { param($o); Copy-Item $src $o; Xor-Region $o 0.3 0.08 33 } }
    @{ n = '09-mid-zeros.mp4';               desc = 'MP4 mid-file zeroed 2% (bad-sector pattern)';  build = { param($o); Copy-Item $src $o; Zero-Region $o 0.5 0.02 } }
    @{ n = '10-scattered-flips.mp4';         desc = 'MP4 with 100 scattered single-byte flips';     build = { param($o); Copy-Item $src $o; $b = [System.IO.File]::ReadAllBytes($o); $r = New-Object System.Random 7; for ($i = 0; $i -lt 100; $i++) { $p = $r.Next(1024, $b.Length - 1024); $b[$p] = $b[$p] -bxor 0xFF }; [System.IO.File]::WriteAllBytes($o, $b) } }
    @{ n = '11-wrong-ext.mkv';               desc = 'MP4 file renamed to .mkv (extension lies)';    build = { param($o); Copy-Item $src $o } }
    @{ n = '12-only-half-header.mp4';        desc = 'MP4 with only first 4 KB (nearly empty)';      build = { param($o); Copy-Item $src $o; $fs = [System.IO.File]::OpenWrite($o); $fs.SetLength(4096); $fs.Close() } }
    @{ n = '13-container-lied-flv.flv';      desc = 'H264+AAC re-muxed into FLV, then tail-truncated'; build = { param($o); Ff "-y -v error -i ""$src"" -c:v copy -c:a copy -f flv ""$o""" | Out-Null; Truncate-Tail $o 300000 } }
    @{ n = '14-mp4-in-avi-container.avi';    desc = 'Re-encoded into MPEG-4 AVI, tail truncated';   build = { param($o); Ff "-y -v error -i ""$src"" -c:v mpeg4 -c:a mp3 -f avi ""$o""" | Out-Null; Truncate-Tail $o 400000 } }
    @{ n = '15-double-damage.mkv';           desc = 'MKV remux + BOTH mid XOR AND tail truncation'; build = { param($o); Ff "-y -v error -i ""$src"" -c copy ""$o""" | Out-Null; Xor-Region $o 0.4 0.02 99; Truncate-Tail $o 200000 } }
)

Write-Host "`nGenerating $($recipes.Count) damaged variants of Big Buck Bunny..."
$library = @{}
foreach ($r in $recipes) {
    $out = Join-Path $variants $r.n
    & $r.build $out
    $library[$r.n] = @{ recipe = $r; path = $out; scannerBroken = (ScannerBroken $out); origSize = (Get-Item $out).Length }
    Write-Host ("  [{0}] {1}  scanner-flags-broken={2}" -f $r.n, $r.desc, $library[$r.n].scannerBroken)
}

# ─── Wire to live plugin ────────────────────────────────────────────────────
Write-Host "`nAuthenticating..."
$authBody = @{ Username = $AdminUser; Pw = $AdminPass } | ConvertTo-Json -Compress
$authH = @{ Authorization = 'MediaBrowser Client="torture", Device="ps", DeviceId="dt1", Version="1"' }
$auth = Invoke-RestMethod -Method Post -Uri "$JellyfinUrl/Users/AuthenticateByName" -Body $authBody -ContentType application/json -Headers $authH
$hdr = @{ Authorization = "MediaBrowser Token=`"$($auth.AccessToken)`", Client=`"torture`", Device=`"ps`", DeviceId=`"dt1`", Version=`"1`"" }

# Remove any prior TortureTest library, add fresh pointing at variants dir
$existing = Invoke-RestMethod -Uri "$JellyfinUrl/Library/VirtualFolders" -Headers $hdr | Where-Object Name -eq 'TortureTest'
if ($existing) {
    try { Invoke-RestMethod -Method Delete -Uri "$JellyfinUrl/Library/VirtualFolders?name=TortureTest&refreshLibrary=false" -Headers $hdr | Out-Null } catch {}
    Start-Sleep -Seconds 1
}
$q = "name=TortureTest&collectionType=movies&paths=$([Uri]::EscapeDataString($variants))&refreshLibrary=false"
Invoke-RestMethod -Method Post -Uri "$JellyfinUrl/Library/VirtualFolders?$q" -Headers $hdr -Body (@{ LibraryOptions = @{} } | ConvertTo-Json) -ContentType application/json | Out-Null

# Clear prior DB state + recycle bin so we measure this run only
Write-Host "Clearing prior torture DB rows + bin..."
$dbPath = "$env:LOCALAPPDATA\jellyfin-v10\data\mediadash\mediadash.db"
& sqlite3 $dbPath "DELETE FROM restored_paths WHERE path LIKE '%torture%'; DELETE FROM issues WHERE path LIKE '%torture%';"
$bin = "$env:LOCALAPPDATA\jellyfin-v10\data\mediadash\recycle"
Get-ChildItem $bin -Directory -EA 0 | ForEach-Object { Remove-Item $_.FullName -Recurse -Force -EA 0 }

# Ensure Playability = Automatic, DryRun = false, all repair rungs on
$plugins = Invoke-RestMethod -Uri "$JellyfinUrl/Plugins" -Headers $hdr
$plugId = ($plugins | Where-Object Name -eq 'MediaDash').Id.Replace('-','')
$cfg = Invoke-RestMethod -Uri "$JellyfinUrl/Plugins/$plugId/Configuration" -Headers $hdr
$cfg.PlayabilityFixMode = 3
$cfg.DryRun = $false
$cfg.RepairAttemptRemux = $true
$cfg.RepairAttemptDropStreams = $true
$cfg.RepairAttemptContainerCoerce = $true
$cfg.RepairAttemptReencode = $true
$cfg.ThoroughPlayabilityCheck = $true
# Empty EnabledLibraries = scan every library. Prevents the torture library from being
# silently skipped when the plugin config has a fixed enabled-libraries whitelist from
# prior sessions (issue seen: scan reports 0 issues on TortureTest despite 14 broken files).
$cfg.EnabledLibraries = @()
Invoke-RestMethod -Method Post -Uri "$JellyfinUrl/Plugins/$plugId/Configuration" -Headers $hdr -Body ($cfg | ConvertTo-Json -Depth 10) -ContentType application/json | Out-Null

# Jellyfin library refresh
$tasks = Invoke-RestMethod -Uri "$JellyfinUrl/ScheduledTasks" -Headers $hdr
$rl = $tasks | Where-Object Key -eq 'RefreshLibrary' | Select-Object -First 1
Invoke-RestMethod -Method Post -Uri "$JellyfinUrl/ScheduledTasks/Running/$($rl.Id)" -Headers $hdr | Out-Null
$sw = [Diagnostics.Stopwatch]::StartNew()
do { Start-Sleep 2; $t = Invoke-RestMethod -Uri "$JellyfinUrl/ScheduledTasks/$($rl.Id)" -Headers $hdr } while ($t.State -ne 'Idle' -and $sw.Elapsed.TotalSeconds -lt 120)
Write-Host "Jellyfin refresh: $($t.State) in $([int]$sw.Elapsed.TotalSeconds)s"

# Wait until Jellyfin has actually indexed all torture variants — the scheduled task can
# report Idle before the library manager has flushed all item registrations. Poll the item
# list until we see one entry per variant (or timeout).
$expectItems = $recipes.Count
$sw.Restart()
while ($sw.Elapsed.TotalSeconds -lt 60) {
    $items = Invoke-RestMethod -Uri "$JellyfinUrl/Items?Recursive=true&IncludeItemTypes=Movie&UserId=$($auth.User.Id)" -Headers $hdr
    $torturedCount = @($items.Items | Where-Object { $_.Path -like '*torture*variants*' }).Count
    if ($torturedCount -ge $expectItems) { break }
    Start-Sleep -Seconds 2
}
Write-Host "Jellyfin sees $torturedCount / $expectItems torture variants after $([int]$sw.Elapsed.TotalSeconds)s"

# MediaDash scan
Invoke-RestMethod -Method Post -Uri "$JellyfinUrl/MediaDash/Scan" -Headers $hdr | Out-Null
Start-Sleep -Milliseconds 500
$sw.Restart()
do { Start-Sleep 2; $s = Invoke-RestMethod -Uri "$JellyfinUrl/MediaDash/Status" -Headers $hdr } while ($s.IsScanning -and $sw.Elapsed.TotalSeconds -lt 300)
$issues = Invoke-RestMethod -Uri "$JellyfinUrl/MediaDash/Issues?type=Playability" -Headers $hdr
$issueCount = if ($null -eq $issues) { 0 } elseif ($issues -is [array]) { $issues.Count } else { 1 }
Write-Host "MediaDash scan: $([int]$sw.Elapsed.TotalSeconds)s -> $issueCount Playability issues"

# Fix
Invoke-RestMethod -Method Post -Uri "$JellyfinUrl/MediaDash/Fix" -Headers $hdr | Out-Null
Start-Sleep -Milliseconds 800
$fixTaskId = ($tasks | Where-Object Key -eq 'MediaDashFix').Id
$sw.Restart()
$lastState = ''
while ($sw.Elapsed.TotalSeconds -lt 1800) {
    Start-Sleep 5
    $ft = Invoke-RestMethod -Uri "$JellyfinUrl/ScheduledTasks/$fixTaskId" -Headers $hdr
    $s = Invoke-RestMethod -Uri "$JellyfinUrl/MediaDash/Status" -Headers $hdr
    $cur = "  t=$([int]$sw.Elapsed.TotalSeconds)s state=$($ft.State) prog=$([int]$ft.CurrentProgressPercentage) items=$($s.FixItemsProcessed)/$($s.FixItemsTotal)"
    if ($cur -ne $lastState) { Write-Host $cur; $lastState = $cur }
    if ($ft.State -eq 'Idle' -and $sw.Elapsed.TotalSeconds -gt 8) { break }
}
$fixDur = (([DateTime]$ft.LastExecutionResult.EndTimeUtc) - ([DateTime]$ft.LastExecutionResult.StartTimeUtc)).TotalSeconds
Write-Host "Fix run: $([int]$fixDur)s"

# ─── Score every variant ────────────────────────────────────────────────────
$hist = Invoke-RestMethod -Uri "$JellyfinUrl/MediaDash/History?limit=100" -Headers $hdr
$recent = @($hist | Where-Object { $_.Path -like '*torture*' -and (Get-Date $_.FixedAtUtc) -gt (Get-Date).AddMinutes(-10) })
$recycled = @{}
Get-ChildItem $bin -Recurse -File -EA 0 | ForEach-Object { $recycled[$_.Name] = $_.FullName }

$results = @()
foreach ($r in $recipes) {
    $entry = $library[$r.n]
    $baseName = [IO.Path]::GetFileNameWithoutExtension($r.n)
    $originalPath = $entry.path
    $mkvVariant = [IO.Path]::ChangeExtension($originalPath, '.mkv')
    $sameNameExists = Test-Path $originalPath
    $mkvExists = ($mkvVariant -ne $originalPath) -and (Test-Path $mkvVariant)
    $chosen = if ($sameNameExists) { $originalPath } elseif ($mkvExists) { $mkvVariant } else { $null }
    $inBin = $recycled.ContainsKey($r.n)

    $historyRow = $recent | Where-Object { (Split-Path $_.Path -Leaf) -eq $r.n } | Select-Object -First 1
    $action = if ($historyRow) { $historyRow.Action } else { '(no history row)' }

    $outcome = ''; $status = ''
    if (-not $entry.scannerBroken) {
        # Variant did not trip the scanner. Should remain untouched.
        if ($sameNameExists -and -not $inBin) { $outcome = 'untouched (scanner passed)'; $status = 'PASS' }
        else { $outcome = 'scanner did not flag but file moved/gone'; $status = 'FAIL' }
    }
    elseif ($chosen) {
        # Repaired to some playable path
        if (StrictDecode $chosen) { $outcome = "repaired -> $(Split-Path $chosen -Leaf) plays"; $status = 'PASS' }
        else { $outcome = "output $(Split-Path $chosen -Leaf) exists but fails -xerror"; $status = 'FAIL' }
    }
    elseif ($inBin) {
        # No playable output but original safely in bin
        $outcome = "unrepairable, recycled to bin"; $status = 'PASS'
    }
    else {
        $outcome = "vanished (no output, not in bin)"; $status = 'FAIL'
    }

    $results += [pscustomobject]@{
        variant = $r.n
        srcBroken = $entry.scannerBroken
        outcome = $outcome
        action = if ($action.Length -gt 60) { $action.Substring(0, 60) + '...' } else { $action }
        status = $status
    }
}

$results | Format-Table -AutoSize variant, srcBroken, outcome, status
$failed = @($results | Where-Object status -eq 'FAIL')
Write-Host ("`n{0}/{1} variants handled correctly." -f ($results.Count - $failed.Count), $results.Count)
if ($failed.Count -gt 0) {
    Write-Host "`nFailed variants:" -ForegroundColor Red
    $failed | Format-Table -AutoSize variant, outcome, action
    exit 1
}
exit 0
