# 01-setup.ps1 — one-time setup for the whole test run:
#   • Generate scratch fixtures (edge-case files the base make-fixtures doesn't cover)
#   • Attach the fixture library to Jellyfin
#   • Wait for Jellyfin to index every fixture item
#   • Set MediaDash into a known-safe config (DryRun ON, everything ManualApprove)
#   • Run the first MediaDash scan so subsequent phases have Issues to work with
#
# Failures here also halt the run — nothing downstream works without a
# scanned library.

function Invoke-Phase-Setup {
    Start-Phase "Setup — attach fixtures + baseline config + initial scan"

    $scratchRoot = $null

    Invoke-Test "Scratch fixture root created" {
        $scratchRoot = New-ScratchFixtureRoot
        Assert-True (Test-Path -LiteralPath $scratchRoot) "scratch root should exist"
        $Script:RunState | Add-Member -MemberType NoteProperty -Name ScratchRoot -Value $scratchRoot -Force
    }

    Invoke-Test "All scratch fixture files generated" {
        $scratchRoot = $Script:RunState.ScratchRoot
        New-AllFixtures -ScratchRoot $scratchRoot | Out-Null
        # Sanity check — verify at least one file per expected fixture folder
        $expectations = @(
            "movies\Suspicious Test (2020)\installer.exe",
            "movies\Broken Nfo (2020)\Broken Nfo (2020).nfo",
            "movies\Corrupt Artwork (2020)\poster.jpg",
            "movies\_orphans\ghost.en.srt",
            "movies\Ass Sub Test (2020)\Ass Sub Test (2020).en.ass",
            "movies\FiveOne Audio (2022)\FiveOne Audio (2022).mkv",
            "movies\Trickplay Test (2020)\Trickplay Test (2020).trickplay",
            "movies\日本語 Test (2023)\日本語 Test (2023).mkv",
            "movies\Zero Byte (2020)\Zero Byte (2020).mkv"
        )
        foreach ($e in $expectations) {
            $p = Join-Path $scratchRoot $e
            Assert-True (Test-Path -LiteralPath $p) "fixture should exist: $e"
        }
    }

    Invoke-Test "Attach base fixture library to Jellyfin" {
        Add-JellyfinLibrary -Name "MediaDash Test $($Script:RunState.RunId)" -Type "movies" -Path (Join-Path $Script:RunState.FixturesRoot "movies")
    }

    Invoke-Test "Attach scratch fixture library to Jellyfin" {
        $moviesScratchDir = Join-Path $Script:RunState.ScratchRoot "movies"
        if (Test-Path -LiteralPath $moviesScratchDir) {
            Add-JellyfinLibrary -Name "MediaDash Scratch $($Script:RunState.RunId)" -Type "movies" -Path $moviesScratchDir
        }
    }

    Invoke-Test "Build Doctor Who reboot fixture (GitHub #43 repro)" {
        $tvRoot = New-DoctorWhoFixture -ScratchRoot $Script:RunState.ScratchRoot
        Assert-True (Test-Path -LiteralPath $tvRoot) "TV root should exist"
        # Verify each of the three reboot folders is present with an episode file
        foreach ($year in @(1963, 2005, 2024)) {
            $ep = Join-Path $tvRoot ("Doctor Who ({0})\Season 01\Doctor Who ({0}) - S01E01.mkv" -f $year)
            Assert-FileExists $ep "reboot fixture $year should have S01E01"
        }
    }

    Invoke-Test "Attach TV scratch library (Doctor Who) to Jellyfin" {
        $tvScratchDir = Join-Path $Script:RunState.ScratchRoot "tv"
        if (Test-Path -LiteralPath $tvScratchDir) {
            Add-JellyfinLibrary -Name "MediaDash TV Scratch $($Script:RunState.RunId)" -Type "tvshows" -Path $tvScratchDir
        }
    }

    Invoke-Test "Jellyfin library scan finishes and indexes >= 8 movies" {
        Wait-ForJellyfinScan -TimeoutSec 300
        Start-Sleep -Seconds 3
        $n = Get-JellyfinItemCount -IncludeItemTypes "Movie"
        Write-Log "    Jellyfin indexed $n movies"
        Assert-GreaterOrEqual 8 $n "expected at least 8 movie items after scan"
    }

    Invoke-Test 'Create corrupt Jellyfin-managed artwork for an owned movie' {
        $items = (Invoke-JfApi -Method GET -Path '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path').Items
        $item = @($items | Where-Object Path -Like '*Corrupt Artwork*')[0]
        Assert-True ([bool]$item) 'the owned artwork movie must be indexed'
        $sprite = Join-Path $Script:RunState.ScratchRoot 'movies/Trickplay Test (2020)/Trickplay Test (2020).trickplay/0.jpg'
        $body = [Convert]::ToBase64String([IO.File]::ReadAllBytes($sprite))
        Invoke-JfApi -Method POST -Path "/Items/$($item.Id)/Images/Primary" -ContentType 'image/jpeg' -Body $body | Out-Null
        $image = @(Invoke-JfApi -Method GET -Path "/Items/$($item.Id)/Images" | Where-Object ImageType -EQ 'Primary')[0]
        Assert-True ([bool]$image.Path) 'uploaded managed artwork path must be returned'
        $metadataRoot = Join-Path $Script:RunState.DataDir 'metadata'
        Assert-SafeLiveTarget -Url $Script:RunState.JellyfinUrl -AllowedFixturePaths @($metadataRoot) -FixturePath $image.Path
        $Script:RunState | Add-Member -NotePropertyName ArtworkFixturePath -NotePropertyValue $image.Path -Force
        $Script:RunState | Add-Member -NotePropertyName ArtworkVideoPath -NotePropertyValue $item.Path -Force
        $Script:RunState | Add-Member -NotePropertyName ArtworkVideoHash -NotePropertyValue (Get-FileHash -LiteralPath $item.Path -Algorithm SHA256).Hash -Force
        [IO.File]::WriteAllBytes($image.Path, [byte[]]@())
    }

    Invoke-Test "Set MediaDash config to safe defaults for testing" {
        # DryRun ON, no window, no pause. Language allow-lists set to ["eng"] so
        # Multi Audio (fra/deu) and Sub Heavy (fra/deu subs) trigger.
        # Trickplay threshold lowered so the padded fixture (~11 MB) triggers.
        # ManualApprove is the serialized enum name. We used to pick DetectOnly for
        # tests but it was removed in 1.0.9 (every mode now does the same opt-in).
        # Automatic is avoided because it can consume fixtures before their assertions.
        $manualModes = @{}
        $ownedLibraries = @(Get-JellyfinLibraries | Where-Object Name -In $Script:RunState.AddedLibraryIds)
        Assert-Equal 3 $ownedLibraries.Count 'all owned fixture libraries must be materialized'
        $Script:RunState | Add-Member -NotePropertyName FixtureLibraryIds -NotePropertyValue @($ownedLibraries.ItemId) -Force
        $manualModes.EnabledLibraries = $Script:RunState.FixtureLibraryIds
        (Get-MediaDashConfig).PSObject.Properties | Where-Object Name -Like '*FixMode' | ForEach-Object {
            $manualModes[$_.Name] = 'ManualApprove'
        }
        (Get-MediaDashConfig).PSObject.Properties | Where-Object Name -Like '*Disposal' | ForEach-Object {
            $manualModes[$_.Name] = 'RecycleBin'
        }
        Set-MediaDashConfig $manualModes
        Set-MediaDashConfig @{
            DryRun = $true
            FirstRunDone = $true
            AnalyticsEnabled = $false
            FixTaskSeeded = $true
            ScannerDisabled = @()
            FixerDisabled = @()
            DuplicateMinAgeDays = 0
            PauseDuringPlayback = $false
            LowSystemImpactMode = $false
            FixWindowStart = ""
            FixWindowEnd = ""
            AllowedAudioLanguages = @("eng")
            AllowedSubtitleLanguages = @("eng")
            SubtitleForceFont = ''
            TrickplayMinSizeMb = 5
            # Default is 100 MB — filters out samples/trailers/extras. Our fixtures
            # are short synthetic clips (30-50 MB); lower to 10 so QualityScanner
            # actually looks at them.
            MinScanFileSizeMb = 0
            UseHardwareEncoder = $false
            QualityMinSavingsPercent = 0
            QualityMinSavingsBytes = 0
        }
        $cfg = Get-MediaDashConfig
        Assert-Equal $true $cfg.DryRun "DryRun should be ON"
        Assert-Equal $false $cfg.PauseDuringPlayback "PauseDuringPlayback should be OFF for tests"
    }

    Invoke-Test "Reset MediaDash scan state so no stale Queued/Dismissed rows block detection" {
        # ReplaceDetectedIssues (MediaDashDb.cs:380) skips insertion when the same path
        # already has a Queued or Dismissed row. Without this reset, previous runs'
        # approved/dismissed issues silently suppress current-run detection on the same
        # fixtures — scanner tests then find 0 issues even though the scanner ran.
        #
        # ConfigChangeRescanner (hosted service, plan §04) subscribes to ConfigurationChanged
        # and schedules a debounced (750ms) targeted rescan when the language allowlists /
        # thresholds change. The prior "Set safe defaults" test just wrote those fields, so
        # a rescan is queued. Wait for that rescan to fully drain (start + finish) BEFORE
        # Reset — otherwise Reset wipes the DB mid-scan and races the rescanner's writes.
        # Poll IsScanning true→false with a small ceiling; the rescanner's scan on 103 movies
        # typically completes in < 15s under ManualApprove modes.
        $rescanTimeout = [DateTime]::UtcNow.AddSeconds(30)
        while ([DateTime]::UtcNow -lt $rescanTimeout) {
            $s = Invoke-JfApi -Method GET -Path "/MediaDash/Status"
            if (-not $s.IsScanning) { break }
            Start-Sleep -Milliseconds 500
        }
        Invoke-JfApi -Method POST -Path "/MediaDash/Reset" -IgnoreErrors | Out-Null
        Start-Sleep -Milliseconds 500
    }

    Invoke-Test "First MediaDash scan completes and produces issues" {
        Start-MediaDashScan -Wait -TimeoutSec 900
        $status = Get-MediaDashStatus
        Assert-False $status.IsScanning "scan should have finished"
        $issues = Get-MediaDashIssues
        Assert-GreaterOrEqual 1 $issues.Count "at least one issue should be detected on a library with known problems"
        Write-Log "    Total issues detected: $($issues.Count)"
    }
}
