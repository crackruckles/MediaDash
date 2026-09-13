# MediaDash Audit — Fix Plan

Sequenced plan for landing fixes to F-001..F-018 without breaking existing behaviour. Ordered
so defensive guardrails ship first (stabilise the surface), data-loss stops next, then state
machine tightening, then the bigger refactors, then polish. Each phase has its own regression
gate that must be green before starting the next.

## Principles (apply to every phase)

1. **One finding, one commit** wherever possible. Bisectable, revertable.
2. **Test first.** Every fix leaves behind at least one test that fails without the fix and passes
   with it. Preferred: assert-based unit test. Acceptable: extend `tools/repair-test/torture-test.ps1`
   or `tools/audit-migration/` for cases that need a live plugin.
3. **No-op default paths.** Guards must be safe (`false` / null / continue) for existing valid
   data. Never introduce a break that only manifests on a specific input shape unless every
   test in the suite catches that shape.
4. **Standing regression gate:** after every fix, `dotnet test` must show 700+/700+ green.
   After every phase, the full BBB torture test (`tools/repair-test/torture-test.ps1`) plus
   the migration harness (`tools/audit-migration/migrate.sh`) must both exit 0.
5. **Live-verify each phase** on `localhost:8099` before moving on. Deploy via
   `tools/deploy-local.ps1`, restart Jellyfin, exercise the specific user flow the phase
   changed. Log evidence into a `phase-N-verify.md` alongside this file.
6. **Never touch production (`192.168.1.117`).** Never push. Fix branch stays local until
   the user pushes.

## Overall sequencing

- **Phase 0** — Setup + tooling: DB-row protection invariant added to `CLAUDE.md`, torture
  test extended with a "malformed row" pass. No code changes to plugin.
- **Phase 1** — Defensive guardrails (F-014, F-015, F-016). Low blast radius, foundational —
  stops the "one bad row bricks a surface" class from biting during the rest of the fixes.
- **Phase 2** — Data-loss stops (F-002, F-004, F-010, F-003). Every finding here is protecting
  user content. Land before touching state-machine or refactor work.
- **Phase 3** — State-machine tightening (F-011, F-001). Both about "unconditional UPDATE/DELETE
  → add a status guard". Small, high value.
- **Phase 4** — Recycle bin directory support (F-007). Real refactor. Isolated to one class +
  one controller endpoint; deferred to its own phase so the risk is easy to bound.
- **Phase 5** — Scanner-fixer parity (F-005, F-008). Two "scanner has guard, fixer doesn't"
  cases; landed together because they exercise the same pattern.
- **Phase 6** — Noise + polish (F-006, F-009, F-013, F-017, F-018, F-012, F-019). Mix of small
  fixes and one real new feature (F-012 back-off table); F-012 is separate at the end because it
  introduces a new DB table. F-019 (top-level FixTask exception handlers never advance status)
  piggybacks on F-012's back-off table because both fixes want the same "per-issue retry cadence
  + failure_count" mechanism; landing them together avoids two half-solutions.

Ship-verification after Phase 6: BBB torture + migration harness + full unit suite + a fresh
manual scan → auto-approve → auto-fix cycle on the fixtures library. If all four green, the
audit round is closed.

- **Phase 7** — Deployment-topology + scale (F-020, F-021). Two audit-session-8 findings that
  surface only on realistic deployments outside the fixture-library shape (UNC/NAS libraries;
  10K+ item libraries). Deliberately deferred behind the Phase 1-6 correctness work — neither
  is a data-loss bug, but both compound with existing findings at scale (F-020 disables the
  friendly-guidance surface Phase 6 improves; F-021 widens the F-014 crash blast radius).

## Phase 0 — Tooling + invariant

**Goal:** codify the "malformed DB row must not crash a surface" discipline so Phase 1's fixes
have a documented invariant they defend.

**Work:**
- Add to `CLAUDE.md` under "Hard rules — safety invariants":
  > 6. The plugin never throws on unexpected DB row shape. Malformed rows (bad Guid strings,
  >    non-object DetailsJson roots, out-of-range enum values) are logged with the row id and
  >    skipped; loading + operating surfaces must return a partial result, never a 500.
