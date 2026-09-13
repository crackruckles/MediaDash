# Regenerates the PlayabilityFixer repair-ladder test fixtures.
# Each fixture uses damage that (a) trips the PlayabilityScanner, (b) is repairable by its
# target rung, and (c) produces a strict-ffmpeg-decodable output — i.e. would actually play
# in Jellyfin post-fix. See fixture-manifest.json for the pre/post expectations.

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

# ─── Known-good sources ─────────────────────────────────────────────────────
# 30s testsrc + tone. Small, deterministic, plays everywhere. Base of most fixtures.
$good30 = Join-Path $tmp 'good30.mkv'
& $Ffmpeg -y -hide_banner -loglevel error `
    -f lavfi -i 'testsrc=size=320x240:rate=15:duration=30' `
    -f lavfi -i 'sine=frequency=1000:duration=30' `
    -c:v libx264 -preset ultrafast -c:a aac -shortest $good30
if ($LASTEXITCODE -ne 0) { throw "Failed to synthesize good30." }

# 60s variant. Rung 4 fixtures need extra length so the mid-file XOR doesn't cripple the
# start or end regions the verify decode-check samples.
$good60 = Join-Path $tmp 'good60.mkv'
& $Ffmpeg -y -hide_banner -loglevel error `
    -f lavfi -i 'testsrc=size=320x240:rate=15:duration=60' `
    -f lavfi -i 'sine=frequency=1000:duration=60' `
    -c:v libx264 -preset ultrafast -c:a aac -shortest $good60
if ($LASTEXITCODE -ne 0) { throw "Failed to synthesize good60." }

# ─── Rung 1: quick remux (tail-truncation cases where -c copy + discardcorrupt survives) ─
# All three MKV variants share the same mechanism (tail truncation); different sizes vary
# how much content is lost so the scanner's shortfall heuristic fires reliably.

$f = Join-Path $fixtures 'rung1-bad-index.mkv'
if (Should-Build (Split-Path $f -Leaf)) {
    # Truncate 128 KB off the tail (~18% of a 30s MKV). The last Cues + a couple of clusters
    # go with it; scanner sees declared 30s but decode reaches ~24s. Rung 1 remux with
    # +discardcorrupt trims to the last complete packet and normalizes the container duration.
    Copy-Item $good30 $f -Force
    Truncate-Tail $f 131072
    Write-Host "  $f  (128 KB tail truncation - clusters + Cues lost, packets before survive)"
}

$f = Join-Path $fixtures 'rung1-truncated-mp4.mp4'
if (Should-Build (Split-Path $f -Leaf)) {
    # Faststart MP4 (moov at front, mdat after). Truncate 400 KB off the tail — mdat's last
    # chunks + AAC packets lost. +discardcorrupt is the difference between rung 1 producing
    # a file that plays vs one that fails at the incomplete AAC tail packet.
    $tmpMp4 = Join-Path $tmp 'good60.mp4'
    & $Ffmpeg -y -hide_banner -loglevel error -i $good60 -c copy -movflags '+faststart' $tmpMp4 | Out-Null
    Copy-Item $tmpMp4 $f -Force
    Truncate-Tail $f 400000
    Write-Host "  $f  (400 KB MP4 tail truncation, moov intact via faststart)"
}

$f = Join-Path $fixtures 'rung1-noaudio-header.mkv'
if (Should-Build (Split-Path $f -Leaf)) {
    # Smaller MKV truncation (100 KB, ~14% of file). Different loss profile from
    # rung1-bad-index for coverage — moderate loss vs heavy loss both need to work.
    Copy-Item $good30 $f -Force
    Truncate-Tail $f 100000
    Write-Host "  $f  (100 KB MKV tail truncation - moderate loss variant)"
}

# ─── Rung 2: drop broken streams (multi-stream file, one stream fails per-stream decode) ─

