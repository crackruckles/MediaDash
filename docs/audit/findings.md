# MediaDash Audit — Findings

Newest at top. Each entry gets a stable ID and stays in the file forever (status: fixed / rejected)
so a future session can trace history. IDs are hand-assigned — start from F-001 and increment.

## Severity legend

- **critical** — data loss, silent corruption, crash-loop, security
- **high** — visible bug hitting normal users, or a safety-invariant violation
- **medium** — edge-case bug, reasonable workaround exists
- **low** — cosmetic / performance / race without user-visible impact
- **noise** — investigated, does not reproduce or not actually a bug (kept for evidence)

## Status

- **open** — filed, not triaged
- **triaged** — user has decided how to handle (fix / defer / reject)
- **fixed** — code change landed; link the commit or CHANGELOG bullet
- **rejected** — not a bug or won't fix; state the reason

## Entry template

Copy this exactly for each new finding. Repro steps must be reproducible without you.

```
### F-NNN — <one-line title>
- Severity: <critical | high | medium | low | noise>
- Status: open
- Area: <scanner name | fixer name | db | api | ui | cross-cutting>
- Discovered: <YYYY-MM-DD by audit-session-N>
- Repro:
  1. <exact command / API call / UI step>
  2. ...
- Expected: <what should happen>
- Actual: <what actually happens>
- Evidence: <log excerpt / db row / stderr — enough to prove it's real>
- File:line: `path/to/file.cs:123` (and any related files)
- Suspected fix: <optional — hypothesis only, not required>
- Notes:
```

---

## Findings

<!-- Insert new findings above this line. Newest first. -->

### F-021 — GET /MediaDash/Issues returns every open issue in one un-paginated payload (measured: 20K rows = 11 MB response, no `limit`/`offset` support), UI + memory unable to grow past a few tens of thousands of issues
- Severity: medium
- Status: open
- Area: api MediaDashController.GetIssues + db MediaDashDb.GetIssues
- Discovered: 2026-09-13 by audit-session-8 (huge-library perf pass)
- Repro:
  1. Add a Jellyfin library backed by 10 000 zero-byte `.mkv` files (`for /L %i in (1,1,10000) do type nul > movie_%i.mkv`), get Jellyfin to index them (~9-minute library refresh), then enable in MediaDash.
  2. Trigger MediaDash scan — completes in ~354 s (35 ms/file, no super-linear scaling vs the ~20 ms/file for the small-library baseline). Result: 20 003 new issues (1 Playability "unreadable" + 1 MissingSubtitle per file), probe_cache grows from 65 → 10 065 rows, DB file grows by 9.4 MB (~940 B/file). Memory footprint of the Jellyfin process stays roughly flat (Windows tasklist shows ~510 MB — no OOM signal). **Scan itself is fine.**
  3. Now call `GET /MediaDash/Issues?openOnly=true&limit=100` — expected 100 rows, actual 20 171 rows in an 11 242 925-byte JSON payload. `limit=5` → same 11 MB. No pagination anywhere.
     ```
     $ curl .../Issues?openOnly=true&limit=100 | python -c "import json,sys; print(len(json.load(sys.stdin)))"
     20171
     $ curl .../Issues?openOnly=true&limit=5   | python -c "import json,sys; print(len(json.load(sys.stdin)))"
     20171
     ```
  4. Extrapolate: a real Jellyfin server with 50K movies + TV episodes routinely accumulates 100K-250K issues after a first scan (Playability + MissingSubtitle + Duplicate + Ungrouped all fire per-item). That's a 100-250 MB response on every dashboard load / tab switch / auto-refresh. The plugin's config page rerenders /Issues frequently (issues-tab open, filter chip click, dismissed-view toggle) — every render allocates a fresh 250 MB HTTP body in the Jellyfin process, then a fresh 250 MB DOM in the browser, then GC-collects them. At real scale that is a foreground UI freeze + a memory-pressure event on the server every few seconds.
- Expected: The endpoint accepts `limit` (max 500, defaults 100 or so) and `offset` (or a `cursor`/`sinceId`) so the UI can page + virtualise. Same shape needed on `/History` (the controller comment at line 631-634 EXPLICITLY says "History tab's per-library chart was client-side sum over the paginated 500-row window" — but `GetHistory` in `MediaDashDb.cs:1072` DOES paginate at DB level (`LIMIT 500`) while the `/History` API controller endpoint at `MediaDashController.cs:611-627` doesn't expose the parameter). The scanner-side aggregation on `/Status.Counts` already shows the whole-library totals so a paginated /Issues doesn't hide anything; the UI can render "20 171 issues (showing 100)" with prev/next.
- Actual: `MediaDashController.GetIssues` at `MediaDashController.cs:300-330` has three query params (`type`, `status`, `openOnly`), NO pagination. `MediaDashDb.GetIssues` at `MediaDashDb.cs:531-560` materialises the entire result set into a `List<Issue>` regardless of caller. Every row also parses through `Guid.ParseExact` on `item_id` (per F-014) — so at 20K rows a single malformed row crashes the whole endpoint AND the crash surface is now 20K rows wide, not "just what the user was looking at". Same "no pagination + full materialisation + Guid.ParseExact" pattern on `/History` controller (though the DB layer paginates that one to 500, so the crash surface is smaller — but still 500× the "one bad row" cost).
- Evidence:
  - Live measurement 2026-09-13 with 10K-fixture library at `C:\dev\mediadash-fixtures\_audit_session8_big`:
    - Wall clock MediaDash scan: 353 913 ms
    - DB growth: 1 064 960 → 10 469 376 bytes (+9 404 416 B, ~940 B/issue)
    - Issue count: 197 → 20 200 (+20 003)
    - probe_cache: 65 → 10 065 (+10 000)
    - `/MediaDash/Issues?openOnly=true&limit=100` returns 20 171 rows / 11 242 925 bytes / 338 ms
    - `/MediaDash/Issues?openOnly=true` (no limit) returns SAME 20 171 rows / 11 242 925 bytes / 330 ms
    - `/MediaDash/Issues?openOnly=true&limit=5` returns SAME 20 171 rows — parameter silently ignored (no error, no warning).
  - `Grep` confirms: no `FromQuery.*limit` / `offset` / `page` anywhere in `MediaDashController.cs`.
- File:line:
  - `Jellyfin.Plugin.MediaDash/Api/MediaDashController.cs:300-330` (`GetIssues` — no pagination params)
  - `Jellyfin.Plugin.MediaDash/Api/MediaDashController.cs:611-627` (`GetHistory` — same shape; DB paginates internally to 500 so smaller blast radius)
  - `Jellyfin.Plugin.MediaDash/Data/MediaDashDb.cs:531-560` (`GetIssues` — full materialisation, no LIMIT clause)
  - `Jellyfin.Plugin.MediaDash/Configuration/configPage.html:8565-8567` (`refreshIssueList` — calls `Issues?openOnly=true` without limit, so any new pagination has to be plumbed through the UI too)
  - `Jellyfin.Plugin.MediaDash/Configuration/configPage.html:8569-8583` (`loadHistory` — same shape)
- Suspected fix: Add `[FromQuery] int limit = 200, [FromQuery] long? afterId = null` to `GetIssues`. `MediaDashDb.GetIssues` gains `int? limit, long? afterId` parameters; SQL becomes `... AND (@afterId IS NULL OR id < @afterId) ORDER BY id DESC LIMIT @limit`. Return DTO adds `NextCursor` (last-id-in-page) and `TotalCount` (a separate `SELECT COUNT(*)` — cheap on the indexed status column). UI implements virtual-scroll or a paged view. Same pattern for `/History` (drop the DB-hardcoded 500 → paginate via query). Bonus: this halves the F-014 blast radius (one bad row crashes ONE page, not the entire endpoint) AND enables the missing sort/filter UX (`?type=Playability&status=Queued&limit=100`) that becomes valuable at 10K+ issues.
- Fix-plan phase: 6 (polish / cross-cutting perf). Not urgent for small libraries but foundational for any user with 10K+ items. Could be lifted to Phase 3 (state-machine) if grouped with F-011's endpoint refactor — the "shared query builder for /Issues + /History" would be a natural home for limit/offset. Alternately, phase 7 if this ends up being the tip of a "responsive-at-scale" iceberg (session 8's scan of 10K files was ~5.9 min — acceptable for a one-off but expensive if it's every scheduled scan; that's a separate perf story).
- Notes: The DB grows ~940 bytes per issue including indexes. At 100K issues the DB file crosses ~100 MB — still fine for SQLite, but every `GetIssues` call materialises the FULL row set into a `List<Issue>` in the .NET heap, then serialises it to JSON (~2-3× object footprint), then transports it over HTTP. That's memory pressure inside the Jellyfin process (competing with transcodes for the same GC heap) and in every browser tab that has the MediaDash dashboard open. Also note: `WasPreviouslyRestored` enrichment in `GetIssues` calls `GetRestoredPathsBlockingAutoQueue(type)` once per distinct type — reasonable at 9 types, but if a future release adds more IssueTypes this becomes O(types) queries per request; combined with un-paginated results it compounds. Complementary shape: session 5's F-014 crash pattern (`Guid.ParseExact` on every row) becomes catastrophic at 20K+ rows because the crash surface widens with library size. Pagination is a defense-in-depth for that too: bad rows in page 3 don't kill pages 1-2.

### F-020 — UNC / network-share library paths silently bypass every disk-space safety check in the plugin (TranscodeFixer, TrackFixer, RecycleBin.MoveToBin, MediaSorterFixer, FixTask.IsBinVolumeCriticallyFull)
- Severity: medium
- Status: open
- Area: cross-cutting RecycleBin.FindDriveForPath + every caller
- Discovered: 2026-09-13 by audit-session-8
- Repro:
  1. Add a Jellyfin library whose Location is a UNC path (e.g. `\\localhost\c$\dev\mediadash-fixtures\_audit_session7\unc_lib`). This is a normal deployment shape for anyone running Jellyfin against a NAS.
  2. Enable that library in MediaDash's `EnabledLibraries` config. Scan — issues are correctly emitted with the UNC path preserved verbatim in `issues.path` (e.g. `\\localhost\c$\dev\mediadash-fixtures\_audit_session7\unc_lib\UNC Broken (2024)\UNC Broken (2024).mkv`).
  3. Approve a Playability fix and let FixTask run. The fixer moves the file into the (local-drive) recycle bin. Live-verified against issue 8777 on 2026-09-13: fix succeeds, recycle_path lands on `C:\Users\...\jellyfin\data\mediadash\recycle\...`, restore-from-history correctly puts the file back at `\\localhost\c$\...`. **Functionally: UNC works.**
  4. **The gap:** examine `RecycleBin.FindDriveForPath(path)` at `RecycleBin.cs:213`. It iterates `DriveInfo.GetDrives()` and looks for a local drive whose `RootDirectory.FullName` prefixes the given path. **UNC paths have no matching local drive**, so the method returns `null` for every UNC source. Live-confirmed via .NET runtime: `Path.GetFullPath("\\\\localhost\\c$\\...")` returns `\\localhost\c$\...` verbatim; `DriveInfo.GetDrives()` returns C:\, D:\, etc., none of which prefix a UNC path; `LibraryGuard.IsUnder(unc, "C:\\")` returns false.
  5. Every caller that pre-checks disk space via `FindDriveForPath` guards on `if (drive is not null && ...)` — so **null quietly skips the check**. Grep confirms the pattern at:
     - `Fixers/TranscodeFixer.cs:141-146`: "Not enough free disk space to re-encode this file" pre-check.
     - `Fixers/TrackFixer.cs:225-230`: "Not enough free disk space to rebuild this file" pre-check.
     - `Fixers/MediaSorterFixer.cs:110-135`: "Not enough free space on the target drive" pre-check for cross-volume moves.
     - `Fixers/RecycleBin.cs:166-184` (`MoveToBin`): "Not enough free space on the recycle bin volume" pre-check — the branch that tells the user "put the bin next to the media" only fires when BOTH src and dst resolve to a drive.
     - `ScheduledTasks/FixTask.cs:1048-1052` (`IsBinVolumeCriticallyFull`): if a user configures a UNC recycle bin path, this returns `false` unconditionally, disabling the "pause fixes when bin volume < 3 GB free" safety pause.
  6. Concrete consequence for TranscodeFixer/TrackFixer: safety invariant #5 in `CLAUDE.md` says "Check free disk space (≥2× source size) before any transcode". For any library backed by UNC (NAS / SMB / DFS), this check is silently skipped. The OS still surfaces "No space left on device" mid-encode via IOException, so no data loss (temp/swap pattern preserves the original), but the user sees the raw ffmpeg error instead of the friendly "needs its own size plus about 500 MB free" preventive message. **The invariant is violated on paper.**
