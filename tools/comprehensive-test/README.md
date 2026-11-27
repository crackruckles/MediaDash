# Comprehensive Jellyfin harness

This is a partial live acceptance matrix. It exercises scanners, fixers, configuration, endpoints, UI smoke and reported bugs. It does not certify every registered scanner/fixer or every setting. See [the harness inventory](../TEST-HARNESSES.md) and [the dated review](../../docs/testing/HARNESS-REVIEW-2026-10-20.md).

Requires PowerShell 7, Jellyfin's ffmpeg, a new run ID, and a dedicated local fixture-only Jellyfin instance at port 8099. All existing library locations must be inside the supplied fixture root; unrelated queued work or running tasks block the run. The active data directory must match `-DataDir`. Do not run concurrently with another operator or harness.

Generate base fixtures into a **new** directory first:

```powershell
# Git Bash or Linux Bash; supply the bundled ffmpeg path when it is not on PATH.
bash tools/make-fixtures.sh C:/dev/mediadash-harness-new "C:/path/to/jellyfin/ffmpeg.exe"
pwsh -NoProfile -File tools/comprehensive-test/run-full-tests.ps1 `
  -ConfirmLive -DataDir C:/path/to/isolated-jellyfin-data `
  -FixturesRoot C:/dev/mediadash-harness-new -RunId acceptance-new
# Narrow a run while retaining required preflight/setup and automatic teardown:
pwsh -NoProfile -File tools/comprehensive-test/run-full-tests.ps1 `
  -ConfirmLive -DataDir C:/path/to/isolated-jellyfin-data `
  -FixturesRoot C:/dev/mediadash-harness-new -OnlyPhases 00,01,02 -RunId scanners-new
```

Phase IDs: 00 preflight, 01 setup, 02 scanners, 03 fixers, 04 combined-pass, 05 config, 06 endpoints, 07 UI, 08 edge-cases, 09 reported-bugs, 99 teardown. UI requires the browse executable. Artwork uses an uploaded Jellyfin-managed image belonging to an owned fixture. Combined-pass coverage exercises both track cleanup and quality conversion with audio cleanup. Base generation creates separately indexed duplicate fixtures with matching local NFO title/year and a combined audio/subtitle fixture.

Results are written under `test-results/<RunId>`: `results.jsonl`, `status.json`, `summary.md`, `config-before.json`, `tasks-before.json`, and failure artifacts. Run IDs cannot be reused. Exit 0 means all selected tests passed; 1 means assertion failure, 2 means execution error, and 3 means incomplete/skipped coverage or rejected phase selection.

Preflight/setup failures stop dependent phases. Teardown continues independent cleanup steps, restores configuration and task triggers, removes only libraries created by this run, and never deletes pre-existing recycle entries. A busy task blocks cleanup and retains isolation; use the saved settings/triggers only after tasks stop. Base fixtures can remain modified by real fixes: the independent copy backup and integrity report support explicit restoration using `restore-fixtures.ps1`. Hard process termination still requires manual recovery.

The older hardlink backups are refused. Regenerate in a new fixture root instead of overwriting historical evidence. A missing, empty or hash-mismatched backup cannot satisfy integrity checks.
