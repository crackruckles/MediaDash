# MediaDash Security Re-audit — 2026-08-19 (v2)

Second independent security audit after the first-pass fixes. Confirms prior fixes landed cleanly and surfaces new findings.

Status legend: `[ ]` open, `[x]` fixed, `[!]` disputed / informational-only.

---

## Confirmed clean (prior fixes verified)

Every fix from `audit-2026-08-19-security.md` re-verified by the fresh audit:
- SQL parameterization — all queries use `AddWithValue` / typed params
- Process invocation — every ffprobe / ffmpeg / smartctl / df call uses `ArgumentList`
- `LibraryGuard.HasReparsePointAncestor` — walks each ancestor, refuses reparse points
- `AssSubtitleFile` — 100 MB cap, UTF-16 rejection, compiled regexes have no backtracking risk
- `BulkUpdateIssues` — 50k cap correctly enforced; status transition guard blocks regression
- `RestoreFromHistory` — `IsInsideLibrary(entry.Path)` gate in place
- `SmartHealthProbeWmi` — drive letter validated to a single ASCII letter before WQL interpolation
- `SmartHealthProbe` — all args passed via `ArgumentList`

---

## New findings

### High

- [x] **SV1. `_audit_cookies.txt` at repo root — empty but ungitignored.**
  Created by libcurl during audit runs. File is empty (verified), but should be prevented from future accumulation and accidental commits.
  **Fix:** deleted the file; added `_audit_cookies.txt` and `*.cookies.txt` to `.gitignore`.

- [x] **SV2. `Fixers/RecycleBin.cs:44-59` — `RecycleBinPath` not validated as library-adjacent.**
  Prior fix (S6) rejects OS-reserved roots but doesn't require the path to be inside a library or the plugin's default data dir. An admin (or a poisoned config XML) could set `RecycleBinPath = /home/jellyfin/.ssh/` and every recycle-bin disposal deposits files there. Escalation: `RestoreFromHistory` reads from `entry.RecyclePath` unguarded → arbitrary-file-read into library.
  **Fix:** in `RecycleBin.Root`, require `IsInsideLibrary(configured)` OR `configured == _defaultRoot` OR the configured path is directly under `_defaultRoot`'s parent (data dir). Also validate `entry.RecyclePath` in `RestoreFromHistory` as being under the effective bin root.

- [x] **SV3. `Api/FileBrowserController.cs:497-587` — Upload allows 50 GB per stream before triggering the cap.**
  Prior fix (S4) caps via a byte counter inside the streaming loop — but that means up to 50 GB is written to disk before the cap fires, and N concurrent uploads consume `N * 50 GB` before any is terminated. No `Content-Length` pre-flight check.
  **Fix:** short-circuit with `413 PayloadTooLarge` when `Request.ContentLength > UploadMaxBytes`; keep the streaming counter as backup for chunked-encoding requests without a declared length.

### Medium

- [ ] **SV4. `Api/FileBrowserController.cs:264-302, 328-390, 459-487` — TOCTOU on Rename/Move/Delete.**
  Between the `IsInsideLibrary` guard and the actual `File.Move` / `Directory.Move` / `MoveToBin` call, a local attacker with write access to a library folder can atomically substitute the validated path with a symlink pointing outside the library. `rename(2)` on Linux resolves the symlink target atomically.
  **Trigger:** microsecond window; requires local filesystem write to library (multi-tenant host). Not exploitable remotely.
  **Fix:** re-run `HasReparsePointAncestor` immediately before the destructive operation, or open a file handle first with `FileShare.Delete` and operate through it. Both are non-trivial for `Directory.Move`.

- [x] **SV5. `Fixers/LibraryGuard.cs:111-167` — `SweepOrphanSidecars` doesn't re-check `IsInsideLibrary`.**
  Enumeration is scoped to library roots and skips reparse points, so the paths are safe today — but every other deletion in the plugin defense-in-depth-checks the path before touching it, and this sweep does not.
  **Fix:** add `if (!IsInsideLibrary(path)) continue;` before `File.Delete(path)` in the sweep loop.

### Low / informational

- [x] **SV6. `Api/SystemStats.cs:649` — `nvidia-smi` resolved via bare-name PATH lookup.**
  Same class as the prior smartctl PATH hardening (S14). Multi-tenant hosts with a hostile user controlling PATH could substitute a malicious binary.
  **Fix:** mirror `SmartHealthProbe.ResolveSmartctl` — check `/usr/bin/nvidia-smi`, `/usr/local/bin/nvidia-smi`, `/opt/nvidia/nvidia-smi` before falling back to bare name.

- [!] **SV7. `Analytics/AnalyticsReporter.cs:31-32` — Supabase anon key hardcoded.**
  Publishable anon keys are meant to ship in clients; the risk depends on the Supabase project's RLS + RPC policies. Not a plugin defect — an infrastructure verification.
  **Action:** verify at deployment that the Supabase project restricts anon role to the `report_stats` RPC only.

- [!] **SV8. `Fixers/*.cs` — `DetailsJson` from DB controls fixer behavior.**
  Suspected low risk: scanners write DetailsJson via `JsonSerializer.Serialize(new { … })` with compile-time field names; fixers extract path fields and gate them through `IsInsideLibrary` before use. No confirmed exploit path.

---

## Priority for 1.0

Ship-blockers:
1. **SV2** (RecycleBin path library-adjacency) — real privilege escalation vector
2. **SV3** (Upload Content-Length pre-check) — DoS ceiling reduction
3. **SV5** (Sweep guard) — defense in depth, one-line fix
4. **SV6** (nvidia-smi path) — one-function fix mirroring existing pattern

Deferrable to 1.0.1:
5. **SV4** (TOCTOU) — non-trivial fix, exploitation requires local FS write + microsecond timing
