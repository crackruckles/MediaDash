# Regenerates the PlayabilityFixer repair-ladder test fixtures.
# Each fixture is broken (or intentionally healthy, for negative controls) in a specific way.
# See fixture-manifest.json for the pre/post expectations validate-*.ps1 rely on.

param(
    [string]$Ffmpeg = '',
    [string[]]$Only = @()   # e.g. -Only rung3-hevc-in-avi.avi
)

$ErrorActionPreference = 'Stop'

if (-not $Ffmpeg) {
    $jellyfinFf = "$env:USERPROFILE\Downloads\jellyfin_10.11.11-amd64\jellyfin\ffmpeg.exe"
    if (Test-Path $jellyfinFf) { $Ffmpeg = $jellyfinFf } else { $Ffmpeg = 'ffmpeg' }
}
Write-Host "Using ffmpeg: $Ffmpeg"

$fixtures = Join-Path $PSScriptRoot 'fixtures'
$tmp = Join-Path $PSScriptRoot '.tmp'
New-Item -ItemType Directory -Path $fixtures -Force | Out-Null
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

function Should-Build($name) {
    if ($Only.Count -eq 0) { return $true }
    return $Only -contains $name
}

function Xor-Middle($path, [double]$fractionStart, [double]$fractionLen, $seed) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $start = [int]($bytes.Length * $fractionStart)
    $len = [int]($bytes.Length * $fractionLen)
    $r = New-Object System.Random $seed
    for ($i = 0; $i -lt $len; $i++) { $bytes[$start + $i] = $bytes[$start + $i] -bxor $r.Next(1, 255) }
    [System.IO.File]::WriteAllBytes($path, $bytes)
}

function Truncate-Tail($path, [int]$bytes) {
    $fs = [System.IO.File]::OpenWrite($path)
    try { $fs.SetLength([Math]::Max(0, $fs.Length - $bytes)) } finally { $fs.Close() }
}

# Known-good source: 30s of testsrc + tone. Small, deterministic, plays everywhere.
$good = Join-Path $tmp 'good.mkv'
& $Ffmpeg -y -hide_banner -loglevel error `
    -f lavfi -i 'testsrc=size=320x240:rate=15:duration=30' `
    -f lavfi -i 'sine=frequency=1000:duration=30' `
    -c:v libx264 -preset ultrafast -c:a aac -shortest $good
if ($LASTEXITCODE -ne 0) { throw "Failed to synthesize good source." }

# Longer good source (for cases where 30s of damage isn't enough).
$goodLong = Join-Path $tmp 'good-long.mkv'
& $Ffmpeg -y -hide_banner -loglevel error `
    -f lavfi -i 'testsrc=size=320x240:rate=15:duration=60' `
    -f lavfi -i 'sine=frequency=1000:duration=60' `
    -c:v libx264 -preset ultrafast -c:a aac -shortest $goodLong
if ($LASTEXITCODE -ne 0) { throw "Failed to synthesize long source." }

# --- Rung 1: quick remux (container damage, no re-encode needed) ---

$f = Join-Path $fixtures 'rung1-bad-index.mkv'
if (Should-Build (Split-Path $f -Leaf)) {
    # Zero out the last cluster region (rough position: 88..93% of file). Kills a real block
    # of data but the container structure recovers via genpts. Only rung 1's -err_detect
    # ignore_err copies through this cleanly.
    Copy-Item $good $f -Force
    $bytes = [System.IO.File]::ReadAllBytes($f)
    $start = [int]($bytes.Length * 0.88)
    $len = [int]($bytes.Length * 0.05)
    for ($i = 0; $i -lt $len; $i++) { $bytes[$start + $i] = 0 }
    [System.IO.File]::WriteAllBytes($f, $bytes)
    Write-Host "  $f  (last-cluster zeros - remux with genpts fixes)"
}

$f = Join-Path $fixtures 'rung1-truncated-mp4.mp4'
if (Should-Build (Split-Path $f -Leaf)) {
    $tmpMp4 = Join-Path $tmp 'good.mp4'
    & $Ffmpeg -y -hide_banner -loglevel error -i $good -c copy -movflags '+faststart' $tmpMp4 | Out-Null
    Copy-Item $tmpMp4 $f -Force
    Truncate-Tail $f 8192
    Write-Host "  $f  (MP4 tail truncated, moov still present via faststart)"
}

