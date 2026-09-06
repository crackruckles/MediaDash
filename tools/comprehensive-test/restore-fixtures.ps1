# restore-fixtures.ps1
# ─────────────────────────────────────────────────────────────────────────────
# Restore MediaDash fixture files from the backup taken during the test run.
# Companion to run-full-tests.ps1's preflight backup step.
#
# Usage:
#   .\restore-fixtures.ps1                    # restore from default backup location
#   .\restore-fixtures.ps1 -DryRun            # show what WOULD be restored, don't touch anything
#   .\restore-fixtures.ps1 -Verify            # only compare — no restore
#   .\restore-fixtures.ps1 -BackupRoot X      # restore from a specific backup location
#
# Behavior:
#   • Reads manifest.json from the backup
#   • For each file: if it's missing OR its SHA256 differs from manifest,
#     restore it from the backup. Untouched files are skipped.
#   • Fresh scratch fixtures ($FixturesRoot/_test-scratch/) are NEVER restored —
#     they're regenerated fresh on next test run.

[CmdletBinding()]
param(
    [string]$FixturesRoot = "C:\dev\mediadash-fixtures",
    [string]$BackupRoot = "",
    [switch]$DryRun,
    [switch]$Verify
)

$ErrorActionPreference = "Stop"

if (-not $BackupRoot) {
    $BackupRoot = $FixturesRoot.TrimEnd('\','/') + "-backup"
}

Write-Host "MediaDash fixture restore"
Write-Host "  Fixtures root: $FixturesRoot"
Write-Host "  Backup root:   $BackupRoot"
Write-Host "  Mode:          $(if ($Verify) {'VERIFY ONLY'} elseif ($DryRun) {'DRY RUN'} else {'LIVE RESTORE'})"
Write-Host ""

if (-not (Test-Path -LiteralPath $BackupRoot)) {
    throw "backup root not found: $BackupRoot"
}

$manifestPath = Join-Path $BackupRoot "manifest.json"
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "manifest.json not found in backup: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
Write-Host "Manifest: $($manifest.files.Count) files, created $($manifest.createdAtUtc), method $($manifest.method)"
Write-Host ""

$missing = 0
$modified = 0
$intact = 0
$restored = 0
$failed = 0

foreach ($entry in $manifest.files) {
    $current = Join-Path $FixturesRoot $entry.path
    $backup  = Join-Path $BackupRoot $entry.path
    $status = "?"

    if (-not (Test-Path -LiteralPath $current)) {
        $status = "MISSING"
        $missing++
    } else {
        try {
            $actualSha = (Get-FileHash -LiteralPath $current -Algorithm SHA256).Hash
        } catch {
            $actualSha = "ERROR"
        }
        if ($actualSha -eq $entry.sha256) {
            $status = "OK"
            $intact++
        } else {
            $status = "MODIFIED"
            $modified++
        }
    }

    if ($status -eq "OK") {
        continue
    }

    if ($Verify) {
        Write-Host "  [$status] $($entry.path)" -ForegroundColor Yellow
        continue
    }

    if ($DryRun) {
        Write-Host "  would restore: [$status] $($entry.path)" -ForegroundColor Cyan
        continue
    }

    # Live restore
    try {
        $dir = Split-Path -Parent $current
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
        }
        Copy-Item -LiteralPath $backup -Destination $current -Force
        Write-Host "  restored: [$status] $($entry.path)" -ForegroundColor Green
        $restored++
    } catch {
        Write-Host "  FAILED:   [$status] $($entry.path) — $($_.Exception.Message)" -ForegroundColor Red
        $failed++
    }
}

Write-Host ""
Write-Host "─── Summary ───"
Write-Host "  OK (unchanged):  $intact"
Write-Host "  Modified:        $modified"
Write-Host "  Missing:         $missing"
if (-not ($Verify -or $DryRun)) {
    Write-Host "  Restored:        $restored"
    Write-Host "  Restore failed:  $failed"
}

if ($Verify -or $DryRun) {
    if (($modified + $missing) -gt 0) { exit 1 } else { exit 0 }
} else {
    if ($failed -gt 0) { exit 1 } else { exit 0 }
}
