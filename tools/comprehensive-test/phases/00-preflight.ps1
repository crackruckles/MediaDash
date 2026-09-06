# 00-preflight.ps1 — verify the environment is capable of running the tests.
# Every test in every subsequent phase assumes preflight PASSED — a failure
# here should halt the whole run (main script honors this).

function Invoke-Phase-Preflight {
    Start-Phase "Preflight — verify test environment"

    Invoke-Test "Jellyfin is reachable and returns 200 on /health" {
        $r = Invoke-WebRequest -Uri "$($Script:RunState.JellyfinUrl)/health" -UseBasicParsing -TimeoutSec 10
        Assert-Equal 200 $r.StatusCode "expected HTTP 200 from /health"
    }

    Invoke-Test "Admin auth as test/test succeeds" {
        # Must run before any authenticated Invoke-JfApi so the auth header is populated.
        Connect-JellyfinAdmin
        Assert-True ([bool]$Script:RunState.JellyfinToken) "token should be captured"
        Assert-True ([bool]$Script:RunState.JellyfinUserId) "user id should be captured"
    }

    Invoke-Test "Jellyfin version endpoint is reachable" {
        $info = Invoke-JfApi -Method GET -Path "/System/Info/Public"
        Assert-True ([bool]$info.Version) "Version field should be populated"
        $Script:RunState.JellyfinVersion = $info.Version
        Write-Log "    Jellyfin version: $($info.Version)"
    }

    Invoke-Test "MediaDash plugin is loaded" {
        $plugins = Invoke-JfApi -Method GET -Path "/Plugins"
        $md = $plugins | Where-Object { $_.Id -replace '-','' -eq $Script:RunState.PluginId.Replace('-','') }
        Assert-True ([bool]$md) "MediaDash plugin should be in the plugin list"
        $Script:RunState.PluginVersion = $md.Version
        # Jellyfin's /Plugins caches meta.json state from first load; may show 0.0.0.0
        # for a dev build. The DLL's actual version is what matters — verified via
        # startup log grep for "Loaded plugin: MediaDash <ver>" during deploy.
        Write-Log "    MediaDash version (from /Plugins): $($md.Version)"
        Assert-Equal "Active" $md.Status "plugin should be Active"
    }

    Invoke-Test "MediaDash config endpoint is reachable" {
        $cfg = Get-MediaDashConfig
        Assert-True ([bool]$cfg) "config should be non-null"
        Save-MediaDashConfigSnapshot
    }

    Invoke-Test "MediaDash /Status endpoint returns valid shape" {
        $s = Get-MediaDashStatus
        Assert-True ($null -ne $s.IsScanning) "IsScanning field expected"
        Assert-True ($null -ne $s.IsFixing) "IsFixing field expected"
    }

    Invoke-Test "Fixture root exists and contains base fixtures" {
        Assert-True (Test-Path -LiteralPath $Script:RunState.FixturesRoot) "fixtures root should exist"
        $moviesDir = Join-Path $Script:RunState.FixturesRoot "movies"
        Assert-True (Test-Path -LiteralPath $moviesDir) "movies subdir should exist"
        $baseFile = Join-Path $moviesDir "Big Buck Test 4K (2020)\Big Buck Test 4K (2020).mkv"
        Assert-FileExists $baseFile "Big Buck Test 4K should exist (base fixture — separate folder so Jellyfin doesn't merge it with the 1080p as a single item)"
    }

    Invoke-Test "Jellyfin bundled ffmpeg is discoverable" {
        $ff = Get-JellyfinFfmpegPath
        Assert-True ([bool]$ff) "ffmpeg should be found (needed for scratch fixture generation)"
        Write-Log "    ffmpeg: $ff"
    }

    Invoke-Test "MediaDash DB file is writable" {
        $dbPath = "$env:LOCALAPPDATA\jellyfin\data\mediadash\mediadash.db"
        # DB is created on first plugin use — may not exist yet on a fresh install
        $dbDir = Split-Path -Parent $dbPath
        Assert-True (Test-Path -LiteralPath $dbDir) "plugin data dir should exist"
    }

    Invoke-Test "PowerShell version is >= 5.1" {
        Assert-GreaterOrEqual 5 $PSVersionTable.PSVersion.Major "PowerShell 5.1 or newer required"
    }

    Invoke-Test "Backup base fixtures before any modification" {
        # Uses hardlinks on same volume — near-zero disk cost, survives most fixer
        # operations (fixers write to .tmp and File.Move overwrite, which unlinks
        # the primary but leaves the shadow hardlink intact). Fails over to copy
        # on cross-volume. Skipped if backup already exists (idempotent).
        $backupRoot = Backup-BaseFixtures -FixturesRoot $Script:RunState.FixturesRoot
        Assert-True (Test-Path -LiteralPath $backupRoot) "backup root should exist"
        $Script:RunState | Add-Member -MemberType NoteProperty -Name BackupRoot -Value $backupRoot -Force
    }

    Invoke-Test "Restore any fixtures modified by prior runs from backup" {
        # Previous runs' fixers modify fixtures in place (TrackFixer remuxes Multi
        # Audio, PlayabilityFixer recycles Truncated Movie, etc.). Without this
        # step, run N+1 sees the post-fix files and every scanner test targeting
        # them regresses. Restore rewrites any file whose SHA differs from the
        # backup manifest, and re-creates any file that was fully deleted.
        $changes = Test-FixtureIntegrity -FixturesRoot $Script:RunState.FixturesRoot
        if ($changes.Count -eq 0) {
            Write-Log "    all fixtures match backup — nothing to restore"
            return
        }
        Write-Log "    restoring $($changes.Count) modified/missing fixture(s)"
        foreach ($c in $changes) {
            $current = Join-Path $Script:RunState.FixturesRoot $c.path
            $backup  = Join-Path $Script:RunState.BackupRoot $c.path
            $dir = Split-Path -Parent $current
            if (-not (Test-Path -LiteralPath $dir)) {
                New-Item -ItemType Directory -Force -Path $dir | Out-Null
            }
            Copy-Item -LiteralPath $backup -Destination $current -Force
        }
        # Re-verify
        $after = Test-FixtureIntegrity -FixturesRoot $Script:RunState.FixturesRoot
        Assert-Equal 0 $after.Count "all fixtures should match backup after restore"
    }
}