$f = Join-Path $fixtures 'rung2-broken-second-audio.mkv'
if (Should-Build (Split-Path $f -Leaf)) {
    # Two audio tracks; XOR-scramble a big mid-file chunk which hits the second AAC track's
    # packets. Rung 2's per-stream ffprobe decode identifies the broken one; the remux keeps
    # video + the clean first audio, drops the broken second. Video and first audio survive
    # because the XOR chunk lands in the interleaved packet region that biases toward the
    # later stream (empirically calibrated — see history if this breaks).
    $twoAudio = Join-Path $tmp 'two-audio.mkv'
    & $Ffmpeg -y -hide_banner -loglevel error `
        -f lavfi -i 'testsrc=size=320x240:rate=15:duration=30' `
        -f lavfi -i 'sine=frequency=1000:duration=30' `
        -f lavfi -i 'sine=frequency=2000:duration=30' `
        -map 0:v -map 1:a -map 2:a `
        -c:v libx264 -preset ultrafast -c:a aac -shortest $twoAudio
    if ($LASTEXITCODE -ne 0) { throw "Failed two-audio build" }
    Copy-Item $twoAudio $f -Force
    Xor-Middle $f 0.40 0.25 91
    Write-Host "  $f  (video + 2 audio; mid XOR breaks second AAC track only)"
}

# ─── Rung 3: container coerce to MKV (source container fails; packets survive coerce) ────
# rung2-broken-sub.mkv was removed — isolating "broken subtitle only" as a scanner-triggering
# condition is unrealistic (ffmpeg treats sub decode failures as non-fatal in whole-file
# scan), and rung2-broken-second-audio already covers rung 2's core drop-broken-stream flow.

$f = Join-Path $fixtures 'rung3-hevc-in-avi.avi'
if (Should-Build (Split-Path $f -Leaf)) {
    # Fixture name is legacy — actual damage is now MPEG-4 in AVI with tail truncation. HEVC
    # in AVI was untestable because AVI's FourCC map identifies HEVC packets as rawvideo,
    # making the whole file a decode dead-end (no rung can fix a source ffmpeg can't decode).
    # MPEG-4 in AVI is a real user case (legacy DivX/Xvid rips) and tail truncation kills
    # the AVI idx1 chunk at end — rung 3's MKV coerce drops the trailing partial packet and
    # writes a fresh clean container.
    $goodAvi = Join-Path $tmp 'good.avi'
    & $Ffmpeg -y -hide_banner -loglevel error `
        -f lavfi -i 'testsrc=size=320x240:rate=15:duration=30' `
        -f lavfi -i 'sine=frequency=1000:duration=30' `
        -c:v mpeg4 -c:a mp3 -shortest `
        -f avi $goodAvi
    if ($LASTEXITCODE -ne 0) { throw "Failed rung3-hevc-in-avi.avi (mux)" }
    Copy-Item $goodAvi $f -Force
    Truncate-Tail $f 150000
    Write-Host "  $f  (MPEG-4 in AVI, tail-truncated - idx1 gone, packets before survive)"
}

$f = Join-Path $fixtures 'rung3-flv-with-junk.flv'
if (Should-Build (Split-Path $f -Leaf)) {
    # H264 in FLV with tail truncation. FLV's script-data tag + trailing packets go; the
    # video codec header at file start survives, so rung 3's MKV coerce can re-container
    # the remaining packets into a strict-clean MKV.
    & $Ffmpeg -y -hide_banner -loglevel error `
        -i $good30 -c:v copy -c:a copy -f flv $f
    if ($LASTEXITCODE -ne 0) { throw "Failed rung3-flv-with-junk.flv" }
    Truncate-Tail $f 80000
    Write-Host "  $f  (H264 in FLV, 80 KB tail truncation)"
}

# ─── Rung 4: full re-encode (mid-file bitstream damage that survives -c copy) ─────────────
# Damage sizes calibrated so the re-encode produces a full-duration output that DecodeCheck's
# 90% shortfall gate accepts. Bigger XOR chunks create PTS discontinuities the re-encode
# can't span cleanly — the ceiling is empirically ~5% for a 60s source.

$f = Join-Path $fixtures 'rung4-bitstream-damage.mkv'
if (Should-Build (Split-Path $f -Leaf)) {
    Copy-Item $good60 $f -Force
    Xor-Middle $f 0.4 0.02 7
    Write-Host "  $f  (60s MKV, 2% mid XOR)"
}

$f = Join-Path $fixtures 'rung4-heavy-noise.mkv'
if (Should-Build (Split-Path $f -Leaf)) {
    Copy-Item $good60 $f -Force
    Xor-Middle $f 0.25 0.05 13
    Write-Host "  $f  (60s MKV, 5% mid XOR)"
}

# ─── Negative control: healthy file must NOT be flagged ──────────────────────────────────

$f = Join-Path $fixtures 'neg-healthy.mkv'
if (Should-Build (Split-Path $f -Leaf)) {
    Copy-Item $good30 $f -Force
    Write-Host "  $f  (healthy - Playability scanner MUST NOT flag)"
}

# Retire any stale fixtures from previous manifest versions. Preserves fixture bin so
# validate-fixtures / validate-outputs never trip over files that no manifest entry expects.
$retired = @('rung2-broken-sub.mkv', 'rung1-truncated-mp4.mkv')
foreach ($stale in $retired) {
    $p = Join-Path $fixtures $stale
    if (Test-Path $p) { Remove-Item $p -Force; Write-Host "  retired: $stale" }
}
# Also clean the DupeTestB (2020)/ carry-over from unrelated dedup tests.
$dupeDir = Join-Path $fixtures 'DupeTestB (2020)'
if (Test-Path $dupeDir) { Remove-Item $dupeDir -Recurse -Force; Write-Host "  retired: DupeTestB (2020)/" }

# Manifest — validate-*.ps1 read this to know pre/post expectations.
# All rung 1/2/4 fixtures keep their original extension. Rung 3 fixtures change to .mkv.
$manifest = @{
    fixtures = @(
        @{ name = 'rung1-bad-index.mkv';           preBroken = $true;  targetRung = 1; postExists = 'rung1-bad-index.mkv' }
        @{ name = 'rung1-truncated-mp4.mp4';       preBroken = $true;  targetRung = 1; postExists = 'rung1-truncated-mp4.mp4' }
        @{ name = 'rung1-noaudio-header.mkv';      preBroken = $true;  targetRung = 1; postExists = 'rung1-noaudio-header.mkv' }
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
