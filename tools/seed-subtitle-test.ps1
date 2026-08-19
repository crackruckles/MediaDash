# Throwaway: seed a test MP4 + two .ass sidecars with unreferenced embedded fonts into the local
# Jellyfin dev library, then trigger a library refresh + MediaDash scan. Run manually or by
# right-clicking → Run with PowerShell. Idempotent — overwrites the same files each time.
param(
    [string]$LibraryDir = 'C:\Users\crackruckles\Downloads\jellyfin_12-amd64\jellyfin\New folder',
    [string]$Ffmpeg    = 'C:\Users\crackruckles\Downloads\jellyfin_12-amd64\jellyfin\ffmpeg.exe',
    [string]$JellyfinUrl = 'http://localhost:8096',
    [string]$User      = 'test',
    [string]$Pass      = 'test'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $LibraryDir)) { New-Item -ItemType Directory -Path $LibraryDir -Force | Out-Null }

$videoBase = 'MediaDash Font Test'
$videoPath = Join-Path $LibraryDir "$videoBase.mp4"
$assMulti  = Join-Path $LibraryDir "$videoBase.en.ass"
$assSimple = Join-Path $LibraryDir "$videoBase.jp.ass"

# --- 1. Video: 5 seconds of colour bars + 440Hz tone. Tiny, portable, always renders.
# ffmpeg writes progress/log to stderr; in PS 5.1 with $ErrorActionPreference=Stop that trips
# NativeCommandError. Suspend Stop just for this call and let the exit code speak for itself.
Write-Host "1) Generating $videoPath..."
$prev = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& $Ffmpeg -y -hide_banner -loglevel error `
          -f lavfi -i 'testsrc=duration=5:size=320x180:rate=15' `
          -f lavfi -i 'sine=frequency=440:duration=5' `
          -c:v libx264 -preset ultrafast -pix_fmt yuv420p `
          -c:a aac -shortest $videoPath
$ErrorActionPreference = $prev
if (-not (Test-Path $videoPath)) { throw "ffmpeg didn't produce $videoPath" }

# --- 2. ASS content builder
# Fills each embedded-font block with ~30KB of harmless ASCII lines shaped like real UUEncoded
# blocks (libass will fail to decode → falls back to system fonts, which is fine for testing).
function New-FontBlock {
    param([string]$Name, [int]$LineCount = 400)
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("fontname: $Name")
    for ($i = 0; $i -lt $LineCount; $i++) {
        [void]$sb.AppendLine('!' * 80)
    }
    $sb.ToString()
}

function New-AssFile {
    param(
        [string[]]$Styles,           # e.g. @('Default,Arial', 'Sign,Verdana')
        [string[]]$InlineOverrides,  # e.g. @('Roboto')
        [string[]]$EmbeddedFonts     # e.g. @('Arial_R0.ttf', 'RandomBloat1_R0.ttf', ...)
    )
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('[Script Info]')
    [void]$sb.AppendLine('Title: MediaDash SubtitleFonts test fixture')
    [void]$sb.AppendLine('ScriptType: v4.00+')
    [void]$sb.AppendLine('PlayResX: 1280')
    [void]$sb.AppendLine('PlayResY: 720')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('[V4+ Styles]')
    [void]$sb.AppendLine('Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding')
    foreach ($s in $Styles) {
        $parts = $s -split ','
        [void]$sb.AppendLine("Style: $($parts[0]),$($parts[1]),40,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1")
    }
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('[Events]')
    [void]$sb.AppendLine('Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text')
    for ($i = 0; $i -lt $Styles.Count; $i++) {
        $styleName = ($Styles[$i] -split ',')[0]
        [void]$sb.AppendLine("Dialogue: 0,0:00:0$($i).00,0:00:0$($i+1).00,$styleName,,0,0,0,,Line $i using $styleName")
    }
    foreach ($ov in $InlineOverrides) {
        [void]$sb.AppendLine("Dialogue: 0,0:00:04.00,0:00:05.00,Default,,0,0,0,,{\fn$ov}Override to $ov")
    }
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('[Fonts]')
    foreach ($f in $EmbeddedFonts) {
        [void]$sb.Append((New-FontBlock -Name $f))
    }
    $sb.ToString()
}

