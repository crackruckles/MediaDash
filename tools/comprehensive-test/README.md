# MediaDash comprehensive test harness

End-to-end test suite that exercises every feature of MediaDash through the plugin (not via code inspection). Runs against a real Jellyfin, uses real ffmpeg, produces real recycle-bin entries, restores from them.

## What it tests

| Phase | What it covers |
|-------|----------------|
| **00 preflight** | Jellyfin reachable, plugin loaded, ffmpeg present, fixtures on disk |
| **01 setup**     | Generate scratch fixtures (10 edge-case files), attach fixture libraries, run initial scan |
| **02 scanners**  | One test per `IScanner` — Duplicate, Quality, Playability, AudioLanguage, SubtitleLanguage, SuspiciousFile, Nfo, Artwork, OrphanCleanup, SubtitleFont, TrickplayOptimize, and false-positive checks on Clean Movie |
| **03 fixers**    | One test per `IFixer` — approve issue, run fix through the plugin, verify file state, verify recycle bin, restore |
| **04 combined-pass** | Sub Heavy fixture with combined Audio+Sub language fix; verifies 1 recycle-bin entry per multi-issue file |
| **05 config**    | DryRun, FixWindow, LowSystemImpactMode, FixTaskSeeded (1.0.7.5 bugfix), EnabledLibraries scope, per-type FixMode, TrickplayMinSizeMb |
| **06 endpoints** | Smoke tests every API route + shape assertions on `LibraryStats` (including new HDR/BitDepth/Channels/Kind/Lang facets) |
| **07 ui**        | Config page mounts, tabs navigable, no JS console errors — uses gstack `browse` tool |
| **08 edge cases** | Unicode filenames, 0-byte files, concurrent Scan/Run, Cancel while running, config-save burst |
| **99 teardown**  | Restore config snapshot, empty test recycle-bin entries, remove test libraries, delete scratch fixtures |

## Prereqs

- Jellyfin 10.11.x running at `http://localhost:8099` with a `test/test` admin user
- MediaDash 1.0.7.5+ deployed and loaded (verify with `grep "Loaded plugin: MediaDash" jellyfin.log`)
- `C:\dev\mediadash-fixtures\` present with base fixtures (run `bash tools/make-fixtures.sh` if missing)
- Jellyfin's bundled `ffmpeg.exe` available (auto-detected — see `helpers.ps1::Get-JellyfinFfmpegPath`)
- PowerShell 5.1 or 7.x

## Run

```powershell
cd C:\dev\mediadash\tools\comprehensive-test
.\run-full-tests.ps1
```

Common variations:

```powershell
# Skip long-running phases (UI + edge cases)
.\run-full-tests.ps1 -SkipPhases 07,08

# Only run scanner + fixer coverage
.\run-full-tests.ps1 -OnlyPhases 02,03

# Halt at first failure instead of continuing (default: keep going)
.\run-full-tests.ps1 -HaltOnFailure

# Quick mode — skip long edge cases
.\run-full-tests.ps1 -Quick

# Named run so you can compare across runs
.\run-full-tests.ps1 -RunId "before-audio-scanner"
```

## Monitoring while running

Open a second terminal and:

```powershell
# Tail the log
Get-Content .\test-results\<runid>\run.log -Wait -Tail 20

# Structured status
Get-Content .\test-results\<runid>\status.json | ConvertFrom-Json | Format-List

# See per-test results as they land
Get-Content .\test-results\<runid>\results.jsonl -Wait | ForEach-Object { $_ | ConvertFrom-Json | Select-Object test, result, durationMs }
```

## After the run

```
test-results/<runid>/
├── run.log             ← full timestamped log
├── results.jsonl       ← one JSON per test (grep, jq, whatever)
├── status.json         ← final summary counters
├── summary.md          ← markdown report you can share
├── artifacts/          ← run-wide artifacts (empty unless something special)
└── failures/<test>/    ← per-failure snapshot: DB, config, diag, JF log tail
```

For each failure, `failures/<test>/` contains:
- `error.json`       — exception + stack trace
- `mediadash.db`     — plugin DB at the moment of failure
- `plugin-config.xml`— exact config at failure
- `diagnostics.json` — MediaDash diagnostics log
- `jellyfin-tail.log`— last 300 lines of Jellyfin's log
- `mediadash-status.json` — `/Status` at failure

## Extending

Each phase is a single `.ps1` under `phases/`. Add a new `Invoke-Test` to any phase, or drop a new phase file in and register it in `run-full-tests.ps1`'s `$AllPhases` array. The framework handles timing, error capture, artifacts, and reporting — you just write the test body.

Assertion helpers available in every test:

- `Assert-Equal`, `Assert-NotEqual`, `Assert-True`, `Assert-False`
- `Assert-GreaterOrEqual`, `Assert-LessOrEqual`
- `Assert-Contains`, `Assert-NotContains`
- `Assert-FileExists`, `Assert-FileMissing`
- `Assert-Match`
- `Wait-For` — polls a predicate until true or timeout

## Design notes

- **Continue on failure** by default. One broken test doesn't halt the run, so you get a full picture in one pass. Use `-HaltOnFailure` for bisection debugging.
- **Every failure snapshots forensics.** DB, config, diagnostics, and JF log tail land in `failures/<test>/`. You can post-mortem without re-running.
- **DryRun defaults ON** between phases so tests don't accidentally torch state.
- **The Devil Wears Prada fixture is inviolate.** Scratch fixtures live under `_test-scratch/` so cleanup can nuke them without touching base fixtures.
- **Real ffmpeg, real Jellyfin, real files.** No mocks, no code inspection. If a scanner works, its fixture is flagged. If a fixer works, its file is modified/removed and recyclable.
