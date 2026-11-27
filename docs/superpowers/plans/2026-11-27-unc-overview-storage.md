# UNC Overview storage implementation

User request: adapt Icabaad's MediaDash PR #91 into the current build.

Design: preserve the PR's distinct, case-insensitive UNC share extraction and network-share presentation. Extend the current directory-based free-space helper to return total capacity without reverting the #89 repair. Probe configured Windows UNC roots in the background with a 30-second cache, one measurement per share and four concurrent measurements overall. Never wait in the dashboard request. Keep the last good reading, label stale values, and omit shares that have never answered. Keep separate shares separate; disclose potential overlap in totals instead of inferring disk identity from free-space similarity. Do not query SMART for shares. Existing warning configuration applies.

Implementation plan (using writing-plans and test-driven-development):

- [x] Add regression tests for UNC roots, nonblocking/cache/retry/concurrency behavior, disk-space measurement, and storage rows/warnings/totals.
- [x] Adapt PR #91 helpers and integrate them into StatusQueryService, preserving concurrent local edits.
- [x] Adapt the Overview badges, stale indication, overlap note, and SMART exclusions; test rendered output.
- [x] Run focused tests, Release build, ordinary .NET suite and UI checks. Attempt disposable-host validation where available, recording environment limits honestly. Results: `docs/testing/PR-91-UNC-OVERVIEW-2026-11-27.md`.

Scope: local source/build only; no commit, push, publication, production deployment or media mutation. Native and container drive handling remains in place. Preserve all unrelated dirty-tree work.

Review focus: hung shares must not consume unbounded workers; distinct disks must never be merged by capacity; device/local/Linux paths must not enter the Windows UNC probe; failed probes must not become zero-capacity successes; stale readings and totals must be clear to users.