- Extend `torture-test.ps1` (or add `torture-malformed-rows.ps1`) that:
  1. Injects 5 malformed rows via sqlite (bad guid, null DetailsJson, array DetailsJson,
     non-object DetailsJson, `{"reason":[1,2]}`).
  2. Hits `/Status`, `/Issues`, `/Fix` and asserts all return 2xx and don't throw.
  3. Baseline: this FAILS today, expected to pass after Phase 1.

**Test:** the new torture step fails as expected. `dotnet test` still 700+ green.

**Success criteria:** Phase 1 has a working failing test to fix against.

## Phase 1 — Defensive guardrails

**Findings:** F-014, F-015, F-016.

**Order within phase:**

1. **F-014** first — malformed `item_id` crashes `/Status`. Fix in `MediaDashDb.GetIssues`
   and `GetIssue`. Swap `Guid.ParseExact` → `Guid.TryParseExact`; on failure log with row id
   and `continue` (or return null for `GetIssue`). Also grep the codebase for every other
   `Guid.ParseExact(` on a DB-read string and apply the same treatment (`GetHashesFor`,
   `GetHistory`, etc.).
2. **F-015** — `TryGetReason` needs the `ValueKind != JsonValueKind.Object` guard before
   `TryGetProperty`, plus `r.ValueKind != JsonValueKind.String` before `GetString()`. Copy the
   pattern already correct in the sibling `TryGetString` in the same file.
3. **F-016** — `Issue.HasBlockingWarnings` gets the same two guards. Same fix shape as F-015.

**Systemic sweep after the three fixes:** grep for every `JsonDocument.Parse(` and every
`try { … } catch (JsonException)` block. Any site that calls `TryGetProperty` or `GetString` /
`GetInt32` etc. without a `ValueKind` check gets the same treatment. Non-JsonDocument JSON
parsers (Newtonsoft, JsonSerializer.Deserialize) are also candidates.

**Tests to add:**
- Unit: `MalformedDbRowsDoNotCrashTests.cs`. Assert `Guid.TryParseExact` behaviour, then a
  fixture that feeds a synthetic Issue with each malformed DetailsJson shape into
  `HasBlockingWarnings` and asserts `false` returned (no throw).
- Integration: extend Phase 0's torture step; it should now PASS.
- Regression: existing 700+ tests remain green.

**Live E2E:** replay Phase 0's malformed-row injections, hit the three endpoints, confirm no
500s and Errors tab remains clean (no "unexpected error" spam).

**Risk:** very low. All fixes are "log and continue" replacements for "throw"; if a row was
handled correctly before, it's still handled correctly after.

**Success criteria:** three findings closed, Phase 0's torture step green, unit suite green.

## Phase 2 — Data-loss stops

**Findings:** F-002, F-004, F-010, F-003.

**Order within phase:**

1. **F-004** first — the widest data-loss surface. One-line change:
   `OrphanCleanupFixer.OrphanCleanupScanner_HasVideoNow` iterates against
   `OrphanCleanupScanner.MediaExtensions` (aliased to `MediaFormats.All`) instead of
   `VideoExtensions`. Rename the method → `SubtreeHasMediaNow` while we're there.
   - Test: unit fixture with a temp folder containing an MP3, assert `SubtreeHasMediaNow`
     returns true. Regression fixture with an empty folder still returns false.
   - Live: replay the F-004 repro (inject empty-folder issue, drop MP3, run fix). Expected:
     fixer refuses with "the folder now contains media — no delete."

2. **F-002** — add explicit `case "container-extension-mismatch": return false;` in
   `PlayabilityFixer.IsStillBrokenAsync`. While in the file, audit every `reason` value that
   `PlayabilityScanner` emits (grep `reason =` in the scanner) and make sure each one has a
   matching case in the switch. Any that only have `default` behaviour → add explicit case.
   - Test: unit test each reason → verify IsStillBrokenAsync returns the right shape for a
     synthetic Issue + a real playable file.
   - Live: replay the F-002 repro (MP3 renamed .mkv), scan, confirm still flagged, run fix,
     assert file is NOT deleted, history row reads "plays fine now" or "rename by hand".

