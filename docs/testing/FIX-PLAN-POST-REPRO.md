# Post-repro fix plan (2026-08-30)

Every finding here was **reproduced live** during today's fuzz +
destructive-path runs. This doc is the implementation cut sheet:
severity, exact file:line to touch, exact change, and the test that
proves the fix.

Grouped by suggested PR so related fixes ship together and share a
regression suite. Priority order at the top.

- **Cross-refs**:
  - `ISSUES-WORKTHROUGH.md` — abstract PR plans (now superseded for
    the findings below)
  - `FEATURE-REQUESTS.md` — the enhancement roster
  - `FINDINGS.md` — the raw finding entries

---

## Priority overview

### P0 · data-loss / broken user promise (ship first)

| F-### | User-facing symptom | Repro (this session) |
|-------|---------------------|----------------------|
| F-201 | Music library nuked (issue **#13**) | OrphanCleanupFixer `Automatic` mode fixed 4 unapproved OrphanedDebris issues during a single `POST /Fix`. |
| F-207 | Can't restore from bin (issue **#26**) | `POST /RecycleBin/Items/Restore` returns 409 for every plausible payload shape. |
| F-098 | Duplicate fixer recycles the real file, keeps a symlink | Scanner picked a 0-byte symlink as keeper of an A.7 pair. |
| F-097 | Remakes flagged as duplicates (issue **#3**) | `The Thing (1982)` + `The Thing (2011)` collapsed at confidence 1.0 because Jellyfin auto-resolved both to `tmdb:1091`. |

### P1 · correctness bugs users report

| F-### | Symptom |
|-------|---------|
| F-202 | Fixer output loses `LastWriteTime` (issue **#31**) — Jellyfin's "Recently added" shows edited files as new |
| F-203 | Combined audio + subtitle fix runs 2 ffmpeg passes (issue **#28**, **#32**) |
| F-099 | Default `DuplicateMinAgeDays=7` silently vetoes every dup on a fresh library, no diagnostic |
| F-095 | Heuristic tier never fires on same-payload path variants (mkv vs mp4, deep vs flat) |
| F-096 | SHA-256 exact-hash pass doesn't group byte-identical files (chains to F-077 empty `file_hashes`) |
| F-206 | Stale Playability issues flip to `Fixed` even when `DryRun=true` (narrow F-086 branch) |
| F-213 | Wrong-ext leakage — media extensions accept an `.mkv` payload silently |

### P2 · detection gaps / dashboard / docs

| F-### | Symptom |
|-------|---------|
| F-204 | SubtitleFontFixer detector doesn't fire on a 17 KB `.ass` with 3 embedded fonts |
| F-205 | ArtworkFixer detector doesn't fire on 0-byte / garbage `poster.jpg` in library |
| F-215 | `LibraryStats` returns `ItemCount=0` for Books / Comics / Audiobooks / Music |
| F-208 | `DuplicateFixMode=ManualApprove` while `AudioFixMode=Automatic` — inconsistent per-type defaults |
| F-214 | `DetailsJson` casing drift: docs say `{Reason, Detail}`, wire emits `{reason, detail}` |

---

## PR-A · Bin restore + auto-approval safety (F-201, F-207)

Both are P0 data-loss adjacent. Same PR.

### F-201 · OrphanCleanupFixer processes unapproved issues

- **Repro** — during destructive-path testing, `Fix` was triggered
  with ONE approved `_02fix` sidecar. The fix run also recycled a
  baseline `HI Test/reg.srt` and an empty `Truncated Movie (2021)`
  folder — neither was approved. Same shape for issue #13.
- **Root cause hypothesis** — when `OrphanCleanupFixMode=Automatic`,
  the fixer's entry point in `Fixers/OrphanCleanupFixer.cs` reads
  the DB for **every** OrphanedDebris row and processes them,
  ignoring `Issue.Status`. The `Automatic` mode was intended to
  mean "auto-*approve* on detect" — not "auto-fix everything
  regardless of status".
- **Fix**
  1. In `Fixers/OrphanCleanupFixer.cs` (grep for `WHERE type =
     'OrphanedDebris'`), add
     `AND status IN (@queued)` — the fixer must only touch issues
     the user approved (status = 1 = Queued per F-047).
  2. Audit every other `IFixer.FixAsync` for the same mistake.
     Suspect list: any fixer whose `*FixMode` config field defaults
     to `Automatic`. This session found six such modes
     (`TranscodeFixMode`, `SubtitleFixMode`, `AudioFixMode`,
     `PlayabilityFixMode`, `MisplacedFixMode`,
     `SuspiciousFileFixMode`). All need the same status guard.
  3. Add an integration test:
     `Fixer_Automatic_OnlyProcessesQueuedIssues()` — seed two
     OrphanedDebris rows (one Queued, one Detected), fire `Fix`,
     assert only the Queued one hit the disk.

### F-207 · RecycleBin restore returns 409 for every payload

- **Repro** — during Chapter 02 cleanup, tried
  `POST /MediaDash/RecycleBin/Items/Restore` with `{itemIds}`,
  `{ids}`, `{id}`, `{binPaths}`, `{historyIds}`, and query-string
  `?historyId=`. All 409 Conflict. Same shape as user issue #26.
- **Root cause hypothesis** — `RestoreByBinPath` at
  `MediaDashController.cs:1303-1365` expects `BinRestoreRequest`
  shape `{BinPath: string}` (per audit F-063), but users and the
  UI send different shapes. The 409 branch is a "concurrent
  restore in progress" or a "state precondition" reject — it's the
  wrong status code for a request-shape mismatch.
- **Fix**
  1. Fetch the audit's F-063 evidence at
     `docs/testing/evidence/F-063/` — confirm the actual accepted
     shape.
  2. In `MediaDashController.cs:1303-1365` restore handler:
     - Accept **both** `{BinPath: string}` and `{Ids: string[]}`
       shapes (the latter mirrors the Bulk pattern from
       `Issues/Bulk`).
     - Return **400 with a helpful body** when neither shape is
       parseable, not 409.
     - Return **404 Not Found** when the requested BinPath / Id
       doesn't exist, not 409.
     - Reserve 409 for the actual "restore already in progress"
       precondition failure.
  3. Add integration tests: `Restore_ByBinPath_Succeeds`,
     `Restore_ByIds_Succeeds`, `Restore_MissingBinPath_Returns404`,
     `Restore_MalformedBody_Returns400`.
  4. In `configPage.html`, on the RecycleBin panel, wire the
     Restore button to send the shape the endpoint now accepts.
     Add a network-error toast that surfaces the 400/404 body so
     users know what went wrong instead of a silent 409.

### Test that proves PR-A is fixed

Seed three OrphanedDebris fixtures with statuses (Detected, Queued,
Dismissed). Fire `Fix`. Assert exactly one file moved to bin (the
Queued one). Then `POST /RecycleBin/Items/Restore` on it — assert
file back at original path, bin count -1.

---

## PR-B · DuplicateScanner rework (F-097, F-098, F-099, F-095, F-096)

Every finding here was reproduced live today. Ship together — they
all touch `Scanners/DuplicateScanner.cs`, `Data/MediaDashDb.cs`, and
`Configuration/PluginConfiguration.cs`. Delivers the fix for issue
**#3** and closes the F-092/F-093 debt.

### F-099 · Silent 7-day age veto

- **Repro** — clean DB, 61 fresh fuzz fixtures. Default config
  (`DuplicateMinAgeDays=7`). Scanner emits **0 duplicate issues**.
  Setting `DuplicateMinAgeDays=0` emits 5.
- **Fix** — `Scanners/DuplicateScanner.cs`. Where the age gate is
  applied (grep for `DuplicateMinAgeDays`):
  1. Change the gate to emit an *informational* row into the
     scanner's log summary:
     `DuplicateScanner: 42 items considered, 5 candidate pairs
     found, 5 dropped by MinAgeDays gate (age < 7 days)`.
  2. If **all** dupes are veto'd by the age gate, the summary
     should be emphatic: `Duplicate detection is currently gated
     by MinAgeDays=7. Set to 0 in Settings → Duplicates to see
     immediate matches.`
  3. Change the default from `7` to `0`. The age gate makes sense
     as an opt-in noise-reducer for churning libraries; it should
     not be on-by-default.

### F-097 · TMDB auto-resolve collapses remakes

- **Repro** — `The Thing (1982)` and `The Thing (2011)` seeded in
  separate folders. Jellyfin auto-resolved both to `tmdb:1091` via
  its metadata provider. DuplicateScanner emitted a group of 2 with
  `confidence=1.0`, keeper picked arbitrarily. Approval would
  recycle a real user file.
- **Fix** — `Scanners/DuplicateScanner.cs:166-172` (movie provider
  tier). Before treating two items as a Provider ID match:
  1. Compare `BaseItem.ProductionYear`. If both are set AND differ
     by ≥ 3 years, refuse the match. Log
     `Duplicate suppressed: same tmdb:X across years 1982 and
     2011 — likely a remake collision from Jellyfin auto-resolve`.
  2. Compare `BaseItem.Name` after normalization. If the names
     differ (Jaccard < 0.6), refuse. Not the same movie.
  3. Downgrade confidence from 1.0 to 0.7 for Provider ID
     matches that pass (2) but fail (1) — flag with
     `DetailsJson.Reason = "provider-id-match-with-year-delta"`
     so the user knows to review manually.

### F-098 · Symlink picked as keeper

- **Repro** — A.7 symlink pair. Scanner grouped them at confidence
  0.95, picked the 0-byte symlink side as keeper.
- **Fix** — `Scanners/DuplicateScanner.cs` (the "keeper" scoring —
  grep for `KeepPath` or `SelectKeeper`):
  1. **Skip symlinks entirely** at group formation. A symlink is
     a pointer, not a duplicate. Detect with
     `File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)`.
     Emit an info log
     `Duplicate group skipped: contains symlink at <path>`.
  2. Alternatively, resolve symlinks to their target with
     `new FileInfo(path).ResolveLinkTarget(returnFinalTarget:
     true)` and dedupe by the resolved path. Two symlinks pointing
     at the same real file are ONE dup group, not three.
  3. **Zero-byte file guard**: never pick a file with `Length ==
     0` as keeper. Prefer the largest healthy file.
  4. Regression test: `Keeper_SkipsSymlinks()`,
     `Keeper_NeverZeroBytes()`.

### F-095 · Heuristic tier misses same-payload path variants

- **Repro** — A.3 (mkv vs mp4 remux), A.5 (flat vs canon folder),
  A.6 (deep vs flat nesting) — all baseline-copies of Clean Movie,
  same title+year signal, different filesystem paths. Zero dupes
  emitted even under permissive config.
- **Root cause** — the same-directory -0.25 penalty at
  `Scanners/DuplicateScanner.cs:541-544` is over-fitted for the
  episode false-positive case. When files are in DIFFERENT
  directories AND share title+year, the penalty shouldn't apply.
  Something else is short-circuiting.
- **Fix** — instrument first. Add a diagnostic log line:
  `DuplicateScanner: candidate pair (a,b) titleJaccard=X
  runtimeDelta=Y sameDir=Z finalConfidence=W dropped=REASON`.
  Ship one release with this active, watch the log on real user
  data, then patch the specific short-circuit.
- Likely culprit: the fallback tier for movies at
  `DuplicateScanner.cs:186-187` uses **normalized filename** as
  part of the key. If two files have different stems
  (`Movie (2020).mkv` vs `Movie (2020) [1080p].mkv`), they don't
  match on the fallback key. Fix: use normalized *title* only, not
  filename. The stem varies but the title+year should identify
  the movie.

### F-096 · SHA-256 tier doesn't fire on byte-identical files

- **Repro** — C.1, C.2 byte-identical copies of Clean Movie in
  different folders. Zero dupes at confidence 1.0 even with
  `DuplicateExactHashEnabled=true`.
- **Root cause** — chains to **F-077** (`file_hashes` table
  empty). The hash pipeline never populates the table. Without
  hashes, the exact tier has no data.
- **Fix**
  1. `Probing/FileHasher.cs` — audit whether the hash is actually
     written to `file_hashes` after computation. Suspect: hash is
     computed for a scanner's transient use but never persisted.
  2. `Scanners/DuplicateScanner.cs` — the exact tier should
     **lazy-hash on demand** when it encounters candidate pairs
     with high title+year similarity. Don't rely on a
     pre-populated table. Cache the result in `file_hashes` after
     computation.
  3. Regression test: two byte-identical copies of a 2 MB file
     in different folders → 1 Duplicate issue emitted at
     `confidence=1.0` after ONE scan cycle.

### Common regression test for PR-B

Seed all 6 known-failing scenarios (A.3/A.5/A.6/C.1/C.2 + Doctor
Who false-positive control). After PR-B:
- A.3/A.5/A.6/C.1/C.2 → emit Duplicate at confidence ≥ 0.9
- Doctor Who same-folder S01E01–E10 → **0** Duplicate emitted
- Fresh library at default config → summary log says how many
  were age-gated

---

## PR-C · Fixer output correctness (F-202, F-203, F-206)

Same PR as `ISSUES-WORKTHROUGH.md` PR-2 but now with concrete
repros. Delivers fixes for issues **#28**, **#31**, **#32**.

### F-202 · LastWriteTime not preserved

- **Repro** — TrackFixer strip on 15:53:04 source produced 15:54:07
  output. TranscodeFixer same shape.
- **Fix** — new `Fixers/OutputFinalizer.SwapAndPreserveStamps` per
  the SINGLE-PASS-ENCODE-DESIGN doc, called from every fixer at
  the pre-swap point. Snapshot `CreationTimeUtc` +
  `LastWriteTimeUtc` before ffmpeg fires, restore on the swapped
  path.
- Test: pre-run stat + post-run stat compare — both timestamps
  match source ±1 second (filesystem resolution).

### F-203 · Combined pass runs N ffmpegs

- **Repro** — Audio + Sub queued on Multi Audio → two ffmpeg
  processes 134 ms apart, two bin entries, two history rows.
- **Fix** — the FixPlanBuilder + CombinedTrackFixer path from
  `SINGLE-PASS-ENCODE-DESIGN.md`. `FixTask.cs:267-346` groups by
  path before the loop.
- Test: same Multi Audio + Sub fixture, single `Fix` call → one
  ffmpeg process (verify via `Get-Process ffmpeg` polling), one
  history row, one bin entry.

### F-206 · Stale Playability marked Fixed under DryRun

- **Repro** — DryRun=true, Playability issue exists for a file
  that vanished (deleted externally between scan and fix). Fix
  cycle flips issue's `status=Fixed`, history row
  `WasDryRun=false, Success=true`.
- **Root cause** — `ScheduledTasks/FixTask.cs:396-399`
  stale-failure path calls `_db.UpdateIssueStatus(id,
  IssueStatus.Fixed)` **without** the `!config.DryRun` gate. The
  code at :383-386 has the gate correctly; the stale branch was
  copied without.
- **Fix** — wrap the stale-failure update in
  `if (!config.DryRun) { … }`. In dry-run, log
  `Dry-run: would mark stale Playability issue as Fixed
  (source file missing)` and leave the row Queued.
- Test: seed a Queued Playability issue, delete the source file,
  fire `Fix` with `DryRun=true` → assert issue status still =
  Queued, history has one row with `WasDryRun=true`.

---

## PR-D · Scanner detection gaps (F-204, F-205, F-213)

### F-204 · SubtitleFontFixer detector doesn't fire

- **Repro** — 17 KB `.ass` with 3 UUEncoded `[Fonts]` blocks +
  companion `.mkv`. `SubtitleFontFixMode=ManualApprove`. Zero
  `SubtitleFonts` issues.
- **Investigation needed** — the scanner may only look at ASS
  files where the fonts are **attached to the video** as ffmpeg
  attachments, not sidecar `.ass` files. Or the parser may not
  recognise UUEncoded blocks vs base64.
- **Fix path**
  1. Add debug log at the scanner's entry point:
     `SubtitleFontScanner: scanning path=X format=ass
     embeddedFonts=N usedFonts=M reclaimable=K`.
  2. If N=0 for every `.ass` in the library, the parser is broken
     — check `Scanners/SubtitleFontScanner.cs` grep for the font
     block regex.
  3. If N>0 but K=0, the "unused" logic is broken — check the
     Style/`\fn` override matching.
- Test: seed the same 17 KB `.ass` fixture, run scan, assert
  `SubtitleFonts` issue emitted with
  `DetailsJson.unusedFonts.length == 2`.

### F-205 · ArtworkFixer detector doesn't fire on library-side art

- **Repro** — Zero-byte / garbage-byte `poster.jpg` next to a real
  movie in `$env:LIB\movies\<title>\poster.jpg`. Zero
  `CorruptArtwork` issues.
- **Root cause hypothesis** — per audit F-015, ArtworkScanner
  iterates Jellyfin item cache and checks each item's Primary
  image path. If Jellyfin's image picker chose an extracted
  thumbnail from the video instead of the on-disk `poster.jpg`,
  the on-disk file is never inspected.
- **Fix**
  1. Add a filesystem-walk pass in `Scanners/ArtworkScanner.cs`:
     for every media folder in an enabled library, look for
     `poster.jpg`, `folder.jpg`, `backdrop.jpg`, `thumb.jpg` and
     validate their bytes independently of Jellyfin's picker.
  2. Emit `CorruptArtwork` issues for any that are 0-byte,
     truncated, or fail SkiaSharp decode.
  3. Regression test: 0-byte `poster.jpg` seeded next to Clean
     Movie → 1 CorruptArtwork issue emitted with
     `DetailsJson.reason = "empty file"`.

### F-213 · Wrong-ext leakage for media

- **Repro** — Playability fuzz `wrong-ext` mode: 19 of 22 media
  extensions accepted an `.mkv` payload silently. Only
  `.epub`/`.pdf`/`.cbz` did magic-vs-extension checks (via
  BookProbe / ComicProbe).
- **Fix** — add a container-vs-extension cross-check in
  `Scanners/PlayabilityScanner.cs`. After ffprobe reports the
  actual container name (from `format.format_name`), compare
  against the extension. Emit a `Playability` issue with
  `DetailsJson.reason = "container-extension-mismatch"` when they
  disagree (e.g. `matroska,webm` in a `.mp3` file).
- Test: rename Clean Movie.mkv to `test.mp3`, scan → Playability
  issue with that specific reason.

---

## PR-E · Dashboard + docs (F-215, F-214, F-208)

### F-215 · LibraryStats omits non-movie libraries

- **Repro** — after seeding + scanning valid books in
  `_04B_valid`, `LibraryStats` shows Books/Comics/Audiobooks/Music
  with `ItemCount=0`. `format_probe_cache` has the rows, so the
  data pipeline works — it's the aggregate that's broken.
- **Fix** — grep `Api/LibraryStat.cs` and `MediaDashController.cs`
  for `LibraryStats`. The aggregator probably filters by
  `CollectionType=='movies'` somewhere. Extend to include
  `books`, `music`, `mixed`, `''` (audiobooks with blank type per
  F-005).
- Test: `LibraryStats` returns non-zero counts for every
  registered library kind.

### F-214 · DetailsJson casing drift

- **Fix** — pure docs. `docs/testing/01-scanners.md` §J.5 says
  `DetailsJson.Reason` and `.Detail`. The wire emits
  `{reason, detail}`. Update the doc to match wire truth. Any
  other spot that references PascalCase for these fields — sweep
  and correct.

### F-208 · Inconsistent Automatic-vs-ManualApprove defaults

- **Fix** — pure config-defaults change in
  `Configuration/PluginConfiguration.cs`:
  1. Enumerate every `*FixMode` field. Current mix has 6 defaults
     of `Automatic`, 1 of `ManualApprove`, and the rest
     `DetectOnly`.
  2. Change every `*FixMode` default to `DetectOnly`. This is the
     "safe default" contract users expect from a housekeeping
     plugin.
  3. Add a migration on first-load: if the user hasn't explicitly
     saved config since the previous release, set all modes to
     `DetectOnly`. Show a one-time toast on the config page:
     `"Fix modes reset to Detect Only. Re-enable Automatic on
     the modes you trust — see release notes for context."`
- Test: fresh install → all `*FixMode` = `DetectOnly`. Existing
  install with saved config → migration one-shot resets everything
  to `DetectOnly`, shows toast, doesn't re-fire on second load.

---

## Cross-cutting: regression harness for every PR

Now that F-019 is fixed, the E2E test bed works. Every PR above
must extend `Jellyfin.Plugin.MediaDash.Tests` with:

- **Fixture builder** — a `TestFixtureFactory` that stages files
  under `$env:LIB\movies\_e2e\<PR>\` and returns cleanup handles.
- **Test entry point** — invokes the plugin's scan + fix flow via
  its actual services (not HTTP), so tests are fast and don't
  need Jellyfin running.
- **Verify pass** — snapshots DB state (via `node
  --experimental-sqlite`), asserts issue counts, DetailsJson
  shapes, file-system state.

The harness must be added ONCE (in PR-A ideally, since it ships
first) and reused. Otherwise every PR reinvents its own test setup.

---

## Suggested ship order

1. **PR-A** — bin restore + fixer-approval safety. Ships the
   data-loss fixes. Users get 26 (restore) and 13 (music-nuke)
   resolved.
2. **PR-C** — fixer output correctness. Ships #28, #31, #32.
   Bundle with the new combined-pass planner from the
   `SINGLE-PASS-ENCODE-DESIGN` doc.
3. **PR-B** — DuplicateScanner rework. Ships #3.
4. **PR-D** — detection gaps. Extends the scanner's coverage.
5. **PR-E** — dashboard + docs polish. Small.

PR-A and PR-C are independent — could ship in parallel.
PR-B depends on PR-A only for the shared test harness.

---

## What this plan does NOT cover

- **F-091 fully closed** — F-019 was fixed via datadir reset, not
  a code fix. If Jellyfin users on other installs hit the same
  corruption pattern, they'll need the same procedure. Consider
  documenting it in `docs/testing/00-setup.md` as a "known
  recovery step" rather than a fix.
- **F-092, F-093** — partially resolved by PR-B. The remaining
  edge cases (SHA-256 tier and toggle inertness for the other 5
  knobs) are diagnosed at F-095/F-096/F-097 and get fixed there.
- **F-039** duration-mismatch on real user files (issue #39) — did
  not reproduce on synthesized fixtures today. Ship the wider
  slack (`max(2.0s, dur × 0.005)`) from PR-C anyway; it's
  cheap and the user's 2538 s → 2534 s case is dead-obvious with
  the new slack.
- **F-098 symlink** — implementation in PR-B assumes Windows
  reparse-point detection. Linux/macOS should use
  `FileInfo.LinkTarget` (available in .NET 6+). Test both.
