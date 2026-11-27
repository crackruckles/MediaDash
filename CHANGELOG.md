# Changelog

Release notes for every published version are on GitHub Releases: https://github.com/crackruckles/MediaDash/releases

The Jellyfin plugin catalog also shows the changelog for each version — open **Dashboard → Plugins → Catalog** in your Jellyfin server, or read `manifest.json` in this repo.

## 1.0.8.2 (unreleased)

### Features and improvements

- Issues offers **Scan & approve all** when listed issues need a fresh scan: it scans again for just those issue types and then approves every issue that comes back ready (blocked ones stay listed with their reason). Enter season number now opens a MediaDash dialog instead of a browser prompt, and an issue replaced by a newer scan reloads the list instead of failing with Not Found.
- A move held by an earlier move that stopped part-way now offers **Move anyway** instead of Fix settings. The earlier move is finished when its video already moved and the rest is unchanged (nothing is overwritten); otherwise its hold is released with every file left where it is. Ungrouped media is then scanned again so the item gets a fresh plan.
- An episode whose season Jellyfin could not tell now offers **Enter season number**. The number is kept for that episode, the move is planned again into that season folder, approved, and the fix run starts. The season in an episode's own filename (S04E01) is now used first, so files like that are no longer blocked.
- Other move findings that need review offer Scan now instead of Fix settings; no setting changes them.
- Added live disk read/write speeds to the Overview system stats, alongside CPU, memory and GPU activity, where the operating system exposes them. Each disk uses a half-circle graph with separate read and write colors on a shared speed scale.
- Stale content can now be deleted from the Issues tab after approval, with a choice of recycling or permanent deletion. Settings offers Off and Ask me first; automatic age-based deletion is deliberately unavailable.
- Files Move and Rename now preview the complete operation before approval. Grouping loose videos includes their matching subtitles, metadata and artwork; History records completed moves for Undo and keeps interrupted operations available for review. (#43)
- Linux and Docker installs can automatically use a managed recycle folder on the source mount when the configured bin is on another filesystem. Existing settings are preserved, and managed recycle folders are excluded from scans.
- Removed the redundant Detect only option from Settings and setup. Existing configurations migrate to Ask me first without resetting other settings or approvals. Types that support automatic fixes still offer Automatic. (#81, #83)
- Scan progress now advances within long scanner stages. The dashboard uses lightweight progress polling and refreshes heavier status information less often. (#81)
- Playability scanning can check two files concurrently when Low system impact mode is off. MediaDash limits its FFmpeg/FFprobe jobs to two in that mode and one in Low system impact mode; waiting jobs can be cancelled without using up their runtime limit.
- With Thorough playability checks off, scans decode short samples at the start, middle and end instead of relying on metadata alone. On supported Linux VAAPI setups, these samples use Jellyfin's configured GPU.
- Grouping and sorting plans use filesystem identity and metadata checks instead of reading every byte of each media file, reducing scan I/O for large folders.
- Free-space errors now identify the target drive, required space and available space, with per-file details in the Errors tab. (#89)
- Right-click (or long-press on touch) **Scan now** to choose which scans to run, for example only Ungrouped media. Tick one or more scans and select Run; findings from the other scans are left unchanged. Scans turned off in Settings are shown but can't be selected.
- Grouping now matches every folder of a show by its TVDb ID, so release folders such as `www.site.org - Show S04E03 1080p` join the show's main `Show (Year)` folder whatever they are called. TVDb IDs come from Jellyfin first; titles Jellyfin could not identify are matched against shows already in your library, then looked up on TVDb with MediaDash's built-in key (or your own, in Settings). Lookups are limited to about 16 per second and cached. Folders that name a different year are never merged.
- After an episode is grouped, the old folder is recycled together with everything left in it (artwork, NFOs, release notes, samples), and Undo restores it. Files that belong to another library item, files added or changed after the scan, and unrecognised videos of 200 MB or more keep the folder for review.
- Every message on the Errors tab was rewritten in plain language: what happened, what MediaDash did about it, and what you can do. Underlying system errors appear at the end as Details. Refused moves now say exactly what changed since approval (for example that a file was rewritten, with its old and new size).
- Emptying the recycle bin can now empty every recycle bin at once (all libraries and drives), or just the current one.

### Fixes

- Duplicates now finds different releases of the same TV episode. Each release name used to count as its own edition, so only identically named copies were ever flagged; copies Jellyfin merged as versions of one episode were skipped too. Episodes now split only on an explicit `{edition-…}` tag or Jellyfin's labelled versions (`Show S01E01 - 1080p` beside `Show S01E01 - 4K`); merged versions count only when every filename names the same episode. Movies are unchanged.
- Duplicate episodes are no longer missed when one release names the episode title and another does not (for example `Show.S09E05.Title.1080p-A` and `show.s09e05.multi.1080p-b`). When both filenames carry the same episode number, the title-similarity veto is skipped; the runtime check still applies.
- An episode whose identity grouping recovers from move history no longer carries along the NFO Jellyfin wrote while it was unidentified (which kept titles like `_unpack`); Jellyfin writes a fresh one.
- Jellyfin's library scan now waits for MediaDash. A library scan that starts while MediaDash is scanning or fixing is paused and runs once when MediaDash finishes, so it indexes the finished result instead of a half-moved library. Run now (scan or fixes) also pauses a library scan that is already running; a scheduled MediaDash run still waits rather than interrupt one. Previously scheduled runs stopped themselves when a library scan began, and Run now ran alongside it.
- Ungrouped media now produces Jellyfin's naming layout. Episodes go to `Series (Year)/Season NN/` instead of keeping their release folder inside the series folder or staying loose in it, and a series folder is only renamed whole when its episodes are already in season folders. Episodes already nested or loose inside a series folder are now reported so they can be regrouped.
- Ungrouped media no longer leaves movies wrapped inside another folder (`Super Troopers/Super Troopers 2 (2018)/`). Wrapped, year-less and release-named movie folders are proposed as `Title (Year)` directly in the library, and a release folder inside a movie's own folder has its video moved up. Versions and multi-part movies keep their shared folder name so Jellyfin still groups them.
- Ungrouped media recovers episodes whose name an earlier grouping move removed (for example a video left as `info/info.mkv` after its release folder was recycled). The season and episode are read from MediaDash's own move history, and the file and its NFO, thumbnail and trickplay are renamed after the original release so Jellyfin can identify it; copies of the same episode are then picked up by Duplicates. An episode numbered only by its folder is renamed when moved out of it, and grouping reuses an existing season folder such as `Season 9` instead of creating `Season 09`.
- Moving many episodes out of one folder no longer refuses the rest as "changed after approval" when Jellyfin rewrites an already-moved episode's NFO at its old path. An episode move only takes files named for its own video, so other items' NFOs and artwork no longer count as changes to it.
- Orphaned debris now reports folders in Movies and Shows libraries that hold only Jellyfin artwork, NFOs and release notes after their video moved away. Artwork and `.txt` files there no longer count as pictures and books.

- Leftover-folder cleanup now recognises matching episode thumbnails, episode NFOs and season posters. TVDb IDs in tvshow.nfo also link alternate series titles for grouping and recoverable cleanup. Inferred leftovers containing unknown files or media stay protected.
- Fixed Ungrouped media scans skipping anime movies and episodes when an Anime destination is configured.
- Orphan cleanup now detects artwork-only release folders after grouping even when Jellyfin no longer lists the old series folder. Matching requires an unambiguous identified show and preserves folders containing media. Inferred folders containing only standard series artwork and tvshow.nfo are recycled even when their contents differ from the canonical show folder, which stays unchanged. Unknown files remain available for review, and NFO-only leftovers use the same recoverable cleanup.
- Grouping now finishes in one fix run: once the last episode leaves a release folder, the leftover folder for that show is recycled in the same run instead of appearing as a new issue to approve afterwards. Dismissed leftovers stay dismissed, and Undo still restores them from the recycle bin.
- Embedded cover removal now works on .m4b/.m4a audiobooks with chapters. The chapter-title track used to make the rewrite fail, so the audiobook kept its embedded cover; chapters are now rebuilt during the rewrite. Its error message no longer suggests a permissions problem when the cause is something else.
- Duplicate detection checks what each episode filename says before offering two files as copies (issue #3). Different parts ("Part 1"/"Part 2", "(1)"/"(2)"), double episodes beside single ones, literal names such as "Season 1 Episode 3" that Jellyfin filed under the wrong number, and specials whose names do not confirm the same special are no longer offered. Episode titles from TVDb tell apart files without episode numbers and raise confidence when both names match. Episodes are grouped by TVDb series, so copies in differently named show folders are found; copies from two show folders are only offered when their names agree on the show. Byte-identical files still match whatever their names say.
- The suspicious-file check now looks inside .iso and .img disc images by reading their file list, without mounting them. An image holding programs or scripts and no DVD/Blu-ray video (a common malware wrapper posing as an episode or film) is flagged like any other executable. An image holding no video at all is flagged for manual approval only. Real DVD and Blu-ray images, including retail discs with PC extras, and images MediaDash cannot read are left alone.
- History no longer promises that every unsuccessful attempt will retry; completed or superseded attempts now say "not applied".
- Fixed unsupported-format conversion settings overflowing their boxes on mobile. Labels wrap beside their checkboxes, with help text underneath.
- Fixed Intel QSV downscaling arguments that could fail hardware re-encoding. The calculated output width is now even, and the existing software fallback remains available. (#93)
- Rebuilds, re-encodes and playability repairs now check free space at the output location, including Linux/LXC bind mounts and Windows network shares. An unavailable measurement is reported separately from insufficient space. (#89)
- Libraries on Windows network shares (`\\nas\share`) now appear on the Overview storage card with a "network share" badge, count towards the free-space total and the low-space warning, and trigger the "recycle bin is on a different volume" notice when it applies. Shares on the same NAS volume are listed once. A sleeping or offline NAS cannot stall the dashboard: readings wait at most 3 seconds and the last good reading is shown, marked as out of date, until the share answers again.
- Playability checks no longer treat photos, unknown durations, temporary storage errors, decode timeouts or unavailable FFmpeg as proof of corruption. Valid stream durations are used when container duration is missing, and cached timeouts are retried. (#72)
- Decode checks tolerate isolated recoverable frame errors while still rejecting excessive decode errors, empty output, fatal failures and detected truncation. Short-file checks retain the expected duration so an early end is not mistaken for success.
- Audio repair verifies audio streams and duration without requiring video, and does not mistake embedded cover art for a video stream.
- Failed media repairs preserve the original and return to Issues with Delete or Ignore. Deletion requires fresh approval and follows the configured disposal setting.
- Language cleanup stops safely when original-language metadata cannot be read, preserving tracks rather than guessing which language can be removed. External subtitle cleanup also avoids treating the media file itself as a subtitle sidecar.
- Orphan cleanup preserves folders containing media companions, including subtitle subfolders. Orphan metadata cleanup uses Jellyfin's configured metadata root and rechecks scope, protection, contents and file identity before recycling. Run Jellyfin's Scan Media Library task after detection and before applying approved metadata cleanup; changed contents require a new scan and approval. History recovery preserves displaced replacements. (#7, #86)
- Artwork repair preserves valid SVG and unknown formats, healthy replacements and files that cannot be verified. Confirmed corrupt artwork is recycled with History restore; failed updates preserve the source and its Jellyfin image binding. Selected-library rescans replace old findings instead of duplicating them. (#88)
- Duplicate detection and fixing refuse hardlinked, symlinked or junction aliases and recheck physical file identity before disposal. Unreadable identity information is handled conservatively.
- Grouping and sorting recheck the approved source, destination library, cancellation and Never-modify protections before changing files. Existing destination files are preserved, and changed or outdated approvals require a new scan and approval. (#43)
- Successful fixes refresh affected Jellyfin items, track lists and folder membership. Completed moves prompt for a Jellyfin library scan if the destination is still missing from the catalog. (#84)
- Automatic scans and fixes defer while Jellyfin's full-library scan is running. If it starts mid-run, MediaDash cancels incomplete scanner results or finishes the current fix safely and leaves the rest queued. Manual runs bypass this gate; realtime imports and metadata refreshes are not covered. (#72)
- Low system impact mode now pauses between hash reads on every platform and launches FFmpeg/FFprobe with idle I/O priority on Linux when ionice is available. Its effect depends on the storage scheduler. (#72)
- Fixed Issues loading and retry behavior: failed requests show an error with Retry, retain previously loaded results and allow switching between Open and Ignored. Latest-attempt lookups handle large retry histories more efficiently and consistently, and disconnected requests stop early. (#80)
- Run fixes now explains empty or blocked runs, including disabled types, missing approval, library scope and changed protections. Execution refusals are summarized on Overview and in Errors; blocked issues remain queued. Bulk approval skips blocked items, and failed task starts display an error. (#80, #81, #83)
- Missing-file issues now explain that approving the fix removes the dead Jellyfin entry, with the option to restore the file first.
- Fixed the dry-run banner shortcut when the Safety settings card is collapsed, mobile recycle-bin row layout, and light-theme readability for filters, recycle chips, warnings, maintenance buttons and Overview actions. (#70)
- Re-running setup now preserves the recycle bin's unlimited size setting. (#65)
- Misplaced files now recognises a film Jellyfin filed as an episode of a same-named show (no real episode number, a "Title Year" name whose year is not the show's, feature length) and moves it to Movies as "Title (Year)". Earlier move records are used to recover the film's name. An episode number that is really a year is no longer used to rename a file "S01E2025".
- Duplicates now matches different releases of one film in different folders (only explicit editions, cuts and resolution tiers stay separate), and finds one file hardlinked under two library entries. Removing the extra name frees no space but ends the double listing.
- Grouping and sorting can now move hardlinked files. Moves are same-filesystem renames, so the other links are unaffected.
- Heavy transcode no longer keeps listing a file after it has been re-encoded: transcode logs from before the last successful re-encode are ignored. (#95)
- Playability no longer reports healthy files as damaged when a subtitle track makes the file look longer than its video and audio; the end-of-file check now starts from where the video and audio end.
- The Overview's Disk I/O panel no longer shows duplicate tiles for LVM, device-mapper and RAID volumes, and its info icon renders correctly.
- A movie sitting alone in a folder of its own `Title (Year)` name inside a TV library is now recognised as a movie and offered for moving to Movies, instead of being proposed for a `Season unknown` folder. Grouping no longer renames a movie folder that is already `Title (Year)` or carries its year in brackets, and never adds the year twice.

### Upgrade notes

- Media moves, recycling and restoration require a same-filesystem rename. Cross-filesystem moves and Undo of older cross-filesystem moves are refused; supported Linux/Docker recycle operations use the managed source-mount folders described above. Other setups need a recycle bin on the source filesystem.
- Older queued grouping and sorting moves may require a fresh scan and approval before they can run.
- These notes describe the current unreleased implementation. Reporter-specific validation remains separate: the original #80 request-service disposal error, #72 import-triggered disk saturation and #81 large-library scan performance are not claimed fully resolved by these changes.

## 1.0.8.1 (2026-10-12)

- fixed the Issues tab going completely unresponsive on 1.0.8.0 — `GET /MediaDash/Issues` was throwing `ObjectDisposedException` at `ObjectResult.ExecuteResultAsync` and the UI silently rendered an empty list, so every button that depended on live issue data felt dead. Root cause: the new per-row "last attempt" lookup (`HistoryStore.GetLatestAttemptsByIssueIds`, added in 1.0.8.0) ran a correlated subquery against `history` with no supporting index — on any server with substantial history the request stalled long enough for Jellyfin to tear down the request scope mid-flight, which then crashed MVC's result serializer. Schema v11 adds a covering index on `history(issue_id, dry_run, fixed_at_utc)`. Migration is non-destructive — no rescan, no history loss, no re-queue needed. (#80)

## 1.0.8.0 (2026-10-11)

### New
- **Protection**: right-click (or long-press on mobile) in the Files tab for "Protect this path" or "Add to issues". Protected paths are skipped by scanners and refused by fixers, even if a fix was already approved.
- **"Original language"** option in audio and subtitle language pickers — keeps your selected languages plus whatever Jellyfin identifies as the original. (#78)
- **Format converters**: images → JPG, music → FLAC/MP3, RMVB → MKV, plus a first-run wizard step to enable them. Audiobook folder-merge remains opt-in.
- **Disable SMART monitoring per server** via right-click on any drive's SMART pill — stops smartctl invocations against SMB shares that were generating noise. (#60)
- **Per-fixer disable toggle** in Settings → Fix order — mute a single fixer without flipping Automatic → Off.

### Fixed
- **Repair safety**: source identity captured before the first repair rung, so a new download mid-encode can't be overwritten; combined audio+subtitle cleanup and audiobook folder-merge re-check settings, protection and approvals before replacing; re-encoding preserves original-language and hearing-impaired tracks.
- **Format conversion safety**: output verified against source duration + audio-stream count; audiobook folder-merge refuses to overwrite an existing `.m4b`; RMVB → MKV preserves alternate audio tracks.
- **Playability**: catches damaged secondary audio tracks; books/comics (PDF/EPUB/MOBI/AZW/DJVU/CBZ/CBR/CB7) refused with a document-aware message instead of the ffmpeg ladder; audio files with embedded cover art no longer false-flagged. (#76)
- **MKV subtitles**: unknown codecs safe; HandBrake `.m4v` with alias subtitle names (dvdsub/pgssub/dvbsub) remuxes via a muxer-whitelist. (#75, #56)
- **Encoder-quirk guards**: Dolby Vision Profile 7 skipped by Quality scanning (prevents enhancement-layer drop); yt-dlp MJPEG thumbnails no longer picked as "video"; DaVinci Resolve timecode streams dropped on MP4-family remux.
- **Books**: pre-KF8 `.azw` (TEXtREAd magic) and `.djvu` now have real probes instead of being flagged unsupported.
- **Scanning accuracy**: Photos library covered; Overview library breakdown includes Music, Books and Photos; `.txt` books aren't orphaned; scan respects Enabled Libraries and continues past inaccessible subfolders; 10 unindexed ebook formats detected via filesystem sweep.
- **Cross-volume moves** refuse symlinks/junctions and verify file count + bytes before deleting the source. **Delete** respects protection rules on children of the targeted folder.
- **Overview**: progress text interpolates the percentage; repair totals include partial repairs; "Last scan" reflects actual last completion; drive storage and SMART samples stay live between polls.
- **Issues tab**: data-loss warnings shown under rows; Approve description matches live Recycle/Permanent setting; reloads after being hidden mid-fetch; open/ignored view toggles don't race; Queued rows get fresh failure messages; "Try to play" works on reverse-proxied Jellyfin; badge matches server.
- **Settings**: Save unblocks after hydration; in-flight save won't mark newer edits clean; Quality tolerance of 0 saves correctly; Scan awaits save resolution.
- **Status polling** recovers from transient failures with 3s / 6s / 12s backoff.
- **Misc UI**: first-run wizard requires at least one library ticked; History Retry on failed rows explains the row stays Queued; Files tab folder picker disables Accept while loading; recycle bin row messages span full width; "Reset scan data" preserves Queued and Dismissed decisions.
- **Calibre**: no longer hangs on verbose installers; paths with spaces work; override path changes picked up without restart; on Linux/macOS Jellyfin, now returns a clear "install Calibre and set CalibreOverridePath" message instead of a cryptic `cmd.exe not found` crash.
- **Light-mode readability**: Files tab right-click menu, Add-to-issues submenu, SMART-disable menu and Protect-this-path dialog were rendering with hardcoded dark colours and became unreadable under Jellyfin's Light theme. Switched to the plugin's theme tokens so they match whichever Jellyfin theme is active.

### Internal
- Shared commit-time policy helper for all fixer/converter paths — settings, protection and approvals re-checked just before the irreversible swap.
- Release publishing refuses to build if source isn't committed or files change mid-build; plugin-load smoke test tightened.
- Per-converter analytics counters in the monthly aggregate report.

## 1.0.7.7 (released 2026-09-14)

- fixed the Playability scanner flagging perfectly playable files as "unplayable" (which the new Repair broken files ladder in 1.0.7.6 then tried to salvage, and if repair failed, recycled). Root cause: a bitrate-vs-size pre-filter in `PlayabilityScanner.EvaluateAsync` compared `Format.BitRate × duration / 8` against actual file size and flagged anything under 60% of expected as `size-truncated`. Efficient x264 encodes (YIFY rips, small MP4 rips of Severance / Landman / Rage / Outlander at 400 MB – 2.4 GB) have real content bitrates 2×+ under the container-declared bitrate — they're not truncated, just efficient — and got mis-flagged. Removed the branch entirely; the decode-check that runs next already catches genuine tail-truncation via its decoded-time-shortfall heuristic (all 15 torture-test variants still pass, including the three MP4 tail-truncated ones the removed branch used to catch first). No user-visible regression, one whole class of false positives gone.
- fixed the Playability scanner mis-flagging DRM-protected files (iTunes Fairplay, Widevine, PlayReady MP4 / M4V) as unplayable. Now checks each stream's `codec_tag_string` for `encv` (encrypted video) or `enca` (encrypted audio) — ISO base media file format markers for encrypted sample entries — and skips playability entirely so the repair ladder can't touch them. Follow-up notification UX shipped in 1.0.7.8.
- fixed folder-based Jellyfin library entries — BDMV / VIDEO_TS / mounted-ISO trees, Blu-ray "BDRemux" layouts where Jellyfin's `Path` points at a directory not a file — being reported as "The file is gone but Jellyfin still lists it" every scan. `File.Exists(path)` correctly returns false for a directory; the scanner now falls back to `Directory.Exists(path)` and skips playability checks for folder-based items rather than flagging them missing.

---

## 1.0.7.6 (released 2026-09-14)

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