3. **F-010** — schema v9 → v10. Add a corrective migration note (existing users have already
   lost data — nothing to restore). The real fix is preventing future migration DELETEs from
   forgetting status filters: add a `WHERE status IN (Detected, Queued)` clause to the v6
   DELETE and any similar future migrations. Add a hard code-review comment in
   `MediaDashDb.MigrateSchema` reminding future migration authors to include status filters
   when purging by type.
   - Test: extend `tools/audit-migration/migrate.sh` — synthesise a v5 DB with Dismissed +
     Fixed rows, run migrations through v10, assert those rows survive.
   - Live: not applicable (migration path only runs on Jellyfin startup; the harness covers
     it).

4. **F-003** — wrap `PlayabilityFixer.TrySwapRepairedAsync`'s `File.Move` in try/catch. On
   failure emit `Playability.RepairSwapAborted` diagnostic naming the temp path, DO NOT delete
   the temp, and add the temp path to a per-run allow-list that `SweepOrphanSidecars` respects.
   - Test: unit test hard to write (needs File.Move to throw); acceptable to add an integration
     test that races an `File.OpenWrite` on `finalPath` immediately before the swap and asserts
     the temp survives + diagnostic fires.
   - Live: not required (edge case is real but repro requires an OS-level race).

**Tests to add:** listed inline above; each finding leaves a test behind.

**Live E2E:** F-004 and F-002 repros — both should now PASS (no data loss). Rerun BBB torture
suite — 15/15 must still pass (no regression in the ladder).

**Risk:** medium. F-004's rename touches the fixer signature; F-002's switch case addition
must not accidentally intercept other reasons; F-010's migration bump must not corrupt any
existing DB. Bounded by the test suite + migration harness.

**Success criteria:** four findings closed. Torture + migration harness green. Live repros
of F-002 and F-004 both blocked.

## Phase 3 — State-machine tightening

**Findings:** F-011, F-001.

**Order within phase:**

1. **F-011** first — the plumbing F-001 needs. Extract
   `TryTransitionOpenIssue(id, target, allowedCurrentStatuses)` in `MediaDashDb`. Have
   `ApproveIssue` require `current == Detected`; `DismissIssue` require
   `current ∈ {Detected, Queued}`; keep `RevertIssue`'s existing guard unchanged. Return 409
   Conflict with a clear message when the transition is refused.
   - Test: `IssueStateMachineTests.cs`. Feed each transition (Approve, Dismiss, Revert) against
     every current status (Detected, Queued, Fixed, Dismissed); assert allowed vs 409.
   - Live: replay F-011 repro (POST /Approve on a Fixed row); assert 409.

2. **F-001** — `ApproveAll` and `BulkUpdateIssues` call
   `_db.RollbackAutoQueuedBlockingWarnings(type)` right after the bulk UPDATE (same shape
   `FixTask` uses after per-type auto-queue). Response includes a `heldForConsent` count so the
   UI can render "N approved, M held for review".
   - Test: unit — inject an issue with a blocking warning, call ApproveAll, assert status is
     still Detected and `heldForConsent = 1`.
   - Live: replay F-001 repro; assert row stays Detected.

**Tests to add:** state-machine matrix + ApproveAll consent rollback.

**Live E2E:** both repros blocked. Full unit suite green.

**Risk:** low. `TryTransitionOpenIssue` returns bool; if refused, controller returns 409 — no
data loss path. Existing callers of `UpdateIssueStatus` continue to work; new guard only fires
on the paths we explicitly opt in.

**Success criteria:** F-001 + F-011 closed. State-machine matrix test locked in.

## Phase 4 — Recycle bin directory support

**Findings:** F-007.

**Isolate this phase.** It's the biggest single refactor and easiest to bound by keeping it
alone.

**Work:**
- Introduce `RecycleBin.IsDirectoryEntry(recyclePath)` helper — a `Directory.Exists` check
  scoped to the bin root.
- `RecycleBin.Restore(historyRow)`: branch on `IsDirectoryEntry`. Directory branch uses
  `Directory.Move` for same-volume and a `CrossDeviceMove(sourceIsDir: true)` fallback path
  (mirror the `MoveToBin` sibling). Collision guard checks both `File.Exists` and
  `Directory.Exists` for the destination.