- Expected: `FindDriveForPath` returns a workable "drive-shaped" answer for UNC paths — either the UNC share root (`\\localhost\c$` synthesised as a `DriveInfo`) or a UNC-aware alternative that returns free/available bytes via `GetDiskFreeSpaceEx` (Windows) / `statvfs` (Linux+CIFS). If neither is feasible, callers should treat `null` as "unknown volume, do a best-effort space probe via a `FileInfo`-based fallback and refuse if that also fails" — not silently skip the check.
- Actual: `null` from `FindDriveForPath` = safety pre-check disabled. No warning logged, no fallback probe, no user-facing signal. The plugin's happy-path testing on local-drive libraries never surfaces this — only NAS/UNC users hit it, and then only when their bin volume is nearly full.
- Evidence:
  - `RecycleBin.cs:213-232` returns null for any UNC path (`DriveInfo.GetDrives()` has no UNC entry).
  - PowerShell reproduction 2026-09-13:
    ```
    PATH: \\localhost\c$\dev\mediadash-fixtures\_audit_session7\unc_lib\UNC Movie (2024)\UNC Movie (2024).mkv
      Full: \\localhost\c$\...  (verbatim)
      Matched drive: (null)     <-- FindDriveForPath returns null
    ```
  - Positive control: successful fix of issue 8777 (2026-09-13) proves the write-path works end-to-end for UNC — restore lands back at `\\localhost\c$\dev\mediadash-fixtures\_audit_session7\unc_lib\UNC Broken (2024)\UNC Broken (2024).mkv`. Session 7 had already recycled the same file 3 times (history rows 2852, 2872, 2892), so UNC → local recycle-bin via `File.Move` is reliably supported by the OS. **The bug is not "UNC breaks the plugin" — it's "UNC silently disables the safety checks the plugin has for local drives."**
