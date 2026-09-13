# MediaDash Audit — Progress

What's been audited, what's in progress, what's queued. Update as you go. A future session reads
this end-to-end to figure out where to resume.

## Legend

- ✓ **done** — audited fully. Any findings recorded in `findings.md`, or the note here says "clean".
- ~ **partial** — started but ran out of context / time. Include a **Resume:** line naming the
  exact next step (which file, which test case, which log to grep for).
- ○ **queued** — planned but not started.

## Session log

- session-8: 2026-09-13 08:30 UTC — UNC live tests + DELETE-library-mid-fix live-triggered + huge library perf pass. Two findings filed (F-020, F-021). **F-020** (**medium**, cross-cutting): UNC / network-share library paths silently bypass every disk-space safety check (TranscodeFixer, TrackFixer, RecycleBin.MoveToBin, MediaSorterFixer, FixTask.IsBinVolumeCriticallyFull). Root: `RecycleBin.FindDriveForPath` iterates `DriveInfo.GetDrives()` looking for a local-drive prefix match — UNC paths (`\\localhost\c$\...`) never match, so it returns null, and every caller guards `if (drive is not null && ...)` which silently skips the check. Positive control: successful fix of issue 8777 (Playability on UNC-mounted broken .mkv, restored back to UNC) proves the write-path works end-to-end for UNC — the bug is "UNC quietly disables the safety net local-drive users get", not "UNC breaks the plugin". Safety invariant #5 (≥2× source size before transcode) violated on paper for UNC libraries. **F-021** (**medium**, api+db): `GET /MediaDash/Issues` returns EVERY open issue in one un-paginated payload — measured 20 171 rows / 11.2 MB / 330 ms for a 10K-fixture library. `limit` query param is silently ignored (returned same 20K rows for limit=5, limit=100, and no limit). Extrapolates to 100-250 MB responses on real 50K-item Jellyfin servers. `/History` has same shape (DB layer paginates to 500 internally so smaller blast radius). No `FromQuery.*limit/offset` anywhere in the controller. Bonus: pagination halves F-014's blast radius (bad GUID row would kill one page, not the whole endpoint). Newly ✓: **UNC live tests** (F-020 filed via live-verified UNC fix + restore + code-inspection sweep; `Path.GetFullPath("\\\\localhost\\c$\\...")` normalises correctly, hostname case preserved but IsUnder uses OrdinalIgnoreCase so no issue there; `\\?\UNC\...` long-path prefix does NOT normalise but nobody accidentally uses that). **DELETE-library-mid-fix** (**live-verified clean** with a real race: staged UNC broken fixture, held file lock via detached PS process to force PlayabilityFixer's 7.5s retry-backoff, approved+fixed, then DELETE /Library/VirtualFolders&name=UNC+Test+Library mid-retry → issue transitioned to Fixed status with History row "The file is outside your library folders; MediaDash will not touch it." — exact `IsStaleFailure`-matched behaviour session 6 predicted from code-inspection). Session 7's UNC Test Library was already enabled + populated; recreated after DELETE test via POST /Library/VirtualFolders with the same ItemId (deterministic hash on name+paths). **Huge library perf** (10 000 zero-byte .mkv fixtures → Jellyfin refresh 545s → MediaDash scan 353.9s (35 ms/file, ~1.75× baseline per-file cost, NOT super-linear), +9.4 MB DB growth (~940 B/issue), +20 003 issues (Playability + MissingSubtitle per file), +10 000 probe_cache rows, Jellyfin process memory stayed ~510 MB (no OOM). Scan itself scales fine. F-021 is the real finding here: the /Issues endpoint's un-paginated response is what breaks at scale, not the scan itself). Still ○ / deferred: **Cross-volume RecycleBin FILE path** (still needs Admin — checked available drives; C:\, R:\, S:\ all point to the same physical NTFS volume; the only FAT32 partition is the ~60MB EFI system reserved with no drive letter; VHD creation needs Admin. Session 5's code-inspection stands; would need USB / VeraCrypt / admin-VHD to live-exercise). **Broken-file torture beyond 15 patterns** (fixture-gap for Phase 0 tooling, not bugs — DoVi/HDR10+/interlaced synthesis needs specialised ffmpeg builds; noted in session-6 log and not extended here). Cleanup done: 10K-file library removed from Jellyfin + MediaDash config, 20 000 stale issues DELETEd from DB, VACUUM attempted (DB stays at 10.5 MB due to running-server lock, will shrink on next Jellyfin restart), 10K files rm -rf'd from disk (background delete completed). Systemic pattern: both F-020 and F-021 are examples of "the plugin has the right shape for local-drive small-library deployments but silently degrades for edge topologies" — F-020 for UNC/NAS, F-021 for large-library scale. Neither is a data-loss bug; both are correctness gaps that only surface on realistic deployments outside the fixture library shape. Audit-wide: **21 findings total.** Diminishing returns strong here — after 4 sessions of live testing plus 4 sessions of code-inspection, the remaining "○ / deferred" targets are all environmental (needs Admin / needs SMB reconfig / needs specialised fixtures), not code-inspection gaps. Recommend closing at 21 and handing off to Phase 0.
- session-6: 2026-09-12 15:15 UTC — Permission-denied + cross-volume file fallback + concurrent Approve/Dismiss + swap-row restorability sweep. One finding filed (F-019). **F-019** (**high**, cross-cutting FixTask): the five top-level exception handlers for UnauthorizedAccessException / SharingViolation / DiskFull / generic IOException / generic Exception all write a History row + Diagnostics dedup entry but do NOT call `UpdateIssueStatus(id, Fixed)`. Only FileNotFound + DirectoryNotFound (the two "missing file" cases) advance status. Result: any exception the fixer raises (as opposed to returning `FixResult.Fail`) traps the row in Queued forever, and every 30-min fix run writes an identical History row + increments the Diagnostics count. Live-reproduced 2026-09-12 with a detached PowerShell process holding an exclusive lock on `_audit_session6\Denied Movie (2020)\Denied Movie (2020).mkv`: MediaDash scan flagged it as Playability id=8004 (technical: "Permission denied"), Approve+Fix wrote 1 History row (`Fix failed — file was locked by another process even after retrying.`) with status still Queued, second Fix run wrote a second identical row with Diagnostics count=2. Cleanup: killed lock process, dismissed issue. Newly ✓: **Permission-denied fuzz** (F-019 filed via sharing-violation live repro; UnauthorizedAccessException code-inspected against the same catch-block shape — Windows-as-Administrator bypasses regular ACLs via SeBackupPrivilege so the Windows branch couldn't be live-triggered here, but the code path is identical to the sharing-violation path that WAS triggered). **Concurrent Approve+Dismiss on same id** (20 parallel racing calls: all returned 204, final DB state was last-writer-wins Dismissed; no orphaned intermediate state, no restored_paths row written for Dismiss transitions; the underlying "any status → any status is legal" state-machine bypass is already captured by F-011 — no new finding needed here). **Recycle bin swap-row restorability** (session-5's concern: not a black hole; force-restore correctly writes a swap-row with IssueId=0 and inherited Type from the entry being displaced, swap-row IS restorable via `POST /History/{id}/Restore` and lands with a `-restored` suffix when the target is now occupied. Cosmetic gap: swap-row inherits displaced entry's Type so bin filter chips misclassify — same shape as session-2's noted-but-not-filed micro concern #3, not filed to avoid duplication). Still ○: **DELETE-library-mid-fix** (code-inspected clean: `LibraryGuard.IsInsideLibrary` re-reads `GetVirtualFolders()` per-call, so mid-fix library removal produces an "outside your library" refusal on the next check → matched by `IsStaleFailure` → row transitions to Fixed. The recycle bin's Root is derived from `applicationPaths.DataPath` not virtual folders, so recycling a file after library removal still works. Not live-triggered because it needs enabling a fresh library in the plugin's `EnabledLibraries` config XML mid-session which is disruptive to the ongoing audit). **Cross-volume RecycleBin FILE path** (subst-drive test on same physical disk showed `File.Move` succeeds with rename semantics — subst doesn't trigger EXDEV. Session-5's code inspection of `CrossDeviceMove(sourceIsDir:false)` at `FileBrowserController.cs:981` still stands as the only assurance; a true cross-filesystem test (USB drive to internal SSD, or NTFS to a VeraCrypt volume) would exercise the copy→verify→delete-source path but is out of scope here). **UNC live tests** (deferred again — needs a same-machine `\\localhost\c$\...` share which requires SMB service reconfig; skipped). **Huge library perf** (not attempted this session — 10K-file synthesis + scan would take ~15 min plus Jellyfin library ingest overhead, not worth it in remaining context). **Broken-file torture beyond 15 patterns** (session-6 read `torture-test.ps1` — 15 patterns cover tail-trunc / mid-XOR / mid-zeros / scattered-flips / wrong-ext / half-header / container-lied / mp4-in-avi / double-damage. Missing per audit spec: DoVi metadata, HDR10+ variants, interlaced content, audio-only-in-video-container, non-linear PTS. Not filed as findings — these are FIXTURE gaps for the Phase 0 tooling extension, not bugs. `progress.md` note only). Systemic pattern noted: three findings this audit round (F-011, F-014, F-019) all share the root cause "trust the DB / trust the input, throw when reality doesn't match" — F-011 trusts every incoming status transition, F-014 trusts every DB Guid string, F-019 trusts that "no exception thrown = fix eventually succeeds". All three are the "one bad thing bricks a big surface" pattern.
- session-5: 2026-09-12 13:45 UTC — UX races + Malformed DetailsJson fuzz + disappearing-file guard sweep. Five findings filed (F-014..F-018). **Critical discovery:** sessions 1-3 were reading the WRONG DB (`AppData\Local\jellyfin-v10\data\mediadash\mediadash.db` — leftover from a previous Jellyfin install). The plugin's live DataDirectory is `AppData\Local\jellyfin\data\mediadash` (no `-v10`). Verified via `GET /MediaDash/Status.DataDirectory`. F-010 (schema v6 → v9 migration replay) tested against a synthetic DB so unaffected; F-011 (Approve on Fixed row) re-verified today against the correct DB and reproduces cleanly — no correction needed to earlier findings' validity. **F-014** (**high**, db+api): any single row with an item_id that isn't 32-hex Guid-N format crashes `GET /Status` (500 from `Guid.ParseExact` in `MediaDashDb.GetIssues`), which brings down the whole dashboard's health surface. Live-reproduced with a 26-char item_id; recovered by DELETE. **F-015** (**high**, PlayabilityFixer): `TryGetReason` catches only JsonException while calling `TryGetProperty` (throws InvalidOperationException on non-object root) and `GetString()` (throws IOE on non-string reason). Two of 7 injected malformed rows crash the fixer, row stays Queued forever, Errors tab gains a scary "unexpected error" panel per fix cycle. **F-016** (**high**, Issue.HasBlockingWarnings): same JsonException-only catch pattern → non-object DetailsJson root aborts `RollbackAutoQueuedBlockingWarnings` for that type, which cascades to abort auto-queue for every remaining type in FixableTypes for the current run. **F-017** (**low**, api): POST /Scan while already scanning returns 204 (success) instead of 409 (already running) — UI gets no signal the click was ignored. **F-018** (**medium**, api+db): /Scan/Suspicious runs inline without checking whether a full scan is in flight; two writers race on `ReplaceDetectedIssues(MalwareRisk, ...)` and one's INSERTs are silently truncated by the other's DELETE. Newly ✓: **UX races** (fix→next-scan staleness verified clean — Issues tab correctly hides Fixed rows via `openOnly=true`; manual-scan-during-scan filed as F-017, ScanSuspicious race as F-018), **Path handling live fixtures** (created emoji / RTL Arabic / Cyrillic / NFC+NFD Café / trailing-ws / 255-char MAX_PATH / symlink fixtures under `_audit_session5/`; scanners walk all cleanly, duplicate scanner correctly excludes symlinks, no silent-skips detected; `Movie   .mkv` trailing-whitespace file was picked as duplicate KEEPER over emoji variant and successfully served as source for a fix — no crashes, no data loss), **Malformed DetailsJson** (F-014/F-015/F-016 filed; disappearing-file guard verified consistent across every fixer — all return "no longer exists" which IsStaleFailure matches). Still ○: cross-volume MoveToBin FILE path (code-inspected, staging + verified copy + source-delete-last pattern looks safe, but not live-exercised); huge library perf; broken-file torture patterns; concurrent-user actions on same issue id under Automatic mode.
- session-3: 2026-09-11 11:00 UTC — migration + concurrency + hardlink + provider-churn. Four
  findings filed (F-010..F-013). F-010 (**high**, db): schema v6 step drops ALL Playability rows
  regardless of status — Dismissed choice silently reverted (comment says "already-Queued" but SQL
  has no status filter). Verified via v0→v9, v3→v9, v5→v9, v8→v9 shell replay against synthetic
  fixtures under `tools/audit-migration/`; column-for-column parity with fresh-v9 install
  (cosmetic sqlite_master DDL text drift on ALTER-added `confidence` column only). F-011 (**high**,
  api): `POST /Issues/{id}/Approve` and `/Dismiss` bypass state-machine — Fixed rows can be
  re-Queued or re-Dismissed unconditionally (Revert already has the guard). Live-reproduced against
  issue 6828, cleaned up. F-012 (**medium**, MissingSubtitleFixer): no provider back-off — 75
  history rows in 6 days for one Emoji Test (2023).mkv fixture, 54 "no matches from any provider"
  rows. F-013 (**medium**, DuplicateScanner): hardlinked twins pass through as duplicates (verified
  same NTFS inode, no reparse-point flag), fixer reports BytesFreed=size but physical bytes freed = 0.
  Newly ✓: Migration (whole cross-cutting concern), SubtitleLanguageScanner (was ~), DuplicateScanner
  (was ~), MissingSubtitleFixer (was ○). Concurrency: single-item Approve/Revert races end at
  Detected (idempotent state machine, F-011 is the deeper issue). Recorded but not filed: 8.3 short-
  name path handling gap in `IsSelfReferentialSubtitle` — no realistic trigger, noted in progress.
- session-2: 2026-09-11 10:20 UTC — cross-cutting pass (recycle bin, path handling, macrolanguage regression, sidecar sweep semantics). Three findings filed (F-007..F-009). Newly ✓: MissingSubtitleScanner (F-008), MediaSorterScanner + MediaSorterFixer (F-009 filed on the fixer; scanner reviewed clean modulo the sidecar-glob concern below), SubtitleFontScanner (reviewed clean, one micro concern noted). Newly ✓ cross-cutting: RecycleBin dir/file symmetry (F-007), API auth verified via live curl (Logo=200 anonymous by design, I18n and every other endpoint = 401 without token; Status=200 with admin token). Concurrency spot-checks: 4 parallel POST /History/{id}/Restore → one 200 + three 409 (message "no longer in the bin" is misleading in the race case but no data loss); 3 parallel POST /Errors/Clear → all 204 (idempotent). StaleContentScanner + TrickplayOptimizeScanner + SuspiciousFileScanner + SubtitleFontScanner + ArtworkFixer + EmbeddedCoverArtFixer + MediaGrouperFixer + DuplicateFixer.SweepDedicatedFolderSidecars + LibraryGuard.IsUnder + FfprobeService cache-key hygiene + MigrateSchema v1-v9 idempotency + FixTask BypassIdleCheckOnce/IgnoreActivityForCurrentRun static-flag safety + RollbackAutoQueuedBlockingWarnings null/malformed JSON handling: all reviewed, no substantive findings. Micro concerns NOT filed (too narrow / cosmetic): (1) `RecycleBinRetentionDays = 0` → `Purge` deletes everything immediately (UI coerces on save so real repro requires hand-editing config XML); (2) ArtworkFixer permanently deletes without honouring `GetDisposal(CorruptArtwork)` (artwork is regenerable, but violates safety invariant #4 on paper); (3) `/RecycleBin/Items/Restore` writes History rows with hardcoded `Type=Duplicate` (cosmetic — filter chips misclassify); (4) `SubtitleFontScanner.FindSidecars` uses `basename + "*" + ext` glob so a video with a stem-prefix sibling steals its sidecar attribution (same shape as F-009 but scanner-side); (5) `LibraryAccessCheck` writes a probe file with GUID name and deletes it (races w/ Jellyfin's library monitor); (6) `RecycleBinAccessCheck` GET has a Directory.CreateDirectory side effect. Regression guards examined: F-001 sibling for `Approve/{id}` per-item confirmed intentional per session-1's note; nothing to add.
- session-1: 2026-09-11 07:00 UTC — big-blast-radius pass. PlayabilityFixer, TrackFixer, FixTask (grace + auto-queue), DuplicateFixer, OrphanCleanupFixer, RecycleBin, MediaSorterFixer / MediaGrouperFixer, NfoFixer, SubtitleFontFixer, EmbeddedCoverArtFixer, SuspiciousFileFixer, TrickplayOptimizeFixer, FileBrowserController, LibraryGuard, ArtworkFixer, TranscodeFixer, TranscodeLogScanner, PlayabilityScanner, QualityScanner, MediaGrouperScanner, MediaDashDb (auto-queue + rollback), Diagnostics dedup, ScheduleMigrator. Six findings filed (F-001..F-006). Regression-guards verified for: `container-extension-mismatch` (BROKEN — F-002), FixTask review-grace 10-min window (holds), FixTask "Run fixes now" bypass (holds), Diagnostics.StableStringHash dedup (holds), TrackFixer bitmap-sub consent gate (partially broken via ApproveAll — F-001), PlayabilityFixer rung-4 collision guard on .mkv (holds), Grouper SxxExx sanitizer (holds), FileBrowser symlink refusal via reparse-point ancestor check (holds), UNC path guard (holds). Fix-time re-verify pattern gap surfaced on NfoFixer (F-005), OrphanCleanupFixer video-only recheck (F-004), swap-safety gap on PlayabilityFixer (F-003), orphan-sidecar sweep patterns miss repair/raw/opt sidecars (F-006).

## Scanners

Each scanner: read source, understand the invariants, then fuzz against edge cases (missing
metadata, weird paths, huge / zero-byte files, symlinks, Unicode, permission-denied). Log any
place where scanner output could cause a fixer to do the wrong thing.

- ○ **AudioLanguageScanner** — probe-per-item, language matching. Watch for: untagged tracks
  (Language == null), tracks with non-ISO codes, files where audio stream count == 1 (safety).
  Session 2 note: `IsAllowed` goes through `LanguageHelper.IsAllowed` which handles both bibliographic
  aliases + macrolanguage equivalence. Same-shape as F-008 does NOT apply here (calls correct helper).
- ✓ **SubtitleLanguageScanner** — Session 3: verified `TrackFixer.IsSelfReferentialSubtitle`
  uses `Path.GetFullPath` + `OrdinalIgnoreCase`, which normalises case + trailing separator + `..`
  segments. It does NOT expand Windows 8.3 short names (`C:\dev\MEDIAD~1\` vs `C:\dev\mediadash\`
  return false when compared this way). Filed as noise: no realistic code path where Jellyfin
  hands the scanner an 8.3 path — Jellyfin's `LibraryOptions.PathInfos` always store full-form
  paths and ffprobe emits full-form. If a user manually configures an 8.3 mount source that gap
  could bite, but it's theoretical. No finding.
- ✓ **PlayabilityScanner** — every emitted `reason` value checked against fixer's re-verify
  switch. `container-extension-mismatch` case missing → F-002 (critical). Zero-byte and
  moov-truncated fixtures behave; container-lie MP3 gets recycled as if unplayable.
- ✓ **DuplicateScanner** — Session 3: symlinks already gated at 604/624 via `IsSymlink`; hardlinks
  are NOT (verified via live NTFS test: identical inode, no reparse-point flag). F-013 filed:
  hardlinked twins pass through as duplicates, fixer over-counts BytesFreed by the file's logical
  size while physical bytes freed = 0.
- ~ **QualityScanner** — audio-oversized emits with `fixerAvailable=false` but is stored as
  Type=Quality; TranscodeFixer will run and fail with a misleading "could not be analyzed" —
  UX bug, low severity. **Resume:** interlaced / HDR flag handling not tested.
- ✓ **MediaSorterScanner** — read; validates targets, gates opt-in kinds, uses `LibraryGuard.IsUnder`
  for the "misplaced" check. Emits `targetPath` in DetailsJson which the fixer consumes safely (fixer
  handles bad/missing JSON). Anime override + JellyfinMetadata vs Filename source both covered.
- ✓ **MediaGrouperScanner** — read the sanitizer regression guard fires unconditionally now.
  Doctor Who reboot / Silo (2023) year-suffix rule verified in code.
- ✓ **MissingSubtitleScanner** — F-008 filed. Local `HasAnyMatch` bypasses the macrolanguage
  equivalence group in `LanguageHelper`, so "nor" allow-list never matches embedded "nob"/"nno"
  tracks and the fixer downloads spurious duplicates.
- ✓ **StaleContentScanner** — Reflection-based UserApiBridge holds for both 10.11 and 12.0 hosts
  (Users vs GetUsers() resolved once at construction). Excluded-libs / genres filters clean.
  `IsStale` pure-function has correct semantics. No findings.
- ✓ **TrickplayOptimizeScanner** — Both storage layouts (data-folder + `-trickplay` sibling) handled.
  ScopedIds gate ensures only enabled-library items considered. `SelectItemsForMediaFolderWalk` /
  `ShouldWalkMediaFolder` probe logic is testable and correct. No findings.
- ✓ **OrphanCleanupScanner** — MediaExtensions widening is in place; empty-folder pass excludes
  library roots + trickplay folders correctly. F-201 confirmed FIXED in the scanner. But
  fixer's re-verify never got the same widening → F-004 (critical).
- ✓ **SubtitleFontScanner** — AssSubtitleFile.Parse handles malformed input. Reclaim-floor (50 KB)
  suppresses noise. `FindSidecars` micro concern: `basename + "*" + ext` glob attributes a
  `<basename>-anything.ass` sidecar to the wrong video when two videos share a stem prefix in the
  same folder. Same shape as F-009. Not filed — issue attribution only, no data loss.
- ✓ **SuspiciousFileScanner** — Curated extension list, `AttributesToSkip = ReparsePoint` protects
  the walk, IgnoreInaccessible tolerates permission-denied subfolders, per-library enable list
  respected via VirtualFolderIdentity. Trailing-dot / trailing-space filename tricks would miss
  but those files are non-executable on Windows anyway. No substantive findings.
- ~ **TranscodeLogScanner** — read; two concerns noted but not filed:
  (a) log's JSON `Path` field is trusted verbatim — issue can carry an arbitrary system path
  (fixer refuses via `IsInsideLibrary` so no data loss, but the path leaks onto the Issues tab);
  (b) failure marker "No such file or directory" is too generic and over-flags. **Resume:** if a
  user reports these, file — otherwise defer.

## Fixers

Each fixer: read source, verify the safety invariants from `CLAUDE.md` (no external deletes,
never last audio track, always verify before swap, disk-space check). Fuzz against files
generated by the equivalent scanner's edge cases.

- ✓ **PlayabilityFixer** — Session 1: 4-rung ladder. F-002 (container-extension-mismatch),
  F-003 (swap-safety window), F-006 (repair-tmp sidecar sweep gap) all filed. Session 2: verified
  `IsStillBrokenAsync` covers every scanner-emitted `reason` except `container-extension-mismatch`
  (F-002) and `missing` (safe — file-exists guard catches upstream). No new findings.
- ✓ **TrackFixer** — Session 1 covered; F-001 (ApproveAll bypass) surfaces the shared consent-gate
  gap. Session 2 read `Issue.HasBlockingWarnings` — handles null/malformed DetailsJson correctly.
- ✓ **TranscodeFixer** — Session 1 covered per log.
- ✓ **DuplicateFixer** — Session 1 covered. Session 2 also read `SweepDedicatedFolderSidecars` —
  correctly refuses when other video/audio remains in the folder, otherwise recycles all remaining
  files (including .nfo/.jpg/etc.) then deletes the folder. Aggressive but the folder-uniqueness
  check makes it safe. No new findings.
- ✓ **MediaGrouperFixer** — Session 1 covered. Session 2 verified cross-volume folder Move is
  refused with a clear message (line 143-150).
- ✓ **MediaSorterFixer** — F-009 filed (sidecar sweep steals metadata from stem-prefix sibling
  videos). Cross-volume move + timestamp preservation + collision guards all present and correct.
- ✓ **MissingSubtitleFixer** — Session 3: F-012 filed. Provider returning zero hits does not
  transition the issue to Fixed (not matched by `IsStaleFailure`), so the fix cycles forever every
  30 min for any file no provider carries. Live evidence: single fixture with 75 history rows across
  6 days, 54 "no matches from any provider" fail rows. F-008 side effect (spurious downloads on
  macrolanguage confusion) still applies once providers DO return a hit.
- ✓ **NfoFixer** — Session 1 filed F-005 (no fix-time re-verify).
- ✓ **OrphanCleanupFixer** — Session 1 filed F-004 (video-only recheck).
- ✓ **ArtworkFixer / EmbeddedCoverArtFixer / SubtitleFontFixer / TrickplayOptimizeFixer /
  SuspiciousFileFixer** — Session 1 covered per log. Session 2 revisited: ArtworkFixer uses
  `File.Delete` without recycle-bin routing (arguably violates safety invariant #4 but artwork
  is regenerable; not filed as a finding). TrickplayOptimizeFixer safety gate `IsSafeTrickplayPath`
  covers both storage layouts. EmbeddedCoverArtFixer re-verifies cover-in-audio at fix time and
  guards against traversal in `EmbeddedCoverFilename` config. SubtitleFontFixer re-parses the file
  at fix time (contrast to F-005 shape).

## Cross-cutting concerns

- ✓ **Concurrency** — Session 2 spot-checks (parallel History Restore = correct single-winner
  semantics with a lying error message; parallel Errors/Clear = idempotent; StartFix state-check-
  before-flag-set holds). Session 5-6 parallel /Approve + /Dismiss confirmed idempotent (state-machine
  bypass is F-011). Session 8: **DELETE-library-mid-fix live-verified clean** — staged UNC broken
  fixture, held file lock to force PlayabilityFixer's 7.5s retry-backoff, DELETE /Library/VirtualFolders
  mid-retry → issue transitioned to Fixed with History row "The file is outside your library
  folders; MediaDash will not touch it.", matched by IsStaleFailure → no infinite retry. Exact
  behaviour session 6 predicted from LibraryGuard.IsInsideLibrary re-reading GetVirtualFolders per-call.
- ✓ **Path handling** — Session 5: live fixtures created under `C:\dev\mediadash-fixtures\movies\_audit_session5\`
  covering emoji (`🎬 Movie.mkv`), RTL Arabic (`فيلم.mkv`), Cyrillic (`Фильм 2024.mkv`), NFC
  and NFD combining-marks (`Café Precomposed.mkv`, `Café Naïve NFD.mkv`), trailing-whitespace
  (`Movie   .mkv`), 255-char MAX_PATH (`llll...\short.mkv`), and NTFS symlink (`symlinked.mkv`).
  All were probed cleanly by scanners; DuplicateScanner correctly excluded the symlink; the
  trailing-whitespace file was picked as duplicate KEEPER over the emoji variant and served
  as a valid source for a successful fix run (emoji variant recycled without crashing). No
  silent-skips or path-manipulation bugs detected. Session 8: **UNC live tests done** — staged
  broken + duplicate fixtures under `\\localhost\c$\dev\mediadash-fixtures\_audit_session7\unc_lib`
  (Windows admin share, no reconfig needed), enabled library in Jellyfin + MediaDash, scanned,
  approved, fixed → UNC broken file recycled to local disk, restored back to UNC path. Issue paths
  stored verbatim as `\\localhost\c$\...` in DB. Positive: `Path.GetFullPath` normalises UNC
  correctly, `LibraryGuard.IsUnder` handles hostname case via OrdinalIgnoreCase, ffprobe + ffmpeg
  handle UNC natively, restore round-trips through the recycle bin. **F-020 filed** — all
  disk-space pre-checks (`RecycleBin.FindDriveForPath`) silently skip for UNC sources because
  `DriveInfo.GetDrives()` has no UNC entries, and every caller guards on `if (drive is not null)`.
  Safety invariant #5 violated on paper for UNC libraries (no data loss in practice — OS still
  raises IOException mid-encode).
- ✓ **Migration** — v0/v3/v5/v8 → v9 all end at correct schema (matches fresh-v9 install
  column-for-column; only cosmetic sqlite_master DDL text drift on ALTER-added `issues.confidence`).
  Data preserved as expected across every ALTER + CREATE. One systemic issue found: F-010 (v6
  step drops ALL Playability rows regardless of status — Dismissed choice silently reverted, Fixed
  history nuked). All test fixtures + shell replay under `tools/audit-migration/`.
- ✓ **Dedup keys** — probe_cache, format_probe_cache, decode_cache are all keyed on `path`
  (unique PK), with size + mtime as staleness filters. Callers pass Jellyfin-normalised paths.
  file_hashes primary key `(path,size,mtime)`. All deterministic across restarts — no per-process
  hash randomisation. Diagnostics.StableStringHash is FNV-1a 32-bit (also verified).
- ○ **Data-loss consent** — bitmap subs are the recent fix. Look for other silent drops:
  DuplicateFixer keeper selection under low confidence, TrackFixer with removeIndexes computed
  from stale probe, MediaSorter cross-drive fallback that copies + deletes.
  Session 2 partial: `Approve/{id}` intentionally does NOT re-check consent per session-1's
  design note ("one deliberate click"). Bulk endpoints DO bypass restored_paths block (not filed
  as it may be intentional — the user explicitly selected the items to bulk-approve).
- ✓ **UX races** — Session 5: fix→next-scan staleness reviewed clean (Issues tab uses
  `openOnly=true` which filters out Fixed rows correctly). Manual-scan-during-scan filed
  as F-017 (returns 204 instead of 409 — UI can't distinguish success from silent no-op).
  /Scan/Suspicious concurrent with full scan filed as F-018 (two writers race on
  `ReplaceDetectedIssues` — one's INSERTs get truncated by the other's DELETE).
- ✓ **API auth** — every controller endpoint has class-level `[Authorize(Policy="RequiresElevation")]`.
  Only `[AllowAnonymous]` on `GET /Logo` (intentional, so `<img>` tags load without a token header).
  Live-tested: unauth /Status → 401, unauth /Logo → 200 PNG. RepairSummary and RepairRungTotal are
  Status-embedded DTO fields, not standalone endpoints — inherit controller auth.
- ○ **Error surface** — Errors tab dedup fixed for hash collision. Also check: the deduped
  message text (does it name paths correctly?), the reload-on-clear behaviour.
- ~ **Recycle bin** — F-007 filed (directories unrestorable + invisible). Retention `Purge`
  uses `Directory.GetLastWriteTimeUtc(dir) < cutoff` — `retentionDays=0` deletes everything
  immediately (UI coerces on save; only reachable via hand-edited config XML — not filed as a
  finding but noted). EmptyAll uses CompareExchange gate so double-fire is safe.
  Session 6: force-restore + swap-row restorability verified live — force=true on an occupied
  path correctly writes a `IssueId=0` swap-row for the displaced file, the swap-row IS restorable
  via `POST /History/{id}/Restore` (lands at `-restored` suffix when the slot is now occupied),
  no black hole. Cosmetic gap noted: swap-row inherits displaced file's `Type` (defaults to
  Duplicate=0 for older `type=0` rows), so bin filter chips misclassify — same shape as session-2's
  micro concern #3, not filed to avoid duplication. **Resume:** cross-volume MoveToBin FILE
  path (`CrossDeviceMove(sourceIsDir:false)`) still code-inspection-only; subst-drive on same
  physical disk didn't trigger EXDEV (File.Move succeeded with rename semantics). A true cross-
  filesystem test needs USB / VeraCrypt / cross-partition setup which was out of scope.

## Fuzz targets

Grouped by "does the pipeline handle real garbage users throw at it".

- ○ **Broken files** — extend `tools/repair-test/torture-test.ps1` with 20 more damage
  patterns (interlaced files, DoVi metadata, HDR10+ metadata, audio-only files misfiled as
  video, 4K files with corrupted first-30s vs corrupted mid vs corrupted end).
- ✓ **Malformed DetailsJson** — Session 5: injected 7 Playability + 3 AudioLanguage rows with
  `{}`, `null`, truncated `{"reason":`, `{"reason":[...]}` (array), `{"warnings":"str"}`, etc.
  Two crashes surfaced: F-015 (PlayabilityFixer.TryGetReason: catch(JsonException) misses
  InvalidOperationException on non-object root and non-string reason) and F-016 (Issue.HasBlockingWarnings
  same shape). F-014 (**high**) also surfaced when I initially used non-Guid item_ids — the
  malformed row crashes the entire /Status endpoint via Guid.ParseExact in MediaDashDb.GetIssues.
  Cleanup verified — DB is back to a clean state.
- ✓ **Missing/unreadable paths** — Session 5: file-disappearing-between-scan-and-fix verified
  clean across all fixers (grep of File.Exists guards shows every one returns "no longer exists"
  which matches IsStaleFailure → row transitions to Fixed, no infinite retry). Session 6:
  permission-denied class covered — sharing-violation live-reproduced (F-019) and shown to trap
  the row in Queued forever with a fresh History row per 30-min retry. Same catch-block shape
  applies to UnauthorizedAccessException, DiskFull, generic IOException, and generic Exception
  paths (code-inspection; Windows admin bypass prevents live ACL deny, but the code path is
  identical to sharing-violation which WAS triggered).
- ✓ **Very large libraries** — Session 8: synthesised 10 000 zero-byte `.mkv` fixtures. Jellyfin
  refresh 545s → MediaDash scan 353.9s (35 ms/file — NOT super-linear, ~1.75× baseline per-file
  cost from the small-library scan). DB grew +9.4 MB (~940 B/issue); +20 003 issues (Playability
  + MissingSubtitle per file); +10 000 probe_cache rows; Jellyfin process memory stayed ~510 MB
  (no OOM). Scan itself scales cleanly. **F-021 filed** — the real problem at scale is the
  /Issues endpoint which returned all 20 171 rows in an 11 MB response regardless of `limit`
  query param. Extrapolates to 100-250 MB per request on realistic 50K-item deployments. Cleaned
  up: 20K stale issues DELETEd, big lib removed from MediaDash config + Jellyfin, 10K files
  rm -rf'd.
- ○ **Unicode** — emoji filenames, RTL Arabic titles, Cyrillic paths, combining marks,
  homoglyphs. Watch for scanner false-negatives (skipping the file entirely).
- ✓ **Concurrent user actions** — Session 5: 3 parallel POST /Issues/{id}/Approve → all 204,
  final status Queued (idempotent). Session 6: 20 parallel Approve+Dismiss on the same
  Detected id all returned 204 with no error, final DB state was last-writer-wins (typically
  Dismissed but could be Queued). No orphaned intermediate state, no restored_paths row
  written by Dismiss. Underlying "any status → any status" state-machine bypass is already
  filed as F-011; no new finding needed for this race. DELETE-library-mid-fix code-inspected
  clean (LibraryGuard re-reads virtual folders per-call → "outside your library" refusal →
  matched by IsStaleFailure → status Fixed; recycle bin root is applicationPaths.DataPath so
  unaffected by library removal).

## Cross-check against fixed bugs (regression guard)

For each 1.0.7.6 fix, confirm the regression can't recur:

- ○ Rung-4 on .mkv sources — extension-change swap for .mkv → .mkv landing
- ○ +discardcorrupt propagation to output — outputs still play under strict `-xerror`
- ○ TrackFixer bitmap-sub drop → warning consent gate honoured for both AudioLanguage and
  SubtitleLanguage issue types
- ○ Diagnostics.StableStringHash — restart → same message → single row not two
- ○ Grouper spooks S01E06 → refuses to emit movie-group; sanitizer strips leaked SxxExx
- ○ FixTask review grace — 10-min window after manual scan blocks scheduled fix; "Run
  fixes now" bypass still works