- `RecycleBin.ListContents`: after the file loop, enumerate `Directory.EnumerateDirectories(dir)`
  and add each as a `RecycleBinItem` with `IsDirectory = true` and a recursive-size sum.
  Sidecar exclusions still apply.
- `RecycleBin.DeleteFile` → rename to `DeleteEntry`. Branch on `IsDirectoryEntry` and call
  `Directory.Delete(recyclePath, recursive: true)` for directories.
- `MediaDashController.RestoreFromHistory`: `if (!File.Exists(entry.RecyclePath) && !Directory.Exists(entry.RecyclePath))`
  for the 409 guard. Same for the `File.Exists(originalPath)` collision check — also check
  `Directory.Exists`.
- `RecycleBinItem` DTO gains an `IsDirectory` bool; UI renders a folder icon for those rows.

**Tests to add:**
- `RecycleBinDirectoryTests.cs`: put a fixture folder in the bin, restore it, assert it lands
  back at the original path with all contents. Also delete-from-bin path. Also cross-volume
  restore (fake via subst).
- Regression: file restore paths must still work identically.

**Live E2E:**
- Replay F-007 repro (approve an OrphanedDebris EmptyFolder fix, hit
  `/RecycleBin/Items` — folder should appear; POST `/History/{id}/Restore` — folder should
  restore to the original path).
- Regression: normal file-based restore (Playability rescue → restore from bin) still works.

**Risk:** medium-high. Touching four sites in `RecycleBin` + one controller endpoint + one DTO.
Bounded by: (a) new branches are additive (existing file paths preserved), (b) test coverage
above.

**Success criteria:** F-007 closed. All prior recycle-bin tests still green. Live repro of
F-007 fully resolved (visible in bin + restorable).

## Phase 5 — Scanner-fixer parity

**Findings:** F-005, F-008.

**Order within phase:**