# 2a. Multi-track scenario: Default + Sign styles, one \fn override, 6 embedded fonts (3 unused)
Write-Host "2a) Writing $assMulti (3 referenced fonts, 3 unused)..."
$multi = New-AssFile `
    -Styles @('Default,Arial', 'Sign,Verdana') `
    -InlineOverrides @('Roboto') `
    -EmbeddedFonts @(
        'Arial-Regular_R0.ttf',
        'Verdana-Regular_R0.ttf',
        'Roboto-Regular_R0.ttf',
        'FanmadeKaraoke1_R0.ttf',
        'RandomBloat2_R0.ttf',
        'UnusedSignFont_R0.ttf'
    )
$multi | Out-File -FilePath $assMulti -Encoding UTF8 -NoNewline
"    $(([System.IO.FileInfo]$assMulti).Length) bytes"

# 2b. Aggressive-bloat scenario: single style, 5 embedded fonts, 4 unused
Write-Host "2b) Writing $assSimple (1 referenced font, 4 unused)..."
$simple = New-AssFile `
    -Styles @('Default,Arial') `
    -InlineOverrides @() `
    -EmbeddedFonts @(
        'Arial-Regular_R0.ttf',
        'MyFavouriteFansubFont1_R0.ttf',
        'MyFavouriteFansubFont2_R0.ttf',
        'PointlessDecorativeFont_R0.ttf',
        'AnotherOne_R0.ttf'
    )
$simple | Out-File -FilePath $assSimple -Encoding UTF8 -NoNewline
"    $(([System.IO.FileInfo]$assSimple).Length) bytes"

# --- 3. Auth to Jellyfin
Write-Host '3) Authenticating to Jellyfin...'
$authHeaders = @{
    'Authorization' = 'MediaBrowser Client="mediadash-seed", Device="ps", DeviceId="mediadash-seed-01", Version="0.0.0"'
    'Content-Type'  = 'application/json'
}
$authBody = @{ Username = $User; Pw = $Pass } | ConvertTo-Json
$auth = Invoke-RestMethod -Method POST -Uri "$JellyfinUrl/Users/AuthenticateByName" `
                          -Headers $authHeaders -Body $authBody
$token = $auth.AccessToken
"    got token, ends in $($token.Substring($token.Length - 6))"

$authHeaders['Authorization'] = $authHeaders['Authorization'] + ", Token=$token"

# --- 4. Force library refresh so Jellyfin picks up the new video
Write-Host '4) Triggering library refresh...'
Invoke-RestMethod -Method POST -Uri "$JellyfinUrl/Library/Refresh" -Headers $authHeaders | Out-Null

# Poll for a couple seconds — the video is 5 seconds long, refresh over 3 files should be instant.
Write-Host '5) Waiting for Jellyfin to see the new video...'
$found = $false
for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Seconds 1
    $items = Invoke-RestMethod -Method GET -Uri "$JellyfinUrl/Items?SearchTerm=MediaDash&Recursive=true&IncludeItemTypes=Movie,Video" -Headers $authHeaders
    if ($items.Items -and ($items.Items | Where-Object { $_.Name -like 'MediaDash Font Test*' })) {
        $found = $true; "    found after ~$($i+1)s"; break
    }
}
if (-not $found) { Write-Warning 'Video not visible via API yet; MediaDash scan may still pick it up if you wait a few more seconds.' }

# --- 6. Kick MediaDash scan directly (bypasses idle check)
Write-Host '6) Triggering MediaDash scan...'
Invoke-RestMethod -Method POST -Uri "$JellyfinUrl/MediaDash/Scan" -Headers $authHeaders | Out-Null
Start-Sleep -Seconds 4

# --- 7. Report what MediaDash sees
Write-Host '7) SubtitleFonts issues after scan:'
$issues = Invoke-RestMethod -Method GET -Uri "$JellyfinUrl/MediaDash/Issues" -Headers $authHeaders
$sub = $issues | Where-Object { $_.Type -eq 'SubtitleFonts' }
if ($sub) {
    $sub | Select-Object Type,Path,SizeSavings,SuggestedFix | Format-List
} else {
    Write-Warning 'No SubtitleFonts issues yet. Give it another 10 seconds and re-run: curl http://localhost:8096/MediaDash/Issues'
}

Write-Host ''
Write-Host 'Ready. Open http://localhost:8096/web/#/dashboard/plugins/configurationpage?name=MediaDash then look at the Issues tab.'
