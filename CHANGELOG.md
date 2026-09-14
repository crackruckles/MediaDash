# Changelog

Release notes for every published version are on GitHub Releases: https://github.com/crackruckles/MediaDash/releases

The Jellyfin plugin catalog also shows the changelog for each version — open **Dashboard → Plugins → Catalog** in your Jellyfin server, or read `manifest.json` in this repo.

## 1.0.7.6 (unreleased)

- fixed the entire dashboard (`GET /MediaDash/Status`, `GET /MediaDash/Issues`) crashing with HTTP 500 when a single row in the `issues` table had a malformed `item_id` (not 32-char hex GUID) — one bad row bricked the whole surface. `MediaDashDb.GetIssues` and `GetIssue` used `Guid.ParseExact` which throws `FormatException` on any non-matching string; the exception bubbled up through the controller → `ExceptionMiddleware` → 500. Now `Guid.TryParseExact` is used everywhere; malformed rows are logged to the Errors tab as `MediaDashDb.MalformedItemId` with a copy-paste `DELETE FROM issues WHERE id = N;` clean-up hint and skipped so the rest of the payload still renders (audit finding F-014).
- fixed `PlayabilityFixer` trapping a Playability issue in `Queued` forever, retrying every 30 min and spamming the Errors tab, when the row's `DetailsJson` had a non-object root (e.g. `null`, `[1,2]`, `42`) or a non-string `reason` field. `TryGetReason` caught `JsonException` but `JsonDocument.Parse` on the malformed input succeeds — the throw actually comes from `TryGetProperty` on a non-object root (or `GetString` on a non-string element), both of which raise `InvalidOperationException` which the narrower catch missed. Added `ValueKind` guards before every property access. The sibling `TryGetString` (used by `TryGetDetail` / `TryGetTechnical`) got the same treatment; both now safely return `null` on any malformed shape (audit finding F-015).
- fixed `Issue.HasBlockingWarnings` (called by `FixTask`'s auto-queue consent-rollback pass) throwing `InvalidOperationException` on a non-object `DetailsJson` root, which aborted the whole rollback pass for that type and cascaded through the remaining `FixableTypes` in the same fix run — auto-queue silently stopped mid-way for types processed after the malformed row. Same fix shape as F-015: add `ValueKind != JsonValueKind.Object` guard before `TryGetProperty`. `IssueDto.ParseWarnings` (called on every `/Issues` API response) had the same latent bug and got the same fix — a single malformed row would otherwise 500 the whole Issues tab (audit finding F-016).
- systemic sweep: applied the same "guard root shape + guard property shape before `GetString`" pattern to every other `JsonDocument.Parse` call across the fix pipeline — `DuplicateFixer.keeperPath` read, `MediaGrouperFixer.action/source/target` read, `MediaSorterFixer.targetPath` read, `MissingSubtitleFixer.missingLanguages` array read, `OrphanCleanupFixer.kind` read, and `TrackFixer.externalFiles` array read. None had reported issues in the field, but each one exposed the same "one malformed row crashes the fix" surface documented by CLAUDE.md safety invariant #6 ("the plugin never throws on unexpected DB row shape"). 15 new unit tests pin every malformed input shape (`null`, arrays, numbers, strings, wrong-type properties, missing properties, malformed JSON) against the guardrail — a regression that removes any `ValueKind` check fails the suite loudly.
- fixed Track fix + Playability repair + Transcode fix reporting "Fix failed due to an unexpected error" on Linux setups where the target volume can't accept `utimensat(2)` timestamp restores (some SMB/NFS mounts, restricted-perm bind mounts, certain FUSE filesystems). Reported for TrackFixer on 1.0.7.5 (GitHub #59, @rolandg-reflow) — the fix HAD already succeeded (video rebuilt and swapped), but the F-202 timestamp restore added in 1.0.7.5 threw `UnauthorizedAccessException` which the surrounding `catch (IOException)` didn't match, so the whole fix reported failure. Widened the catches in TrackFixer, PlayabilityFixer, and TranscodeFixer to `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)` — the same shape MediaGrouperFixer and MediaSorterFixer already had. Timestamp drift on Recently Added still gets logged as an info-level trace but never fails the fix.
- fixed the Recycle bin settings hard-refusing to save when the target volume looks like it has less than 5 GB free, breaking Proxmox LXC / Docker bind-mount / restricted-container setups where .NET's `DriveInfo.GetDrives()` doesn't see the actual bind-mounted media volume and reports the small container root disk instead (GitHub #58, @jwtoler). The 5 GB check is now a soft warning with a red "Save anyway" button — users whose real drive has terabytes free but whose `DriveInfo` view is misleading can proceed. The real safety gate (FixTask's 3 GB free-space floor via `IsBinVolumeCriticallyFull`) still runs at fix-time against whatever volume MediaDash can actually measure, so a genuinely low-space volume still pauses fixes correctly. First-run wizard's equivalent check unchanged (hard-block still appropriate during setup).
- added Repair broken files ladder: before deleting an unplayable file, MediaDash now tries to salvage it in four steps — quick remux, drop broken streams, change container to .mkv, re-encode video. Each step is toggleable under Settings → MediaDash → Files that won't play → Repair broken files; all four are on by default. Container-change step resets Jellyfin watch history for the affected title.
- Files that won't play row action is now "Repair / Remove file" (was "Remove file") with hover tooltip clarifying that repair is attempted first and removal only happens if every enabled step fails.
- fixed the fix scheduler retrying "outside your library" refusals every 30 minutes forever — when a fixer refuses because the target's library was renamed or removed, the issue is now retired instead of piling on the Errors tab. Re-adding the library re-emits the issue on the next scan.
- fixed the repair ladder leaving a `.mediadash.repair.tmp*` sidecar next to the target when a rung's extension-change swap collided with an existing file at the new path. Collision is now refused before the source is recycled and the ladder falls through to the next rung.
- fixed Norwegian audio and subtitle tracks being flagged as unwanted: a "nor" allowed-language entry now also matches "nob" (Bokmål) and "nno" (Nynorsk), and vice versa. Most Norwegian media is tagged with the specific variant, so the pre-fix behaviour would silently delete every track for users who only added the macrolanguage.
- fixed the recycle bin blowing past its configured size cap during a single fix run — the cap now re-checks between items, not only at run start. When it trips mid-run, the remaining queue defers to the next window and the pause reason is shown on the Overview.
- fixed the Recycle bin tab mislabelling older-version auto-fix recycles as "Manual delete via Files tab". A real Files-tab delete now writes a history row (new IssueType `ManualDelete`) so the label is accurate; entries with no history row read "Recycled by MediaDash — origin not recorded" instead of a false accusation.
- fixed Duplicate fix leaving the removed copy's per-title folder plus .nfo / poster / backdrop / clearlogo / thumb sidecars behind. When the video's folder is dedicated (no other media, no sub-directories, keeper elsewhere, not a library root), MediaDash now sweeps the remaining sidecars into the recycle bin and prunes the empty folder. Restore is per-sidecar from the Recycle bin tab. Co-mingled folders are left untouched.
- added "Keep TMDB / TVDB ID in the canonical name" setting under Files wasting space. When "Rename re-encoded files to a canonical name" is on, the renamed output preserves an existing `[tmdbid-N]` (movies) or `[tvdbid-N]` (episodes) tag from the source filename — Sonarr / Radarr default naming survives the re-encode. Falls back to Jellyfin's provider IDs when the source has no tag. Off by default.
- fixed the Errors tab showing "Fix run failed — the fix pipeline threw an unexpected error" (with a Report an issue prompt) when a fix run was really just paused because the recycle bin hit its cap or the drive was low on space. Recycle-bin-full pauses, low-drive-space pauses, "everything set to Off" (`FixTask.NoRunnable`), "some issues skipped because their type is Off" (`FixTask.SomeSkipped`), and the Low-impact-mode tip now each render with their own title, icon, and actionable hint instead of hiding behind the catch-all "unexpected error" label. Malformed-issues-row diagnostics (`MediaDashDb.MalformedItemId` from F-014) also render with a labelled title and a paste-the-DELETE-statement hint instead of a bare source code.
- "This file can't be played" rows on the Issues tab now show a plain-language **Why:** one-liner stating what the scanner actually found (e.g. "The file contains no video stream.", "The file's extension is '.mp3' but its container is 'matroska,webm'."). Whitespace / line breaks collapsed to a single line and capped at 120 chars with ellipsis so raw ffprobe / ffmpeg error dumps (which can be many lines long) stay scannable. The reason also lands in the History-tab / Recycle-bin-restore entry: "removed unplayable file X — no video stream (kept in recycle bin)". Reason data was already captured by the scanner but never rendered.
- fixed Media Grouper collapsing year-differentiated TV reboots into a single folder (GitHub #43, reported by @hesourman for Doctor Who 1963/2005/2024, confirmed by @Vaygrim for `Silo (2023)/` getting renamed to bare `Silo/`). Jellyfin's TVDb match returns a bare `SeriesName` for all reboots of a show and strips year suffixes from names it "knows" — grouping on `SeriesName` alone destroyed the year-suffixed folders users manually created on disk. `MediaGrouperScanner` now runs a `ResolveSafeSeriesName` resolver first: (1) if the episode's parent-of-parent (or direct parent, for no-Season layouts) is a `Series Name (YYYY)` folder under the library root, that folder name is used as-is; (2) if not, and Jellyfin has a `PremiereDate`, the year is appended to `SeriesName` (matches Jellyfin's own recommended folder-naming convention); (3) plain `SeriesName` as last resort. Backported from the Media Organiser design spec §5.2.1; the future organiser (v1.0.8.0) reuses the same rule via a shared pure function so a fix in one can't drift from the other. 11 unit tests cover Doctor Who 1963/2005/2024 + Silo (2023) + PremiereDate fallback + no-Season layout + library-root-with-year guard.
- fixed the Errors tab filling with an alarming raw "ffmpeg failed" row when a Quality re-encode or a Track rebuild ran against a genuinely corrupt source file (DVR-recorded `.mpg` with mpeg2/ac3 bit-rot, MP4 with missing moov, truncated MKV, .flv with junk header, etc.). ffmpeg's own output tail ("expacc 127 is out-of-range", "Invalid frame dimensions 0x0", "moov atom not found", "could not find codec parameters", etc.) is now pattern-matched by a new `FfmpegExecutor.IsCorruptSourceError` classifier; when detected the raw diagnostic is suppressed (no encode will ever succeed on an unreadable input, so surfacing the tail is noise) and the fixer's history row instead reads "Can't re-encode — the source file appears corrupt (ffmpeg couldn't decode it). Enable 'Repair broken files' under Files that won't play to try to salvage it, or remove the file manually." Real encoder-side failures (bad codec params, disk full, unknown encoder, bitstream filter missing) still surface unchanged. 13 unit tests cover the classifier including the verbatim stderr from the user report.
- fixed the Errors tab filling with "ffmpeg failed" rows during repair runs. Each rung in the Playability repair ladder (quick remux → drop broken streams → container to MKV → video re-encode) is designed to fail on files it can't handle so the next rung can take over — but every rung's exit-code failure was being surfaced as a top-level ffmpeg error, so a single file falling through all four rungs produced up to four alarming Errors-tab rows before either succeeding or getting recycled. `FfmpegExecutor.RunAsync` now takes a `recordDiagnosticOnFailure` flag (mirrors the existing `recordDiagnosticOnTimeout`); the four repair rungs pass `false` so their probe-style failures stay in the debug log only. The final outcome (repair success or delete) still lands in History as before. Non-repair ffmpeg callers (TranscodeFixer, TrackFixer) keep today's diagnostic behaviour.
- raw ffmpeg / ffprobe error dumps no longer bleed into the History-tab, Recycle-bin-tab, or Issues-tab primary text. Fix messages, action strings, and "Why:" one-liners now stay plain-language ("Removed unplayable file X — The video stream is damaged and the decoder rejected part of it (kept in recycle bin).", "Rebuilding the file failed; the original is untouched.", etc.) and the raw multi-line ffmpeg / exception text is truncated and appended in-line instead of clobbering the top-line summary. The Recycle-bin chip for unplayable files also shortens from "Unplayable file removed" to **Damaged file**. Every user-visible fixer message across PlayabilityFixer, TrackFixer, TranscodeFixer, EmbeddedCoverArtFixer, SubtitleFontFixer, NfoFixer, OrphanCleanupFixer, MediaGrouperFixer, and the FixTask exception handlers is affected; the raw stderr / exception message is preserved in the truncated tail so troubleshooting and support tickets still have the technical context.
- fixed the opportunistic scheduled fix task racing a user's live review of the Issues tab. Before: click Scan → issues appear → start reading them → the scheduled fix task (fires every 30 min in the fix window) opportunistically catches your review in progress, auto-fixes half the issues out from under you, feels like the plugin is bugging out. After: any user-triggered scan grants a 10-minute review grace during which the SCHEDULED fix task skips (logs the reason with time remaining). The "Run fixes now" button always bypasses so users who WANT to fix immediately can. Scheduled scans (unattended) don't set the timestamp — the grace only fires after a manual click where there's actually a reviewer to protect. 4 unit tests pin the grace constant + the timestamp state contract; live-server E2E verifies the skip + bypass paths both fire.
- fixed Media Grouper suggesting `Group under <filename>` (one folder per episode) for TV episodes reported by users. Two overlapping root causes, both fixed: (a) when Jellyfin's TVDb/TMDb match failed on a real TV file, some builds populate `Episode.SeriesName` with the raw filename ("spooks S01E06") — `ResolveSafeSeriesName` only ran the filename sanitizer when `SeriesName` was empty, so this leak slipped through and grouped the episode under `spooks S01E06/` instead of `spooks/`; (b) when the same episode file was misfiled into a Movies library, Jellyfin classified it as a Movie and `BuildMovieCandidate` proposed grouping it under `movies/spooks S01E06/` — a movies-folder full of one-episode folders. Fix (a): `ResolveSafeSeriesName` now runs the SxxExx / NxN extractor as a defense-in-depth sanitizer whenever the resolved name still carries an episode marker. Fix (b): `BuildMovieCandidate` refuses candidates whose canonical name OR filename looks like a TV episode — those are Media Sorter's concern (a separate scanner already flags them for library relocation). No-op for well-formed movie names (Blade Runner, Interstellar, 2012 the movie). 15 new test cases cover the reported spooks example plus common variants (The Office, dotted-source `My.Show.S01E08.1080p`, `Some Show - 2x05`, movies with numeric titles that must NOT trigger the guard).
- fixed the Errors tab accumulating a duplicate row for the same recurring error message on every Jellyfin restart — six restarts while a MediaSorter target folder was misconfigured produced six identical `Media sorter — bad target folder` rows instead of one row with `count = 6`. Root cause: the dedup key on the `diagnostics` table was `(source, String.GetHashCode(message))`, but `String.GetHashCode()` in .NET Core+ is randomised per process as a hash-flooding mitigation, so the same message text hashed to a different value after every restart and slipped past the `ON CONFLICT DO UPDATE` clause as a "new" row. Replaced with `Diagnostics.StableStringHash` (FNV-1a 32-bit — deterministic across processes and platforms). Existing pre-fix rows stay in place but any genuinely active condition immediately settles onto its properly-deduplicated row on the next occurrence; resolved-and-stale duplicates can be cleared from the Errors tab manually. 5 unit tests pin the hash values so accidental algorithm drift doesn't silently break every user's dedup on upgrade.
- added data-loss consent gate for Track fix: when the language cleanup would incidentally drop bitmap subtitle tracks (VobSub / PGS / DVB in an `.m4v` / `.mp4` / `.m4a` / `.mov` container — see the codec-tag issue also fixed this release), the scanner now attaches a blocking warning to the issue and the FixTask auto-queue leaves it as Detected even under Automatic mode. Users see a yellow "⚠ needs review — data loss" chip on the Issues tab, a red panel spelling out exactly which tracks will be dropped, and an Approve-button hover tooltip that names the trade before the click. Only manual Approve promotes the issue to Queued — the fixer never silently drops subtitles under automatic scheduling. Existing FixMode = ManualApprove flow is unchanged (already required a click); Automatic flow now requires one deliberate click for these specific files. Guard is intrinsic to the situation (not a preference) so no new setting was added. Six unit tests plus a live-server E2E via directly-injected DB row prove the gate holds under auto-queue AND yields on manual Approve.
- fixed Track fix (audio / subtitle language cleanup) failing with a raw `ffmpeg failed on '<file>': [ipod @ ...] Tag text incompatible with output codec id '98314'` diagnostic when the source was an MP4-family file (`.m4v`, `.mp4`, `.m4a`, `.mov`) carrying bitmap subtitles — VobSub, PGS, or DVB (codec `98314` is VobSub). The mp4/ipod muxer refuses those subtitle codecs under `-c copy`, so the whole remux exited non-zero and the pre-fix file was left untouched (GitHub #56, reported by @fadern for HandBrake-encoded `TaleSpin (1990)/…m4v` files with a Danish VobSub track). TrackFixer's remux now folds any MP4-muxer-incompatible subtitle stream into the same negative-map list it already builds for wrong-language tracks, so the fix succeeds and the History-tab row spells out what was dropped ("… Also dropped 1 bitmap subtitle track the .m4v container cannot hold under -c copy."). Fires only when the target extension is MP4-family AND the source has a genuinely incompatible bitmap sub; MKV / M2TS / TS remuxes pass through unchanged, and text subs (subrip, ass, mov_text) are never dropped. 6 unit tests cover the codec / extension matrix.
- fixed the Repair broken files ladder never getting past rung 1 or rung 2 for any `.mkv` source — every `.mkv` file whose repair required rung 3 (container coerce) or rung 4 (video re-encode) silently fell through to delete instead of being salvaged. The `TrySwapRepairedAsync` collision guard was comparing the extension-changed target path against `File.Exists(finalPath)` without accounting for `Path.ChangeExtension(x.mkv, ".mkv")` being a no-op — the "collision" it detected was the source file itself, still on disk pre-recycle. Fix computes `pathActuallyChanged = extensionChanged && !string.Equals(finalPath, issue.Path)` and only applies the collision guard + the library-monitor double-notify when the extension truly changes. Rung 4 on `.mkv` sources (the most common repair path for bit-flip damage) now works instead of always deleting.
- fixed rung 1 (quick remux) producing outputs that failed strict decode on any file with tail-truncation damage — MP4/M4V downloads interrupted mid-transfer had a partial AAC packet at the tail that `-c copy` copied through, then the AAC decoder choked on the incomplete tail and Jellyfin refused to direct-play the "repaired" file. Added `+discardcorrupt` to the `-fflags` on all three repair ffmpeg calls (rungs 1, 3, 4); the demuxer now drops the trailing partial packet at read time so the remux writes a clean container terminating at the last complete packet. No effect on whole files or MKVs where the packet boundary was already clean.
- added `tools/repair-test/torture-test.ps1` — real-file end-to-end test that downloads a Big Buck Bunny clip and breaks it 15 different ways (MP4 / MKV / FLV / AVI tail truncations at various sizes, mid-file XOR bit-rot at 0.5–8 %, zeroed regions, 100 scattered byte flips, EBML head damage, moov-only-4KB stubs, extension-lied files, doubled damage), then runs the whole scan + fix loop against a live Jellyfin and verifies every output either plays end-to-end under `-xerror` OR was safely recycled with the original preserved. 15/15 handled correctly on the current build in ~21 s of fix work — including 8 % XOR damage on real 720p H264 that rung 4 re-encodes cleanly.
- reworked the `tools/repair-test/` fixture suite to actually test what real users hit. Nine broken fixtures now cover: MKV tail truncation (128 KB + 100 KB), MP4 with intact faststart moov + 400 KB tail truncation, two-audio MKV with one broken track, MPEG-4 in AVI + tail truncation (fixture name is legacy — HEVC-in-AVI was untestable because ffmpeg mis-identifies HEVC packets as rawvideo), H264 in FLV + tail truncation, and two mid-file bitstream XOR variants (2%, 5%). Every broken fixture ends the run with a strict-`-xerror`-decodable output somewhere on disk (either its original name if rung 1/2 handled it, or the `.mkv` variant if rung 3/4 did) — the "would Jellyfin actually play the repaired file?" gate is now enforced. `validate-fixtures.ps1` mirrors the plugin's scanner (exit-code OR 90 % shortfall) instead of the stricter `-err_detect explode` alone. `validate-outputs.ps1` accepts whichever rung succeeded first (semantic bar is "playable output exists" not "specific rung fired"). Retired `rung2-broken-sub.mkv` — isolating a broken-subtitle-only scanner trigger is unrealistic and `rung2-broken-second-audio.mkv` already covers rung 2's drop-broken-stream flow. 9/9 fixtures end-to-end through a live Jellyfin at localhost:8099 in ~8 s of fix work.
- fixed the four Repair broken files checkboxes (Settings → Files that won't play → Repair broken files) rendering unchecked on first render. If the Settings page was ever Save-submitted before the load pass reached those inputs, all four flags got persisted as `false` — which silently disabled the whole repair ladder and every unplayable file went straight to delete without a rescue attempt, no visible indication anywhere. Initial checkbox HTML now defaults `checked` so a race can never bake in the off state. Existing users whose config was persisted as `false` need to re-tick the boxes in Settings (or delete the four `<RepairAttempt…>` lines from `plugins/configurations/Jellyfin.Plugin.MediaDash.xml` and restart — the constructor defaults will apply cleanly).

---

## 1.0.7.5 (released 2026-09-06)

- added Fix window setting (only run scheduled fixes between hours you choose)
- added Low system impact mode (throttles ffmpeg so it doesn't hog CPU or disk on daily-driver machines)
- fix run progress now shows items remaining / total instead of a time estimate
- fixed Blu-ray remuxes rejected for duration mismatch when the source container had unreliable metadata (Spider-Verse AV1, Lion King 2019 and similar)
- fix runs now check every 30 minutes (was 15)
- added Delete button per row on the Recycle bin tab
- fixed Recycle bin listing crash when a batch auto-purged mid-request
- fixed Recently Added timestamps drifting on files moved between drives
- fixed Media Grouper re-emitting the same Ungrouped issue every scan when the target folder was already occupied (the Group fix would then fail "same name already exists" every fix run — user report: Yellowstone (2018) duplicated at both root and inside the canonical series folder)
- cleared stale "unplayable" false-positives left over from earlier versions
- Overview now respects your library selection
- fixed Files tab errors when part of the config isn't ready yet
- fixed already-converted trickplay folders re-appearing on the Issues tab and re-running every scan (the scanner now skips sprites already handled by an earlier fix, and re-flags only what Jellyfin regenerates)
- fixed the fix scheduled task getting put back after users delete it from Dashboard → Scheduled Tasks (Settings → Save no longer resurrects the trigger; the explicit "Reset scheduled task" button still works)
- fixed hardware-accelerated re-encodes running with software decode (NVENC / QSV / VAAPI encoders now decode on GPU too when the source is h264, hevc, vp9, or av1; falls back to CPU decode on any failure, and legacy input codecs use today's software-decode path unchanged). AMF and VideoToolbox stay on software decode: ffmpeg has no GPU-resident scale filter for their pixel formats, so hardware decode would force a GPU↔CPU copy per frame for the downscale step and end up slower than software decode on the same workload.
- fixed Errors tab badge showing "999+" while the tab itself listed only a handful of rows (the every-3s refresh was summing dedup occurrences instead of the persisted row count, so a single hot spammy error inflated the badge)
- added "Show ignored" toggle on the Issues tab so previously-dismissed issues are visible again, with per-row Unignore and a bulk "Unignore all shown" (individual dismiss was already reversible via Undo; there was no way to see or un-dismiss anything after leaving the tab)
- fixed "Fix run finished with failures" dashboard alert stacking one modal per scheduled fix run when the page was left open — the alert now fires ONCE per page session and its message aggregates every completed run since the page loaded (reload to be alerted about future runs)
- scheduled scan now runs daily at midnight (was 2 AM) so it only has to catch a day's worth of changes; idle-check still defers if a viewer arrives at 00:00. Existing installs keep whatever time you set — only the fresh-install default changed.
- scheduled fix now silently skips when nothing is queued (was Info-logging every 30 min even when there was nothing to do). Fix run still fires every 30 min inside the fix window when idle, but only does real work when the scanner has produced something to fix.

625 / 625 tests green. One binary for Jellyfin 10.11 and 12.0.

---

## 1.0.7.4

- fixed duplicate remuxes when a file needed both track cleanup and re-encoding

594 / 594 tests green. One binary for Jellyfin 10.11 and 12.0.

---

## 1.0.7.3

- overhauled Recycle bin
- overhauled duplicate detection
- fixed symlinks / 0-byte de-dupe issue
- fixed orphan debris music / audiobook issue
- fixed remake duplicate detection issue
- fixed file date issue on rebuilt files
- fixed dry-run marking vanished files as fixed
- added container / extension mismatch check
- fixed files being wrongly flagged as unplayable
- fixed drive-health errors repeating on every refresh
- fixed remuxes failing on files with negative or shifted timestamps
- fixed duplicate remuxes when a file needed both audio and subtitle cleanup
- added Jellyfin logs shortcut in the Files tab
- renamed "Copy diagnostics" to "Report an issue"

One binary for Jellyfin 10.11 and 12.0.

---

## 1.0.7.2

- fixed sharing violations in scanners (files that "were in use" during a fix)
- fixed catch-all "Fix run — disk error" hiding four different causes
- fixed 15-minute retry storm on files Sonarr / Radarr had already renamed
- fixed unreachable NFS / SMB mounts locking up whole scan runs
- fixed Errors tab lighting up twice for large Blu-ray remuxes that actually succeeded
- added Uninstall instructions to the README
- 444 / 444 tests green

---

## 1.0.7.1

- fixed SmartHealth noise for users upgrading from 1.0.6
- added Recycle bin shortcut on the Files tab (read-only)
- added "Merge into current bin" button to legacy-batch rows on the Errors tab
- release tooling now pins the version tag to the exact commit

---

## 1.0.7

- Recycle bin will not touch anything MediaDash didn't create — closes the "MediaDash Empty deleted my other tool's files" case
- fixed Recycle bin retention silently never purging
- fixed Files tab actions racing symlink swaps
- fixed startup blocking on a slow / offline Recycle bin location
- added "Pause fixes when the bin reaches N GB" setting
- added "Reset scheduled task" button (Maintenance)
- added Hearing-impaired subtitle mode
- added "Ignore subtitle provider rate limits" toggle (default on)
- added right-click hide on the System Performance card
- Windows SMART no longer spams the Errors tab for NVMe drives Windows can't read
- language chip and task-pill sizing fixes
- language packs regenerated across 9 UI languages

---

## 1.0.6

- fixed duplicate detection false positives (Futurama specials, franchise / episode collapse)
- added confidence scores (0.00–1.00) to every duplicate report
- added three-tier duplicate matching: byte-identical → provider ID → heuristic
- added "auto-fix confidence threshold" setting (default 0.80)
- added file hash cache so identical files don't re-hash
- fixed artwork fix leaving posters 404-ing until the next Jellyfin refresh
- fixed dry-run writing to disk in edge cases
- analytics ID now rotates monthly instead of being a permanent UUID

---

## 1.0.0

- first stable release after two security audits, two UX audits, and two docs / migration audits
- 70+ correctness and safety findings closed
- Recycle bin gained verified cross-volume copy and symlink refusal
- upload size cap enforced before the transfer starts
- Windows-reserved filenames (CON, PRN, AUX, NUL, COM1…) refused on the Files tab
- Corrupt artwork and Ungrouped media detectors fully wired into the UI
- analytics off by default

---

## 0.9.9.x

- added Redownload warning banner when a file MediaDash fixed comes back
- added one-click restore of the "optimised twin" from the Recycle bin
- added post-Jellyfin 12 cleanup sweep for orphan trickplay folders
- added Recycle bin cross-volume warning
- fixed VAAPI encoder detection on some Linux distributions

---

## 0.9.1

- fix scheduling switched from a daily time picker to an opportunistic 15-minute check
- automatic migration from any legacy daily-time schedule on first boot after upgrade

---

## 0.9.0

- Duplicate, Playability, Misplaced, Stale and Quality scanners now cover Music, Audiobooks, Books and Comics
- added Corrupt artwork scanner + fixer (metadata folder only — never touches user-placed art)
- added EPUB / PDF / MOBI / AZW3 integrity probes
- added CBZ / CBR / CB7 integrity probes
- added audio ceilings to the Quality scanner (MP3 > 320 kbps, AAC > 256 kbps; audiobooks opt-in)
- one binary works on Jellyfin 10.11 and 12.0

---

## 0.7.3

- added Stale content scanner (default: unplayed for 365+ days, Detect only)
- Jellyfin 12.0 compatibility
- Playability scanner catches three new "sort of plays" failure modes

---

## 0.7.x (before 0.7.3)

- configurable fix schedule
- UI localisation added for German, Spanish, French, Italian, Dutch, Portuguese (Brazil), Russian, Simplified Chinese
- opt-in community stats board
- Settings tab redesign
- mobile-responsive UI pass

---

## 0.6.0

- added Missing subtitles fix (downloads via Jellyfin's configured providers)
- added multi-step first-run wizard
- added hardware GPU picker
- fixed AMD APU GPU usage reporting on Rembrandt / Phoenix chips
- queued issues now count toward "Space you could reclaim"

---

## 0.5.x

- added Misplaced files scanner
- added History tab filter chips
- added first-run library-access check
- added Recycle bin cross-volume warning
- added hardware encoder + preferred GPU pickers
- added Errors tab retry button
- added canonical rename after re-encode
- added ffprobe cache
- Skip HDR content default flipped on

---

## 0.4.x

- added multi-GPU system stats card (NVIDIA, Windows perf counters, Linux sysfs)
- added Files tab
- added per-fix disposal picker (bin vs delete)
- permission errors now surface on the Errors tab
- added thorough playability check (opt-in — decodes samples)
- added thumbnails on the Issues tab

---

## 0.1 – 0.3

- five original scanners: duplicates, playability, quality, subtitles, audio
- dry-run default and Recycle bin so every fix is reversible
- verify-before-swap: the rebuilt file has to play before it replaces the original
- three-question first-run — usable in under a minute

---

## Reporting issues

Use the **Report an issue** button on the Errors tab — it copies your MediaDash / Jellyfin / OS versions and every recent error to your clipboard, and opens a fresh GitHub issue in a new tab. Paste and describe what you were doing when it happened.
