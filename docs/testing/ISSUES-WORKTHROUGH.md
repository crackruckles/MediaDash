# GitHub Issues — Workthrough Plan

Cross-references every open GitHub issue against the audit findings
(`FINDINGS.md`) and lays out a concrete fix order. Closed issues are
listed at the bottom for pattern reference — most closed issues describe
the same root cause as an open one.

Format per issue:
- **Root cause** — one-liner hypothesis
- **Findings** — F-### from our audit that overlap (if any)
- **Fix** — concrete change (file / behaviour, not code)
- **Priority** — P0 data-loss, P1 core-broken, P2 UX/feature, P3 nice

Repo: <https://github.com/crackruckles/MediaDash>. Issue list snapshot
in `docs/testing/issues/gh-issues-raw.json` (37 issues, 21 open, 16
closed at capture time).

---

## Recycle Bin — the biggest cluster (7 open, 4 closed)

### #40 · "Both are and are not files in the Recycle Bin"

- **Root cause** — bin *file count* and *item list* are computed from
  two different sources. UI shows `FileCount=16 292` from the size
  scanner while `Items[]` from the item enumerator returns empty. Same
  class of bug as F-061 (during audit, `Info.FileCount=2` but
  `Items[]` had 1 row).
- **Findings** — F-061, F-033 (RecycleBin/Info shape drift), F-060.
- **Fix** — one canonical source of truth. Either `RecycleBinInfo.
  FileCount` should be `Items.Count`, or both should read from the
  same DB view. Suspect: `RecycleBin.SizeScan` background writes a
  cached count that goes stale when the item enumerator can't parse a
  row.
