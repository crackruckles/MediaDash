# Orphan Metadata Confirmation Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan task-by-task. Preserve the concurrent dirty tree; no commits, pushes, deployment or server restarts are authorized.

**Goal:** Prevent an owner lookup gap from immediately authorizing metadata deletion, while allowing confirmed orphan cleanup to finish.

**Architecture:** Store a versioned folder observation in the existing issue details, alongside the existing detection timestamp. Before real metadata disposal, require an unchanged observation and a successful Jellyfin library scan that started after detection and has finished. Existing live owner, scope, policy, protection, link and cancellation checks remain. Preview and recovery do not require a new confirming scan.

**Tech Stack:** Existing C#/.NET 9, SQLite issue details, System.Text.Json, TimeProvider and Jellyfin task APIs. No schema change, new dependency, user option or automatic full-library scan.

**Spec:** User priority order and valid-workflow requirement in `docs/testing/ORDERED-REPAIRS-2026-10-29.md`.

## Global Constraints

- Honor the configured disposal method and dry-run; do not silently substitute quarantine for permanent disposal.
- Preserve queued approvals while confirmation is pending. An old issue without a folder observation needs rescan/reapproval, not a fabricated observation at deletion time.
- Confirmation is a completed successful host scan, not a delay, another immediate lookup, a failed/cancelled scan or a pre-detection scan.
- Snapshot directory/file identity, size and modification state; unknown/unreadable or changed contents must remain in place.
- Keep safe History restoration working without a fresh host scan.
- Keep #80, #81/#83 and #88 in full regression coverage; targeted refresh remains the last TODO.

## Review Focus

- A temporarily absent owner returns during the confirming scan: no removal, queued approval survives.
- True orphan remains absent: same approval can preview, defer, then actually recycle/delete after confirmation; recycle restoration works.
- Candidate or child file changes between detection and fixing, including equal-size replacements: no removal.
- Restart and repeated MediaDash scans cannot manufacture confirmation or discard the queued observation.
- Missing task manager, malformed/legacy details, stale/failed/cancelled/future task results: preserve bytes and explain the required next action.

### Task 1: Observation and confirmation with workflow regressions

**Files:** Add `Data/OrphanMetadataObservation.cs`; modify `Scanners/OrphanCleanupScanner.cs`, `Fixers/LibraryGuard.cs`, `Fixers/OrphanCleanupFixer.cs`; extend `OrphanMetadataSafetyTests.cs`. All paths are under the existing plugin/test project directories.

- [x] Add failing real-files/SQLite tests for pending confirmation, returning owner, changed candidates and successful post-confirmation cleanup/restore.
- [x] Capture the observation during detection, validate it at disposal and read the existing persisted host task result through the injected task manager.
- [x] Update valid-workflow fixtures to perform the confirming host-scan step explicitly.
- [x] Run focused tests; request bounded independent review and resolve concrete findings.
- [x] Run the complete reported-issue gate on frozen source and record actual results and remaining acceptance gaps.

Ruling: use the existing persisted issue observation and host task result rather than a new quarantine or database table — preserves disposal preferences and avoids new maintenance/state while requiring a later confirming scan.

Ruling: repair the shared Unix identity argument handling — the new scanner relies on that existing helper, and an owned Linux regression proved that its old invocation failed.

Final results: Windows and Ubuntu offline suites 1,805/1,805 each; UI 45/45; gate self-tests 19/19. Acceptance gate remains DO NOT PUSH for uncommitted/missing acceptance evidence. Details and residual concurrent-writer limits are in `docs/testing/ISSUE-86-CONFIRMATION-2026-10-29.md`.
