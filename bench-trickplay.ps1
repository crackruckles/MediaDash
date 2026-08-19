param(
    [Parameter(Mandatory=$true)][string]$In,
    [int[]]$Qualities = @(75, 80, 85, 90)
)

if (-not (Test-Path $In)) { throw "no such file: $In" }

$tools = @{
    cwebp    = { param($q,$i,$o) & cwebp -q $q -sharp_yuv -m 6 $i -o $o 2>$null }
    ffmpeg   = { param($q,$i,$o) & ffmpeg -y -i $i -c:v libwebp -quality $q -preset picture $o 2>$null }
    img2webp = { param($q,$i,$o) & img2webp -sharp_yuv -q $q -mixed $i -o $o 2>$null }
}

$available = $tools.Keys | Where-Object { Get-Command $_ -ErrorAction SilentlyContinue }
$missing   = $tools.Keys | Where-Object { $_ -notin $available }
if ($missing) { Write-Host "skipping (not on PATH): $($missing -join ', ')" -ForegroundColor Yellow }
if (-not $available) { throw "none of cwebp/ffmpeg/img2webp are on PATH" }

$orig = (Get-Item $In).Length
Write-Host "input: $In ($orig bytes)"

$rows = foreach ($q in $Qualities) {
    foreach ($t in $available) {
        $out = Join-Path $env:TEMP "bench_${t}_q${q}.webp"
        Remove-Item $out -ErrorAction SilentlyContinue
        $sw = [Diagnostics.Stopwatch]::StartNew()
        & $tools[$t] $q $In $out
        $sw.Stop()
        if (-not (Test-Path $out)) { continue }
        $size = (Get-Item $out).Length
        [pscustomobject]@{
            q       = $q
            tool    = $t
            bytes   = $size
            saved   = "{0:N1}%" -f ((1 - $size / $orig) * 100)
            ms      = $sw.ElapsedMilliseconds
            preview = $out
        }
    }
}

$rows | Sort-Object q, bytes | Format-Table -AutoSize
Write-Host "`neyeball previews at %TEMP%\bench_*.webp"