1. **F-008** first (smaller change): expose `LanguageHelper.SameEquivalenceGroup` (make it
   `internal static` or `public static`). Refactor `MissingSubtitleScanner.HasAnyMatch` to
   call `LanguageHelper.IsAllowed`-shape logic OR the equivalence-group check. Preserve the
   "und" policy difference — MissingSubtitleScanner returns false for `und` (untagged track
   can't be trusted); `LanguageHelper.IsAllowed` returns true. Introduce a small
   `LanguageHelper.HasMatch(language, allowed, undPolicy)` overload with an enum for the
   policy; both call sites use it.
   - Test: unit — "nor" allow-list + embedded "nob" → HasAnyMatch returns true (no false
     positive). Regression: existing MissingSubtitleScanner tests still green.
   - Live: not strictly needed (unit coverage is enough; provider download is out-of-scope).

2. **F-005** — extract `NfoScanner.IsNfoBroken(string path)` from the scanner's XML parse logic.
   Have `NfoFixer.FixAsync` call it at fix time; on `false` return `FixResult.Fail("NFO now
   parses cleanly — the file was likely repaired since the scan; nothing was deleted.")` with
   an `IsStaleFailure`-shaped message so the auto-retry loop terminates and the issue moves
   to Fixed.
   - Test: unit — inject a corrupt NFO issue, hand-repair the file, run fix, assert refused
     with "plays cleanly now".
   - Live: replay F-005 repro. Assert refused.
   - Extend: check `SubtitleFontFixer` and `EmbeddedCoverArtFixer` for the same missing gate.
     File findings if either has the same pattern (they may need Phase-5-continued or Phase-6).

**Tests to add:** listed inline.

**Risk:** low. F-008 refactor is behind a common helper; F-005 adds a fix-time check that only
fires when the file has been repaired. No new failure mode introduced.

**Success criteria:** F-005 + F-008 closed. Language tests + NFO re-verify test locked in.

## Phase 6 — Polish + noise + F-012 back-off + F-019 exception-catch status flip

**Findings:** F-006, F-009, F-013, F-017, F-018, F-012, F-019.

**Order within phase (increasing risk):**

1. **F-006** — extend `LibraryGuard.SidecarPatterns` with `*.mediadash.repair.tmp*`,
   `*.mediadash.raw*`, `*.mediadash.opt*` plus long-path hash variants. Two-line change.
   - Test: unit — assert each new pattern matches the expected sidecar shape; assert no user
     files match (`.mediadash.` prefix reserved).
   - Live: drop fake orphans, run fix, sweep count reflects all patterns.

2. **F-017** — return 409 Conflict with a descriptive body when `scanTask.State != TaskState.Idle`.
   Same shape for `StartFix`. UI-side handling stays as follow-up.
   - Test: unit — mock task manager, assert 409 on already-running.
   - Live: two rapid `POST /Scan` calls; second returns 409.

3. **F-013** — hardlink detection. Windows: P/Invoke `GetFileInformationByHandle` for the
   `FileIndexHigh/Low` pair; Linux/macOS: `stat` via `Mono.Unix.Native.Syscall.stat` OR
   read `nlink` from `.NET`'s `FileInfo` if available. Same-volume + same inode → mark as
   `IsHardlink`; route through `IsSymlink`-style rejection. `BytesFreed` for hardlink pairs
   → 0 (no physical reclaim).
   - Test: platform-specific unit — Windows fixture creates hardlink pair, assert
     `DetectHardlink(a, b) == true`; unrelated files return false. Guard with
     `[PlatformSpecific(OS.Windows)]` if needed.
   - Live: hardlink pair fixture on `%LOCALAPPDATA%\jellyfin\...`, run scan, assert not
     flagged.

4. **F-009** — `MediaSorterFixer.MoveSidecars` gains a sibling-collision check. Walk source
   directory ONCE, build set of other video/audio stems. For each candidate sidecar, refuse
   if a longer prefix stem exists.
   - Test: unit — two-video/one-sidecar-per-video fixture; assert only the moved video's
     sidecars follow.
   - Live: replay F-009 repro; assert the behind-the-scenes .nfo stays put.

5. **F-018** — add a `SemaphoreSlim(1,1)` in `SuspiciousFileScanner` (or gate on `scanTask.State`
   in the controller). Second option is cheaper if the scheduler is the only concurrent
   caller. Live-test both scenarios (scheduled + manual concurrent).
   - Test: unit — mock a scheduled scan mid-flight, call `/Scan/Suspicious`, assert 409.
   - Live: race two calls, assert MalwareRisk table has only one scan's INSERTs.

6. **F-019** — extend the per-issue retry cadence introduced for F-012 to cover every top-level
   FixTask exception handler. Two-tier approach: (a) permission-denied / generic-Exception →
   transition the row to `IssueStatus.Fixed` with an actionable "needs user action" message so
   the retry loop stops (fresh scan re-emits if condition persists); (b) sharing-violation /
   disk-full → increment `failure_count` on the same `issue_retry` shape F-012 introduces, back
   off (double per failure, cap 4h), and set `next_retry_utc`. FixTask filters `WHERE
   status=Queued AND (next_retry_utc IS NULL OR next_retry_utc < now)`.
   - Test: unit — synthesise an Issue whose fixer throws `UnauthorizedAccessException`, run
     one FixTask cycle, assert row transitions to Fixed AND a History row is written naming
     the permission issue. Repeat for `IOException`-sharing-violation → assert failure_count
     increments + next_retry_utc pushed to now+backoff, subsequent immediate FixTask cycle
     skips the row. Assert Diagnostics count doesn't grow unbounded across N repeats.
   - Live: replay F-019's exclusive-lock repro; assert only ONE History row after 3 consecutive
     Fix runs, and the third Fix run reports "skipped: retrying in Nh".
   - Minimum-viable-first: if the full retry-cadence table is Phase 6-late, ship a
     one-line `IsStaleFailure(ex.Message)` check inside each of the five exception catches as
     a Phase 1 defensive patch. Extends the existing keyword-matched short-circuit to cover
     the raised-exception messages ("permission denied", "was locked", "no space left").

7. **F-012** — new `subtitle_lookup_backoff` table. Schema v11.
   - Columns: `(path TEXT, langs_canonical TEXT, provider_set_hash TEXT, attempted_at_utc INT,
     failure_count INT, next_retry_utc INT, PRIMARY KEY(path, langs_canonical, provider_set_hash))`
   - Fixer consults it before searching. Scanner consults it before emitting a MissingSubtitle
     issue.
   - Retry cadence: 1 day → 3 days → 7 days → 30 days.
   - Purge weekly: rows for deleted files.
   - Test: unit — feed a provider-failure result, assert row inserted; assert re-detection
     suppressed within window; assert re-detection allowed after next_retry_utc.
   - Live: replay F-012 emoji-fixture scenario; assert not re-scanned within 24h; assert
     history rows stop accumulating.

**Risk:** varied. F-006 and F-017 are trivial; F-009 needs careful stem-parsing tests; F-013
is P/Invoke (highest risk in Phase 6, keep isolated); F-018 depends on which fix path;
F-012 introduces schema migration + new table (real feature scope).

**Success criteria:** all six findings closed. Torture + migration + full unit suite still
green. Emoji fixture no longer accumulates history rows on 24h re-check.

## Phase 7 — Deployment topology + scale

**Findings:** F-020, F-021.

**Order within phase:**

1. **F-021 first** (foundational): add pagination to `MediaDashController.GetIssues` and
   `MediaDashDb.GetIssues`. Query params `[FromQuery] int limit = 200, [FromQuery] long? afterId = null`.
   SQL becomes `... AND (@afterId IS NULL OR id < @afterId) ORDER BY id DESC LIMIT @limit`. DTO
   gains `NextCursor` + `TotalCount`. UI (`Configuration/configPage.html:8565-8567`) implements
   virtual-scroll or Prev/Next. Same pattern for `/History` (already paginates internally to 500,
   just needs to expose the param). Bonus: halves F-014's crash blast radius (bad row kills one
   page, not the endpoint).
   - Test: unit — inject 500 issues, assert paginated fetch returns exactly `limit` rows and
     `afterId` cursor advances correctly. Regression: existing single-request callers still work
     when they omit the params (defaults).
   - Live: replay session 8's 10K-fixture setup, assert `/Issues?limit=100` returns 100 not 20K.