- **Priority** — **P0** (users can't recover 93 GB).

### #26 · "Cannot revert / restore files" (user with 128 GB in bin, dry-run showed 8 GB fixes)

- **Root cause** — restored files fail because the bin-path metadata
  written by some scanners is missing/wrong (see maintainer comment:
  "two of the scanners were not pushing the path details correctly").
  Also touches F-063 — Restore returns `{RestoredTo, Suffixed}` and
  the request payload is `{BinPath}` — the client may be sending
  `{ids:[...]}`.
- **Findings** — F-063, F-087 (Restore/AdoptBatch DTO drift), F-086
  (dry-run flips issue status to Fixed in DB but "8 GB reclaimed"
  number confuses users when 128 GB is in the bin).
- **Fix** — normalise the recycle-record write path: every fixer must
  persist `OriginalPath` + `BinPath` + `SourceHistoryId` before the
  file is moved. Restore path re-reads that record. Also add the
  `-restored` suffix (maintainer already committed to this).
- **Priority** — **P0** (users can't get their files back).

### #33 · `RecycleBin.SizeScan`: "Could not stat recycled file … Total-size total will be short by one entry."

- **Root cause** — a bin entry's DB row references a file that was
  externally deleted (e.g. via Windows Explorer). Size scanner
  doesn't tolerate the gap and short-changes the total.
- **Findings** — none direct; adjacent to F-061 (info-vs-items count
  drift).
- **Fix** — SizeScan should treat missing files as "0 bytes, mark
  entry as `Vanished`". Emit one INFO log per missing entry, not one
  ERROR per scan cycle.
- **Priority** — **P1**.

### #19 · "Fix run — disk error … cannot access the file because it is being used by another process"

- **Root cause** — Jellyfin's own scanner holds a read lock on the
  same file when the fixer tries to open it exclusive. Windows file
  sharing.
- **Findings** — R.3 in chapter 01 tested this: with a `.NET File.
  Open(..., 'Read', 'None')` handle held, the scanner completed in
  4 s with no retry-log line. The scanner survived, but the FIXER
  path (not tested this session because DryRun=on) is where the lock
  bites.
- **Fix** — TrackFixer / TranscodeFixer must open with
  `FileShare.Read` (or `.ReadWrite`) rather than `.None` when reading
  the source. Retry-with-backoff (max 3 × 2 s) before failing. Add a
  clear "held by another process — try pausing Jellyfin scans and
  retry" message.
- **Priority** — **P1**.

### #29 (closed) · Recycle bin defaults to boot drive, size warning is display-only

Referenced by open #17, #20, #24 which all describe the same crash
mode. Marked closed but the "warn me when bin exceeds X GB" fix is
partial — needs a hard *stop-writing* cap, not just a banner.

- **Followup for the maintainer** — audit whether the current build
  still allows writes past the cap.
- **Findings** — audit noted `RecycleBinPath` defaults to
  `<DataPath>/mediadash/recycle` (T-002). That default is fine on
  fixed servers but toxic on containers/NASes where `DataPath` is on
  a 20 GB volume.

---

## Fixer — the second cluster (5 open, hitting core paths)

### #39 · TrackFixer fails on duration mismatch ("original 2538.4s, new 2534.0s")

- **Root cause** — `-c copy` remux drops the last few seconds
  because some containers/codecs place a "hint" packet after the
  last frame and ffmpeg's copy pass rounds it away. The
  post-verification duration check is too strict (< 5s slack).
- **Findings** — F-013 (fixer runs multiple ffmpeg passes;
  combining into one would likely dodge this) and F-034 (Sub Heavy
  fixture doesn't actually contain rus/fra — hints that
  ffprobe-reported duration != playable duration on some
  Matroska files).
- **Fix** — Two knobs:
  1. Widen the duration slack from ±2s to ±5s (or 1% of duration,
     whichever is larger).
  2. If mismatch persists, re-encode the audio drop path with
     `-c:v copy -c:a copy -avoid_negative_ts make_zero
     -fflags +genpts` — fixes the packet-hint problem in most
     Matroska cases.
- **Priority** — **P1** (blocks every non-English user's use case).

### #31 · File Dates — new file carries today's date, breaks "Recently Added"

- **Root cause** — fixer writes with default filesystem stamps.
- **Findings** — **F-016** (maintainer note filed during audit).
- **Fix** — after any fixer that writes a new file, call
  `File.SetCreationTimeUtc` and `File.SetLastWriteTimeUtc` from the
  source metadata BEFORE the source is recycled. See F-016 for the
  full plan.
- **Priority** — **P1**.

### #28, #32 · Combine subtitle + audio removal into one action

- **Root cause** — fixer runs one ffmpeg per queued category on the
  same file (three category → three passes).
- **Findings** — **F-013** (maintainer note filed during audit).
- **Fix** — a per-item planner that combines all queued track ops
  into a single `ffmpeg -map ...` call with all `-map -0:a:<n>` /
  `-map -0:s:<n>` selectors in one graph. Maintainer already
  committed in the issue thread.
- **Priority** — **P1**.

### #34 · Throttle re-encoding / muxing (I/O throttling)

- **Root cause** — fixer runs ffmpeg at full I/O.
- **Fix** — expose a config knob (`FixerIoThrottleMBps`,
  `FixerCpuNiceness`). On Windows, set process priority to
  `BelowNormal`; on Linux, `ionice -c 3`. Add a `--limit_output_speed`
  ffmpeg flag path.
- **Priority** — **P2**.

### #13 · "Music library nuked" — user hadn't enabled music

- **Root cause** — Misplaced scanner + orphan-cleanup ran on a
  library that the user hadn't opted-in. Music files matched
  "orphaned debris" because their extensions didn't match the
  scanner's per-library codec filter (or the filter was empty →
  all files unrecognised).
- **Findings** — Adjacent to F-025 (Trickplay 5-item sample kills
  whole-library walk — same shape: a scanner's early-exit
  heuristic making the wrong decision at scale), F-035 (music
  library scanner semantics), F-021 (OrphanCleanup double-emit).
- **Fix** — every scanner **must** honour `EnabledLibraryIds` from
  config. Currently some scanners (per F-021 evidence) walk all
  registered libraries regardless. Add a whole-library dry-run
  preview: "this run will touch libraries: [Movies, Music]" — and
  refuse to proceed if any library isn't explicitly listed.
- **Priority** — **P0** (data loss).

---

## Scanner — misidentifications & false-positives (4 open, 1 closed)

### #3 · Mis-Identified Duplicates (comment thread: Doctor Who classic all flagged, Six Million Dollar Man collapsed to 3 eps)

- **Root cause** — DuplicateScanner uses Jellyfin's item ID (which
  collapses versions) as one of its signals; when metadata is thin
  (older shows), Jellyfin puts many episodes under one item and the
  scanner reports them all as duplicates of each other.
- **Findings** — **F-029** (DuplicateScanner misses TRUE duplicates
  — two items sharing a media path — while producing false
  positives here). Also F-015 (fixture design maps to real user
  behaviour: the same folder-name → same item collapse).
- **Fix** — the maintainer's comment says: mark Duplicate as
  manual-only and rework. Recommend also making it *item-aware*:
  never call two files "duplicates" if they belong to a Series with
  different `IndexNumber` values (i.e. `S01E01` vs `S01E02`), even
  if Jellyfin conflated their item IDs.
- **Priority** — **P0** (delete-friendly false positive).

### #38, #23 · SmartHealth WMI failures on NVMe drives / mount points

- **Root cause** — `SmartHealthProbeWmi` calls Win32 methods that
  don't cover NVMe. On failure it retries every scan cycle and
  spams the Errors tab.
- **Findings** — **F-073** (SMART data lives on `/Status.Drives`,
  not `/Environment`; the doc's assumption is wrong), F-078
  (probing diagnostics not persisted).
- **Fix** — probe NVMe via `Msft_PhysicalDisk` (WMI namespace
  `root/Microsoft/Windows/Storage`) — this DOES surface NVMe wear
  and temperature. And: rate-limit the error emission to once per
  drive per session (not every scan).
- **Priority** — **P2** (annoyance, not correctness).

### #6 · SmartHealth "Permission denied" on `smartctl` (Linux, unprivileged)

- **Root cause** — Jellyfin runs as non-root, `smartctl` needs
  `CAP_SYS_RAWIO` or setuid.
- **Findings** — F-073, F-078.
- **Fix** — detect permission error, downgrade to WARN-once (not
  ERROR-per-scan), surface a one-line "grant CAP_SYS_RAWIO or set
  `SmartHealthEnabled=false`" doc link on the Environment tab.
- **Priority** — **P2**.

### #18 (partly closed with v1.0.7) · Advanced Subtitle Rules — SDH/HI handling

- **Root cause** — `SubtitleLanguageScanner` treats every `.en.srt`
  as equivalent, so SDH sidecars get flagged as orphaned debris.
- **Findings** — **F-021** (OrphanCleanup double-emit — same code
  path that flagged HI Test `sdh.srt` as orphaned in our fixture
  library despite the parent `.mkv` existing). **F-032** (Subtitle
  DetailsJson shape), **F-033** (no disable toggle for detection).
- **Fix** — maintainer already shipped v1.0.7 with SDH/HI detection.
  Follow-up per the last comment: allow per-language "missing subs"
  policy (e.g. "only warn if eng subs missing, don't warn about
  missing 'my native language' subs").
- **Priority** — **P2** (partial ship, needs follow-through).

---

## Config UI / Scanner Enablement (2 open, 1 closed)

### #21 (closed) · "With just Embedded cover art enabled, other scans are still ran"

Related pattern: scanners run regardless of the enable toggle. Watch
for regressions.

### #22, #30, #27 · Per-library / per-folder rules

- **Root cause** — settings are global; users want per-library
  policy for target resolution, wanted audio languages, and whether
  to extract subs to sidecars.
- **Findings** — **F-084** (three settings — `CodecPreferenceOrder`,
  `ScanCpuThreads`, `ScanBelowNormalPriority` — already exist in
  config but have no UI control; UI is a shape-blocker).
- **Fix** — refactor `PluginConfiguration` to a per-library override
  layer. `PerLibraryOverride[libraryId] = { MaxHeight,
  AllowedAudioLanguages, AllowedSubtitleLanguages, ...
  fallback-to-global }`. Then rebuild the Settings tab to render one
  card per registered library.
- **Priority** — **P2** (feature).

### #35 · Edit the queue (remove accidentally-approved issues)

- **Root cause** — no UI or API to unqueue.
- **Fix** — add `POST /MediaDash/Issues/{id}/Unqueue` (flip
  `IssueStatus` back from Approved/Queued to Detected). Frontend
  button on the Issues tab. Maintainer said "fix coming in next
  build".
- **Priority** — **P1** (users report accidents).

### #36 · Explain why file can't be played

- **Root cause** — Playability issue's `DetailsJson` has
  `{Reason, Detail}` but the UI only surfaces `SuggestedFix`.
- **Findings** — **F-017** (Playability DetailsJson field-name
  drift). **F-031** (Quality DetailsJson `videoBitrate=0` — same
  info-loss pattern).
- **Fix** — Issue card should render `DetailsJson.Reason` in bold
  and `DetailsJson.Detail` (ffmpeg tail) in a collapsed
  `<details>`. Requires the Issues panel HTML in Configuration.
- **Priority** — **P2**.

### #37 · Delete watched media

- Feature request. Maintainer wary of auto-delete.
- **Fix** — add opt-in `AutoDeleteWatchedAfterDays` on the Stale
  scanner. Only for items with playState=Played by ALL users
  (whitelist). Never on-by-default. **F-036** (Stale uses
  DateCreated not mtime) needs fixing first.
- **Priority** — **P3**.

---

## Cross-cutting docs / plumbing

### #39, #33, #26, #6 all include "Copy diagnostics" instructions that don't work

- **Root cause** — Errors tab is missing a `Copy diagnostics`
  button. Users have to open "Report an issue" (which spawns a new
  GH tab) to get the diagnostics onto clipboard.
- **Fix** — add a standalone `Copy diagnostics` button on the
  Errors tab that just calls `navigator.clipboard.writeText` with
  the same payload the "Report an issue" flow prepares.
- **Priority** — **P2** (docs/discoverability, affects every bug
  report).

### #38, #23 · "Consistent error with no way to silence it"

- **Root cause** — every scan cycle re-emits the same "WMI failed
  → falling back to smartctl" INFO as an ERROR. There's no
  per-drive suppression.
- **Fix** — see #38/#23 above. Dedupe by `(component, drive, error
  kind)` — one entry per session.
- **Priority** — **P2**.

---

## Closed issues — pattern reference (short table)

| # | Title | Fixed by | Recurring theme |
|---|-------|----------|-----------------|
| 29 | Bin fills boot drive, no cap | Warning banner added, cap NOT enforced | Bin location (see #40) |
| 24, 17, 20, 2 | Bin location / can't empty | v1.0.x fixes | Bin location (see #40) |
| 25 | Offload transcoding to remote | Declined (out of scope) | — |
| 21 | Scanners run regardless of toggle | Fixed | Config gating (watch for regression on #13, #22) |
| 14 | Persian missing from lang list | Fixed | Language list |
| 12 | Feedback | — | — |
| 10 | Dry-run still physically moves | Docs clarified | See F-086 — dry-run still writes to DB |
| 9 | Partial localisation | Fixed | See F-080 (26 nested-leaf strings still English) |
| 8 | 1.0.5 not supported on JF 10.11.5 | Version pin | Ties to F-010 (meta.json version) |
| 7 | Subs in subfolder flagged orphan | Fixed | Adjacent to F-021 |
| 5 | Scan too resource-intensive | Perf work | Ties to #34 (throttle) |
| 4 | .strm links | Added skip-list | — |
| 1 | Cross-device move fails | Cross-volume path added | Foundation for #17 |

---

## Suggested execution order

Do these in one PR each so the release notes land clean.

1. **PR-1: Bin data-model unification** — F-060/F-061/F-063 + issue
   #40, #26, #33. Ships fix for "restore fails" and "count vs items
   drift".
2. **PR-2: Fixer output correctness** — F-013 + F-016 + issues #28,
   #31, #32, #39. Combined ffmpeg pass + date preservation +
   duration-slack widening. Same PR because they all touch the
   fixer output finalise step.
3. **PR-3: Scanner enablement gating** — issue #13, closed #21.
   Every scanner must honour `EnabledLibraryIds`. Add a "libraries
   this run will touch" preview in the scan API.
4. **PR-4: DuplicateScanner rework** — issue #3, F-029. Ship
   manual-approval-only mode; series-aware detection.
5. **PR-5: DryRun DB semantics** — F-086. Dry-run must NOT mark
   issues as Fixed in the DB.
6. **PR-6: Diagnostics & SMART noise** — issues #6, #23, #38 +
   F-073, F-078. Dedupe error emission + NVMe WMI path + "Copy
   diagnostics" button.
7. **PR-7: Config surface completeness** — F-084 + issues #22, #30,
   #27. Per-library overrides + expose the three missing settings
   in UI.
8. **PR-8: Queue editing** — issue #35. Add unqueue endpoint + UI
   toggle.
9. **PR-9: Playability detail surfacing** — issue #36 + F-017,
   F-020. Render `DetailsJson.Reason` in issue cards.
10. **PR-10: i18n gap fill** — F-080 — translate the 26 missing
    nested strings across the 8 non-en locales.

Deferred (feature or opt-in): #37 (auto-delete watched), #34
(throttle), #18 follow-up (SDH per-language missing-subs
whitelist), #22 folder-based audio-language rules.

---

# Concrete plans (post-exploration)

Every plan below cites `file:line` from
`C:\dev\mediadash\Jellyfin.Plugin.MediaDash\`. Line numbers are on
`main` as of the audit — expect drift.

## PR-1 · Bin data-model unification (issues #40, #26, #33)

**Root cause of #40 (16 292 files vs "empty" claim)** — two different
count sources:

- `MediaDashController.cs:1216` (dashboard summary) reads
  `RecycleBin.GetContents()` which walks the FS
  (`RecycleBin.cs:316`).
- `MediaDashController.cs:1246` (detail list) reads
  `RecycleBin.ListContents()` (`RecycleBin.cs:378`) which enumerates
  differently and skips the `.mediadash-owned-v1` marker + origin
  manifest sidecar (`RecycleBin.cs:332-335`).

**Fix**

1. Extract a single `BinEntryEnumerator` from
   `GetContents`/`ListContents`. Return `IEnumerable<BinEntry>` with
   `BinPath`, `SizeBytes`, `RecycledAtUtc`, `OriginalPath?`,
   `Vanished:bool`. Both callers project from this one source. Kills
   the divergence and fixes F-060/F-061.
2. `BinRestoreRequest.cs` — current shape is `{BinPath}` (audit
   F-063). Add optional `{Ids:string[]}` alt so a client sending
   either shape works. `MediaDashController.RestoreByBinPath` at
   `MediaDashController.cs:1303-1365` needs the small dispatch.
3. `RecycleBin.SizeScan` (#33 error): `GetContents` at
   `RecycleBin.cs:338-366` currently *does* skip
   `FileNotFoundException` — the error the user sees is emitted
   from a different path (probably the total-size reconciler). Grep
   for `"Could not stat recycled file"` — likely
   `Fixers/RecycleBin.cs` around the SizeScan block. Wrap the stat
   in `try/catch (FileNotFoundException) { entry.Vanished = true; }`
   and change the log level from ERROR to INFO with dedupe
   (see PR-6).
4. `#26 restore fails` — the maintainer's "two scanners not pushing
   path details correctly" claim doesn't reproduce in the current
   code: every fixer that calls `RecycleBin.MoveToBin(issue.Path)`
   writes the origin manifest at `RecycleBin.cs:445-461`. Regression
   test: script all 12 fixers under `Fixers/`, invoke each on a
   dummy file, assert the origin manifest line is present. If the
   test passes, close #26 with "fixed in vX.Y.Z" and add the test
   to CI so it can't regress.

**Test** — `MediaDashDbTests`-style: seed 3 bin entries, delete one
via `Remove-Item` outside the plugin, hit `GET /RecycleBin` +
`GET /RecycleBin/Items` and assert the two report the SAME count
(both `2`, with entry 1 flagged `Vanished:true`).

## PR-2 · Fixer output correctness (issues #28, #31, #32, #39 + F-013, F-016)

**#39 duration mismatch** — audit says `OutputVerifier.cs:53-62`
uses **2.0 s slack** and reads `videoStream.Duration` first (falls
back to the MKV DURATION tag then `format.Duration`). Real cause:
the audio-strip path shrinks a container's *format* duration even
when the video stream is unchanged.

**Fix**

1. `OutputVerifier.cs:53-62` — change slack to
   `max(2.0, originalDurationSec * 0.005)` (0.5 % or 2 s,
   whichever larger). Rationale: 4 s over 2 500 s = 0.16 %, well
   inside player tolerance. Cap at 30 s so bogus files don't slide.
2. `Fixers/FfmpegExecutor.cs` — audit-cited on the encode call. Add
   `-avoid_negative_ts make_zero -fflags +genpts` to the base
   argument list at whichever line the executor builds the argv.
   Kills the packet-hint drift that's driving the mismatch.

**#31 file dates** — audit confirmed `File.SetCreationTimeUtc` /
`SetLastWriteTimeUtc` are called **nowhere** in TrackFixer,
TranscodeFixer, or RecycleBin.

**Fix** — new helper `Fixers/OutputFinalizer.SwapAndPreserveStamps`.
Called from every fixer between the ffmpeg output and the
recycle-old-original step:

```csharp
var srcInfo = new FileInfo(originalPath);
var origCreated  = srcInfo.CreationTimeUtc;
var origModified = srcInfo.LastWriteTimeUtc;
// … ffmpeg pass writes tmpOutput, verifier runs, recycle old …
File.Move(tmpOutput, originalPath);
File.SetCreationTimeUtc (originalPath, origCreated);
File.SetLastWriteTimeUtc(originalPath, origModified);
```

Call sites to update: `TrackFixer.cs:178`, `TranscodeFixer.cs:229`.

**#28 / #32 combined pass** — audit trace: `FixTask.cs:267-346`
iterates queued issues one by one, calls `fixer.FixAsync` at line
347. Nothing batches by path.

**Fix**

1. New `Fixers/CombinedTrackFixer` that accepts a list of
   `(IssueType, RemoveIndexes[])` tuples for a single path. Builds
   ONE ffmpeg command with all `-map -0:a:<n>` / `-map -0:s:<n>`
   selectors combined.
2. `FixTask.cs` — before the loop at 267, group queued issues by
   `Path`. If a group has >1 track-op issue, route to
   `CombinedTrackFixer`; else fall through to per-fixer as today.
3. Dry-run for the combined path must set
   `FixResult.WasDryRun = true` (see PR-5 — this is the assertion
   that's currently loose).

**#19 sharing violation** — audit already traced this to
`FixTask.RunFixWithSharingRetryAsync:612-625` — retry with
`500ms/2s/5s` backoff. Users still hit it. Two knobs:

- Increase max retries from 3 to 5, add jitter.
- Before the retry loop, `POST /Library/Refresh?cancel=true` (or
  Jellyfin's task-cancel endpoint) so Jellyfin's own scan can
  release its handles. Wait 3 s. Retry.

**#34 I/O throttling** — audit found `FfmpegExecutor.cs:233-248`
already lowers to `BelowNormal`. Add:

- New config field `FixerMaxIoBandwidthMBps` (int, default 0 =
  unlimited).
- On Windows: no direct bandwidth cap; use `-max_muxing_queue_size`
  and `-re` (real-time output) hints. Document the limit.
- On Linux: shell out via `ionice -c 3 -n 7` prefix when available.

## PR-3 · Scanner enablement gating (issue #13)

**Root cause of #13** — `OrphanCleanupScanner.cs:120-124` calls
`VirtualFolderIdentity.GetEnabledFolders(_libraryManager,
config.EnabledLibraries)` correctly. BUT the empty-folder detection
inside the scanner uses `VideoExtensions` only (line 44) to decide
"is this dir empty of media?", so a music folder full of `.flac`
files reads as "empty of media → orphaned debris → recycle
everything".

**Fix**

1. `OrphanCleanupScanner.cs` — change the "is this folder empty?"
   check to use `MediaExtensions` (video + audio + book + comic),
   not `VideoExtensions`. Line 44 is the constant. Line
   `~127-144` gates the pass — check whether music libraries are
   correctly excluded from `EnabledLibraries` at
   `PluginConfiguration.EnabledLibraries` default.
2. Add a preview endpoint `GET /MediaDash/Scan/PreviewScope` that
   returns `{ libraries: [{Id, Name, Kind, Enabled, Scanners: […]}] }`
   so the UI can show "this run will touch: Movies, Comics" before
   the user clicks Scan. `MediaDashController.cs` — mirror the
   Libraries endpoint shape.
3. Ship a one-time config migration: on plugin load, if any music /
   audiobook library is currently in `EnabledLibraries`, set
   `OrphanScanEmptyFolders = false` for those libraries. Prevents
   the exact recurrence of #13.

## PR-4 · DuplicateScanner rework (issue #3, F-029)

**Root cause of #3** — `DuplicateScanner.cs:150-254` uses the
`SeriesId:Season:Episode` composite key for episodes. Jellyfin
collapses old shows with thin metadata into "one episode with many
media sources", so all episodes match the same group key.
`DuplicateScanner.cs:541-544` penalises "same directory, distinct
stems" by -0.25 which is meant to catch this, but for shows the
penalty isn't enough.

**F-029 (missed dupes)** — `MediaDashDb.cs:394-398`: the insert-
dedup clause skips insert only when a row with same `(type, path)`
is already `Queued` or `Dismissed`. Two `Detected` rows with same
path can coexist if they differ on `Confidence` — which is what the
Big Buck double-item case produces.

**Fix**

1. Add UNIQUE constraint `(type, path, status)` on `issues`
   (migration in `Data/MediaDashDb.cs`). Combined with `INSERT OR
   REPLACE` — keep the row with higher Confidence.
2. `DuplicateScanner.cs` — series-episode branch: refuse to emit a
   Duplicate if any of the pair's `BaseItem.IndexNumber` differs.
   Different episode numbers = not duplicates, no matter what
   Jellyfin's item collapsing says. Line ~159-161 is the group-key
   builder.
3. Add config field `DuplicateFixMode` default `ManualApprove` (was
   already, but the auto-queue path via
   `DuplicateAutoFixConfidence` at `FixTask.cs:204` should ONLY
   fire when Confidence >= 0.95 AND (SHA-256 match OR identical
   ProviderID). No heuristic auto-approve.
4. Rescan-reopens-Dismissed — `MediaDashDb.cs:396-397` currently
   skips re-insert if Dismissed. That means the user has to manually
   un-dismiss. Add a `DismissTtl` (default 90 days) after which a
   Dismissed row is treated as absent. Column addition:
   `dismissed_until_utc TIMESTAMP NULL`.

## PR-5 · DryRun DB semantics (F-086)

**Root cause** — `FixTask.cs:383-386` correctly gates
`_db.UpdateIssueStatus(id, Fixed)` on `!result.WasDryRun`. BUT:

- `FixTask.cs:396-399` — the stale-failure path updates status to
  Fixed **without** the DryRun gate. If a queued fix's source file
  went missing between scan and fix (or during a dry-run
  simulation), the row flips to Fixed unconditionally.
- Every fixer's `FixAsync` must set `result.WasDryRun = true` when
  `config.DryRun` is true. Audit some fixers didn't (F-086 root).

**Fix**

1. Add `Debug.Assert(config.DryRun == result.WasDryRun)` at
   `FixTask.cs:382` — any fixer that lies about its dry-run state
   trips the assert in Debug builds. Log-and-continue in Release.
2. Gate line 396-399 on `!config.DryRun`. If dry-run and file
   vanished, log INFO ("would fail — file missing") and leave the
   row `Queued`.
3. Audit each `IFixer.FixAsync` at `Fixers/*Fixer.cs`. Any
   implementation that doesn't set `WasDryRun` on its returned
   `FixResult` gets a one-line change. Base class `FixResult.cs`
   should have `WasDryRun` default to `config.DryRun` if unset —
   fail-safe.
4. Add a scan-hydration pass: on load, `SELECT ... WHERE status =
   Fixed AND fixed_at_utc > (server_start_utc - 1h) AND was_dry_run
   = 1` → flip these back to Queued. Recovers users already burned
   by F-086 without asking them to re-approve.

## PR-6 · Diagnostics & SMART noise (issues #6, #23, #38 + F-073)

**Root cause** — `SmartHealthProbe.cs:87-94` calls WMI first, falls
through to smartctl on `null` or `Unknown`. No per-drive
"known-unsupported" cache. `SmartHealthProbe.cs:111-115` also does
NOT check the drive's media/interface type — it just calls smartctl
blind on NVMe (which reliably fails with either "Not supported" or
"Permission denied").

**Diagnostics dedup** — `Diagnostics.cs:79` dedupes by
`source + full message` hash. Repeats increment `Count`, don't
create new rows. BUT the message includes the drive letter
("WMI query failed for C:") so `C:` and `D:` are separate rows,
each spamming their own count.

**Fix**

1. `SmartHealthProbe.cs` — add a static
   `_knownUnsupported = new ConcurrentDictionary<string, string>()`
   keyed by `driveRoot`, valued by "unsupported reason". Check
   before every WMI call at line 89; skip and return
   `SmartHealthResult { available:false, reason:cached }` if
   present. Populate on first failure at 90-93.
2. NVMe detection — before calling smartctl, use
   `MSFT_PhysicalDisk.MediaType` (WMI) or `/sys/block/*/queue/rotational`
   (Linux) or `nvme id-ctrl` availability check. If NVMe and
   smartctl unavailable/unsupported, fall back to
   `MSFT_PhysicalDisk.HealthStatus` (which DOES work on NVMe) and
   emit a `SmartHealthResult` with just Status + Temperature, no
   attribute counters.
3. `Diagnostics.cs:79` — extend dedup by adding a "category" hash
   that strips drive letters / paths from the message before
   hashing. E.g. `SmartHealth.Wmi: WMI query failed for C:` →
   category `SmartHealth.Wmi:WMI query failed`. One category, count
   grows across drives. Add a `Drives:string[]` field to
   `DiagnosticEntry` that accumulates which drives are affected.
4. `configPage.html` Errors tab — add a `[Copy diagnostics]` button
   next to `[Report an issue]`. Wires to
   `navigator.clipboard.writeText(diagnosticsPayload)`. Kills the
   confusion in #33/#26/#39/#6 where users can't find how to copy.

## PR-7 · Config surface completeness (F-084 + issues #22, #30, #27)

**Root cause of F-084** — `PluginConfiguration.cs` declares:

- `CodecPreferenceOrder` (string[], line 276)
- `ScanCpuThreads` (int, default 0=auto, line 343)
- `ScanBelowNormalPriority` (bool, default true, line 351)

`configPage.html` has no `<input>` for any of them (searched
lines 1-8100; only `AnalyticsEnabled` at 7223/7769/7893/8045 is
present of the shape you'd expect).

**Fix**

1. Add three inputs to the Safety card at
   `configPage.html:3524-3568`:
   - `<input id="mdCfg-ScanCpuThreads" type="number" min="0"
     max="64">` labeled "Scan CPU threads (0 = auto)"
   - `<input id="mdCfg-ScanBelowNormalPriority" type="checkbox">`
     labeled "Run fixes at below-normal priority"
   - `<textarea id="mdCfg-CodecPreferenceOrder">` labeled "Codec
     preference (comma-separated)" — accepts `hevc,h264,av1`
     format.
   Wire the three ids into the existing save-config handler (grep
   for how `mdCfg-DryRun` is wired at ~7223).
2. Per-library overrides (#22, #30, #27) — separate work, tracked
   as PR-7b:
   - `PluginConfiguration.PerLibrary[libId] = { MaxHeight?,
     AllowedAudioLanguages?, AllowedSubtitleLanguages?,
     ExtractSubsToSidecars? }` — every field nullable → falls back
     to the global.
   - New `mdSet-perlib` card in Settings tab; render one row per
     registered library from `/MediaDash/Libraries`. Save via the
     existing config PUT.

## PR-8 · Queue editing (issue #35)

**Existing patterns** —
`MediaDashController.cs`:
- `Approve` at line 381 (Detected → Queued)
- `Dismiss` at line 394 (Detected → Dismissed)
- `Revert` at line 409 (Queued/Dismissed → Detected)

`Revert` already does what #35 needs. But the UI hides it, or the
naming is confusing. Two options:

1. **Minimal** — no new endpoint. `configPage.html` — surface the
   existing Revert button on Queued issues in the Issues panel.
   Label it "Remove from queue".
2. **Additional endpoint** for clarity —
   `POST /MediaDash/Issues/{id}/Unqueue` that internally calls the
   Revert handler with a stricter guard: only if
   `status == Queued`. Returns 409 if `Fixed`.

Ship #1 first (small diff, matches maintainer's promise). #2 as a
follow-up if a client needs cleaner semantics.

## PR-9 · Playability detail surfacing (issue #36 + F-017)

**Root cause of #36** — Playability issue's `DetailsJson` is
`{Reason:"decode-error", Detail:"[matroska,webm @ ...] File ended
prematurely ..."}` (from audit F-017). The UI Issue card only
renders `SuggestedFix`.

**Fix** — pure frontend change in `configPage.html`. Locate the
Issues panel's issue-card template (grep for "SuggestedFix"). Add:

```html
<details class="mdIssue-detail" ${issue.DetailsJson ? "" : "hidden"}>
  <summary>Why?</summary>
  <div class="mdIssue-reason">${issue.DetailsJson?.Reason ?? ""}</div>
  <pre class="mdIssue-ffmpeg">${issue.DetailsJson?.Detail ?? ""}</pre>
</details>
```

Style with CSS to be collapsed by default. Applies to all issue
types automatically (Reason field present on most —
CorruptNfo/Playability/SubtitleLanguage confirmed by audit).

## PR-10 · i18n gap fill (F-080)

**Root cause of F-080** — 8 non-English locale bundles (de, es,
fr, it, nl, pt-BR, ru, zh-CN) each miss the same 26 nested-leaf
translations: whole subtrees `types.CorruptNfo`,
`types.SubtitleFonts`, `types.Ungrouped`, `wizSteps[9..16]`,
`html.settings.safety.analyticsHint`.

**Fix** — pure bundle work. Extract the 26 English strings from
`Configuration/i18n/en.json`, ship a translation script that
outputs 8 stubbed patches (each string wrapped in `<TRANSLATE>...
</TRANSLATE>` markers) for manual translation. Human review of
each. No code change.

Suggested tool: DeepL API for the initial draft; a first-language
speaker reviews each locale before merge.

---

# Verification checklist per PR

Every PR must include:

1. **A regression test** covering the audit's `F-###` — file under
   `Jellyfin.Plugin.MediaDash.Tests/`.
2. **A row added to `FINDINGS.md`** with `Fixed by: PR-N` moving
   the finding from Open to Resolved.
3. **A row in `INDEX.md` blockers list** removed if the F-### was
   listed there.
4. **A CHANGELOG entry** naming the GitHub issue #NN so users can
   trace their bug report to the release.

For PR-1, PR-2, PR-4, PR-5 (data-loss potential) — must be run
against the same E2E chapter-02 harness that would have caught
the bug: seed a fixture, run the fix, prove no data loss.
