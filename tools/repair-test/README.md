# Playability repair tests

`run_live.py` runs generated fixtures through a real local Jellyfin server. It checks detection, dry run, approval, each repair method, history, repair counters, recycling, full audio/video decode, complete HTTP delivery, rescan and restoration. See [COVERAGE.md](COVERAGE.md) for the coverage map and outstanding gates. A green run is evidence for this matrix, not every possible media file or a full-plugin release verdict.

## Run the live matrix

Requires Python 3.11+, Jellyfin's ffmpeg/ffprobe, and an idle local test server with the intended plugin already deployed. Start the v10 instance using `~/Downloads/start-jellyfin-v10.bat`. The runner verifies the active server's data directory; it does not deploy or restart Jellyfin.

From the repository root:

```powershell
python -B tools/repair-test/run_live.py --list-cases
python -u -B tools/repair-test/run_live.py --confirm-live `
  --url http://localhost:8099 `
  --ffmpeg "$env:USERPROFILE/Downloads/jellyfin_10.11.11-amd64/jellyfin/ffmpeg.exe" `
  --data-dir "$env:LOCALAPPDATA/jellyfin-v10" `
  --output C:/dev/mediadash-repair-new-run
```

The output directory must be new and outside existing libraries. Only loopback URLs are accepted; HTTP redirects and proxy forwarding are rejected. Credentials default to local `test/test`; use `--username` and the `MEDIADASH_TEST_PASSWORD` environment variable to override. Never run concurrently with another operator or automatic test harness.

Existing queued work blocks the run by default. On a dedicated test server, `--hold-existing-queue` saves those approvals to `queue-before.json`, temporarily dismisses them while other fix modes are disabled, and restores them during cleanup. Dismissed rows survive scanner invalidation; reverting them to Detected before scanning could lose the saved approvals. It does not permit foreign work in any test fix batch. This is an explicit opt-in because Jellyfin exposes a global Fix task.

`torture-test.ps1 -ConfirmLive` is a compatibility wrapper. It accepts `-Python`, `-Ffmpeg`, `-DataDir`, `-OutputDir` and optional `-HoldExistingQueue`.

## Results and cleanup

- `summary.json`: exit code and assertion totals; `results.json`: individual results and diagnostics.
- `fixtures.json`: original hashes, paths, stream counts and fixture expectations.
- `history-*.json`: current-run history, matched by issue ID, full path and baseline history ID. Old recycle entries cannot satisfy a new repair.
- `server.json`, `status-*.json`, `config-before.json`, `queue-before.json`: host identity, counters and restoration evidence.
- `process-*.log`: ffmpeg/ffprobe stderr. Media and evidence remain for investigation.

Exit 0 requires recorded checks to pass. Failures return 1; incomplete/blocked-only results return 2. A missing case, missing detection, stale task completion, API error or teardown failure cannot silently produce a clean result. Assertions are not separate user scenarios: the matrix contains 15 fixtures plus one excluded-library control.

The runner limits enabled libraries to its generated library, disables other fix types, cancels only tasks it started, removes its temporary libraries, dismisses its own open issues, restores held approvals and checks configuration restoration. It never empties a shared recycle bin or deletes shared history. If an owned task cannot stop, it retains the isolated configuration and records failure; recover using the saved configuration/queue only after that task has stopped. Hard process termination still requires manual recovery.

The live delivery check downloads and hashes the complete small fixture. Browser/client playback, seeking, hardware decoding and device compatibility are separate coverage gates.

## Common-format acceptance library

`run_formats.py` creates 89 small owned fixtures across five typed libraries: movies, music, audiobooks, books and photos. It includes healthy files, zero-byte files, invalid payloads, truncation, extra language tracks, a wrong extension, duplicate and oversized candidates, cover art, malformed NFO, an inert script, orphan subtitles, real CBR/CB7 archives, MOBI and an EPUB carrying a DRM marker. Synthetic HDR metadata tests scanner acceptance, not visual HDR accuracy.

```powershell
python -B tools/repair-test/run_formats.py --self-test
python -u -B tools/repair-test/run_formats.py --generate-only `
  --ffmpeg "$env:USERPROFILE/Downloads/jellyfin_10.11.11-amd64/jellyfin/ffmpeg.exe" `
  --rar "C:/Program Files/WinRAR/Rar.exe" `
  --sevenzip "C:/Program Files (x86)/Lua/5.1/7z.exe" `
  --calibre "C:/Program Files/Calibre2/ebook-convert.exe" `
  --output C:/dev/mediadash-format-source-new
python -u -B tools/repair-test/run_formats.py --confirm-live --hold-existing-queue `
  --fixtures-source C:/dev/mediadash-format-source-new `
  --ffmpeg "$env:USERPROFILE/Downloads/jellyfin_10.11.11-amd64/jellyfin/ffmpeg.exe" `
  --data-dir "$env:LOCALAPPDATA/jellyfin-v10" `
  --output C:/dev/mediadash-format-live-new
```

Tool paths above are machine examples; pass installed executables. Real archive and MOBI generation require RAR, 7-Zip and Calibre. The runner records each detected issue, hashes every fixture before/after dry run, checks fix history against exact owned paths and IDs, decodes applicable outputs, and restores recycled originals. Expected detection failures remain failures; it never inserts artificial fixture issues into the database. Temporary library registrations are removed during cleanup; files and evidence remain.

Use `--pause-for-ui` for browser checks between detection and fixing. When `ui-ready.json` appears, inspect the owned library, approve and undo an owned row, and capture UI evidence. Create an empty `ui-continue` file in the output directory within 15 minutes to continue. Do not leave unrelated or owned approvals queued at handoff. Run `run_live.py` separately for repair-rung, streaming and rescan coverage; a common-format run alone does not establish every user workflow.

## Harness self-tests

```powershell
python -B -m unittest discover -s tools/repair-test -p "test_*.py"
./tools/repair-test/validator-self-tests.ps1
./tools/comprehensive-test/task-wait-self-tests.ps1
./tools/comprehensive-test/self-tests.ps1
```

The Python tests cover safety boundaries, task completion attribution, nonzero failure verdicts, full-path history attribution, queued issue queries and failure-safe cleanup. PowerShell self-tests verify the legacy offline validator and the shared comprehensive-test framework.

## Legacy offline fixtures

`regenerate.ps1` creates nine older fixtures. It stamps original SHA-256 values in `fixture-manifest.json`; `-Only <filename>` preserves the original hashes of fixtures it does not regenerate. Old manifests without hashes require full regeneration. The old README's broken-subtitle fixture was retired and is not coverage.

```powershell
./tools/repair-test/regenerate.ps1
./tools/repair-test/validate-fixtures.ps1
# Manually scan/fix only this fixture library, then:
./tools/repair-test/validate-outputs.ps1
```

These validators reject empty manifests and missing tools; the output validator additionally requires original hashes and rejects ambiguous outputs. They invoke ffmpeg directly, with all audio/video streams mapped, so shell punctuation in a filename is not executed as commands. Offline recycle hashes alone cannot establish which run created an entry. Use the live runner for that attribution and for Jellyfin integration. Historical target-rung hints in the legacy manifest do not prove which method actually ran.

For the combined offline gate, run `pwsh -NoProfile -File tools/test-harnesses.ps1` from the repository root. The old `torture-malformed-rows.ps1` now invokes isolated production-parser/timestamp regression tests; it does not mutate a running database or certify live HTTP endpoint behavior. See [the current harness inventory](../TEST-HARNESSES.md).
