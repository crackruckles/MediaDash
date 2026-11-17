# MediaDash test harnesses

Reported-issue push/release gate: `python -B tools/reported_issue_gate.py`. See [requirements and evidence rules](../docs/testing/REPORTED-ISSUE-PUSH-GATES.md). It runs the offline product/UI suites and rejects missing acceptance evidence. Install the local pre-push hook with `pwsh -NoProfile -File tools/install-push-gate.ps1` in each clone.

Run the offline gate from the repository root with PowerShell 7:

```powershell
pwsh -NoProfile -File tools/test-harnesses.ps1
# Faster harness-only check; explicitly omits .NET:
pwsh -NoProfile -File tools/test-harnesses.ps1 -SelfTestsOnly
```

Every invocation creates a new evidence directory under `artifacts/harness-tests`. `summary.json` records every child exit code and log. Missing tools and failed suites return nonzero; live integration is explicitly recorded as NOT RUN. Dependencies: .NET 9, Node, Python 3.11+, PowerShell 7 and Bash (Git Bash on Windows).

| Harness | Purpose | Execution boundary |
|---|---|---|
| `reported_issue_gate.py` | All ten reported-issue families, required passing test coverage, source identity and committed acceptance evidence | Included by CI, release and the full offline runner; missing live/NAS/reporter evidence blocks |
| `ReportedUncAcceptanceTests` | Both real free-space APIs on an owned Windows UNC file | Explicit external run; never a silent skip or a local-drive substitute |
| .NET test project | Production logic, SQLite migration, safety and regression tests | Disposable files/DBs; included in offline gate |
| Three `comprehensive-test/ui-*-self-tests.js` scripts | Dashboard logic and lifecycle regression tests | Node stubs; included; browser visuals/playback remain separate |
| `comprehensive-test/self-tests.ps1` | Result accounting, live guards, cleanup ownership, history and backup integrity | Stubbed dependencies; included |
| `comprehensive-test/task-wait-self-tests.ps1` | Fast completion, stale completion, failure and rejected task requests | Stubbed API; included |
| `repair-test/validator-self-tests.ps1` | Offline pre/post repair validator verdicts | Temporary files and decoder shims; included |
| `release-self-tests.ps1` | Release source/staging refusal checks | Temporary Git repositories; included; does not publish |
| `repair-test/test_*.py` and format self-test | Python harness isolation, cleanup, fixture-source and integration-probe contracts | No server; included |
| `make-fixtures.sh` | Base comprehensive/integration media generation | New output directory; actual ffmpeg required |
| `comprehensive-test/run-full-tests.ps1` | Multi-phase Jellyfin acceptance | Explicit live opt-in, matching data directory, fixture-only instance; see its README |
| `repair-test/run_live.py` / `torture-test.ps1` | Repair ladder, history, recycle, decode, streaming, rescan and restore | Explicit live opt-in; see repair README and COVERAGE.md |
| `repair-test/run_formats.py` | 89 common-format fixtures and acceptance flows | `--generate-only` is offline; live mode requires opt-in |
| `repair-test/regenerate.ps1`, `validate-fixtures.ps1`, `validate-outputs.ps1` | Nine legacy corrupt/healthy fixtures and offline decode/hash checks | Generation writes fixtures; validators do not establish current-run server provenance |
| `live-recycle-tests.ps1` | Recycle/restore and collision endpoints | `-ConfirmLive -DataDir <active data dir>`; owns a unique scratch directory |
| `integration-test.sh` / `integration_probe.py` | Disposable Docker startup, indexing and scan-only acceptance | Docker plus host ffmpeg; bounded waits and exact container ownership |
| `.github/workflows/smoke.yaml` | Plugin loading under Jellyfin 10.11 and 12 containers | Docker startup/page smoke only, no repair acceptance |
| `audit-migration/migrate.sh` | Production schema regression tests | Runs `SchemaUpgradeTests` on disposable DBs; historical SQL/DBs are archived evidence |
| `repair-test/torture-malformed-rows.ps1` | Malformed-row production regression tests | Runs isolated .NET tests; no longer injects into an active server DB |
| `seed-subtitle-test.ps1` | Manual font fixture seeding helper | Explicit live opt-in and owned library directory; not an automated pass/fail gate |
| `comprehensive-test/restore-fixtures.ps1` | Restore/verify independent fixture backups | Validates paths and backup/restored hashes; use `-Verify` for read-only checks |
| `comprehensive-test/simulate-doctor-who` | Historical standalone explanation/reproduction | Copies old logic; not a regression gate or proof about current production code |

`deploy-local.ps1`, `reset-jellyfin-v10.ps1`, `configure-libraries.ps1` and `release.ps1` are operational tools, not tests. The offline gate never invokes them.

A passing harness self-test proves the harness contract under its simulated conditions. Current-build live acceptance still requires real fixture detection, approval, fix, history, recycle, playback/streaming and restoration on the supported server/platform matrix. Skipped cases are incomplete coverage, never release certification.