- File:line:
  - `Jellyfin.Plugin.MediaDash/Fixers/RecycleBin.cs:213-232` (`FindDriveForPath` — root cause)
  - `Jellyfin.Plugin.MediaDash/Fixers/RecycleBin.cs:166-184` (`MoveToBin` cross-volume space pre-check)
  - `Jellyfin.Plugin.MediaDash/Fixers/TranscodeFixer.cs:141-146` (invariant #5 pre-check)
  - `Jellyfin.Plugin.MediaDash/Fixers/TrackFixer.cs:225-230` (invariant #5 pre-check)
  - `Jellyfin.Plugin.MediaDash/Fixers/MediaSorterFixer.cs:110-135` (cross-volume move pre-check)
  - `Jellyfin.Plugin.MediaDash/ScheduledTasks/FixTask.cs:1042-1069` (bin-volume-critically-full safety pause)
  - `Jellyfin.Plugin.MediaDash/Api/MediaDashController.cs:94-105, :280, :981` (dashboard bin-volume drive-info surface)
- Suspected fix: Two options.
  1. **Minimal (recommended):** in `FindDriveForPath`, when the local-drive iteration returns none, extract the UNC share root (`Path.GetPathRoot(fullPath)` returns `\\server\share` for UNC) and construct a synthetic `DriveInfo`-equivalent whose `AvailableFreeSpace` is computed via `GetDiskFreeSpaceEx` (P/Invoke on Windows) against the UNC path. On Linux, `statvfs` on the CIFS mountpoint yields the same info. Return a small `IDriveLike` record so callers keep their existing `if (drive is not null)` shape. Wire the same UNC-aware probe into every caller.
  2. **Cheapest:** change every caller's guard from `if (drive is not null && free < X)` to `if (drive is null || free < X)` so `null` fails-safe (refuse the fix with "could not determine free space on the volume — free some space and try again"). Downside: false positives on legitimate UNC libraries whose target has terabytes free — every UNC-backed fix would refuse. Not acceptable.
  3. **Middle:** log a one-liner warning "Skipping disk-space pre-check for UNC path — free space cannot be probed on this volume" and continue. Documents the behaviour without introducing false-refusals.
- Fix-plan phase: 6 (polish / cross-cutting). The safety-invariant angle argues for Phase 2, but UNC deployments haven't been formally scoped in the plan and no user has reported the disk-full-mid-encode symptom, so it's a slow-burn item. If lifted to Phase 2, group with F-002/F-004 as "cross-cutting safety pre-check hardening" and land under the invariant-additions in Phase 0.
- Notes: The audit's UNC-live-tests brief for session 7-8 assumed UNC would either work cleanly or expose loud breakage. Reality: it works quietly but ships without the friendly-guidance safety net local-drive users get. Downstream consequence for the roadmap's "NAS setup guide" work — the config-page UX for "put the recycle bin next to your media" is invisible to UNC users because the "you're on cross-volume, warning!" branch never fires. Related: `IsBinVolumeCriticallyFull` returning `false` on a UNC bin means the "Paused: bin volume has X GB free" banner never appears for UNC bin configs — so a user who sets a NAS recycle bin never sees the low-space warning even when the NAS is 99% full. Compounds F-007's shape (recycle bin surface has edge-case blind spots on non-C: shapes).

### F-019 — FixTask top-level exception handlers (UnauthorizedAccessException / SharingViolation / DiskFull / generic IOError / generic Exception) never advance the issue's status, so any transient- or permanent-failure exception loops forever every 30 min — a new History row + refreshed Errors diagnostic per attempt
- Severity: high
- Status: open
- Area: cross-cutting FixTask.ExecuteAsync per-issue exception handlers
- Discovered: 2026-09-12 by audit-session-6
- Repro:
  1. Pick any Playability fixture (a real, playable-format file that the scanner will flag as unreadable — a short `.mkv` full of ASCII does this, `ffprobe` returns `Invalid data`). Start a detached process that opens the file with `FileStream(FileShare.None)` and holds it for 10+ minutes.
  2. Trigger a MediaDash scan: `POST /MediaDash/Scan`. Scanner emits a Playability issue with `technical: "Permission denied"` (Windows ffprobe reports the sharing-locked file as unreadable with that string). Issue lands in Detected.
  3. `POST /MediaDash/Issues/{id}/Approve` → issue moves to Queued. `POST /MediaDash/Fix`. FixTask picks it up, PlayabilityFixer tries to `File.Move` the source into the recycle bin, blocks on the sharing violation, retries 3× (RunFixWithSharingRetryAsync's ~7.5s backoff), still blocked → the outer catch at `FixTask.cs:767 catch (System.IO.IOException ex) when (IsSharingViolation(ex))` fires. Writes a History row ("Fix failed — file was locked by another process even after retrying.") + records a `FixTask.FileLocked` Diagnostics row. **Does NOT advance the issue's status.**
  4. `POST /MediaDash/Fix` again 30 seconds later. Issue still Queued, gets picked up, blocks on the sharing violation, writes ANOTHER identical History row, increments the Diagnostics `count` from 1→2, still leaves issue Queued.
  5. Every scheduled fix run (every 30 min inside the fix window) does the same thing forever. History table accumulates identical failure rows indefinitely; Errors tab shows "FixTask.FileLocked | ... count=N" that grows without bound; issue never transitions out of Queued.
- Expected: When an exception is caught at the top-level FixTask handler AND the underlying cause is unlikely to self-resolve within a scan window (permission denied on Windows/Linux — user has to change ACLs; disk full — user has to free space; unexpected exception — probably a code bug that won't fix itself), the row should either (a) transition to Fixed with an actionable message so the retry loop stops, or (b) increment a per-issue `failure_count` and back off (double the retry interval each time, cap at 24h) so the log/History tables don't fill unbounded. Sharing violations legitimately CAN self-resolve (trickplay finishes, ffmpeg releases the file, user closes VLC) so retrying is fair — but not every 30 min without a back-off, and not indefinitely with a fresh row each time.
- Actual: `FixTask.cs:704-869` has six exception handlers wrapping the per-issue fix loop:
  - `catch (OperationCanceledException)` (704): rethrows — cancels the whole run, not per-issue.
  - `catch (UnauthorizedAccessException ex)` (708): writes History + Diagnostics, **no status flip**.
  - `catch (FileNotFoundException ex)` (729): writes History + Diagnostics, `_db.UpdateIssueStatus(issue.Id, IssueStatus.Fixed);` — CORRECT (stale-file exit).
  - `catch (DirectoryNotFoundException ex)` (749): same treatment as FileNotFound — CORRECT.
  - `catch (IOException ex) when (IsSharingViolation(ex))` (767): writes History + Diagnostics, **no status flip**.
  - `catch (IOException ex) when (IsDiskFull(ex))` (787): writes History + Diagnostics, **no status flip**.
  - `catch (IOException ex)` (804): generic IO catch — writes History + Diagnostics, **no status flip**.
  - `catch (Exception ex)` (822): generic catch — writes History + Diagnostics, **no status flip**.
  Contrast with the SUCCESS-PATH handler at line 683-702 that DOES call `IsStaleFailure(result.Message)` on a fixer-returned `FixResult.Fail(...)` message and correctly advances stale rows to Fixed. The exception-caught path doesn't run that check because it never gets a `result.Message` — the fixer threw before returning. Net effect: any exception the fixer raises (as opposed to returning `FixResult.Fail`) traps the issue in Queued for every future run until the underlying condition clears OR the user manually Dismisses.
- Evidence: Live-reproduced 2026-09-12 on `localhost:8099`. Created `_audit_session6\Denied Movie (2020)\Denied Movie (2020).mkv` (11-byte ASCII), spawned `powershell -File hold-lock.ps1` that opens the file with `FileShare.None` for 600s. Scanner emitted issue id 8004 as Playability with `technical: "Permission denied"`. Approved → Fix run #1: 1 History row (`Fix failed — file was locked by another process even after retrying.`), status stayed 1 (Queued), Diagnostics `FixTask.FileLocked | Denied Movie (2020).mkv | count=1`. Fix run #2 (right after): 2 History rows (identical action string), status still 1 (Queued), Diagnostics count=2. Killing the lock-holding process + one more fix run WOULD have succeeded — the retry is designed to eventually succeed. Problem is the unbounded History-row growth + the fact that if the lock is permanent (a stuck jellyfin transcode subprocess, a rescue-mode filesystem, a permissions problem that requires human intervention) the loop never terminates.

  Same shape would reproduce for the UnauthorizedAccessException catch on Linux where the file has `chmod 000` — attempted, but Windows-as-Administrator bypasses regular ACLs via SeBackupPrivilege so this branch couldn't be triggered here; code inspection confirms the same "write history + record diagnostic, no status flip" pattern applies to lines 708-728.
- File:line:
  - `Jellyfin.Plugin.MediaDash/ScheduledTasks/FixTask.cs:708-728` (UnauthorizedAccessException)
  - `Jellyfin.Plugin.MediaDash/ScheduledTasks/FixTask.cs:767-786` (IOException sharing-violation)
  - `Jellyfin.Plugin.MediaDash/ScheduledTasks/FixTask.cs:787-802` (IOException disk-full)
  - `Jellyfin.Plugin.MediaDash/ScheduledTasks/FixTask.cs:804-820` (IOException generic)
  - `Jellyfin.Plugin.MediaDash/ScheduledTasks/FixTask.cs:822-869` (Exception generic)
  - Contrast with `Jellyfin.Plugin.MediaDash/ScheduledTasks/FixTask.cs:693-696` (success-path stale-failure detection — correct pattern)
- Suspected fix: Two-level approach.
  1. **Permission-denied / generic-Exception** (fundamentally not self-healing without human action): advance the issue to a new `IssueStatus.Blocked` state (or reuse `IssueStatus.Fixed` with a "needs user action" tag in the DetailsJson) so the retry loop stops. Fresh scan re-emits if condition persists AND is still relevant.
  2. **Sharing-violation / disk-full** (transient): introduce a per-issue `failure_count` + `next_retry_utc` column on `issues`. On exception, `failure_count++` and set `next_retry_utc = now + backoff(failure_count)` with a cap (e.g., double per failure, cap at 4h). FixTask filters `WHERE status=Queued AND (next_retry_utc IS NULL OR next_retry_utc < now)`. On success, reset failure_count. Same shape covers F-012 (subtitle back-off) — a single generic retry-back-off table subsumes both fixes.

  Minimum viable fix if the above is too heavy: extend `IsStaleFailure` to match "file was locked", "permission denied", "unexpected error" (Fixed-when-fires wording) and use `IsStaleFailure(ex.Message)` inside each exception catch to opt into status-flip. Less precise but a one-liner per catch.
- Fix-plan phase: 6 (polish + back-off). Same phase as F-012 because both want the same per-issue retry-back-off table; landing them together avoids two migrations and two half-solutions. If the user wants immediate mitigation before Phase 6, the "one-line IsStaleFailure extension" is a Phase 1 candidate (defensive, low blast radius).
- Notes: Impact scales with library size and time. A single user-locked file quietly fills the History table at 2 rows/hour (48/day, 17520/year) for a single issue; a permissions-locked directory of 100 files does the same × 100. Silent because the Errors tab dedup keeps the count under one banner and the user sees "count: 17520" without realising each retry ATTEMPTED to recycle their file. Also compounds with F-011: user's only escape from the loop is to manually Dismiss the issue via `POST /Issues/{id}/Dismiss` — which per F-011 succeeds unconditionally. No self-service UI for "retire this issue permanently" today. Related: session 5's `IsStaleFailure` fixer-message check IS matched for the disappearing-file case ("no longer exists") because those come from `FixResult.Fail("... no longer exists ...")`, not from raised exceptions. Missing-file is the ONLY exception-caught path that also flips status (FileNotFoundException + DirectoryNotFoundException) — so the pattern IS understood by the code, just not applied consistently.

### F-018 — POST /Scan/Suspicious runs concurrent with a scheduled/manual scan, racing the MalwareRisk ReplaceDetectedIssues transaction — last-writer-wins truncates the other's results
- Severity: medium
- Status: open
- Area: api MediaDashController.ScanSuspicious + scanner SuspiciousFileScanner + db MediaDashDb.ReplaceDetectedIssues
- Discovered: 2026-09-12 by audit-session-5
- Repro:
  1. Trigger a full scan: `POST /MediaDash/Scan`. Confirm `IsScanning=true` via `/Status`.
  2. Immediately (while `IsScanning=true`): `POST /MediaDash/Scan/Suspicious`. HTTP 200 with an `{Detected, ElapsedMs}` payload — no busy-signal or 409 refusal.
  3. Both writers race on `_db.ReplaceDetectedIssues(IssueType.MalwareRisk, ...)`. Each writer opens its own transaction and issues a `DELETE FROM issues WHERE type = <MalwareRisk> AND status = Detected` followed by an INSERT of its scan's results. Whoever commits last wins; the other's Detected rows have been deleted by the loser's DELETE and its INSERTs were made against a soon-to-be-truncated set.
- Expected: One of two: (a) `/Scan/Suspicious` refuses when a MalwareRisk scan is already in progress (return 409 with "A scan is already running — try again in a moment."); or (b) both writers serialize on a per-type lock so the "at least one full result set is coherent" invariant holds. Today, the transactions are atomic individually but the two writers can produce a set-union that never actually existed in either scan.
- Actual: `MediaDashController.StartScan` guards on `scanTask.State == TaskState.Idle` (line 341) — so /Scan-during-/Scan is silently a no-op (see F-017). But `/Scan/Suspicious` (line 358-377) runs inline on the request thread with no such guard, so it happily fires alongside a scheduled or manual full scan. Both call `SuspiciousFileScanner.ScanAsync/RunScanAsync` (same underlying walker) and both terminate with `_db.ReplaceDetectedIssues(scanner.Type, issues, null)` at line 371 / the FixTask/ScanTask equivalent. `ReplaceDetectedIssues` at `MediaDashDb.cs:456-523` opens a fresh SqliteConnection + transaction per call — two overlapping transactions produce a race where one DELETE strips the other's INSERTs.
- Evidence: Static reasoning + live confirmation that /Scan/Suspicious returns 200 with `{"Detected":0,"ElapsedMs":2}` while /Status still reports `IsScanning=true`. The 2ms elapsed on the second call shows it didn't actually walk anything (no files matched in the test scratch) but the DELETE still happened; a real user with ANY MalwareRisk detections in an active scan would see them silently drop when the inline /Scan/Suspicious's transaction commits.
- File:line:
  - `Jellyfin.Plugin.MediaDash/Api/MediaDashController.cs:358-377` (`ScanSuspicious` — no in-flight check)
  - `Jellyfin.Plugin.MediaDash/Data/MediaDashDb.cs:456-523` (`ReplaceDetectedIssues` — per-call transaction, no cross-type lock)
- Suspected fix: Cheapest: refuse when a MalwareRisk scan is already in flight — mirror the `StartScan` guard. `if (scanTask.State != TaskState.Idle) return Conflict("A library scan is running; try /Scan/Suspicious again after it finishes.");`. Downside: forces the "quick virus check" button to wait for the whole scheduled scan. Better shape: `SuspiciousFileScanner` guards its own execution with a `SemaphoreSlim(1,1)` — the scheduled scan takes the semaphore around its Suspicious pass, the API endpoint respects the same semaphore. Then the two writers can never overlap. Same shape needed for /Scan/Cancel semantics — a cancellation of the full scan doesn't cancel the inline /Scan/Suspicious call.
- Notes: The user-visible symptom depends on which scan writes first. If the manual /Scan/Suspicious finishes AFTER the scheduled scan's SuspiciousFileScanner pass, the manual scan's INSERTs sit on top of the scheduled scan's INSERTs — but its DELETE first stripped everything the scheduled scan committed, so the "combined" state = just the manual scan's results. If the manual /Scan/Suspicious commits FIRST, then the scheduled scan's SuspiciousFileScanner runs later and its DELETE + INSERT owns the final state. Either way, one scan's results are silently thrown away. Very low probability in normal use (Suspicious scan is fast — seconds — and users rarely hit both simultaneously) but a script that polls /Scan/Suspicious plus a scheduled scan is a real hit. Also worth cross-checking: does the same race exist for other single-scanner endpoints? Grep for `_scanners.OfType<`. Today `/Scan/Suspicious` looks like the only one but future single-scanner endpoints will inherit the same shape without a shared lock.

### F-017 — /MediaDash/Scan returns 204 (success) when called while a scan is already running, giving the UI no way to signal "your click was ignored"
- Severity: low
- Status: open
- Area: api MediaDashController.StartScan (and StartFix has the same shape)
- Discovered: 2026-09-12 by audit-session-5
- Repro:
  1. `POST /MediaDash/Scan` → 204 (scan starts).
  2. Before the scan finishes: `POST /MediaDash/Scan` → 204 (silently no-op — `scanTask.State != TaskState.Idle` skips the second Execute call).
  3. UI-side: the dashboard's "Scan now" button click looks successful (no error toast, no busy indicator on the button itself). If the user isn't watching the progress bar carefully, they'll assume their click landed and a fresh scan is starting. It didn't.
- Expected: When a scan is already in flight, the endpoint should return 202 Accepted with a body like `{ "Status": "already-running", "ProgressPct": <n> }`, or 409 Conflict with a clear message. The UI can then render a "Already scanning — X% complete" toast or fold the button into the same "Stop scan" affordance the running scan already has.
- Actual: `MediaDashController.StartScan` at `MediaDashController.cs:336-348`:
  ```csharp
  if (scanTask is not null && scanTask.State == TaskState.Idle)
  {
      ScanTask.BypassIdleCheckOnce = true;
      _taskManager.Execute(scanTask, new TaskOptions());
  }

  return NoContent();
  ```
  When scan is not idle, the block is skipped and `NoContent()` (204) is returned unconditionally. No signal to the caller that the request was a no-op. Same shape at `StartFix` (line 436-448) and probably `StartCancel` variants (grep confirms).
- Evidence: Live-reproduced 2026-09-12. Two consecutive `POST /MediaDash/Scan` calls returned 204 each — first started the scan, second was silently no-op. `Status.IsScanning` remained true across both. The Jellyfin server did NOT log a warning about the ignored second call.
- File:line: `Jellyfin.Plugin.MediaDash/Api/MediaDashController.cs:336-348` (`StartScan`), also `:436-448` (`StartFix` — same pattern), plus every `if (task.State == TaskState.Idle)` gate in the file.
- Suspected fix: Return 409 Conflict (or 202 with in-progress metadata) when the guard fails:
  ```csharp
  if (scanTask.State != TaskState.Idle)
  {
      return Conflict(new { Status = "already-running", Message = "A scan is in progress. Wait for it to finish, then click Scan again." });
  }
  ```
  UI side: interpret 409 as "user tried to double-click during in-flight" and either flash the "Already scanning" hint or promote the button to the "Cancel scan" affordance. Same pattern for StartFix. Regression test: two rapid POST /Scan → first 204, second 409.
- Notes: Low severity — no data loss, no wrong action taken. But it's a UX class the codebase already treats as important (see the FixTask review-grace machinery, the "Ignore activity for this run" flag, the manual-scan-completed timestamp — a lot of code exists to make sure user-facing scan/fix events feel responsive and honest). This is the one gap where the endpoint lies to the client about what it did.

### F-016 — Issue.HasBlockingWarnings throws InvalidOperationException on non-object DetailsJson root, aborting the whole RollbackAutoQueuedBlockingWarnings pass for that type
- Severity: high
- Status: open
- Area: db Issue.HasBlockingWarnings (called by FixTask auto-queue consent-gate + tests)
- Discovered: 2026-09-12 by audit-session-5
- Repro:
  1. Inject any issue whose `details` column is a JSON literal that is NOT an object — e.g. `null`, `[1,2]`, `42`, `"just a string"`, or the trivially malformed `""` (empty string only avoided by the leading `IsNullOrWhiteSpace` guard):
     ```
     sqlite3 mediadash.db "INSERT INTO issues (type,item_id,path,details,suggested_fix,size_savings,status,detected_at_utc) VALUES (4,'<validGUID>','C:\path\file.mkv','null','',0,0,17580000000000000);"
     ```
  2. Set the type's FixMode to Automatic so auto-queue moves the row to Queued (or the DB shim `UPDATE issues SET status=1 WHERE ...` for the injected row). Trigger a scheduled fix run (`POST /MediaDash/Fix`) — the FixTask calls `_db.RollbackAutoQueuedBlockingWarnings(type)` for every FixableType in Automatic mode. That method calls `GetIssues(type, Queued).Where(i => i.HasBlockingWarnings)`. On the malformed row, the `Where` predicate throws.
  3. Uncaught exception bubbles out of the whole `foreach (var type in FixableTypes)` loop — the auto-queue pass aborts partway through. Types processed BEFORE the malformed row's type auto-queue normally; types AFTER never get their auto-queue call or their `RollbackAutoQueuedBlockingWarnings` call this run.
- Expected: `HasBlockingWarnings` returns `false` for any DetailsJson that isn't a well-formed object with a `warnings` array — matches the docstring "malformed / missing JSON returns false, preserving the historical auto-queue behaviour for pre-migration rows that never got a warnings array."
- Actual: `TryReadBlockingWarnings` at `Data/Issue.cs:82-102` only wraps a `try/catch(JsonException)`. `JsonDocument.Parse("null")` / `"[1,2]"` / `"42"` are all VALID JSON (JsonException never fires), and the subsequent `doc.RootElement.TryGetProperty("warnings", out var warnings)` throws `System.InvalidOperationException: The requested operation requires an element of type 'Object', but the target element has type 'Null'/Array/Number` — which the `catch (JsonException)` does NOT catch. The exception propagates all the way out.
- Evidence: Live-reproduced 2026-09-12 by injecting three rows with `details = null`, `[1,2]`, `42`. GET `/MediaDash/Status` continued working (it doesn't call `HasBlockingWarnings`), but the Errors tab afterwards showed exactly the crash message:
  ```
  FixTask: C:/.../Clean Movie.mkv: The requested operation requires an element of type 'Object', but the target element has type 'Null'.
  FixTask: C:/.../Clean Movie.mkv: The requested operation requires an element of type 'String', but the target element has type 'Array'.
  ```
  (First message is from HasBlockingWarnings' `TryGetProperty("warnings", ...)` on root=null; second is from PlayabilityFixer's `TryGetReason` — F-015. Same root cause: `catch(JsonException)` doesn't catch `InvalidOperationException`.)
- File:line:
  - `Jellyfin.Plugin.MediaDash/Data/Issue.cs:82-102` (`TryReadBlockingWarnings`)
  - `Jellyfin.Plugin.MediaDash/Data/MediaDashDb.cs:985-988` (call site — LINQ `Where` swallows nothing; enumerator surfaces the throw to the FixTask top loop)
  - Downstream: `Jellyfin.Plugin.MediaDash/ScheduledTasks/FixTask.cs:245-298` (auto-queue foreach that runs `RollbackAutoQueuedBlockingWarnings` per type)
- Suspected fix: Widen the catch OR gate on RootElement ValueKind before TryGetProperty. Preferred (matches the docstring intent): early-return `false` unless root is Object. Line 84 already parses; add one line:
  ```csharp
  using var doc = JsonDocument.Parse(DetailsJson);
  if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
  if (!doc.RootElement.TryGetProperty("warnings", out var warnings) || warnings.ValueKind != JsonValueKind.Array) return false;
  ```
  Same shape fix applies to every JsonDocument-parsing helper that assumes an object root without checking. Fastest audit target: grep `.RootElement.TryGetProperty` and add a ValueKind guard to every hit whose surrounding try only catches JsonException.
- Notes: Fresh installs never write these malformed rows (the scanners write object-only). But three real ways users hit this in the wild: (1) migration-shape drift — a future schema change writes an array-shaped details somewhere and the loader doesn't back-fill legacy rows; (2) user or third-party tool experimenting with the DB (auditing, debugging); (3) a partial write / corruption during a hard-kill (SIGKILL mid-INSERT could truncate to `null` if the value column happens to serialise as such). Category (1) is the real risk — MediaDash's own scanner code base grew from primitive strings to nested objects between 1.0.6 and 1.0.7 without a schema migration to canonicalise older rows. Any type that stored a different shape historically is a landmine. F-015 is the sibling bug in PlayabilityFixer with the same root cause; both should be fixed together.

### F-015 — PlayabilityFixer.TryGetReason crashes fix run on non-object DetailsJson root or non-string "reason" value; queued row loops forever + spams Errors tab
- Severity: high
- Status: open
- Area: fixer PlayabilityFixer
- Discovered: 2026-09-12 by audit-session-5
- Repro:
  1. Inject a Playability issue (type=1) with a real, playable video path but a malformed DetailsJson that either (a) has a non-object root or (b) has `"reason"` set to a non-string JSON value:
     ```
     sqlite3 mediadash.db "INSERT INTO issues (type,item_id,path,details,suggested_fix,size_savings,status,detected_at_utc) VALUES (1,'<validGUID>','C:\path\playable.mkv','null','',0,1,17580000000000000);"
     sqlite3 mediadash.db "INSERT INTO issues (type,item_id,path,details,suggested_fix,size_savings,status,detected_at_utc) VALUES (1,'<validGUID>','C:\path\playable.mkv','{\"reason\":[\"not\",\"string\"]}','',0,1,17580000000000000);"
     ```
  2. `POST /MediaDash/Fix` — FixTask picks up the Queued row and hands it to PlayabilityFixer. `IsStillBrokenAsync` calls `TryGetReason(issue.DetailsJson)`. The method parses the JSON (both `null` and `{"reason":[...]}` are valid JSON), then hits either the `TryGetProperty` (root=null) or the `GetString()` (reason=Array) inside a `try { } catch(JsonException)` block. Neither of those throws `JsonException`; they throw `InvalidOperationException`, which is NOT caught.
  3. The exception propagates out to FixTask's per-issue try/catch, which catches it as "unexpected error", writes a "Fix failed due to an unexpected error." history row with success=0, and — critically — does NOT flip the issue to Fixed (message isn't matched by `IsStaleFailure`). The Errors tab gains a `FixTask` diagnostic quoting the raw exception message. Row stays Queued.
  4. 30 minutes later the scheduled fix run picks the same row up, crashes identically, adds another row to History and increments the Errors dedup count.
- Expected: `TryGetReason` returns null for any DetailsJson that isn't a well-formed object with a string `reason` — matching the sibling `TryGetString` helper five lines below which correctly checks `v.ValueKind != JsonValueKind.String` before calling `GetString()`. PlayabilityFixer's re-verify then falls into the `default` case, judges the file playable (real fixture is a Clean movie), and returns "plays fine now — Re-scan" which IS matched by `IsStaleFailure` and terminates the loop.
- Actual: `TryGetReason` at `PlayabilityFixer.cs:636-647` uses `try { ... } catch (JsonException)` around `TryGetProperty` + `GetString()`. Neither call throws `JsonException`; both throw `InvalidOperationException` on shape mismatch. Live evidence:
  ```
  == history rows after fix ==
  issue=7633 details=null                        action="Fix failed due to an unexpected error." success=0
  issue=7634 details={"reason":["not","string"]} action="Fix failed due to an unexpected error." success=0
  == row statuses after fix ==
  7633|1  (still Queued)
  7634|1  (still Queued)
  == Errors tab ==
  FixTask | C:/.../Clean Movie.mkv: The requested operation requires an element of type 'Object', but the target element has type 'Null'.
  FixTask | C:/.../Clean Movie.mkv: The requested operation requires an element of type 'String', but the target element has type 'Array'.
  ```
  Contrast the sibling helper `TryGetString` at `PlayabilityFixer.cs:666-688`: it explicitly checks `v.ValueKind != JsonValueKind.String` before calling `GetString()`. So `TryGetDetail` / `TryGetTechnical` don't crash on the same input — only `TryGetReason` does.
- File:line: `Jellyfin.Plugin.MediaDash/Fixers/PlayabilityFixer.cs:636-647` (`TryGetReason`)
- Suspected fix: Copy the guard pattern from `TryGetString`:
  ```csharp
  private static string? TryGetReason(string detailsJson)
  {
      try
      {
          using var details = JsonDocument.Parse(detailsJson);
          if (details.RootElement.ValueKind != JsonValueKind.Object) return null;
          if (!details.RootElement.TryGetProperty("reason", out var r) || r.ValueKind != JsonValueKind.String) return null;
          return r.GetString();
      }
      catch (JsonException)
      {
          return null;
      }
  }
  ```
  Fix restores the intended behaviour: unrecognised or missing reason → default probe (which correctly judges the Clean fixture playable → "plays fine now" → IsStaleFailure → status Fixed → loop terminates).
- Notes: Same shape as F-016 (Issue.HasBlockingWarnings). Both catch only `JsonException` while calling APIs that throw `InvalidOperationException` on shape mismatch. Every `try { JsonDocument.Parse(...); ... } catch(JsonException)` site in the codebase deserves audit. Real-world impact: a single malformed row (from a botched migration, a user editing the DB, or a partial write during hard-kill) hangs the whole fix loop for that issue's row forever, spams the Errors tab (which now shows a scary "unexpected error" panel to the user), and blocks the user's clear-fix-and-forget workflow until they either delete the row from the DB manually or use the /Issues/{id}/Dismiss endpoint (which per F-011 will happily transition ANY status to Dismissed).

### F-014 — Any single malformed item_id in the issues table (non-GUID hex-N format) crashes GET /MediaDash/Status with 500, breaking the whole plugin dashboard
- Severity: high
- Status: open
- Area: db MediaDashDb.GetIssues + api MediaDashController.GetStatus
- Discovered: 2026-09-12 by audit-session-5
- Repro:
  1. Insert a single row whose `item_id` column is anything OTHER than 32 lowercase-hex chars (Guid "N" format):
     ```
     sqlite3 mediadash.db "INSERT INTO issues (type,item_id,path,details,suggested_fix,size_savings,status,detected_at_utc) VALUES (1,'anything-not-32-hex','C:\x.mkv','{}','',0,0,17580000000000000);"
     ```
     (Realistic path: a test script that seeds fixture rows without generating a real Guid; a user restoring an older MediaDash DB where item_id was stored as some other format; a manual DB fix-up gone wrong.)
  2. `curl http://localhost:8099/MediaDash/Status -H "X-Emby-Token: <token>"` → **HTTP 500 "Error processing request."**. The dashboard tab that polls `/Status` every 3 seconds now shows a permanent loading spinner / error state. Every other endpoint that calls `GetIssues` (`/Issues`, several internal callers) also 500s. Only fix: delete the offending row from SQLite by hand.
- Expected: `GetIssues` skips (with a diagnostic) any row whose `item_id` can't be parsed as a Guid, or defers the parse to caller code that can log and continue. Broken data in one row should never brick the whole plugin's status endpoint — the Status endpoint is the health probe for the dashboard's own error surface, so when Status is down there's no in-plugin way to see WHY it's down.
- Actual: `MediaDashDb.GetIssues` (line 548) hard-fails on the first bad row with `System.FormatException: Guid should contain 32 digits with 4 dashes (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx).` from `Guid.ParseExact(reader.GetString(2), "N")`. The exception bubbles out of the enumerator, aborts the whole result build, and unwinds through `GetStatus` → controller → `ExceptionMiddleware`, which returns 500 with the generic "Error processing request." body. Same crash surface exists at `MediaDashDb.cs:1030` in the single-row `GetIssue` helper.
- Evidence: Live reproduced 2026-09-12. Full stack from the Jellyfin log:
  ```
  System.FormatException: Guid should contain 32 digits with 4 dashes (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx).
     at System.Guid.ParseExact(String input, String format)
     at Jellyfin.Plugin.MediaDash.Data.MediaDashDb.GetIssues(Nullable`1 type, Nullable`1 status)
     at Jellyfin.Plugin.MediaDash.Api.MediaDashController.GetStatus()
  ```
  Reproduced with `item_id = 'audit5f0malformedempty_obj'` (26 chars, not hex). Fixed by `DELETE FROM issues WHERE item_id LIKE 'audit5f0malformed%';` — Status endpoint recovered within a poll cycle.
- File:line:
  - `Jellyfin.Plugin.MediaDash/Data/MediaDashDb.cs:548` (`GetIssues` — `Guid.ParseExact(reader.GetString(2), "N")`)
  - `Jellyfin.Plugin.MediaDash/Data/MediaDashDb.cs:1030` (`GetIssue` sibling — same line, same crash)
  - Every caller of GetIssues / GetIssue in the plugin is downstream-vulnerable — a partial list from a quick grep: `MediaDashController.GetStatus`, `GetIssues`, `ApproveIssue` (via `GetIssue`), `DismissIssue`, `RevertIssue`, `StartFix`, `Errors`, plus every scanner's `ReplaceDetectedIssues` bulk delete transaction which doesn't Guid-parse but does read the same rows.
- Suspected fix: Use `Guid.TryParseExact` and log-skip bad rows; do not abort the whole read. Cheapest patch:
  ```csharp
  var itemIdStr = reader.GetString(2);
  if (!Guid.TryParseExact(itemIdStr, "N", out var itemId))
  {
      _logger?.LogWarning("Skipping issue row id={Id} with malformed item_id '{ItemId}'", reader.GetInt64(0), itemIdStr);
      continue;
  }
  result.Add(new Issue { ..., ItemId = itemId, ... });
  ```
  Repeat at `GetIssue` (return null instead of throwing on malformed). Belt-and-braces: add a startup migration that identifies and quarantines (or deletes) rows whose item_id isn't valid Guid-N so the risk surface stays small over time. Sanity-check the same shape everywhere Guid.ParseExact is called with a DB-string argument (grep the codebase — several places call it in the DTO builders too).
- Notes: Combined with F-011 (which lets you Dismiss any status) and F-016 (which crashes auto-queue on shape drift), MediaDash has a small family of "one bad row bricks a big surface" bugs whose common cause is *"trust the DB's shape, throw when it doesn't hold"*. All three should be triaged as one theme: **stop treating DB reads as invariants**. The current safety-invariant list in CLAUDE.md talks about file-system safety; adding "the plugin never throws on unexpected DB row shape — malformed rows are logged and skipped" would formalise the same discipline for the database side.

### F-013 — Duplicate scanner + fixer treat hardlinked twins as normal duplicates: no bytes reclaimed but reported as saved
- Severity: medium
- Status: open
- Area: scanner DuplicateScanner + fixer DuplicateFixer
- Discovered: 2026-09-11 by audit-session-3
- Repro:
  1. In a monitored library folder, create a file and a hardlink to it:
     ```
     ffmpeg -f lavfi -i "sine=frequency=440:duration=2" -c:a libmp3lame -y a.mp3
     mv a.mp3 /library/movies/Movie/A.mkv       # hypothetical
     cmd /c "mklink /H B.mkv A.mkv"             # NTFS hardlink; Linux uses `ln`
     ```
  2. Trigger a scan → DuplicateScanner emits a duplicate group `{A.mkv, B.mkv}` at Tier 0 confidence (byte-identical SHA-256, identical size).
  3. Approve; DuplicateFixer runs; picks one as loser and moves it to the recycle bin.
  4. Check the surviving hardlink: it still points to the original inode. Check physical disk usage: unchanged.
  5. Overview / history: "Reclaimed X GB" — but zero bytes were physically freed.
- Expected: Either (a) the scanner rejects hardlinked candidates from a duplicate group with the same "IsSymlink" reasoning already applied to reparse points at `DuplicateScanner.cs:604,624` (0 physical bytes are ever recoverable by removing a hardlink; the reclaimable-savings claim is a lie); or (b) the fixer detects same-inode candidates and refuses with "these paths share the same physical file — deleting one won't free any disk space; use a real duplicate if you want to reclaim bytes."
- Actual: `Candidate.IsSymlink = fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)` (`DuplicateScanner.cs:390,424`) catches symbolic links and junctions but NOT hardlinks — hardlinks have no reparse-point flag; they're just additional directory entries pointing to the same inode (verified: `python -c "import os,ctypes;print(os.stat('h1.mkv').st_ino, os.stat('h2.mkv').st_ino, ctypes.windll.kernel32.GetFileAttributesW('h1.mkv'))" → same inode, attrs=0x20 (Archive only)`). Fixer's `BytesFreed = size` (`DuplicateFixer.cs:152`) reports the file's logical size, not the physical delta on disk (which is 0 for hardlink dedup). The user's Overview "Reclaimed since install" grows by 20 GB per hardlinked pair recycled, without a single byte actually freeing up.
- Evidence: Live check on Windows 11 NTFS:
  ```
  $ echo 'x' > h1.mkv && cmd //c "mklink /H h2.mkv h1.mkv"
  $ python -c "import os;print([os.stat(f).st_ino for f in ('h1.mkv','h2.mkv')])"
  [16325548649636362, 16325548649636362]     # same inode
  $ python -c "import ctypes;[print(hex(ctypes.windll.kernel32.GetFileAttributesW(f)&0x400)) for f in ('h1.mkv','h2.mkv')]"
  0x0
  0x0                                         # neither has FileAttributes.ReparsePoint
  ```
  Sonarr/Radarr users with `Use Hardlinks` enabled in the *arr suite regularly end up with hardlinked twins (torrent client seed path + media library path both hardlink to one inode). This is exactly the topology the scanner should not flag as reclaimable.
- File:line:
  - `Jellyfin.Plugin.MediaDash/Scanners/DuplicateScanner.cs:390,424` (`IsSymlink = fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)` — misses hardlinks)
  - `Jellyfin.Plugin.MediaDash/Fixers/DuplicateFixer.cs:152` (`BytesFreed = size` — no physical-vs-logical distinction)
- Suspected fix: Track `(size, mtime, first-1MB-hash)` or a Windows-side `GetFileInformationByHandle` result to detect same-inode candidates. Simplest: after hashing, if two candidates have identical SHA-256 AND identical size, do a follow-up inode check:
  ```csharp
  // On Windows: BY_HANDLE_FILE_INFORMATION.FileIndexHigh/Low uniquely identifies the file on the volume.
  // On Linux/macOS: (st_dev, st_ino) — st.st_ino is already surfaced by Mono, works cross-platform.
  ```
  If same-volume + same-inode: mark all such candidates as `IsHardlink = true` and route through the same "not eligible as keeper OR loser" branch as `IsSymlink`. Fixer stays untouched. Regression test: create two hardlinked fixtures + one real duplicate; only the real duplicate gets a loser assignment.
- Notes: Not data loss (the deletion of a hardlink is safe — the inode survives as long as any hardlink references it, so the "kept" copy stays intact). But every hardlinked pair recycled adds noise to the Overview + Recycle Bin without any real space benefit, and users on the *arr stack that specifically avoids duplicate physical storage via hardlinks will see their Overview brag about 100 GB reclaimed while `df -h` shows no change. Compounds if the "keeper" happens to be recycled and then permanently purged from the bin (`RecycleBinRetentionDays`) — at that point the hardlink to the recycled inode is gone, and the OTHER hardlink (the "loser" that stayed in the library) becomes the sole reference to the file. Still no data loss, but the semantics are inverted from what the user thinks happened.

### F-012 — MissingSubtitleFixer has no provider-failure back-off; a file that no provider carries becomes forever-Queued and burns provider quota every fix cycle
- Severity: medium
- Status: open
- Area: fixer MissingSubtitleFixer + ScheduledTasks FixTask
- Discovered: 2026-09-11 by audit-session-3
- Repro:
  1. Have an English-allow-list library. Add any video whose exact filename hash is niche enough that OpenSubtitles / Addic7ed / etc. return zero hits (test fixture: any zero-byte or unusual file — providers can't match). Real-world hit: 4K/8K niche films, foreign-language films whose OpenSubtitles user hasn't uploaded subs for, exotic anime series numbering.
  2. Trigger a scan → MissingSubtitle issue emitted, auto-queued.
  3. Wait 30 min (or `POST /MediaDash/Fix`) → fixer contacts providers, all return zero hits, fix returns `FixResult.Fail("eng: no matches from any provider")`.
  4. Repeat every 30 min for as long as the file is on disk.
- Expected: After N provider failures, the fixer either (a) marks the issue Fixed (accepting "no subs available" as the terminal state, matches what the user experiences), or (b) writes a `MissingSubtitle.NoProviderMatch` back-off row keyed on `(path, langs, providerSet)` that suppresses re-attempts for a reasonable window (24h? 7 days? user-configurable). Something breaks the churn loop.
- Actual: `FixTask.IsStaleFailure` (`FixTask.cs:1018-1042`) does not match "no matches from any provider", so the issue stays Queued forever. `MissingSubtitleFixer.FixAsync` has no back-off table. Every 30-min fix cycle contacts every configured provider for every language, burns free-tier quota (OpenSubtitles daily limit is 5 downloads for free accounts, 20 searches), and writes a new "eng: no matches from any provider" History row. Live evidence from the current install: `C:\dev\mediadash-fixtures\_test-scratch\movies\Emoji 🎬 Test (2023)\Emoji Test (2023).mkv` has **75 history rows** across 6 days, 54 of them "eng: no matches from any provider", none of them succeeding.
- Evidence:
  ```
  sqlite3 mediadash.db "SELECT substr(action,1,40), COUNT(*) FROM history
    WHERE path LIKE '%Emoji Test (2023).mkv' GROUP BY substr(action,1,40);"
    eng: no matches from any provider | 54
    Preview only — no files were changed | 20
    The library item is no longer available | 1
  ```
  Time range: 2026-09-04 05:23 → 2026-09-10 13:47 (six days of churn on one file).
- File:line:
  - `Jellyfin.Plugin.MediaDash/Fixers/MissingSubtitleFixer.cs:116-120` (empty-hits branch — writes `attempts.Add("...no matches...")` and continues, no persistence)
  - `Jellyfin.Plugin.MediaDash/Fixers/MissingSubtitleFixer.cs:150-153` (`return FixResult.Fail(reason)` — but the reason string isn't matched by IsStaleFailure so status stays Queued)
  - `Jellyfin.Plugin.MediaDash/ScheduledTasks/FixTask.cs:1018-1042` (`IsStaleFailure`) — "no matches from any provider" isn't listed
  - `Jellyfin.Plugin.MediaDash/Data/MediaDashDb.cs:494` (scanner suppresses re-detection only for Queued/Dismissed rows, so this is the FIXER side of the loop, not the scanner)
- Suspected fix: Two options, lazy first:
  1. **Cheap:** add `"no matches from any provider"` to `IsStaleFailure`'s substring list. After one failed cycle the issue becomes Fixed → scanner still won't re-emit (because Fixed rows DO get re-emitted per `MediaDashDb.cs:489` comment "Fixed rows must NOT suppress"). Hmm — that alone isn't enough; a Fixed row lets the next scan emit a fresh Detected row that re-Queues.
  2. **Right way:** add a `subtitle_lookup_backoff` table keyed on `(path, langs_canonical, provider_set_hash)` with `attempted_at_utc + failure_count + next_retry_utc`. Fixer consults it before searching; scanner consults it before emitting a MissingSubtitle issue. Retry cadence: 1 day → 3 days → 7 days → 30 days. Purge rows when the file is deleted (weekly self-heal). Two DB reads per file per scan is cheap.
  3. **Simplest that works:** treat "no matches" as a partial-success — the fixer's contract accepts "we did what we could; the state on disk is now as good as it can be without user action". Return `FixResult { Success = true, Message = "no providers carry <langs> for this file — try uploading the sub manually if you have it" }`. That transitions the issue to Fixed, and the next scan won't re-flag it (because `HasEmbedded... == false` is still true and the scanner runs before the "Fixed row shouldn't suppress" logic kicks in — WAIT: it WILL re-flag). So even option 3 needs the back-off table if the design goal is "the user only sees this once until they actually add a sub".
- Notes: Bites hardest on multi-language allow-lists (e.g. `eng,swe,fin`) with sparse coverage of niche titles. Each fix cycle hits every provider for every language for every path where at least one lang is unavailable. Rate-limit spillover is real: OpenSubtitles will start returning 429 after ~10 searches/min for free accounts, cascading into F-008-shaped mis-attribution ("provider unreachable"). Also worth noting: the recent `SubtitleIgnoreRateLimit` config toggle at `MissingSubtitleFixer.cs:95` uses `isAutomated: !config.SubtitleIgnoreRateLimit` — leaving that OFF costs the user free-tier quota faster since Jellyfin's non-automated path returns wider result sets. Provider-side rate limiting is already causing pain; the fix loop's lack of a "we tried, it didn't work, wait a while" gate compounds it.

### F-011 — POST /Issues/{id}/Approve and /Dismiss unconditionally overwrite ANY status, including Fixed
- Severity: high
- Status: open
- Area: api MediaDashController.ApproveIssue + DismissIssue (and db MediaDashDb.UpdateIssueStatus)
- Discovered: 2026-09-11 by audit-session-3
- Repro:
  1. Pick any issue currently in `status = Fixed`: `sqlite3 mediadash.db "SELECT id FROM issues WHERE status=2 LIMIT 1;"` (or approve+run a fix once to create one).
  2. `curl -X POST "http://localhost:8099/MediaDash/Issues/<id>/Approve" -H "X-Emby-Token: <token>"` → HTTP 204.
  3. Query row: `status` transitioned from 2 (Fixed) to 1 (Queued). Fixed row was un-fixed via the API.
  4. `curl -X POST "http://localhost:8099/MediaDash/Issues/<id>/Dismiss"` → HTTP 204; row transitions 1 → 3 (Dismissed).
- Expected: Approve, Dismiss, and Revert all share the same "which transitions are legal" state machine. Revert already enforces it (`current != Queued && current != Dismissed` returns 409 with "Can only revert issues in Queued or Dismissed state ... If the fix has already run, use the History tab's Restore instead."). Approve should refuse when current status is Fixed with a similar 409, since re-queueing a Fixed row means running a fixer against a file that's already been recycled / re-encoded / relocated — the user should go through the History-tab Restore flow instead. Dismiss should refuse when Fixed for the same reason.
- Actual: `MediaDashController.ApproveIssue` and `DismissIssue` (`MediaDashController.cs:387-390` and `:400-403`) both call `_db.UpdateIssueStatus(id, ...)` unconditionally. `UpdateIssueStatus` (`MediaDashDb.cs:771-779`) has no status filter — it's `UPDATE issues SET status = @status WHERE id = @id`. The `BulkUpdateOpenIssueStatus` sibling (`MediaDashDb.cs:806`) enforces the guard ("Only transitions rows currently in Detected or Queued to the target status") but the single-item path predates it.
- Evidence: Live-reproduced 2026-09-11 against Playability issue id 6828 (`Big Buck Test (2020) - 2160p.mkv`, status=Fixed):
  ```
  before: id=6828, type=1, status=2 (Fixed)
  POST /Issues/6828/Approve -> 204
  after:  id=6828, type=1, status=1 (Queued)   <-- fixed row re-queued
  POST /Issues/6828/Dismiss -> 204
  after:  id=6828, type=1, status=3 (Dismissed)
  ```
  Test cleaned up: row reset to status=2. Contrast Revert on the same id (untested here since it's already documented at the source): status=Fixed → 409.
- File:line:
  - `Jellyfin.Plugin.MediaDash/Api/MediaDashController.cs:387-390` (`ApproveIssue`)
  - `Jellyfin.Plugin.MediaDash/Api/MediaDashController.cs:400-403` (`DismissIssue`)
  - `Jellyfin.Plugin.MediaDash/Data/MediaDashDb.cs:771-779` (`UpdateIssueStatus` — no status filter)
  - Compare guard at `MediaDashController.cs:416-427` (`RevertIssue` — has the state-machine check).
- Suspected fix: Add a `AND status IN (<Detected>, <Queued>, <Dismissed>)` guard to `UpdateIssueStatus` (or expose a new `TryTransitionOpenIssue(id, target)` helper that ApproveIssue / DismissIssue use). ApproveIssue's valid preconditions = Detected. DismissIssue's valid preconditions = Detected or Queued. Both return 409 on Fixed. Downstream side-effect: FixTask's re-check at `FixTask.cs:517-521` already catches the case where a re-queued Fixed row makes it to the fix loop (status != Queued at that moment would skip), but the current bug allows it to actually queue, so nothing catches "Fix row A → un-Approve → re-Queue → Fix picks it up (status IS Queued now)".
- Notes: Downstream user-visible pain depends on the IssueType of the re-queued Fixed row:
  - **Playability / Duplicate**: file was already recycled. Fixer's `File.Exists` guard catches it and writes a "Fix failed — file no longer exists" History row. Errors tab gains a noise row. No data loss.
  - **TrackFixer / TranscodeFixer**: file still exists but is already cleaned. Re-fix may attempt another remux/re-encode against a stale probe cache — best case is a no-op with a confusing error, worst case is another swap cycle burning IO and writing a duplicate History row that dilutes the "reclaimed" totals on Overview.
  - **MediaSorterFixer / MediaGrouperFixer**: the source path no longer holds the file (it was moved). Fixer refuses via `IsInsideLibrary` or path-not-found.
  - No data-loss ceiling reached, but the whole state machine's premise — Fixed is terminal — is broken by a one-line curl. Should be closed for the same reason `restored_paths` was added: user intent (both "restore this" and "this is already Fixed") deserves protection from stale-client flapping. Only real-world hit for this today is a UI bug or a script that iterates ids; low probability of hitting organically. Filing as high because a single-item bulk-approve script + a stale client == real risk of noisy History rows on active servers.

### F-010 — Schema v6 migration silently reverts every dismissed Playability issue (and drops Fixed rows too)
- Severity: high
- Status: open
- Area: db MediaDashDb.MigrateSchema
- Discovered: 2026-09-11 by audit-session-3
- Repro:
  1. Start with any pre-v1.0.7.3 MediaDash database (schema v5 or lower — anything before the v6 bump). Any user upgrading from 1.0.6 → current hits this path.
  2. Seed the `issues` table with a Playability row the user explicitly Dismissed (`status=3`, `type=1`):
     ```
     sqlite3 mediadash.db "INSERT INTO issues (type,item_id,path,details,suggested_fix,size_savings,status,detected_at_utc)
       VALUES (1,'p-dis','C:\\media\\playdis.mp4','{\"reason\":\"no-video\"}','remove',100,3,17560000000000000);"
     sqlite3 mediadash.db "PRAGMA user_version = 5;"
     ```
  3. Start Jellyfin (triggers `MigrateSchema`) and inspect the row: `sqlite3 mediadash.db "SELECT * FROM issues WHERE item_id='p-dis';"` → 0 rows.
  4. Trigger a scan. The scanner re-detects the file as Playability (reason: no-video), the user's Dismiss choice is silently forgotten. Under the shipping `PlayabilityFixMode = Automatic` default the file is then recycled/deleted without a fresh Approve click.
- Expected: The v6 migration purges only the rows it needs to (rows that could carry the pre-v1.0.7.3 `Truncating packet` false-positive — i.e. `status IN (Detected, Queued)`). Rows the user explicitly Dismissed keep their status, matching the "dismiss preserves your choice" invariant that `IssueStatus.Dismissed` documents ("re-scans will not re-report it"). Rows in status=Fixed are similarly preserved so the Issues-tab history isn't retroactively rewritten.
- Actual: `MediaDashDb.cs:337` executes `DELETE FROM issues WHERE type = 1` with no status filter — it drops all four statuses (Detected, Queued, Fixed, Dismissed). Live replay proves it: fed a v5 fixture with all four statuses represented, the migration purges every Playability row and leaves the non-Playability control rows (including a Dismissed SubtitleLanguage row) untouched. Full session log at `tools/audit-migration/` (build-v5-with-dismissed.sql + migrate.sh replay).
- Evidence:
  ```
  ===BEFORE===
  1|1|C:\media\playdet.mp4|0  (Detected)
  2|1|C:\media\playque.mp4|1  (Queued)
  3|1|C:\media\playfix.mp4|2  (Fixed)
  4|1|C:\media\playdis.mp4|3  (Dismissed)  <-- purged
  5|0|C:\media\dupdet.mkv|0
  6|2|C:\media\quadet.mkv|0
  7|3|C:\media\subs.mkv|3      (Dismissed control, non-Playability)  <-- survives
  ===AFTER (v9)===
  5,6,7 only
  ```
  Code comment at `MediaDashDb.cs:330-331` explicitly claims the intent is "drop already-Queued Playability rows" — the SQL is broader than the comment.
- File:line: `Jellyfin.Plugin.MediaDash/Data/MediaDashDb.cs:337` (the `DELETE FROM issues WHERE type = " + (int)IssueType.Playability` in the `if (current < 6)` block). Related: `Jellyfin.Plugin.MediaDash/Data/IssueStatus.cs:17-18` documents the Dismissed contract ("re-scans will not re-report it").
- Suspected fix: Narrow the DELETE to statuses the false-positive actually poisoned:
  ```csharp
  clearQueued.CommandText = "DELETE FROM issues WHERE type = " + (int)IssueType.Playability
      + " AND status IN (" + (int)IssueStatus.Detected + "," + (int)IssueStatus.Queued + ")";
  ```
  This matches the code comment, preserves user Dismiss choices, and preserves Fixed history rows. Regression test: fixture with all four statuses; assert only Detected + Queued get purged.
- Notes: Compounds under Automatic mode. A user with 100 broken files pre-v1.0.7.3 who Dismissed 20 of them ("I know these are broken, they're my low-priority-to-fix pile") upgrades to 1.0.7.3+, has all 20 dismisses reverted, then the next auto-fix run recycles the whole pile without any UI signal that a mass revert happened. Recycle bin catches the actual bytes, but the surprise factor is bad and requires manual per-item Restore. Also worth noting: `restored_paths` (added in v5) exists precisely to encode "user reversed a fix, don't re-fix" — the same design principle should apply to Dismiss. Ideally a future migration back-fills a `restored_paths` row for every Dismissed Playability path before the DELETE, giving belt-and-suspenders protection.

### F-009 — MediaSorterFixer sidecar sweep steals metadata from same-folder siblings whose filename starts with the moved file's stem
- Severity: medium
- Status: open
- Area: fixer MediaSorterFixer
- Discovered: 2026-09-11 by audit-session-2
- Repro:
  1. Set up a shared folder that contains a mis-placed movie plus a legitimate companion file whose name has the movie's stem as a prefix (common with feature+extras releases):
     ```
     /tv/wrong-pile/Inception (2010).mkv           # <-- to be moved out
     /tv/wrong-pile/Inception (2010).nfo           # actually belongs to the above (correct)
     /tv/wrong-pile/Inception (2010)-behind-the-scenes.mkv   # legit companion, stays put
     /tv/wrong-pile/Inception (2010)-behind-the-scenes.nfo   # belongs to the companion
     /tv/wrong-pile/Inception (2010)-poster.jpg              # ambiguous; probably belongs to the movie
     ```
  2. MediaSorterScanner flags `Inception (2010).mkv` as Misplaced.
  3. Approve and run the fix.
- Expected: only the sidecars unambiguously belonging to `Inception (2010).mkv` follow it to the target folder. Anything belonging to `Inception (2010)-behind-the-scenes.mkv` stays put with its owner.
- Actual: `MoveSidecars` (`Fixers/MediaSorterFixer.cs:242-307`) matches every file in the source folder whose name starts with `<sourceStem>.` OR `<sourceStem>-` and has a sidecar extension. `Inception (2010)-behind-the-scenes.nfo` starts with `Inception (2010)-`, extension `.nfo` is in the sidecar set → file gets moved to the target folder as `<targetStem>-behind-the-scenes.nfo`, orphaning the behind-the-scenes companion of its metadata. The companion video stays behind (not misplaced), so on the next scan the freshly-orphaned `.nfo` (and any co-moved artwork) is either lost in the target folder or gets swept by OrphanCleanup as debris.
- Evidence: Static read. The prefix pattern in `MediaSorterFixer.cs:269-270` is unconditionally `stem+"."` or `stem+"-"`. `SidecarExtensions` includes `.nfo`, `.jpg`, `.jpeg`, `.png`, `.webp`, `.tbn`, `.bif`, and every subtitle extension. No sibling-collision check exists (the code only checks whether the DESTINATION already has a file with the same name — line 286-292 — not whether the SOURCE name might belong to a different sibling video in the source folder).
- File:line: `Jellyfin.Plugin.MediaDash/Fixers/MediaSorterFixer.cs:258-306` (`MoveSidecars`), specifically the prefix match at line 269-270.
- Suspected fix: Before selecting sidecars, walk the source directory ONCE to build a set of "other video stems in this folder" (any file whose extension is in `MediaFormats.Video`/`MediaFormats.Audio` other than `sourceMedia`). Then for each candidate sidecar, if there exists another stem that is a LONGER prefix of the candidate's name than `sourceStem`, refuse the move (that sibling has a stronger claim). One-line-per-candidate check that keeps the current happy-path unchanged. Regression test: two-video, one-sidecar-per-video fixture; only the moved video's sidecars follow.
- Notes: Common in real libraries — Plex/Jellyfin naming conventions produce plenty of `<Movie> - Behind the Scenes.mkv`, `<Movie> - Interview.mkv`, `<Show> - Season 1 Extras.mkv` files sitting alongside the main title. The bug is silent (no error, no warning, no diagnostic) — the user only notices when Jellyfin loses artwork for the extras or when OrphanCleanup starts flagging the newly-orphaned sidecars in the target folder. Same shape bug does NOT appear in DuplicateFixer's `SweepDedicatedFolderSidecars` — that one only sweeps a folder proven to hold no other video/audio (checked at `DuplicateFixer.cs:220-228`).

### F-008 — MissingSubtitleScanner ignores the macrolanguage equivalence group; users allowing "nor" get spurious downloads over their "nob" / "nno" subs
- Severity: medium
- Status: open
- Area: scanner MissingSubtitleScanner
- Discovered: 2026-09-11 by audit-session-2
- Repro:
  1. `PluginConfiguration.AllowedSubtitleLanguages = ["nor"]`, `MissingSubtitlesFixMode = Automatic`. Ensure a video has an embedded Norwegian subtitle stream tagged `language=nob` (Bokmål — the way most Norwegian releases actually tag).
  2. Trigger a MediaDash scan.
  3. Observe the file emits a `MissingSubtitles` issue with `missingLanguages=["nor"]`, even though a Norwegian sub is embedded.
  4. Fixer downloads a duplicate `.nor.srt` sidecar next to the file (or a whole set from Jellyfin's provider list) that then trips the SubtitleLanguage cleanup on the next fix cycle.
- Expected: The 1.0.7.6 macrolanguage-equivalence fix ("nor" ↔ "nob" ↔ "nno") applies symmetrically — a wanted "nor" is satisfied by an existing "nob" or "nno" track, matching how `LanguageHelper.IsAllowed` handles the removal side.
- Actual: `MissingSubtitleScanner.HasAnyMatch` (`Scanners/MissingSubtitleScanner.cs:80-100`) does its own comparison — `string.Equals(Normalize(entry), Normalize(language), Ordinal)` — with no call to the `SameEquivalenceGroup` helper that lives in `LanguageHelper`. So `Normalize("nor") == "nor"` and `Normalize("nob") == "nob"` never match and the "missing Norwegian" branch fires. Same asymmetry for any future group added to `LanguageHelper.EquivalenceGroups`.
- Evidence: Static read of both files. The 1.0.7.6 changelog specifically calls out this class of bug on the removal side ("… silently delete every track for users who only added the macrolanguage"). The MissingSubtitleScanner's private `HasAnyMatch` was written *before* the equivalence-group work and was not updated when `LanguageHelper.IsAllowed` grew the group check. `AudioLanguageScanner.cs:52` and `SubtitleLanguageScanner.cs:43,56` both call `LanguageHelper.IsAllowed` correctly — MissingSubtitleScanner is the only outlier.
- File:line:
  - `Jellyfin.Plugin.MediaDash/Scanners/MissingSubtitleScanner.cs:80-100` (broken local `HasAnyMatch`)
  - `Jellyfin.Plugin.MediaDash/Scanners/LanguageHelper.cs:40-43` (macrolanguage group — private) and `93-116` (`IsAllowed`)
- Suspected fix: Delete `HasAnyMatch` and inline the check via `LanguageHelper`. Semantic wrinkle: `IsAllowed` returns true for `Normalize == "und"`, and MissingSubtitleScanner's current code deliberately returns false for "und" (an untagged track can't be trusted to contain the wanted language). Two clean options:
  1. Expose `LanguageHelper.HasMatch(language, allowed, treatUndAs)` with an enum for the "und" policy; both call sites choose. Deletes the local copy and closes the drift point.
  2. Cheaper: expose `LanguageHelper.SameEquivalenceGroup` as `internal` (or `public static`) and call it from `HasAnyMatch` right after the current `string.Equals` line.
  Either fix is one line at each site. Regression test: add "nob" fixture to whatever unit test covers MissingSubtitleScanner scenarios (grep for `MissingSubtitleScannerTests`).
- Notes: This isn't just a false-positive nuisance — the MissingSubtitleFixer then downloads Norwegian subs from Jellyfin's providers. The download is stored with whatever tag the provider used (often `nor`), the next scan sees both the original `nob` and the new `nor` sidecar, and SubtitleLanguageScanner probably starts flagging the new sidecar as a duplicate to remove. Users with a "nor"-only allow-list on a library full of Norwegian media get a churn cycle: download → over-collect → clean up → re-download.

### F-007 — Recycled directories (OrphanedDebris EmptyFolder) are unrestorable AND invisible in the Recycle bin tab
- Severity: high
- Status: open
- Area: api MediaDashController.RestoreFromHistory + fixer RecycleBin (ListContents / DeleteFile / Restore)
- Discovered: 2026-09-11 by audit-session-2
- Repro:
  1. Approve any OrphanedDebris EmptyFolder issue and let the fix run — a directory ends up recycled at `<bin>/<batch>/<foldername>/`. Example: history id 1509 with `path=C:\dev\mediadash-fixtures\movies\_audit_session1\audit_empty_then_music`, `recycle_path=C:\Users\crackruckles\AppData\Local\jellyfin-v10\data\mediadash\recycle\20260911-083729-957-b4fb8b2b\audit_empty_then_music` (still on disk as a directory).
  2. `POST /MediaDash/History/1509/Restore` → 409 "This file is no longer in the recycle bin." — but the folder is 100% still in the bin.
  3. `GET /MediaDash/RecycleBin/Items?limit=200` → the recycled folder does not appear in the listing at all.
- Expected: (a) Restore succeeds and the folder is moved back to its original path; (b) the folder appears in the Recycle bin tab listing so the per-item Delete / Restore UI can act on it.
- Actual: `RestoreFromHistory` guards on `!System.IO.File.Exists(entry.RecyclePath)` (`MediaDashController.cs:734`) which returns false for directories → 409 with a misleading "no longer in the bin" message. `RecycleBin.ListContents` (`Fixers/RecycleBin.cs:411-412`) uses `Directory.EnumerateFiles(dir)` (non-recursive, files only) and never yields the recycled subdirectory, so `GET /RecycleBin/Items` silently omits every OrphanedDebris entry. Downstream: `RecycleBin.Restore` (line 299-310) itself uses `File.Exists` collision guard + `MoveAcrossVolumes` → `File.Move` — no `Directory.Move` branch — so even if the controller guard were fixed, restoring a directory on Windows across volumes would fall through to `CrossDeviceMove(sourceIsDir: false)` and try to copy a folder as a file.
- Evidence: Live reproduced 2026-09-11. Bin batch is `20260911-083729-957-b4fb8b2b/`, sqlite row id 1509 still shows `restored=0`. Only the two sidecar files (`.mediadash-owned-v1`, `.mediadash-origin`) plus the recycled dir sit under the batch. `curl POST /MediaDash/History/1509/Restore` → 409 body `"This file is no longer in the recycle bin."`. `curl /RecycleBin/Items?limit=200` returned 12 items, none named `audit_empty_then_music`.
- File:line:
  - `Jellyfin.Plugin.MediaDash/Api/MediaDashController.cs:734` (RestoreFromHistory guard)
  - `Jellyfin.Plugin.MediaDash/Fixers/RecycleBin.cs:411-412` (ListContents non-recursive EnumerateFiles)
  - `Jellyfin.Plugin.MediaDash/Fixers/RecycleBin.cs:299-310` (Restore uses File.Exists + File.Move; no Directory branch)
  - `Jellyfin.Plugin.MediaDash/Fixers/RecycleBin.cs:317-322` (DeleteFile uses File.Delete — fails on dirs)
- Suspected fix:
  - Controller guard: `if (!System.IO.File.Exists(entry.RecyclePath) && !System.IO.Directory.Exists(entry.RecyclePath))`.
  - `RecycleBin.Restore`: check `Directory.Exists(recyclePath)` and use `Directory.Move` + a `CrossDeviceMove(sourceIsDir: true)` fallback path (same pattern already exists in `MoveToBin`). Same for the `File.Exists(originalPath)` collision guard — also check `Directory.Exists`.
  - `RecycleBin.ListContents`: after the file loop, also enumerate `Directory.EnumerateDirectories(dir)` and add each as a `RecycleBinItem` with the recursive-size sum. Sidecar exclusions still apply.
  - `RecycleBin.DeleteFile`: branch on `Directory.Exists` to call `Directory.Delete(recyclePath, recursive: true)` for recycled folder entries.
- Notes: OrphanedDebris is the primary source of recycled directories today (`MediaGrouperFixer` also creates target folders but doesn't recycle old ones). Because ANY user who approves an OrphanedDebris fix ends up with an un-restorable, invisible entry, this is a systemic user-visible break of the "safe by default, recyclable" contract. Compounds F-004: a user who lost music to the F-004 bug tries to Restore from History, gets a 409 telling them the file is gone, and their only recovery path is manual mv from the bin folder in a shell.

### F-006 — Repair-ladder temp sidecars (`.mediadash.repair.tmpN.*`) are never swept
- Severity: medium
- Status: open
- Area: fixer PlayabilityFixer + cross-cutting LibraryGuard.SweepOrphanSidecars
- Discovered: 2026-09-11 by audit-session-1
- Repro:
  1. Drop these fake orphans in any library folder:
     ```
     touch orphan_test.mediadash.tmp.abc.mkv
     touch orphan_test.mediadash.new.xyz
     touch orphan_test.mediadash.repair.tmp1.mkv
     touch orphan_test.mediadash.repair.tmp3.mkv
     ```
  2. `POST /MediaDash/Fix` (triggers end-of-run `SweepOrphanSidecars`).
  3. Check what survived.
- Expected: All four are swept — every MediaDash-owned sidecar pattern the plugin ever writes should be reachable by the end-of-run sweep.
- Actual: `mediadash.tmp.*` + `mediadash.new.*` are removed, `mediadash.repair.tmp1.mkv` + `mediadash.repair.tmp3.mkv` survive. Log confirms: "Removed 2 orphan MediaDash sidecar file(s)" — should be 4. Every rung-1 through rung-4 PlayabilityFixer temp leaks across a hard kill (SIGKILL, container restart mid-repair) and stays in the user's library until they see it and delete it by hand.
- File:line: `Jellyfin.Plugin.MediaDash/Fixers/LibraryGuard.cs:15-24` (`SidecarPatterns`). Patterns cover `mediadash.tmp` / `mediadash.new` / `mediadash.swap` / `mediadash.strip` / `mediadash.upload.tmp` but NOT `mediadash.repair.tmp*`. Repair temps are minted at `PlayabilityFixer.cs:212,289,335,373` via `SidecarPath(issue.Path, "repair.tmpN", ext)`.
- Suspected fix: Add `"*.mediadash.repair.tmp*"`, `"mediadash.repair.tmp.*"` (long-path hash-fallback shape), plus `"*.mediadash.raw*"` and `"*.mediadash.opt*"` for the EmbeddedCoverArtFixer temps at `EmbeddedCoverArtFixer.cs:234-235` (`raw`/`opt` markers, also missing from the pattern list). Confirm no user files use the same suffix — trivially unique with `mediadash.` prefix.
- Notes: Not data-loss (repair leaks are stale artefacts, not user content) but they can accumulate to many gigabytes on servers whose ffmpeg gets killed frequently (Docker OOM, LXC restart, `sudo systemctl restart jellyfin` mid-repair). The user report class is "MediaDash is filling my library with `.mediadash.repair.tmp1.mkv` files." Also flags every EmbeddedCoverArt cover-extraction crash — `<coverfilename>.mediadash.raw.png` / `.mediadash.opt.webp` in album folders.

### F-005 — NfoFixer has no fix-time re-verify: a user-repaired NFO gets deleted anyway
- Severity: medium
- Status: open
- Area: fixer NfoFixer
- Discovered: 2026-09-11 by audit-session-1
- Repro:
  1. Corrupt an NFO next to a movie: `echo garbage > "movie.nfo"`.
  2. Run MediaDash scan → CorruptNfo issue emitted (`suggested_fix = Delete corrupt NFO`). Approve it.
  3. Between the Approve click and the next scheduled fix run (up to 30 min), edit `movie.nfo` by hand — write a valid `<movie>…</movie>` XML.
  4. Fix runs. NfoFixer deletes / recycles the now-valid NFO with `Delete corrupt NFO "movie.nfo"`.
- Expected: NfoFixer re-parses the NFO at fix time. If it now parses as valid XML with a recognised root, refuse with "the NFO now parses cleanly — the file was likely repaired since the scan; nothing was deleted."
- Actual: NfoFixer's `FixAsync` (`NfoFixer.cs:39-97`) only checks the path ends in `.nfo`, `IsInsideLibrary`, and `File.Exists`. No content re-verify. `NfoScanner` has the parser but the fixer doesn't call back into it.
- Evidence: Static read of `NfoFixer.cs`. Contrast with `PlayabilityFixer.IsStillBrokenAsync` (double-probe), `TrackFixer` (`ComputeRemovableIndexes` on fresh probe), `OrphanCleanupFixer.OrphanCleanupScanner_HasVideoNow` (re-walk) — every other fix has some fix-time gate, NfoFixer is the outlier.
- File:line: `Jellyfin.Plugin.MediaDash/Fixers/NfoFixer.cs:39-97`.
- Suspected fix: Reuse the same XML validity check `NfoScanner` runs (extract into `NfoScanner.IsNfoBroken(string path)`). Fixer calls it at fix time; if the file now parses, `FixResult.Fail("NFO now parses cleanly...")` with `IsStaleFailure`-shaped message so the auto-retry loop terminates and the issue moves to Fixed. Next scan won't re-flag.
- Notes: Recycle bin mitigates worst case, but users with `NfoDisposal = PermanentDelete` lose their repair silently. Applies equally to `SubtitleFontFixer` and `EmbeddedCoverArtFixer` — check both for the same missing gate.

### F-004 — OrphanCleanupFixer live re-verify uses video-only extensions; new music/book/comic files get recycled
- Severity: critical
- Status: open
- Area: fixer OrphanCleanupFixer
- Discovered: 2026-09-11 by audit-session-1
- Repro:
  1. Set `OrphanCleanupFixMode = Automatic` in plugin config.
  2. Inject an EmptyFolder OrphanedDebris Detected issue for a folder that will be non-empty:
     ```
     mkdir C:\dev\mediadash-fixtures\movies\audit_empty_then_music
     # inject issue via sqlite: type=13, DetailsJson={"kind":"EmptyFolder","bytesEstimate":0}
     ```
  3. Drop an MP3 (music arrived after the scan) into `audit_empty_then_music/`.
  4. `POST /MediaDash/Fix`.
  5. The folder is recycled — including the MP3.
- Expected: The fixer's live re-verify treats any Jellyfin-recognised media (video / audio / book / comic / picture) as "the folder is no longer empty" and refuses. This matches the scanner's own `SubtreeHasMedia` gate which was widened to `MediaFormats.All` under F-201 to stop full music/audiobook libraries getting wiped.
- Actual: `OrphanCleanupFixer.OrphanCleanupScanner_HasVideoNow` (line 235) uses `OrphanCleanupScanner.VideoExtensions.Contains(Path.GetExtension(f))`. `VideoExtensions` is `MediaFormats.Video` — video only. Any audio/book/comic/picture file that appeared between scan and fix is invisible to the fixer's re-check, and the whole subtree is recycled.
- Evidence: Live reproduced at 2026-09-11 against issue id 12058. After adding `appeared.mp3` to `audit_empty_then_music/`, the fix ran and produced history row 1509 `Delete empty folder "audit_empty_then_music" (moved to recycle bin)` — folder including the MP3 was recycled to `.../recycle/…/audit_empty_then_music/`.
- File:line: `Jellyfin.Plugin.MediaDash/Fixers/OrphanCleanupFixer.cs:235` (`OrphanCleanupScanner_HasVideoNow`) — should call the same shape as `Scanners/OrphanCleanupScanner.cs:253` (`SubtreeHasMedia` which uses `MediaExtensions = MediaFormats.All`).
- Suspected fix: Rename `OrphanCleanupScanner_HasVideoNow` → `SubtreeHasMediaNow`, iterate against `OrphanCleanupScanner.MediaExtensions` (aliased to `MediaFormats.All`). Same one-line change is the F-201 sibling for the fixer.
- Notes: This is the direct sibling of the F-201 fix already applied to the scanner but never propagated to the fixer. It's the exact scenario the CHANGELOG's "OrphanedDebris server-side Automatic-disable" attempts to work around at `FixTask.cs:266-268`, but the block only fires under Automatic mode — a user who manually approves the empty-folder issue (thinking the folder is safe) then drops a file into it before the fix runs still loses data.

### F-003 — PlayabilityFixer swap has no crash-safety window between MoveToBin and File.Move
- Severity: high
- Status: open
- Area: fixer PlayabilityFixer
- Discovered: 2026-09-11 by audit-session-1
- Repro (by inspection; runtime repro requires an antivirus / OS lock racing at the exact wrong moment):
  1. Read `TrySwapRepairedAsync` from `MoveToBin` at line 512 through the `File.Move(overwrite:false)` at line 513.
  2. There is no try/catch/finally around the two-step operation.
  3. If `File.Move(repairedTempPath, finalPath, overwrite: false)` throws (e.g. `IOException 0x80070020` because a scanner / AV opened `finalPath` in the window; a `PathTooLongException` because the `.mkv` extension change pushed the path past `MAX_PATH` on Windows without long-path enabled; an `UnauthorizedAccessException` because the parent folder just lost the write bit), the source has already been recycled and no file lands at `finalPath`.
- Expected: The swap is atomic from the caller's point of view — either the repaired file replaces the source and the source is recycled, or neither happens and the source stays put.
- Actual: Source is recycled. Repaired temp is leaked at `<source>.mediadash.repair.tmp3.mkv` (or tmp1/tmp2/tmp4 depending on rung). The FixTask exception handler in `FixTask.cs:770-820` writes a "Fix failed" history row that names the source, not the leaked temp — user has no clue their content is actually still on disk under a sidecar name. Sidecar sweep at end of fix run then deletes the leaked temp, at which point the user has lost the repaired copy AND the original is only recoverable from the recycle bin.
- Evidence: Code path inspection. `TranscodeFixer` (485L) and `TrackFixer` (`RunTrackRemuxAsync` lines 355-390) both wrap the equivalent swap in try/finally with a `Track.SwapAborted` diagnostic and DO NOT delete the leaked swap when the original is disposed — PlayabilityFixer is the only fixer that lacks the guard.
- File:line: `Jellyfin.Plugin.MediaDash/Fixers/PlayabilityFixer.cs:512-534` (`TrySwapRepairedAsync`).
- Suspected fix: Wrap `File.Move` in try/catch. On failure, DO NOT delete the temp (it's the user's only intact copy); emit a `Playability.RepairSwapAborted` diagnostic that names the temp path and instructs the user to rename it manually. Add the temp path to the orphan-sidecar sweep's allow-list so the end-of-run sweep doesn't kill it.
- Notes: Same swap-safety pattern the TrackFixer author called out at `TrackFixer.cs:305-309` — the reason there's a Track.SwapAborted diagnostic in that file is because they hit exactly this class of bug once already.

### F-002 — Container/extension-mismatch Playability issue silently deletes healthy audio-only files
- Severity: critical
- Status: open
- Area: fixer PlayabilityFixer
- Discovered: 2026-09-11 by audit-session-1
- Repro:
  1. Build a tiny MP3 and rename it .mkv so the extension lies about the container:
     ```
     ffmpeg -f lavfi -i "sine=frequency=440:duration=2" -c:a libmp3lame -y a.mp3
     cp a.mp3 audit_lies_extension.mkv        # inside a library
     ```
  2. Refresh Jellyfin library, run MediaDash scan. Scanner emits an issue with `{"reason":"container-extension-mismatch"}`.
  3. Approve the issue (PlayabilityFixMode=Automatic → auto-queued anyway). Run a fix.
  4. The .mkv file is recycled / deleted with action "Removed unplayable file …".
- Expected: The fixer's `IsStillBrokenAsync` re-verify should treat container-extension-mismatch as an *organisational* problem, not a broken-file one, and either refuse (leaving the file for the user) or route to a separate fixer. Never silently recycle a file that plays fine end-to-end.
- Actual: The scanner's `container-extension-mismatch` reason has no matching case in the fix-time re-verify switch. It falls into `default`, which requires a video stream and positive duration to consider the file playable. For any audio-only file with a video-typical extension (or vice versa: video demuxed OK but no audio) the default judges "no video → broken" and the file gets recycled or permanently deleted (depending on `PlayabilityDisposal`).
- Evidence: Live reproduced at 2026-09-11 against issue id 12026 (`audit_lies_extension.mkv`, an MP3 renamed .mkv). History row: `Removed unplayable file audit_lies_extension.mkv — The file's extension is '.mkv' but its container is 'mp3'` (success=1, recycled to `.../recycle/20260911-083319-095-.../audit_lies_extension.mkv`).
- File:line: `Jellyfin.Plugin.MediaDash/Fixers/PlayabilityFixer.cs:572-633` — the switch in `IsStillBrokenAsync`. `Scanners/PlayabilityScanner.cs:139` is where the "container-extension-mismatch" reason is emitted.
- Suspected fix: Add an explicit `case "container-extension-mismatch": return false;` in `IsStillBrokenAsync` so the fixer refuses (Fail with "plays fine now — extension lies about container; rename by hand"). Ideally route the issue class to a rename-only fixer that changes the extension in-place instead of deleting. Same-shape audit needed for every other `reason` value the scanner can emit but the fixer doesn't case (grep `PlayabilityScanner.cs` for `reason =`).
- Notes: PlayabilityFixMode defaults to Automatic in the shipping config, so this fires without user intervention for anyone whose library has a mislabelled audio file. The `.strm` file class is separately excluded by the scanner (see PlayabilityScanner probe branches) — this bug is about legitimately-playable files that just have the wrong extension.

### F-001 — ApproveAll and BulkUpdateIssues bypass the data-loss consent gate
- Severity: high
- Status: open
- Area: api
- Discovered: 2026-09-11 by audit-session-1
- Repro:
  1. Inject an AudioLanguage Detected issue with a blocking warning:
     ```
     sqlite3 %LOCALAPPDATA%\jellyfin-v10\data\mediadash\mediadash.db \
       "INSERT INTO issues (type,item_id,path,details,suggested_fix,size_savings,status,detected_at_utc) VALUES (4,'11111111111111111111111111111111','C:\dev\FAKE.mkv','{\"warnings\":[{\"code\":\"bitmap-subs-dropped\",\"blocking\":true,\"message\":\"drops PGS\"}]}','',100,0,17570000000000000);"
     ```
  2. `POST http://localhost:8099/MediaDash/Issues/ApproveAll?type=AudioLanguage` (auth as admin).
  3. Query status of the inserted row.
- Expected: The row stays Detected (or the endpoint 400s / returns "N approved, M held for consent") because auto-queue's blocking-warning walk-back is the whole point of the consent gate.
- Actual: Row transitions Detected → Queued. Next scheduled fix run (or "Run fixes now") will silently drop the bitmap subs without a per-item click / warning-tooltip surface.
- Evidence: Just reproduced live at 2026-09-11 (see repro). Same bypass reproduces via `POST /MediaDash/Issues/Bulk {"Ids":[<id>],"Action":"Approve"}`.
- File:line: `Jellyfin.Plugin.MediaDash/Api/MediaDashController.cs:541` (`ApproveAll`), `:567` (`BulkUpdateIssues`). Consent gate today only runs inside `FixTask.ExecuteAsync` right after per-type auto-queue: `Jellyfin.Plugin.MediaDash/ScheduledTasks/FixTask.cs:286`.
- Suspected fix: After the bulk UPDATE, call `_db.RollbackAutoQueuedBlockingWarnings(type)` (for ApproveAll) or an id-scoped variant that keeps blocking-warning rows Detected and reports the held count in the response. Per-item Approve is fine — that's the "one deliberate click" the current design counts on.
- Notes: This is exactly the invariant the 1.0.7.6 bitmap-sub fix (F #56) was meant to protect. Regression path is any user hitting "Approve all shown" on the Issues tab.


<!-- Reference: findings shipped in 1.0.7.6 for context — do NOT re-file these as new bugs:
     - PlayabilityFixer collision guard (Path.ChangeExtension no-op on .mkv)
     - +discardcorrupt added to repair rungs for MP4 tail truncation
     - TrackFixer bitmap-sub silent drop (issue #56) — now behind data-loss consent gate
     - Diagnostics.StableStringHash (Errors-tab duplicates across restarts)
     - MediaGrouperScanner sanitizer for SxxExx-shaped SeriesName (spooks S01E06)
     - MediaGrouperScanner refuses TV-episode-shaped Movie candidates
     - FixTask review-grace after user-triggered scan (10 min)
-->