2. **F-020** (UNC-aware disk-space probing): extend `RecycleBin.FindDriveForPath` to fall back
   to a UNC-share probe when the local-drive iteration returns null. Windows: `GetDiskFreeSpaceEx`
   P/Invoke against the UNC path directly. Linux: `statvfs` on the CIFS mountpoint (should work
   transparently via .NET's existing `DriveInfo` for mounted CIFS shares — the gap is Windows-
   specific). Return a small `IVolumeInfo`-shaped record so callers keep `if (drive is not null)`
   semantics. Every caller now works for both local and UNC paths.
   - Test: unit — mock the P/Invoke, assert non-null return for a UNC path. Regression: local
     drive still returns the same DriveInfo shape.
   - Live: replay session 8's UNC fixture setup, trigger an oversized transcode against a bin
     configured on a nearly-full drive → assert the "not enough space" pre-check fires with the
     friendly message instead of the raw ffmpeg error.

**Risk:** low-medium. F-021 touches DB read paths + API surface + UI — bounded by the "opt-in
via defaults" shape (existing single-request behaviour preserved when caller omits params).
F-020 introduces one P/Invoke on Windows (surrounded by try/catch → null on failure, so no
new crash surface).

**Success criteria:** both findings closed. Big-library scan (10K+ fixtures) hits `/Issues`
without pulling the full result set. UNC library backed by a nearly-full share triggers the
friendly disk-space warning instead of the raw OS error.

**Not blocking earlier phases.** If Phase 6 ships without Phase 7 the plugin continues to work
correctly for the median local-drive small-library user — Phase 7's targets are the "responsive
at scale" and "reliable on NAS" tier that becomes relevant as the plugin picks up production
deployments.

## Regression gate (run after every phase)

Every phase MUST pass these before declaring the phase done:

1. `dotnet build Jellyfin.Plugin.MediaDash.sln` — 0 errors, 0 warnings.
2. `dotnet test --nologo` — 100% pass, count >= 700.
3. `tools/repair-test/torture-test.ps1` — 15/15 variants handled correctly.
4. `tools/audit-migration/migrate.sh` (run through v0 → v10 fixtures) — no data loss on
   Dismissed / Fixed rows through the whole chain.
