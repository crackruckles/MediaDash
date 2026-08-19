# MediaDash — Build changelog

Covers the two independent-audit passes recorded in
`audit-2026-08-18.md` and `audit-2026-08-18-v2.md`.
Prior release: `0.9.9.4`.

## Improvements

**Automation coverage**
- `FixMode.Automatic` now takes effect for `MissingSubtitles`, `Ungrouped`,
  `HeavyTranscode`, `FailedTranscode`, and `EmbeddedCoverArt`. Previously
  the setting was silently ignored for those five types and detected issues
  sat in `Detected` forever.

**Language matching**
- Track-language matching now folds ISO 639-1 (2-letter) tags to
  ISO 639-2/T via `CultureInfo` before comparing against the allowed list.
  Files tagged `en` / `fr` / `de` / etc. are correctly matched against
  `["eng"]` / `["fra"]` / `["deu"]` and no longer flagged for removal.

**Path safety hardening**
- `LibraryGuard.IsInsideLibrary` now refuses any path whose ancestor chain
  contains a reparse point (symlink / NTFS junction). Defense against
  hostile-multi-tenant escape and accidental cycles.
- `SweepOrphanSidecars` and `FileBrowserController.CopyDirectory` skip
  reparse points during enumeration — a symlink loop under a library root
  can no longer stack-overflow the Jellyfin process or spin CPU until
  thread-pool starvation.
- `FileBrowserController.IsSimpleName` rejects Windows reserved names
  (`CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9`) and trailing dot / space.
- `RestoreOptimizedCopy` now gates the target path through
  `LibraryGuard.IsInsideLibrary` before touching it.
- `ArtworkFixer` / `ArtworkScanner` prefix check for `InternalMetadataPath`
  now uses the shared boundary-aware helper (a raw `StartsWith` had
  accepted sibling folders like `metadata-evil`).

**Diagnostics**
- Ring-buffer dedup keys off the pre-truncation message hash so two
  different long errors sharing a truncation prefix no longer collapse
  into one entry with a misleading `Count`.
- Dedup replaces the head entry (rather than mutating in place) so
  concurrent `Recent()` callers can't observe torn reads while a fixer is
  serializing the response.
- `MediaDashDb.Open()` records SQLite lock failures to the Errors tab
  before rethrowing — previously the resulting API 500 had no breadcrumb.
- `AnalyticsReporter` logs non-2xx analytics responses at Warning instead
  of Debug so backend schema drift becomes visible.

**Performance**
- `MediaDashDb.GetIssue(id)` is now a targeted `WHERE id=@id` SELECT
  instead of a full-table scan through the C# collection.
- `RestoreFromHistory` calls the existing `GetHistoryEntry(id)` helper
  instead of scanning the whole history.
- `BulkUpdateOpenIssueStatus` chunks id parameters in batches of 500 so
  bulk actions on 1000+ issues no longer trip SQLite's default 999-param
  cap.
- `MediaDashDb.RelocateIssuePaths` uses `COLLATE NOCASE` on the boundary
  match so sibling issues get re-pointed after case-varying moves on
  Windows (previously the fix was invisible on the second run).

**Robustness**
- `SmartHealthProbe`: `smartctl` output is drained concurrently on stdout
  AND stderr before `WaitForExit`, so large SMART payloads (>64 KB) and
  stderr warnings no longer deadlock the child process.
- `SmartHealthProbe`: `df` fallback now uses `ProcessStartInfo.ArgumentList`
  so mount paths containing spaces resolve correctly.
- `LibraryGuard.SidecarPatterns` / `FfmpegExecutor.SweepStaleMediaDashFfmpegs`
  extended to cover `strip` and `swap` markers — orphan `.mediadash.strip.*`
  sidecars from an interrupted embedded-cover-art fix are now swept.
- `FfmpegExecutor.SweepStaleMediaDashFfmpegs` only kills processes older
  than 5 minutes; live sibling ffmpegs from concurrent fixers are no
  longer collateral damage.
- `FfmpegExecutor.TryKill` awaits `WaitForExit(3s)` after sending the
  signal so the caller's `finally` no longer races OS-level teardown.
- `PostUpgradeCleanup` now sweeps every plausible per-GUID trickplay root
  (`<data>/trickplay`, `<data>/metadata/trickplay`,
  `<InternalMetadataPath>/trickplay`) instead of silently no-op'ing on
  installs where the layout differs.
- `IsCrossDeviceError` checks `HResult` (17 on Windows / 18 on Linux)
  before falling back to string match; localized Windows / Linux
  systems now correctly detect EXDEV.
- `Directory.EnumerateFiles(...AllDirectories)` calls in `RecycleBin`
  and `OrphanCleanupScanner` use `EnumerationOptions.IgnoreInaccessible`
  so one unreadable subfolder can't abort a whole scan mid-walk.
- `AssSubtitleFile.Parse` caps input at 100 MB; a malformed or malicious
  `.ass` sidecar can no longer OOM the plugin.
- `FileBrowserController.VirtualFolders` iterations take a `ToList()`
  snapshot to defend against concurrent library-add mutations.

**Config cleanup**
- Removed the unused `MaxConcurrentTranscodes` setting (it had no wiring
  and every fix ran single-threaded regardless).