$f = Join-Path $fixtures 'rung1-noaudio-header.mkv'
if (Should-Build (Split-Path $f -Leaf)) {
    # Corrupt the SeekHead/Info EBML elements early in the file. MKV magic (0..12) stays
    # intact so demuxer opens it, but seek metadata is scrambled - rung 1's genpts rebuilds it.
    Copy-Item $good $f -Force
    $bytes = [System.IO.File]::ReadAllBytes($f)
    $r = New-Object System.Random 27
    for ($i = 24; $i -lt 800; $i++) { $bytes[$i] = [byte]$r.Next(0, 255) }
    [System.IO.File]::WriteAllBytes($f, $bytes)
    Write-Host "  $f  (SeekHead/Info scrambled - genpts rewrite recovers)"
}

# --- Rung 2: drop broken streams (video/audio survive, one track fails decode) ---

$f = Join-Path $fixtures 'rung2-broken-sub.mkv'
if (Should-Build (Split-Path $f -Leaf)) {
    # Build a valid ASS sub, then XOR-scramble its embedded bytes inside the container after mux.
    # ffmpeg's ASS decoder chokes on malformed dialogue lines under -err_detect explode.
    $goodSub = Join-Path $tmp 'good.ass'
    $ass = @"
[Script Info]
Title: t
ScriptType: v4.00+

[V4+ Styles]
Format: Name, Fontname, Fontsize
Style: D,Arial,20

[Events]
Format: Layer, Start, End, Style, Text
Dialogue: 0,0:00:00.00,0:00:30.00,D,hello world
"@
    $ass | Out-File $goodSub -Encoding utf8 -NoNewline
    & $Ffmpeg -y -hide_banner -loglevel error `
        -i $good -i $goodSub `
        -map 0:v -map 0:a -map 1:s `
        -c copy -c:s ass $f
    if ($LASTEXITCODE -ne 0) { throw "Failed rung2-broken-sub.mkv" }
    # XOR the sub payload region (roughly the last 2% of the mux is trailing sub packets).
    Xor-Middle $f 0.97 0.02 42
    Write-Host "  $f  (video + audio + ASS sub, sub bytes scrambled)"
}

$f = Join-Path $fixtures 'rung2-broken-second-audio.mkv'
if (Should-Build (Split-Path $f -Leaf)) {
    # Two audio tracks, one is an AAC stream we corrupt post-mux.
    $twoAudio = Join-Path $tmp 'two-audio.mkv'
    & $Ffmpeg -y -hide_banner -loglevel error `
        -f lavfi -i 'testsrc=size=320x240:rate=15:duration=30' `
        -f lavfi -i 'sine=frequency=1000:duration=30' `
        -f lavfi -i 'sine=frequency=2000:duration=30' `
        -map 0:v -map 1:a -map 2:a `
        -c:v libx264 -preset ultrafast -c:a aac -shortest $twoAudio
    if ($LASTEXITCODE -ne 0) { throw "Failed two-audio build" }
    Copy-Item $twoAudio $f -Force
    Xor-Middle $f 0.40 0.25 91  # 25% chunk - broad enough to force per-stream decode failure
    Write-Host "  $f  (video + 2 audio tracks; broad mid XOR to break decode)"
}

# --- Rung 3: container coerce to MKV (source container is broken/unfixable) ---

