# Playability Repair — Design

**Status:** approved, ready for implementation plan
**Target release:** 1.0.8.0 (feature bundle — see §7)
**Related:** `PlayabilityScanner`, `PlayabilityFixer`, `RecycleBin`, `FfprobeService`

---

## 1. Problem

`PlayabilityScanner` correctly identifies broken files, but `PlayabilityFixer` only deletes them. Many "unplayable" files are container-level damage (bad index, wrong duration, missing moov atom, truncated download, extension mismatch, one corrupt subtitle stream) and are recoverable with a remux or a targeted stream drop. Today's fixer throws all of them away.

## 2. Goal

Add a repair pass to `PlayabilityFixer` that runs *before* the existing delete path. Four repair rungs, each attempted only if the previous one's output failed ffprobe verification. Original file untouched until a rung produces a verified working replacement. If every rung fails, fall through to today's delete-to-recycle-bin behavior.

## 3. Non-goals (v1)

- New scanner or new `IssueType`. Reuse `IssueType.Playability`.
- New fixer class. All logic lives in `PlayabilityFixer`.
- Per-file repair heuristics chosen at scan time. The fixer tries rungs in fixed order.
- Audio re-encoding. Audio issues are the audio-conversion scanner's territory.
- Repairing files outside library paths (`LibraryGuard` already blocks this — unchanged).

## 4. Design

### 4.1 Repair ladder

Rungs run in fixed order. Each rung's execution is gated by its own config toggle (see §4.4). Between rungs, the previous temp file is discarded.

| # | Name | ffmpeg strategy | Recovers |
|---|---|---|---|
| 1 | **Quick remux** | `-err_detect ignore_err -fflags +genpts+igndts -i <src> -map 0 -c copy -avoid_negative_ts make_zero -y <tmp>` — same extension as source | Bad container index, wrong duration, moov-atom issues, EOF truncation |
| 2 | **Drop broken streams** | Per-stream decode check via ffprobe; identify streams that error; remux with `-map -0:<idx>` excluding them. Same extension. | Files that play but have one corrupt subtitle/audio track breaking decode |
| 3 | **Container coercion** | Remux to `.mkv` (universal container). File is renamed, so **Jellyfin re-indexes it and watch history for the title resets.** | Codec-container mismatch (e.g. HEVC in AVI); files whose extension lies about their container |
| 4 | **Re-encode video** | `-c:v libx264 -preset medium -crf 20 -c:a aac -b:a 192k` into MKV. Slow (hours per file). Runs in the background scheduled scan; runtime is not a concern. | Everything else — bitstream damage that survives remuxing |

Each rung writes to a sibling temp file: `<original-name>.repair-tmp<N>.<ext>` where `<N>` is the rung number. `ponytail:` comment on each rung method names the specific failure mode it targets, so future edits know why the rung exists.

### 4.2 Between-rung verification

After each rung writes its temp file, verify before promoting:

