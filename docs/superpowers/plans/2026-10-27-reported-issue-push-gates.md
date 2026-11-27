# Reported-issue push gates

**Goal:** Prevent the reported defects or missing acceptance evidence from producing a successful local push/release check or CI result. This task adds tests and gates, not product repairs.

**Scope:** #80, #81/#83, scan progress/performance, #86, #87, #88, #89, #92, #43 and disappearing issues. Preserve all existing dirty work. Use owned temporary fixtures; do not deploy, restart Jellyfin, commit or push.

- [x] Add desired-behavior tests for XML migration, managed metadata admission, Spanish-original retention, decoder infrastructure failures, artwork lookup failure, output-header classification, partial companion moves and refused-row visibility.
- [x] Add a loaded-history regression and an explicit real-Windows-UNC acceptance test. External tests must fail when their prerequisites are missing; offline runs must report that external acceptance is still required.
- [x] Add one source-bound gate that requires passing .NET/UI tests, nonzero coverage for each issue family, no skipped tests and matching acceptance artifacts for scenarios requiring a live host or reporter media.
- [x] Invoke it from CI, the release script and a locally installed pre-push hook. Refuse dirty or mismatched outgoing source at push time; preserve existing hooks.
- [x] Exercise gate validators against missing/stale/failing/skipped evidence and verify a real local push is refused using an owned temporary Git remote.
- [x] Run the full offline suite, record expected product failures separately from harness defects, and document every remaining external acceptance requirement.

Verification: 1,695 offline .NET tests executed, 1,684 passed and 11 desired-behavior regressions failed, with zero skipped. Dashboard 43/43 and gate self-tests 19/19 passed. The separate UNC acceptance test failed explicitly because no owned UNC test directory was supplied. Final gate run: `artifacts/reported-issues/913b78fd696d483d8f8b600e8ae4554d/summary.json`. Verdict: DO NOT PUSH. Product defects and all external acceptance requirements remain open; this plan's tests/gates deliverable is complete.

**Review focus:** No passing audit of known-bad behavior, no skipped/missing test treated as coverage, no stale evidence, no tests against different code than the outgoing commit, no accidental mutation of production media or existing hooks.