$f = Join-Path $fixtures 'rung3-hevc-in-avi.avi'
if (Should-Build (Split-Path $f -Leaf)) {
    & $Ffmpeg -y -hide_banner -loglevel error `
        -f lavfi -i 'testsrc=size=320x240:rate=15:duration=15' `
        -f lavfi -i 'sine=frequency=1000:duration=15' `
        -c:v libx265 -preset ultrafast -x265-params 'log-level=error' `
        -c:a mp3 -shortest `
        -f avi $f
    if ($LASTEXITCODE -ne 0) { throw "Failed rung3-hevc-in-avi.avi" }
    Write-Host "  $f  (HEVC muxed into AVI - spec-noncompliant)"
}

$f = Join-Path $fixtures 'rung3-flv-with-junk.flv'
if (Should-Build (Split-Path $f -Leaf)) {
    # H264 in FLV, then scramble both a mid-file chunk AND some header bytes so the FLV demuxer
    # errors under -err_detect explode. MKV coerce with -err_detect ignore_err survives it.
    & $Ffmpeg -y -hide_banner -loglevel error `
        -i $good -c:v copy -c:a copy -f flv $f
    if ($LASTEXITCODE -ne 0) { throw "Failed rung3-flv-with-junk.flv" }
    $bytes = [System.IO.File]::ReadAllBytes($f)
    $r = New-Object System.Random 55
    # Scramble the FLV header script tag area (roughly bytes 13..1000). Real header magic (0..12)
    # stays intact so ffmpeg still opens it as FLV — just enough to make packet parsing angry.
    for ($i = 13; $i -lt 1024; $i++) { $bytes[$i] = [byte]$r.Next(0, 255) }
    [System.IO.File]::WriteAllBytes($f, $bytes)
    Write-Host "  $f  (H264 in FLV, script/metadata tag scrambled)"
}

# --- Rung 4: re-encode (bitstream damage that survives remux) ---

$f = Join-Path $fixtures 'rung4-bitstream-damage.mkv'
if (Should-Build (Split-Path $f -Leaf)) {
    Copy-Item $goodLong $f -Force
    Xor-Middle $f 0.4 0.05 7
    Write-Host "  $f  (5% mid-file XOR - remux copies past the damage)"
}

$f = Join-Path $fixtures 'rung4-heavy-noise.mkv'
if (Should-Build (Split-Path $f -Leaf)) {
    Copy-Item $goodLong $f -Force
    Xor-Middle $f 0.25 0.20 13    # 20% chunk - much more aggressive
    Write-Host "  $f  (20% mid-file XOR - remux definitely fails verify)"
}

# --- Negative control: healthy file must NOT be flagged ---

$f = Join-Path $fixtures 'neg-healthy.mkv'
if (Should-Build (Split-Path $f -Leaf)) {
    Copy-Item $good $f -Force
    Write-Host "  $f  (healthy - Playability scanner MUST NOT flag)"
}

# Manifest - validate-*.ps1 read this to know pre/post expectations.
$manifest = @{
    fixtures = @(
        @{ name = 'rung1-bad-index.mkv';           preBroken = $true;  targetRung = 1; postExists = 'rung1-bad-index.mkv' }
        @{ name = 'rung1-truncated-mp4.mp4';       preBroken = $true;  targetRung = 1; postExists = 'rung1-truncated-mp4.mp4' }
        @{ name = 'rung1-noaudio-header.mkv';      preBroken = $true;  targetRung = 1; postExists = 'rung1-noaudio-header.mkv' }
        @{ name = 'rung2-broken-sub.mkv';          preBroken = $true;  targetRung = 2; postExists = 'rung2-broken-sub.mkv' }
        @{ name = 'rung2-broken-second-audio.mkv'; preBroken = $true;  targetRung = 2; postExists = 'rung2-broken-second-audio.mkv' }
        @{ name = 'rung3-hevc-in-avi.avi';         preBroken = $true;  targetRung = 3; postExists = 'rung3-hevc-in-avi.mkv' }
        @{ name = 'rung3-flv-with-junk.flv';       preBroken = $true;  targetRung = 3; postExists = 'rung3-flv-with-junk.mkv' }
        @{ name = 'rung4-bitstream-damage.mkv';    preBroken = $true;  targetRung = 4; postExists = 'rung4-bitstream-damage.mkv' }
        @{ name = 'rung4-heavy-noise.mkv';         preBroken = $true;  targetRung = 4; postExists = 'rung4-heavy-noise.mkv' }
        @{ name = 'neg-healthy.mkv';               preBroken = $false; targetRung = 0; postExists = 'neg-healthy.mkv' }
    )
}
$manifest | ConvertTo-Json -Depth 5 | Out-File (Join-Path $PSScriptRoot 'fixture-manifest.json') -Encoding utf8

Remove-Item $tmp -Recurse -Force
Write-Host "`nDone. Fixtures at:`n  $fixtures`nManifest at:`n  $(Join-Path $PSScriptRoot 'fixture-manifest.json')"
