# fixtures.ps1 — augment the base fixtures from tools/make-fixtures.sh with
# additional edge-case files that the base script doesn't cover:
#   • Suspicious file (an .exe next to a movie)
#   • Corrupt NFO
#   • Corrupt / 0-byte artwork
#   • Orphaned sidecar (.srt with no matching video)
#   • Trickplay folder with real JPGs
#   • Subtitle .ass with a font ref
#   • Files with Unicode names
#   • 0-byte file
#   • Long-path file (>240 chars)
#   • 5.1 audio for combined-pass tests
#
# Idempotent — safe to re-run. Creates a scratch fixture root under
# $FixturesRoot\_test-scratch so we can clean up without touching the base
# fixtures (Devil Wears Prada file is inviolate per project rules).

function Backup-BaseFixtures {
    <#
    Snapshot the entire base fixtures directory before the test suite starts
    modifying files. Uses hardlinks where possible (same volume) — zero disk
    cost, survives most fixer operations (fixers use "write to .tmp then
    File.Move overwrite" which deletes the primary; hardlink shadow survives).

    Falls back to file copy on cross-volume or filesystems without hardlink
    support. Skips if backup already exists (idempotent) unless -Force is set.

    Also writes a manifest.json with SHA256 for every file so a post-run
    tamper check can identify what got modified.

    Backup lives at $FixturesRoot + "-backup" by default (e.g.
    C:\dev\mediadash-fixtures-backup). Restore via companion script
    restore-fixtures.ps1.
    #>
    param(
        [Parameter(Mandatory)][string]$FixturesRoot,
        [string]$BackupRoot = ($FixturesRoot.TrimEnd('\','/') + "-backup"),
        [switch]$Force,
        [switch]$SkipManifest
    )

    if (-not (Test-Path -LiteralPath $FixturesRoot)) {
        throw "fixtures root does not exist: $FixturesRoot"
    }

    if ((Test-Path -LiteralPath $BackupRoot) -and -not $Force) {
        Write-Log "Backup already exists at $BackupRoot — skipping (use -Force to refresh)"
        return $BackupRoot
    }

    if ($Force -and (Test-Path -LiteralPath $BackupRoot)) {
        Write-Log "Force refresh — removing existing backup at $BackupRoot"
        Remove-Item -LiteralPath $BackupRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $BackupRoot | Out-Null

    # Hardlinks were tried first for the zero-disk-cost path, but they share the
    # inode with the primary. When the fixer replaces the primary via File.Move
    # on Windows (or when we regenerate via ffmpeg -y O_TRUNC), the shadow's
    # inode mutates too — restoring copies the mutated shadow back over the
    # primary, which is worse than useless. Full copy always. Disk cost is
    # measurable but predictable.
    $useHardlinks = $false
    Write-Log ("Backing up fixtures: {0} -> {1} (method: copy)" -f $FixturesRoot, $BackupRoot)

    $files = Get-ChildItem -LiteralPath $FixturesRoot -Recurse -File -Force
    $bytesBackedUp = 0L
    $manifest = @{
        fixturesRoot = $FixturesRoot
        backupRoot = $BackupRoot
        createdAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        method = if ($useHardlinks) { "hardlink" } else { "copy" }
        files = @()
    }

    foreach ($f in $files) {
        $rel = $f.FullName.Substring($FixturesRoot.Length).TrimStart('\','/')
        $dst = Join-Path $BackupRoot $rel
        $dstDir = Split-Path -Parent $dst
        if (-not (Test-Path -LiteralPath $dstDir)) {
            New-Item -ItemType Directory -Force -Path $dstDir | Out-Null
        }
        $done = $false
        if ($useHardlinks) {
            try {
                New-Item -ItemType HardLink -Path $dst -Target $f.FullName -Force | Out-Null
                $done = $true
            } catch {
                Write-Log "  hardlink failed for $rel — falling back to copy: $($_.Exception.Message)"
            }
        }
        if (-not $done) {
            Copy-Item -LiteralPath $f.FullName -Destination $dst -Force
        }
        $bytesBackedUp += $f.Length

        if (-not $SkipManifest) {
            try {
                $hash = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
            } catch {
                $hash = ""
            }
            $manifest.files += @{ path = $rel; bytes = $f.Length; sha256 = $hash }
        }
    }

    $sizeGb = [Math]::Round($bytesBackedUp / 1GB, 2)
    Write-Log "Backup complete: $($files.Count) files, $sizeGb GB (method: $($manifest.method))"

    if (-not $SkipManifest) {
        $manifestPath = Join-Path $BackupRoot "manifest.json"
        $manifest | ConvertTo-Json -Depth 6 | Set-Content -Path $manifestPath -Encoding utf8
        Write-Log "Manifest written: $manifestPath"
    }

    return $BackupRoot
}

function Test-FixtureIntegrity {
    <#
    Compare current fixtures against manifest.json in the backup. Returns a
    list of files that have changed since backup. Useful at the end of a test
    run to see what the fixers actually modified.
    #>
    param(
        [Parameter(Mandatory)][string]$FixturesRoot,
        [string]$BackupRoot = ($FixturesRoot.TrimEnd('\','/') + "-backup")
    )
    $manifestPath = Join-Path $BackupRoot "manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        Write-Log "No manifest at $manifestPath — cannot check integrity"
        return @()
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $changes = @()
    foreach ($entry in $manifest.files) {
        $current = Join-Path $FixturesRoot $entry.path
        if (-not (Test-Path -LiteralPath $current)) {
            $changes += [PSCustomObject]@{ path = $entry.path; kind = "DELETED"; expectedSha = $entry.sha256; actualSha = "" }
            continue
        }
        try {
            $actual = (Get-FileHash -LiteralPath $current -Algorithm SHA256).Hash
        } catch {
            $actual = "ERROR"
        }
        if ($actual -ne $entry.sha256) {
            $changes += [PSCustomObject]@{ path = $entry.path; kind = "MODIFIED"; expectedSha = $entry.sha256; actualSha = $actual }
        }
    }
    return $changes
}

function New-ScratchFixtureRoot {
    $root = Join-Path $Script:RunState.FixturesRoot "_test-scratch"
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    Write-Log "Scratch fixture root: $root"
    return $root
}

function New-SyntheticVideo {
    param(
        [Parameter(Mandatory)][string]$OutPath,
        [int]$Width = 1280, [int]$Height = 720,
        [int]$DurationSec = 5,
        [string]$AudioLang = "eng",
        [int]$AudioChannels = 2,
        [string]$VideoBitrate = "800k",
        [string]$AudioCodec = "aac"
    )
    $ffmpeg = Get-JellyfinFfmpegPath
    if (-not $ffmpeg) { throw "ffmpeg not found — cannot build fixtures" }
    $dir = Split-Path -Parent $OutPath
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

    $chanLayout = switch ($AudioChannels) { 6 { "5.1" } 8 { "7.1" } default { "stereo" } }
    $argList = @(
        "-y","-v","error",
        "-f","lavfi","-i","testsrc2=duration=${DurationSec}:size=${Width}x${Height}:rate=24",
        "-f","lavfi","-i","sine=frequency=440:duration=${DurationSec}:sample_rate=48000",
        "-map","0:v","-map","1:a",
        "-c:v","libx264","-preset","ultrafast","-b:v",$VideoBitrate,
        "-c:a",$AudioCodec,
        "-metadata:s:a:0","language=$AudioLang",
        $OutPath
    )
    # For channel counts > 2, add a channel layout filter
    if ($AudioChannels -gt 2) {
        # Rewrite args to include -filter:a for channel expansion
        $argList = @(
            "-y","-v","error",
            "-f","lavfi","-i","testsrc2=duration=${DurationSec}:size=${Width}x${Height}:rate=24",
            "-f","lavfi","-i","sine=frequency=440:duration=${DurationSec}:sample_rate=48000",
            "-map","0:v","-map","1:a",
            "-c:v","libx264","-preset","ultrafast","-b:v",$VideoBitrate,
            "-c:a",$AudioCodec,"-ac","$AudioChannels","-channel_layout",$chanLayout,
            "-metadata:s:a:0","language=$AudioLang",
            $OutPath
        )
    }
    & $ffmpeg @argList 2>&1 | Out-Null
    if (-not (Test-Path -LiteralPath $OutPath)) { throw "ffmpeg failed to produce $OutPath" }
}

function New-AllFixtures {
    param([string]$ScratchRoot)

    # 1. Suspicious file — an .exe with an executable header sitting in a movie folder.
    Write-Log "  building: SuspiciousFileScanner fixture"
    $suspDir = Join-Path $ScratchRoot "movies\Suspicious Test (2020)"
    New-Item -ItemType Directory -Force -Path $suspDir | Out-Null
    New-SyntheticVideo -OutPath (Join-Path $suspDir "Suspicious Test (2020).mkv") -DurationSec 3
    # A minimal MZ header — SuspiciousFileScanner looks for this signature
    $mz = [byte[]]@(0x4D,0x5A,0x90,0x00,0x03,0x00,0x00,0x00,0x04,0x00,0x00,0x00,0xFF,0xFF)
    [System.IO.File]::WriteAllBytes((Join-Path $suspDir "installer.exe"), $mz)

    # 2. Corrupt NFO — well-formed XML but with a missing required tag / obvious rot.
    Write-Log "  building: NfoScanner fixture"
    $nfoDir = Join-Path $ScratchRoot "movies\Broken Nfo (2020)"
    New-Item -ItemType Directory -Force -Path $nfoDir | Out-Null
    New-SyntheticVideo -OutPath (Join-Path $nfoDir "Broken Nfo (2020).mkv") -DurationSec 3
    Set-Content -LiteralPath (Join-Path $nfoDir "Broken Nfo (2020).nfo") -Value "<movie><title>" -Encoding utf8

    # 3. Corrupt / 0-byte artwork
    Write-Log "  building: ArtworkScanner fixture"
    $artDir = Join-Path $ScratchRoot "movies\Corrupt Artwork (2020)"
    New-Item -ItemType Directory -Force -Path $artDir | Out-Null
    New-SyntheticVideo -OutPath (Join-Path $artDir "Corrupt Artwork (2020).mkv") -DurationSec 3
    # 0-byte poster.jpg
    New-Item -ItemType File -Force -Path (Join-Path $artDir "poster.jpg") | Out-Null

    # 4. Orphaned sidecar — .srt with no matching video
    Write-Log "  building: OrphanCleanupScanner fixture"
    $orphanDir = Join-Path $ScratchRoot "movies\_orphans"
    New-Item -ItemType Directory -Force -Path $orphanDir | Out-Null
    Set-Content -LiteralPath (Join-Path $orphanDir "ghost.en.srt") -Value "1`n00:00:01,000 --> 00:00:05,000`nOrphan" -Encoding utf8

    # 5. Subtitle .ass with unsafe font ref
    Write-Log "  building: SubtitleFontScanner fixture"
    $assDir = Join-Path $ScratchRoot "movies\Ass Sub Test (2020)"
    New-Item -ItemType Directory -Force -Path $assDir | Out-Null
    New-SyntheticVideo -OutPath (Join-Path $assDir "Ass Sub Test (2020).mkv") -DurationSec 3
    $ass = @"
[Script Info]
ScriptType: v4.00+
[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Impact,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1
[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,Test line
"@
    Set-Content -LiteralPath (Join-Path $assDir "Ass Sub Test (2020).en.ass") -Value $ass -Encoding utf8

    # 6. 5.1 audio movie — needed for future audio scanner + combined-pass tests
    Write-Log "  building: 5.1 audio fixture"
    $fiveOneDir = Join-Path $ScratchRoot "movies\FiveOne Audio (2022)"
    New-Item -ItemType Directory -Force -Path $fiveOneDir | Out-Null
    New-SyntheticVideo -OutPath (Join-Path $fiveOneDir "FiveOne Audio (2022).mkv") -DurationSec 5 -AudioChannels 6 -VideoBitrate "1200k"

    # 7. Trickplay JPG folder — for TrickplayOptimizeScanner
    Write-Log "  building: trickplay JPG fixture"
    $trickDir = Join-Path $ScratchRoot "movies\Trickplay Test (2020)"
    New-Item -ItemType Directory -Force -Path $trickDir | Out-Null
    New-SyntheticVideo -OutPath (Join-Path $trickDir "Trickplay Test (2020).mkv") -DurationSec 3
    # Write a simple .trickplay sibling folder with fake-JPG bytes (real JPG magic)
    $tpFolder = Join-Path $trickDir "Trickplay Test (2020).trickplay"
    New-Item -ItemType Directory -Force -Path $tpFolder | Out-Null
    $jpgMagic = [byte[]]@(0xFF,0xD8,0xFF,0xE0,0x00,0x10,0x4A,0x46,0x49,0x46)
    # Pad to trigger min-size threshold (10 MB default) — write 15 fake ~750 KB JPGs
    $padding = New-Object byte[] 750000
    for ($i = 0; $i -lt 15; $i++) {
        $bytes = $jpgMagic + $padding
        [System.IO.File]::WriteAllBytes((Join-Path $tpFolder "$i.jpg"), $bytes)
    }

    # 8. Unicode filename — Japanese
    Write-Log "  building: unicode filename fixture"
    $uniDir = Join-Path $ScratchRoot "movies\日本語 Test (2023)"
    New-Item -ItemType Directory -Force -Path $uniDir | Out-Null
    New-SyntheticVideo -OutPath (Join-Path $uniDir "日本語 Test (2023).mkv") -DurationSec 3

    # 9. Emoji filename — Windows path handling test
    Write-Log "  building: emoji filename fixture"
    $emojiDir = Join-Path $ScratchRoot "movies\Emoji 🎬 Test (2023)"
    try {
        New-Item -ItemType Directory -Force -Path $emojiDir | Out-Null
        New-SyntheticVideo -OutPath (Join-Path $emojiDir "Emoji Test (2023).mkv") -DurationSec 3
    } catch {
        Write-Log "  emoji fixture skipped: $($_.Exception.Message)"
    }

    # 10. 0-byte file — bare edge case
    Write-Log "  building: 0-byte file fixture"
    $zeroDir = Join-Path $ScratchRoot "movies\Zero Byte (2020)"
    New-Item -ItemType Directory -Force -Path $zeroDir | Out-Null
    New-Item -ItemType File -Force -Path (Join-Path $zeroDir "Zero Byte (2020).mkv") | Out-Null

    Write-Log "Scratch fixtures written to $ScratchRoot"
    return $ScratchRoot
}

function New-DoctorWhoFixture {
    param([string]$ScratchRoot)
    # Reproduces GitHub #43 (hesourman, 2026-08-31): three Doctor Who reboots on
    # disk get collapsed by MediaGrouperScanner into one "Doctor Who" folder
    # with mixed episodes across all Season 01s. Root cause per code trace:
    # MediaGrouperScanner.cs:220 uses RenameTemplate.Scrub(episode.SeriesName)
    # for the target folder name — no year disambiguation. Jellyfin returns
    # "Doctor Who" for episodes from all three folders when the metadata match
    # collapses them (default TVDb resolution).
    #
    # Fixture: tv/Doctor Who (1963)/Season 01/E01.mkv
    #         tv/Doctor Who (2005)/Season 01/E01.mkv
    #         tv/Doctor Who (2024)/Season 01/E01.mkv
    Write-Log "  building: Doctor Who reboot fixture (GitHub #43)"
    $tvRoot = Join-Path $ScratchRoot "tv"
    foreach ($year in @(1963, 2005, 2024)) {
        $seriesDir = Join-Path $tvRoot ("Doctor Who ({0})" -f $year)
        $seasonDir = Join-Path $seriesDir "Season 01"
        New-Item -ItemType Directory -Force -Path $seasonDir | Out-Null
        # Each episode named to expose SxxExx pattern to Jellyfin's naming parser
        $epName = "Doctor Who ({0}) - S01E01.mkv" -f $year
        New-SyntheticVideo -OutPath (Join-Path $seasonDir $epName) -DurationSec 3
    }
    Write-Log "    3 reboot folders created under $tvRoot"
    return $tvRoot
}

function Remove-ScratchFixtures {
    $root = Join-Path $Script:RunState.FixturesRoot "_test-scratch"
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
        Write-Log "Removed scratch fixtures"
    }
}
