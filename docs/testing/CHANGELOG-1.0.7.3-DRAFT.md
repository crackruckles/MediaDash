# 1.0.7.3 (proposed)

## Data-loss safety
- OrphanCleanup Automatic mode blocked server-side — auto-queue guard extended from Duplicate to `OrphanedDebris`; issues stay `Detected` until manually approved (GitHub #13; F-201)
- Recycle bin restore returns 400 (was 409) for missing `BinPath`, accepts new `BinPaths[]` batch shape that restores multiple items independently and reports per-entry outcomes in a `BatchRestoreResult`, and surfaces `BinPath` on every `/RecycleBin/Items` row (GitHub #26; F-207)
- `RedownloadWarnings/{id}/RestoreOptimized` disambiguates two same-basename bin candidates by manifest match — the source's on-disk path is compared against each candidate's `.mediadash-origin` sidecar, so a manifest-tagged twin wins over ambiguous window-only matches
- Duplicate keeper never picks a symlink or 0-byte file — `Rank()` excludes reparse points + zero-length from keeper pool (F-098)
- New 3 GB free-space floor on the bin volume: fix runs skip / pause mid-queue and surface `FixTask.BinVolumeCriticallyFull` diagnostic + pause banner when the drive drops below it
- Restore never overwrites: default `History/{id}/Restore` now returns `200` with `RestoredTo`/`Suffixed` and lands the file as `<name>-restored<ext>` (or `-restored-2`, `-3`, ...) when the original slot is occupied; `?force=true` keeps the destructive swap-to-bin behaviour
- Restored `(path, type)` tuples are recorded in a new `restored_paths` table and blocked from auto-queue forever (manifest-only restores use a `-1` "any type" sentinel that blocks every scanner); manual Approve still overrides

## Duplicate detection
- Remakes no longer collapse — `DuplicateScanner` ProviderID key now includes `ProductionYear` (GitHub #3; F-097)
- `DuplicateMinAgeDays` default 7 → 0; when the gate vetoes candidates, one `INFO` log line names the count and gate (F-099)

## Fixer output
- `TrackFixer` + `TranscodeFixer` restore source `CreationTimeUtc`/`LastWriteTimeUtc` on the rebuilt output (GitHub #31; F-202)
- Dry-run stale-failure path now respects `!config.DryRun` before flipping an issue to `Fixed` (F-206)
- `TrackFixer` external-sidecar recycles and `EmbeddedCoverArtFixer` pre-strip audio recycles now emit per-file history rows via new `FixResult.AdditionalRecycled` list, so every recycled sidecar gets its own Restore button in the bin instead of a dead-end "no history" row
- Subtitle-provider quota (OpenSubtitles free tier, "download limit reached" / "download quota") detected mid-run: remaining `MissingSubtitles` items skipped silently, kept `Queued` for the next run, one summary log line

## Playability
- `PlayabilityScanner` cross-checks ffprobe's `format_name` against file extension; mismatches emit `Playability` with `reason="container-extension-mismatch"` (F-213)

## Recycle bin lifecycle
- New per-batch `.mediadash-origin` sidecar remembers the source path of every recycled file, so restores work even when the `HistoryEntry` row is gone (Files-tab manual deletes, purged history, pre-manifest orphans)
- Legacy unmarked batches sitting in the current bin root are now auto-adopted on startup (marker written) instead of raising `RecycleBin.LegacyBatchNeedsReview`; that diagnostic is retired and existing rows are purged from the diagnostics table
- Failed auto-adopts fall back to a new `RecycleBin.LegacyMigrationFailed` diagnostic explaining the manual marker-file workaround
- New `GET /RecycleBin/OtherBins` + `POST /RecycleBin/Consolidate` endpoints discover past bin-root locations from `HistoryEntry.RecyclePath` and move MediaDash-shaped batches into the current bin (cross-volume safe via `FileBrowserController.CrossDeviceMove`); Recycle bin tab renders a "Consolidate legacy locations" banner + one-click "Consolidate all" button
- New `GET /RecycleBin/DiskInfo?path=...` reports total/free bytes, `MeetsFiveGbMinimum`, and a `SuggestedPauseCapGb`; Settings save and first-run wizard now pre-flight the bin volume and refuse to save (or advance) below 5 GB free, with a "Go to Recycle bin settings" jump button
- Wizard step 13 adds a "Recycle bin folder (optional)" input alongside the retention field; when the pause-cap is still 0 (disabled), Save seeds it with the disk-derived suggestion
- `GET /RecycleBin/Items` now returns rich per-row metadata: `AutoPurgesAtUtc`, `BinPath` on every row, `Provenance` (`History`/`Manifest`/`Orphan`), `Reason`, `IssueType`, `ActionText`, `RestoreHint`; UI renders a reason chip, "recycled X days ago", "auto-deletes in N days" (yellow at ≤3 days, red at ≤0), verbatim action line, and a per-row "How to recover" help bubble
- `POST /RecycleBin/Items/Restore` restores manifest-only bin files by `BinPath`; refuses with 409 when the origin isn't inside a configured library or when no manifest exists; logs a history row for audit
- Overview hero gets a "Sitting in recycle bin now" footer with size, count, and retention days (or "Nothing in the recycle bin.")
- `RecycleBin.GetContents()` and `ListContents()` handle concurrent purge / restore races: `FileNotFoundException` / `DirectoryNotFoundException` are swallowed silently (previously logged noisy warnings on the fast status poll)
- `GetContents()` no longer inflates the file count by 1 when `FileInfo` throws
- Bin-swap paths in `History/{id}/Restore?force=true` and `RecycleBin/Items/{id}/RestoreOptimizedTwin` (or equivalent) now log the swapped-out file as its own history row so it stays restorable
- Manifest sidecar (`.mediadash-origin`) is excluded from bin size/count totals alongside the ownership marker

## File browser
- New "Jellyfin logs" shortcut appears at the file-browser root (icon `article`, teal), same read-only carve-out treatment as the Recycle bin shortcut; supports listing + per-file download but never write, upload, rename, or delete
- `GET /Files/List` returns `IsLogsDir` on listings inside the logs folder; `GET /Files/Download` unlocks log files for download so users can attach them to bug reports
- Download links now send `ApiKey=` (capitalised) instead of `api_key=` for Jellyfin 10.10+ compatibility
- `FileEntry.Kind` gains `"logs"` variant

## Diagnostics / Errors
- `RecycleBin.LegacyBatchNeedsReview` diagnostic retired; startup cleanup purges any stale rows
- New `FixTask.BinVolumeCriticallyFull` diagnostic keyed off the 3 GB floor
- Errors-tab button rename in copy: "Copy diagnostics" → "Report an issue" (behaviour: copies diagnostics + opens the issue tracker in a new tab)

## Data / DB
- Schema bumped to v5; new `restored_paths(path, type, restored_at_utc)` table with `-1` sentinel for "any type" restores from manifest-only entries
- `MediaDashDb.MarkTypeQueued` auto-queue query now joins `restored_paths` and excludes any path the user has restored (matching type or the wildcard sentinel)
- New APIs: `MarkPathRestored(path, IssueType)`, `MarkPathRestoredForAnyType(path)`, `WasPathRestored(path, type)`, `GetRestoredPathsBlockingAutoQueue(type)`

## Config UI / config schema
- Bin volume free-space pre-flight before Save on Settings page and step 13 of the wizard; hard refuses at <5 GB free and shows a red "Refused to save / This location isn't safe" callout with a jump-to-card button
- Overview donut hero adds bin footer (size + retention hint or empty state)
- Issue rows show "you restored this before" badge (amber) on Detected rows whose `(path, type)` is in `restored_paths`; hint text explains why auto-queue is skipping the row
- Issue rows for `Queued` and `Dismissed` state gain an "Undo" button that hits `POST Issues/{id}/Revert`; success flips back to Detected + re-renders, failure surfaces `undoFailedMsg`
- Approve/Ignore hints now split into `**Approve:** ... · **Dismiss:** ...` so each half describes its own button
- Recycle bin tab: rows now carry a reason chip (blue for scanner-owned, grey for manual delete, amber for orphan), relative-time recycled-ago string, retention countdown, verbatim `ActionText`, and a `RestoreHint` help bubble
- Recycle bin "no history" fallback replaced with `no origin` label + tooltip; rows with manifest-provenance now offer a Restore button that POSTs to the new manifest-restore endpoint
- Force-restore confirm prompt removed; restore is non-destructive by default and reports `Restored as <basename>` when a `-restored` suffix was applied
- Wizard step 13 gains recycle-bin path input + inline "This location isn't safe" callout tied to the same disk-info pre-flight
- Hero labels: "Reclaimed since install" → "Space trimmed since install" with new hint text explaining that bin retention delays actual disk-space recovery; "Reclaimed since install per library" → "Space trimmed per library"
- Dismiss hint reworded: "Excludes this file from future scans of this issue type" → "Won't report this file for this issue again."
- Missing-subtitles action-hint reworded to match single-provider reality
- `esc()` now also escapes `"` and `'` so payloads dumped into `title="..."` / `data-*` attributes can't break out

## Other API contract changes
- New `POST /Issues/{id}/Revert` returns 204 (Queued/Dismissed → Detected), 404 (unknown id), or 409 (any other status; already-fixed items must use History/Restore)
- `GET /Status` gains `RecycleBinBytes`, `RecycleBinFileCount`, `RecycleBinRetentionDays`
- `GET /Issues` responses gain `WasPreviouslyRestored` per row (batched lookup — one query per distinct type in the result)
- `POST /History/{id}/Restore` return type changed from 204 to 200 with `RestoreResult { RestoredTo, Suffixed }` body
- New DTOs: `BinRestoreRequest`, `RestoreResult`, `ConsolidateRequest`, `ConsolidateResult`, `OtherBinLocation`, `RecycleBinDiskInfo`, plus `RecycleProvenance` enum on `RecycleBinItem`
- `DirectoryListing` gains `IsLogsDir`
- `EnvInfo` doc reworded to reflect the "Report an issue" button rename
- `FixResult` gains `AdditionalRecycled: IReadOnlyList<RecycledSidecar>`

## Fixers
- `EmbeddedCoverArtFixer` records each pre-strip audio original in `AdditionalRecycled` via new optional args on `StripResult.Ok(freed, path, recyclePath)`, so every stripped audio's original gets its own bin-tab Restore button

## Docs / issue template
- README, docs/index.html, and `.github/ISSUE_TEMPLATE/bug_report.md` all updated to point at "Errors tab → Report an issue" (with the bug icon) instead of "Copy diagnostics"

## Tests
- 16 new test files: `ComputeSuggestedPauseCapGbTests`, `ConsolidateEndpointGateTests`, `FixTaskHistoryFanoutTests`, `FixTaskSubtitleQuotaTests`, `FixerMoveToBinParityTests`, `IssueRevertTests`, `RecycleBinAutoAdoptBehaviorTests`, `RecycleBinConsolidateBetweenTests`, `RecycleBinDeriveRootTests`, `RecycleBinDiscoverOtherBinRootsTests`, `RecycleBinManifestRoundTripTests`, `RecycleBinOriginManifestTests`, `RecycleReasonMapperTests`, `RestoreResolveNonCollidingPathTests`, `RestoredPathsTests`, plus updated `OptimizedTwinSelectorTests` for the 5-tuple `ListContents` shape. Coverage centres on the recycle bin origin-manifest round trip, consolidation between bin roots, disk-info gate math, auto-queue blocking via `restored_paths`, per-sidecar history fan-out, restore collision suffixing, and revert-endpoint state machine.
