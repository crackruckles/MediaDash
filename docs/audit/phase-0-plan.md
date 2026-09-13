# Phase 0 — Plan (Setup + tooling)

Goal: codify the "malformed DB row must not crash a surface" discipline so Phase 1's
fixes have a documented invariant they defend. NO plugin source changes.

## Work items (one commit each)

- [ ] **P0-1** — Add cross-cutting safety invariants #6, #7, #8 to `CLAUDE.md` under
  "Hard rules — safety invariants". Text lifted from `fix-plan.md` §"Cross-cutting
  invariants added to `CLAUDE.md`".
  - Commit: `phase-0/setup: add DB-shape, state-machine, scanner-fixer-parity invariants`

- [ ] **P0-2** — Add `tools/repair-test/torture-malformed-rows.ps1` — a sibling to
  `torture-test.ps1`. Injects 5 malformed rows via sqlite, hits `/Status`, `/Issues`,
  `/Fix`, asserts all return 2xx and don't throw. Baseline: MUST fail against current
  build (that's Phase 1's regression test).
  - Rows: bad guid `item_id`, `details = 'null'`, `details = '[1,2,3]'`,
    `details = '42'`, `details = '{"reason":[1,2]}'`.
  - Commit: `phase-0/setup: torture-malformed-rows harness (baseline failing test)`

## Regression gate (write to phase-0-verify.md after both commits)

1. Build clean (`dotnet build /warnaserror`) — 0 errors, 0 warnings.
2. Unit suite green (`dotnet test --nologo`) — ≥ 700 pass.
3. BBB torture (`torture-test.ps1`) — 15 / 15.
4. Migration harness (`tools/audit-migration/migrate.sh`) — no data loss.
5. Live smoke: `/Status` responds; `torture-malformed-rows.ps1` FAILS as baseline.