5. Live smoke on `localhost:8099`:
   - Fresh scan finds the fixtures library's expected issues.
   - `/Status` returns 200 with sensible payload.
   - Auto-approve + fix run finishes without Errors-tab noise.
   - The specific repros for the phase's findings all no longer reproduce.

Log the run in `docs/audit/phase-N-verify.md` with git SHA of the phase's last commit + the
five gate results.

## Files that change per phase (rough estimate)

| Phase | Files touched | Tests added | Migrations |
|---|---|---|---|
| 0 | `CLAUDE.md`, `tools/repair-test/*` | 0 (Phase 1 fills it) | 0 |
| 1 | `Data/MediaDashDb.cs`, `Data/Issue.cs`, `Fixers/PlayabilityFixer.cs` | ~15 | 0 |
| 2 | `Fixers/OrphanCleanupFixer.cs`, `Fixers/PlayabilityFixer.cs`, `Data/MediaDashDb.cs` | ~10 | v10 (migration comment + guard only) |
| 3 | `Data/MediaDashDb.cs`, `Api/MediaDashController.cs` | ~15 | 0 |
| 4 | `Fixers/RecycleBin.cs`, `Api/MediaDashController.cs`, `Api/RecycleBinItem.cs`, `Configuration/configPage.html` | ~10 | 0 |
| 5 | `Scanners/MissingSubtitleScanner.cs`, `Scanners/LanguageHelper.cs`, `Fixers/NfoFixer.cs`, `Scanners/NfoScanner.cs` | ~8 | 0 |
| 6 | `Fixers/LibraryGuard.cs`, `Api/MediaDashController.cs`, `Scanners/DuplicateScanner.cs`, `Fixers/DuplicateFixer.cs`, `Fixers/MediaSorterFixer.cs`, `Fixers/MissingSubtitleFixer.cs`, `Scanners/SuspiciousFileScanner.cs`, `Data/MediaDashDb.cs`, `ScheduledTasks/FixTask.cs`, plus a new P/Invoke helper file | ~25 | v11 (subtitle back-off + generic issue-retry table, combined) |
| 7 | `Api/MediaDashController.cs`, `Data/MediaDashDb.cs`, `Configuration/configPage.html`, `Fixers/RecycleBin.cs`, plus a new UNC-aware volume-info helper | ~10 | 0 |

## Not in scope for this round

- Roadmap items in `tools/roadmap/roadmap.json` (long-term features).
- The DB growth / VACUUM roadmap item (`i-bl-16-plugin-data-growth-controls`) — separate
  workstream, not driven by the audit.
- New UX surfaces beyond the minimum needed to reveal a fix (e.g. F-007 gets a folder icon;
  F-017 gets a "Already scanning" hint; no bigger UX pass).
- Any GitHub / prod deploys — user drives those manually.

## Cross-cutting invariants added to `CLAUDE.md`

Add these under "Hard rules — safety invariants":

6. **The plugin never throws on unexpected DB row shape.** Malformed rows (bad Guid strings,
   non-object DetailsJson roots, out-of-range enum values) are logged with the row id and
   skipped; loading + operating surfaces must return a partial result, never a 500.
7. **Every status-mutating endpoint calls the shared state-machine helper.** Direct
   `UPDATE issues SET status = …` is banned outside `TryTransitionOpenIssue` /
   `BulkUpdateOpenIssueStatus`. This keeps Approve, Dismiss, Revert, and the auto-queue
   consent rollback path from silently disagreeing on which transitions are legal.
8. **Every scanner check has a matching fix-time re-verify.** If a scanner uses
   `MediaFormats.All` to decide "media present", the corresponding fixer's re-verify uses
   `MediaFormats.All` too. Extract shared helpers where duplication drifts.

## Ready-to-start signal

When the user greenlights this plan, work begins at Phase 0. Do not skip phases; do not
reorder without re-analysing the dependency chain (Phase 1 protects the surface Phase 2 needs
stable; Phase 4's refactor depends on the state-machine guard added in Phase 3, etc.).