- ffprobe the temp.
- Duration within 2s of the original's expected duration (per CLAUDE.md safety invariant #3).
- At least one video stream present (invariant #2).
- At least one audio stream present (invariant #2).
- Full-file decode sample (reuse existing `ProbingScannerBase` sampling — 5s from start, middle, end; for files <60s use `PlayabilityScanner.ShouldSampleWholeFile` full-decode path).

Verify pass → move original to `RecycleBin`, rename temp into place, return `FixResult.Success` with the rung name in the message. Verify fail → delete temp, proceed to next enabled rung.

The temp file **is** the "copy before touching" safety requirement — the original is never mutated in place, only recycled after a verified replacement exists.

### 4.3 Safety hard-stops

- Rung 2 refuses to run if dropping the identified broken streams would leave zero video or zero audio streams. Skip to rung 3 in that case.
- Pre-flight: ≥2× source size free disk before rungs 1–3; ≥3× source size free before rung 4. Fail closed to today's delete path if pre-flight fails.
- Re-verify at fix time that the file is still broken (existing `PlayabilityFixer.IsStillBrokenAsync` — unchanged).
- All destructive steps continue to respect `Configuration.GetDisposal(IssueType.Playability)` and the global dry-run toggle (invariant #4).

### 4.4 Settings card

New card in `configPage.html` under the existing Playability section:

**Title:** Repair broken files
**Blurb:** "Before deleting an unplayable file, MediaDash can try to repair it. Each step is more aggressive than the last. Steps run in order and stop at the first one that produces a working file."

| Checkbox label | Config property (bool) | Default |
|---|---|---|
| Quick remux — fix container damage without re-encoding | `RepairAttemptRemux` | true |
| Drop broken streams — remove tracks that fail to decode (keeps at least one video and one audio track) | `RepairAttemptDropStreams` | true |
| Change container — repack into MKV. **Jellyfin will treat the file as new and watch history for this title will reset.** | `RepairAttemptContainerCoerce` | true |
| Re-encode video — last resort. Can take hours per file, runs in the background. | `RepairAttemptReencode` | true |

All four unchecked = repair disabled = identical to today's behavior. No separate master toggle; "all off" IS master-off.

### 4.5 Flow

```
FixAsync(issue)
├── file missing? → fail (unchanged)
├── outside library? → fail (unchanged)
├── IsStillBrokenAsync? no → fail "plays fine now" (unchanged)
│
├── if RepairAttemptRemux         → try rung 1 → verify → swap + return Success
├── if RepairAttemptDropStreams   → try rung 2 → verify → swap + return Success
├── if RepairAttemptContainerCoerce → try rung 3 → verify → swap + return Success
├── if RepairAttemptReencode      → try rung 4 → verify → swap + return Success
│
└── fall through → existing delete-to-recycle-bin path (unchanged)
```

Swap = move original to `RecycleBin`; rename verified temp into place; refresh Jellyfin library monitor.

### 4.6 Fix-result messaging

Success messages name the rung so users can see what worked:

- Rung 1: `"repaired <name> (quick remux)"`
- Rung 2: `"repaired <name> (dropped N broken stream(s): <kinds>)"`
- Rung 3: `"repaired <name> (container changed .<old>→.mkv; Jellyfin watch history for this item was reset)"`
- Rung 4: `"repaired <name> (video re-encoded)"`
- All rungs failed → today's delete message (unchanged).

## 5. Files touched

Five files, no new ones:

1. `Jellyfin.Plugin.MediaDash/Fixers/PlayabilityFixer.cs` — add `TryRepairAsync`, four `TryRung<N>Async` private methods, verify helper. Branch inserted before the existing delete block.
2. `Jellyfin.Plugin.MediaDash/Configuration/PluginConfiguration.cs` — four new bool properties, defaults `true`.
3. `Jellyfin.Plugin.MediaDash/Configuration/configPage.html` — new "Repair broken files" card with four checkboxes.
4. `Jellyfin.Plugin.MediaDash.Tests/Fixers/PlayabilityFixerRepairTests.cs` — new file, 8 unit tests (see §6).
5. `CHANGELOG.md` — bullets per repo format rule.

Real-world test fixtures live in `tools/repair-test/` (new folder — this is test infra, not plugin code).

## 6. Tests

### 6.1 Unit (8)

1. Rung 1 succeeds → temp swap, original in recycle bin, verify called once.
2. Rung 1 fails verify, rung 2 drops one broken subtitle, verifies, swaps.
3. Rung 2 refuses (dropping would leave 0 audio), rung 3 coerces to MKV, verifies, swaps. Success message includes watch-history warning.
4. Rungs 1–3 fail, rung 4 re-encodes, verifies, swaps.
5. All rungs fail → falls through to existing delete path; original recycled, no leftover temp files.
6. Safety: rung 2 never leaves 0 audio or 0 video (asserted directly on the stream-drop planner).
7. Individual toggle OFF skips its rung and moves to the next (parameterized across all four).
8. **Regression:** all four toggles OFF → identical output and side-effects to today's `PlayabilityFixer`.

### 6.2 Real-world (per repo E2E rule)

`tools/repair-test/` contains four broken-file fixtures with a `README.md` describing what each is corrupt in and how to regenerate:

- `fixture-bad-index.mkv` — recoverable by rung 1
- `fixture-decode-error-sub.mkv` — recoverable by rung 2
- `fixture-hevc-in-avi.avi` — recoverable by rung 3 (container coercion)
- `fixture-bitstream-damage.mkv` — recoverable only by rung 4

Each fixture is scanned and fixed through localhost:8099, then the repaired file is played through the Jellyfin web player to confirm it actually works. Not "the code returned success" — actual playback.

## 7. Version & release

**Target:** 1.0.8.0.

Ships as part of the post-1.0.7.x feature bundle alongside the audio-conversion scanner, media organiser, and Library-tab redesign (per `project_mediadash_roadmap`). Third-digit bump per the locked `feature.feature.feature.bugfix` rule — this adds capability, not a bugfix.

Ordering within the 1.0.8.0 bundle is the roadmap's call; recommendation is media-organiser → audio-conversion → this repair change → Library redesign (touches least surface area first).

Release process unchanged: `tools/release.ps1 -Version X.Y.Z.W -Changelog "..."` (do not hand-edit manifest checksums, per CLAUDE.md).

## 8. Follow-ups (out of scope, note only)

- Fifth rung: audio re-encode. Currently belongs to the separate audio-conversion scanner; if that scanner ships in the same bundle, wire the two together so repair can hand off audio-only damage.
- Per-library repair settings (mirror the audio-scanner pattern once that lands).
- Repair statistics on the Overview tab: "N files repaired this month, X GB saved from deletion." Requires a new counter in the SQLite state.
- Configurable rung-4 encoder target (currently hardcoded h264/aac). Wait for user demand before adding the knob.