## Bug fixes

**Data safety (critical)**
- `RecycleBin.MoveAcrossVolumes` (and by extension `Restore`) now verifies
  the cross-volume copy size before deleting the source. A truncated
  write on a filling volume no longer destroys the user's only copy.
- `OrphanCleanupFixer` metadata-safety gate no longer uses hardcoded
  `OrdinalIgnoreCase`; on case-sensitive filesystems, a crafted issue
  row can no longer bypass the guard.

**Fixer correctness**
- `PlayabilityFixer` audio-kind files flagged with `reason=no-audio` were
  being recycled even after real audio had been restored; the re-verify
  now branches on the persisted reason and correctly checks audio streams.
- `PlayabilityFixer` `reason=size-truncated` re-verification now re-runs
  the actual size-vs-bitrate heuristic instead of only checking duration,
  so truncated files can actually be removed.
- `TranscodeFixer` swap sidecar name now includes a per-run GUID so a
  fresh run on the same target cannot collide with an orphan from a
  hot-reload / crash-mid-encode.
- `TrackFixer.IsSelfReferentialSubtitle` normalizes both paths through
  `Path.GetFullPath` before comparison; legacy relative-path issue rows
  can no longer trick the guard into recycling the just-remuxed video.
- `EmbeddedCoverArtFixer` now refuses if any folder-cover file matching
  the scanner's `{cover|folder|album|front}.{jpg|jpeg|png|webp}` set
  exists — previously an album with `folder.jpg` got a duplicate
  `cover.jpg` written next to it.
- `EmbeddedCoverArtFixer` validates the configured cover filename;
  path-traversal and absolute paths in `config.EmbeddedCoverFilename`
  are rejected.
- `MediaSorterFixer` cross-volume detection now uses
  `RecycleBin.FindDriveForPath` — `Path.GetPathRoot` was returning `/`
  for every Linux path and skipping the free-space pre-check.
- `MediaSorterFixer` catches generic `IOException` (target-exists TOCTOU,
  etc.) and writes a proper `FixResult.Fail` so the History row is
  preserved.
- `MissingSubtitleFixer` catches `TaskCanceledException` from provider
  network stalls when the outer cancellation token hasn't fired — a
  DNS timeout no longer aborts the whole fix run.

**Scanner correctness**
- `DuplicateScanner` guards `Path.GetFileName(movie.Path)` /
  `Path.GetFileName(book.Path)` against null — an item with a missing
  path no longer NREs mid-scan.

**Database / relocation**
- `RelocateIssuePaths` normalizes trailing separators on both `oldPath`
  and `newPath` so callers that pass a trailing `/` or `\` no longer
  silently miss the prefix rewrite.
- `RelocateIssuePaths` boundary match is now case-insensitive
  (`COLLATE NOCASE`); on Windows, sibling issue rows whose stored path
  differs in casing from the post-move path get re-pointed correctly.

**RecycleBin**
- `RecycleBin.FindDriveForPath` now enforces a separator boundary on the
  drive-root prefix; on Linux, `/mnt/media` no longer swallows paths
  under `/mnt/media-backup`.
- `RecycleBin.MoveToBin` folder name suffixed with a short GUID —
  timestamp-only names no longer collide when two files with the same
  basename are recycled in the same millisecond.
- `RecycleBin.EmptyAll` uses `Interlocked.CompareExchange` as an atomic
  gate; two `POST /RecycleBin/Empty` requests racing the check no longer
  both launch and race in-flight state.
- `RecycleBin.MoveToBin` trims a trailing separator before extracting the
  basename so `move-to-self` can't happen when a caller passes a
  directory path with a trailing slash.
- `RecycleBin.ListContents` reads the recycled-at timestamp from the
  folder name (`yyyyMMdd-HHmmss-fff`) instead of the filesystem creation
  time — the listing is now correct on FAT / exFAT / cross-timezone
  filesystems.

**API + controllers**
- `FileBrowserController.Upload` temp file names include a per-request
  GUID; two concurrent uploads to the same target can no longer collide
  on the temp file, and the failure/cancel `catch` can no longer delete
  another upload's in-flight temp.
- `LibraryGuard.SidecarPatterns` covers `.mediadash.upload.tmp` so
  aborted uploads are swept alongside other orphaned sidecars.

**Long-running tasks**
- `FixTask` unexpected-exception handler now writes a `History` row
  alongside the diagnostic so failures can be audited (previously only
  the diagnostic was recorded).

**Substrings / short-name edge cases**
- `AssSubtitleFile.IsReferenced` requires ≥ 3 characters on the shorter
  side before a bidirectional-substring match counts; canonicalized
  short font names (`a`, `co`) can no longer spuriously match every
  embedded font and prevent stripping.

**Deferred / non-actionable (recorded, not fixed)**
- `SubtitleFontFixer` sequential-write race — not reachable under the
  current single-runner `FixTask` scheduling; recorded for future
  concurrent-fixer work.
- `AnalyticsReporter` `HttpClient` connection caching — acceptable
  cost/benefit as observed.
- `TrickplayOptimizeFixer` cross-filesystem `File.Move` atomicity — the
  existing orphan sweep already covers the SIGKILL-during-move case.
