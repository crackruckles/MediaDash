# MediaDash Security Audit — 2026-08-19

Independent security-focused audit ahead of 1.0. Read-only.

Status legend: `[ ]` open, `[x]` fixed, `[!]` disputed / not-a-bug on closer look, `[~]` in progress.

---

## Critical

- [x] **S1. `Probing/SmartHealthProbe.cs:112` — smartctl args concatenated as string.**
  `ProcessStartInfo(smartctl, "-j -H -A " + device)` tokenizes on whitespace; a `device` string with spaces or extra tokens breaks out of the intended three arguments. On Linux, `device` can be influenced by unusual mount names combined with the `df` line-parsing weakness (S15).
  **Fix:** use `ArgumentList.Add(...)` mirroring the ffprobe/ffmpeg pattern.

- [x] **S2. `Probing/SmartHealthProbeWmi.cs:73` — WQL string interpolation.**
  `"SELECT * FROM MSFT_Volume WHERE DriveLetter = '" + vLetter + "'"`. Currently not exploitable — `vLetter` comes from `DriveInfo.GetDrives()` — but the pattern is fragile and will bite if a user-controllable value ever reaches this method.
  **Fix:** validate `vLetter` is exactly one A-Z before interpolation.

- [x] **S3. `Api/MediaDashController.cs:577-648` — `RestoreFromHistory` missing LibraryGuard.**
  `RestoreOptimizedCopy` (line 1119) explicitly guards `entry.Path`; this endpoint does not. A poisoned history row (older DB, hand-edited, compromised backup) → `_recycleBin.Restore(recyclePath, "/etc/cron.d/evil")` writes attacker-controlled bytes to any path Jellyfin can write.
  **Fix:** add `_libraryGuard.IsInsideLibrary(entry.Path)` gate after the `entry is null` check.

## High

- [x] **S4. `Api/FileBrowserController.cs:477` — Upload has no size cap.**
  `[DisableRequestSizeLimit]` removes ASP.NET Core's guard entirely; no secondary cap. Any admin can stream a multi-TB body → disk fill → Jellyfin/host crash. Also triggerable by a misconfigured client.
  **Fix:** wrap `Request.Body.CopyToAsync` in a byte-count-capped copy, or set `IHttpMaxRequestBodySizeFeature.MaxRequestBodySize` per-endpoint.

- [x] **S5. `Api/FileBrowserController.cs:256-263, 316-322, 446-449` — TOCTOU on Move/Rename/Delete.**
  `File.Exists(target) → return Conflict → File.Move(source, target)` isn't atomic; two concurrent requests both pass the check, second Move overwrites the first (Linux behavior).
  **Fix:** on Linux use `File.Move(..., overwrite: false)` and catch `IOException` on collision (kernel-level atomic).

- [x] **S6. `Fixers/RecycleBin.cs:36-41` + `Configuration/PluginConfiguration.cs:160` — `RecycleBinPath` unvalidated.**
  Admin can set `RecycleBinPath = "/etc"`; recycled files land at `/etc/<timestamp>/<original-name>`. Combined with a library file named to target a sensitive filename, could introduce persistence.
  **Fix:** reject OS-reserved roots (`/etc`, `/bin`, `/usr`, `C:\Windows`) on save; require path either empty or inside a library / sibling of a library root.

- [x] **S7. `Scanners/NfoScanner.cs:67` — no reparse-point guard.**
  Uses `SearchOption.AllDirectories` unlike every other scanner. Symlink cycle under a library root recurses until OOM / process kill.
  **Fix:** `EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = ReparsePoint, IgnoreInaccessible = true }`.

## Medium

- [!] **S8. `Fixers/DuplicateFixer.cs:108` — `DisposalMethod.PermanentDelete` no confirmation gate.**
  Concern requires config edit AND bypass of DryRun. Existing safeguards are sufficient; a bulk-confirm gate would be UX bloat. Disputed for 1.0.

- [!] **S9. `Api/MediaDashController.cs:1228` — `[AllowAnonymous]` on `/I18n/{locale}`.**
  Locale files are embedded assembly resources with no secrets. Anonymous serving is intentional (translation bundles load early during page render before auth completes). Disputed.

- [x] **S10. `Fixers/DuplicateFixer.cs` — `keeperPath` not LibraryGuarded.**
  Scanner-written `DetailsJson.keeperPath` is trusted; only the issue.Path is guarded. Defense-in-depth against a scanner ever writing an out-of-library path.
  **Fix:** `_guard.IsInsideLibrary(keeperPath)` check before recycling `issue.Path`.

- [!] **S11. `Api/MediaDashController.cs:973-976` — `EmptyAll` fire-and-forget race.**
  Auditor's own note: "The implementation is actually correct" — the `Interlocked.CompareExchange` gate in `EmptyAll` is the real invariant. Controller pre-check is just a cheap optimization. Informational only.

- [x] **S12. `Api/MediaDashController.cs:483` — `BulkIssueRequest.Ids` unbounded.**
  DB layer already chunks to 500 per query, so no SQL param overflow. But a 10M-id array allocates a huge list in memory before chunking.
  **Fix:** controller-level cap (e.g., 50k), return `BadRequest` above the cap.

## Low

- [!] **S13. `Api/MediaDashController.cs:1167-1172` — Diagnostic buffer exposes internal paths.**
  Admin-only endpoint returning paths + exception messages. Within threat model; intended for debugging. Auditor recommends path-scrubbing — reject as unnecessary complexity for admin-only surface.

- [x] **S14. `Probing/SmartHealthProbe.cs:196` — smartctl bare-name fallback.**
  Bare `smartctl` in `ProcessStartInfo` resolves via `PATH`; a hostile-multi-tenant user could plant a fake binary earlier in PATH.
  **Fix:** on Linux, try absolute paths (`/usr/sbin/smartctl`, `/usr/bin/smartctl`) before bare name; log a warning if only bare name resolves.

- [x] **S15. `Probing/SmartHealthProbe.cs:378-390` — `df` output line trusted whole.**
  Line starting with `/dev/` returned in full; a mount that produces `/dev/sda1 extra-text` passes the whole line into smartctl args (compounds S1).
  **Fix:** `line.Split(' ', RemoveEmptyEntries)[0]` before returning.
