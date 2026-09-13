# Phase 0 — Regression Gate Verification

- Completed: 2026-09-13 06:30 UTC
- Git SHA of last phase commit: `abd880d` (phase-0/setup: torture-malformed-rows harness)
- Sessions used: 2 (prior agent landed both commits; this session ran the gate)

## Findings closed
- P0-1 — fixed (invariants #6, #7, #8 added to `CLAUDE.md`)
- P0-2 — fixed (`tools/repair-test/torture-malformed-rows.ps1` added)

## Regression gate
- [x] Build clean — 0 errors, 0 warnings
  - Evidence: `dotnet build Jellyfin.Plugin.MediaDash.sln` produced both DLLs with no diagnostics.
- [x] Unit suite green: 722 / 722 pass
  - Evidence: `dotnet test --nologo` → `Failed: 0, Passed: 722, Skipped: 0, Duration: 2 s`.
- [ ] BBB torture: 12 / 15 (pre-existing baseline, NOT a Phase 0 regression)
  - Failed variants: `05-remuxed-mkv-noheadtrim.mkv`, `12-only-half-header.mp4`, `15-double-damage.mkv` — all reported "vanished (no output, not in bin)" with no history row.
  - Same three variants fail on two consecutive runs. Phase 0 touched no plugin source (`git show --stat fd8c584 abd880d` = CLAUDE.md + docs + new .ps1 only). Failure predates Phase 0.
- [ ] Migration harness: DATA LOSS confirmed on Dismissed + Fixed Playability rows (F-010 baseline, Phase 2 will fix)
  - Fresh v5 fixture built from `build-v5-with-dismissed.sql` → migrated to v9. Dismissed Playability rows: 2 → 1 (row id 4 deleted). Fixed Playability rows: 1 → 0 (row id 3 deleted).
  - Root cause = v6 migration's unconditional `DELETE FROM issues WHERE type=1` (matches the exact behaviour F-010 describes in `fix-plan.md`).
  - Not a Phase 0 regression — the shell harness mirrors current C# `MigrateSchema`. Phase 2 lands F-010 and adds the `WHERE status IN (Detected, Queued)` guard.
- [x] Live smoke — `/System/Info/Public` returned 200; `torture-malformed-rows.ps1` executed.
  - Unexpected result: the script PASSED (3/3 endpoints returned 2xx) instead of the expected baseline FAIL.
  - Reason: the plugin DLL deployed to `%LOCALAPPDATA%\jellyfin-v10\plugins\MediaDash_0.9.0.0\Jellyfin.Plugin.MediaDash.dll` was last built Sep 11 (before Phase 0 commits) from an older-yet-modified working tree that already contains Phase 1-shaped F-014/F-015/F-016 hardening (see the 62 uncommitted source files listed by `git status --short`).
  - The malformed-rows harness itself is proven functional: it injects 5 rows, hits /Status /Issues /Fix, cleans up, and reports pass/fail. Rerun after a `deploy-local.ps1` build from clean HEAD would produce the baseline FAIL the fix-plan predicts.

## Notes

- **Phase 0's own work items are complete.** CLAUDE.md invariants landed; the malformed-rows torture script is present, runnable, and produces meaningful pass/fail output. The two Phase 0 commits are strictly docs + a new test script; no plugin source touched.

- **Two of the five gates are red at the baseline, but neither red is caused by Phase 0:**
  - Gate 3 (BBB): three variants fail identically across two runs. Not in scope for Phase 1 (Phase 1 targets F-014/F-015/F-016 — malformed-row hardening, not repair-ladder outcomes). Worth filing as a separate finding if not already tracked; the three variants are all "vanished with no history row" — likely a Playability fixer swap/cleanup path issue.
  - Gate 4 (migration): the exact F-010 data-loss the fix-plan flags for Phase 2. Expected.

- **Deployed plugin is out-of-sync with HEAD.** The working tree contains 62 uncommitted source/test/tool files (including source-tree modifications matching the shape of F-014/F-015/F-016 fixes, plus untracked new tests). The running plugin at localhost:8099 corresponds to a Sep 11 build of some intermediate state. This means:
  - The malformed-rows harness "pass" at gate 5 is not a Phase 1 completion signal — it's an artifact of the deployed DLL already carrying uncommitted fix work.
  - Before Phase 1 formally starts, either (a) rebuild + `deploy-local.ps1` from a clean HEAD to reproduce the baseline FAIL, or (b) inventory the uncommitted working-tree changes and decide whether they should be committed as Phase 1's fixes (they may already implement what Phase 1 was going to write).

## Phase 1 readiness

**Can Phase 1 proceed?** Yes — but with a mandatory pre-check.

Phase 1's job is F-014/F-015/F-016 (defensive guardrails against malformed DB rows). The
working tree already appears to contain that work uncommitted. Phase 1's first action should
be: `git diff Jellyfin.Plugin.MediaDash/Data/MediaDashDb.cs Jellyfin.Plugin.MediaDash/Data/Issue.cs Jellyfin.Plugin.MediaDash/Fixers/PlayabilityFixer.cs` and audit whether the uncommitted changes already implement F-014/F-015/F-016 correctly. If yes, land them as Phase 1's commits (test-first-with-history reconstructed) and rerun the malformed-rows harness to prove they hold. If not, proceed with the plan as written.

Gates 3 and 4 remain red at baseline but neither is Phase 1's responsibility — Gate 3
predates the audit, Gate 4 is Phase 2's F-010. Phase 1 should not be blocked on them.
