# Phase Execution Runbook

One-agent-per-phase workflow. Each agent runs until the phase is done OR context runs out;
if the latter, a fresh agent picks up cold from the state files.

## Files each phase produces

- **`fix-plan.md`** — the source of truth for what the phase must do. The agent updates its
  own phase section with checkboxes (`- [ ]` → `- [x]`) as it lands each finding's fix.
- **`docs/audit/phase-N-plan.md`** — the agent's granular todo, one item per commit or
  intermediate step. Created at start-of-phase; updated after every commit.
- **`docs/audit/phase-N-verify.md`** — regression-gate results (build, tests, torture,
  migration, live smoke). Written when the phase is complete.

## Resumption rule

A fresh agent starting a phase:

1. Reads `fix-plan.md` for its phase section.
2. Checks whether `docs/audit/phase-N-plan.md` already exists. If yes → resume from the
   first unchecked item. If no → create it from the fix-plan.
3. Reads recent git log with `git log --oneline -20` to see what already landed. Any
   commit prefixed `phase-N/` is from a prior agent in this phase — check it off in
   phase-N-plan.md if not already.
4. If `phase-N-verify.md` exists AND all five gates are green → the phase is done. Stop
   and report "Phase N was already complete."

## Working style within a phase

- **One commit per finding fixed.** Message format: `phase-N/F-NNN: <one-line summary>`.
- **Test-first.** For every fix: (a) add a failing test that pins the bug; (b) run the
  suite and confirm it fails; (c) land the fix; (d) run the suite and confirm it passes.
  Commit test+fix together.
- **After every commit**: run `dotnet test --nologo` and confirm ≥ 700 pass. If red, stop
  and diagnose. Do NOT stack a second fix on a broken suite.
- **After every commit**: `dotnet build /warnaserror`. Zero warnings.
- **After the phase's last finding fix**: run the full regression gate (see below) and
  write `phase-N-verify.md`.

## Root-cause discipline

The audit findings include a `Suggested fix` line. Treat it as a SHAPE HINT, not a directive.
Before writing code:

1. Reproduce the bug locally (deploy the current build, exercise the repro from the finding).
2. Understand WHY it happens — is the finding's suggested fix addressing the surface symptom
   or the underlying cause?
3. Grep the codebase for the same pattern elsewhere. If N call sites share the anti-pattern,
   fix them all in the same commit (or a series of commits with a shared helper).
4. Land the fix that eliminates the class of bug, not just the specific instance.

Example: F-014 (`Guid.ParseExact` throws on malformed item_id) is filed against `GetIssues`
and `GetIssue`. Root cause = "every DB read that Guid-parses without TryParse throws". Fix =
sweep every `Guid.ParseExact(reader.GetString(...), ...)` and swap to TryParse, not just the
two named sites.

## The regression gate (run before writing phase-N-verify.md)

Five checks. All must be green. Failure means DO NOT declare the phase done — either fix
the failure or roll back the phase's last commit.

1. **Build clean**: `dotnet build Jellyfin.Plugin.MediaDash.sln /warnaserror` → 0 errors, 0
   warnings.
2. **Unit suite green**: `dotnet test --nologo /consoleloggerparameters:NoSummary` → 100%
   pass, count ≥ 700.
3. **BBB torture green**: `powershell -File tools/repair-test/torture-test.ps1` → 15/15
   variants handled correctly.
4. **Migration harness green**: `bash tools/audit-migration/migrate.sh` → all migration
   fixtures pass without data loss.
5. **Live smoke on localhost:8099**:
   - Deploy: `powershell -File tools/deploy-local.ps1`
   - Restart Jellyfin (kill process, start-jellyfin-v10.bat)
   - `GET /MediaDash/Status` → 200
   - `POST /MediaDash/Scan` → auto-fix cycle on the fixtures library — no Errors-tab noise
     related to this phase's changes
   - The specific repros for the phase's findings all NO LONGER reproduce

## Verify file template

```markdown
# Phase N — Regression Gate Verification

- Completed: <YYYY-MM-DD HH:MM UTC>
- Git SHA of last phase commit: <sha>
- Sessions used: <N>

## Findings closed
- F-NNN — <status: fixed | deferred with reason | rejected>
- ...

## Regression gate
- [x] Build clean (0 errors, 0 warnings)
- [x] Unit suite green: X / Y pass
- [x] BBB torture: 15 / 15
- [x] Migration harness: <output summary>
- [x] Live smoke: <what was tested, what proved the fixes>

## Notes
<Any deviations from the fix-plan, systemic surprises, or follow-ups worth filing.>
```

## Between-phase handoff

When a phase agent finishes and returns its summary, the orchestrator (parent conversation):

1. Reads `phase-N-verify.md`.
2. If all five gates green → spawn Phase N+1 agent.
3. If any gate red → spawn a Phase-N continuation agent to fix the gap.
4. If findings had to be deferred → note it in the summary to the user.

## Hard rules (unchanged, restated)

- Local Jellyfin `http://localhost:8099` (test / test) is fair game.
- **Never touch `192.168.1.117`** (production).
- **Never post to GitHub.** No `gh` mutations.
- **Never push.** Commits stay local until the user pushes.
- Fixture data under `C:\dev\mediadash-fixtures\` is fair game; production media is not.
