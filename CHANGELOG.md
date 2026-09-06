# Changelog

Release notes for every published version are on GitHub Releases: https://github.com/crackruckles/MediaDash/releases

The Jellyfin plugin catalog also shows the changelog for each version — open **Dashboard → Plugins → Catalog** in your Jellyfin server, or read `manifest.json` in this repo.

## 1.0.7.5 (unreleased)

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
