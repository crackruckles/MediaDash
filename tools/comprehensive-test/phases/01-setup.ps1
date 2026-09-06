# 01-setup.ps1 — one-time setup for the whole test run:
#   • Generate scratch fixtures (edge-case files the base make-fixtures doesn't cover)
#   • Attach the fixture library to Jellyfin
#   • Wait for Jellyfin to index every fixture item
#   • Set MediaDash into a known-safe config (DryRun ON, everything DetectOnly)
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
        # Remove any leftover library from a prior run
        Remove-JellyfinLibrary "MediaDash Test"
        Add-JellyfinLibrary -Name "MediaDash Test" -Type "movies" -Path (Join-Path $Script:RunState.FixturesRoot "movies")
    }

    Invoke-Test "Attach scratch fixture library to Jellyfin" {
        Remove-JellyfinLibrary "MediaDash Scratch"
        $moviesScratchDir = Join-Path $Script:RunState.ScratchRoot "movies"
        if (Test-Path -LiteralPath $moviesScratchDir) {
            Add-JellyfinLibrary -Name "MediaDash Scratch" -Type "movies" -Path $moviesScratchDir
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
        Remove-JellyfinLibrary "MediaDash TV Scratch"
        $tvScratchDir = Join-Path $Script:RunState.ScratchRoot "tv"
        if (Test-Path -LiteralPath $tvScratchDir) {
            Add-JellyfinLibrary -Name "MediaDash TV Scratch" -Type "tvshows" -Path $tvScratchDir
        }
    }

    Invoke-Test "Jellyfin library scan finishes and indexes >= 8 movies" {
        Wait-ForJellyfinScan -TimeoutSec 300
        Start-Sleep -Seconds 3
        $n = Get-JellyfinItemCount -IncludeItemTypes "Movie"
        Write-Log "    Jellyfin indexed $n movies"
        Assert-GreaterOrEqual 8 $n "expected at least 8 movie items after scan"
    }

    Invoke-Test "Set MediaDash config to safe defaults for testing" {
        # DryRun ON, no window, no pause. Language allow-lists set to ["eng"] so
        # Multi Audio (fra/deu) and Sub Heavy (fra/deu subs) trigger.
        # Trickplay threshold lowered so the padded fixture (~11 MB) triggers.
        # FixMode fields NOT touched — defaults are DetectOnly which is what
        # scanners need. (Touching EmbeddedCoverFixMode via PATCH triggers 500
        # from the JSON binder — see GH-TBD.)
        Set-MediaDashConfig @{
            DryRun = $true
            PauseDuringPlayback = $false
            LowSystemImpactMode = $false
            FixWindowStart = ""
            FixWindowEnd = ""
            AllowedAudioLanguages = @("eng")
            AllowedSubtitleLanguages = @("eng")
            TrickplayMinSizeMb = 5
            # Default is 100 MB — filters out samples/trailers/extras. Our fixtures
            # are short synthetic clips (30-50 MB); lower to 10 so QualityScanner
            # actually looks at them.
            MinScanFileSizeMb = 10
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
        Invoke-JfApi -Method POST -Path "/MediaDash/Reset" -IgnoreErrors | Out-Null
        Start-Sleep -Seconds 1
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
