# MediaDash — agent guide

Project rules, architecture and safety invariants are in `CLAUDE.md` and `PLAN.md`. Read them first;
they apply to every agent. This file covers the **test environment**: three disposable Jellyfin
hosts you can break freely, because every run rolls them back to a known-good snapshot.

## The test hosts

| Alias | Platform | Jellyfin | Media root (as Jellyfin sees it) |
|---|---|---|---|
| `mdtest-linux`   | Debian 13 container, native package | 12.4    | `/srv/media` |
| `mdtest-docker`  | Debian 13 container, official image | 10.11.x | `/media` (host path `/srv/media`) |
| `mdtest-windows` | Windows Server 2025 VM, service as NetworkService | 12.4 | `C:\mdtest\media` |

Together they cover both plugin ABIs (10.11 and 12), Linux, Docker and Windows path handling.
Each host has a `golden` snapshot: fixture media, MediaDash installed and configured, a completed
Jellyfin scan and a baseline MediaDash scan.

## The loop

Always run the scripts with **PowerShell 7** (`pwsh`), never `powershell.exe`.

```powershell
./tools/testenv/testenv.ps1 -PreflightOnly     # check credentials, SSH, Proxmox and snapshots (~10 s)
./tools/testenv/testenv.ps1                    # build this working tree, test on all three hosts
./tools/testenv/testenv.ps1 -Hosts linux       # one host (fastest: ~2-3 min)
./tools/testenv/testenv.ps1 -ScanOnly          # detection only, no fixes
./tools/testenv/testenv.ps1 -Types Playability,CorruptNfo   # approve/fix only these types
./tools/testenv/testenv.ps1 -Hosts windows -KeepState       # leave the host running to inspect
```

Each run builds the plugin from **the current working tree** (never the published release),
then per host: rolls back to `golden` → deploys the build → seeds fresh transcode logs → scans and
compares with the fixture manifest → approves → runs a Jellyfin library scan → runs the fix task →
rescans → collects results → rolls back and stops the host. Hosts run one at a time because the
Proxmox server is short on RAM.

Exit codes:

| Exit | Meaning | What to do |
|---|---|---|
| 0 | No problems | Done |
| 1 | MediaDash problems: missing/unexpected issues, failed fixes, a build error, a plugin that will not load | Read `summary.md`; these are about the code |
| 2 | The **test environment** is broken: credentials, SSH, Proxmox, missing snapshots, or another run in progress | Do not change MediaDash code for this. Report the message; it says what is wrong and how to fix it |

Every run starts with a preflight that checks the environment before touching any host, and holds
a lock (`artifacts/testenv/testenv.lock`) so two agents cannot test on the shared hosts at once. If
you hit "another test-env run is in progress", wait; do not delete the lock unless that process is
genuinely gone. A full three-host run takes about 8 minutes; if your command timeout is shorter,
run one host at a time (`-Hosts linux`, then `-Hosts docker -SkipBuild`, then `-Hosts windows -SkipBuild`).

Results land in `artifacts/testenv/runs/<timestamp>/` (written even when a run stops early):

- `summary.md`: start here. Per-host counts and every problem in one list.
- `<host>/scan-compare.json`: matched / missing / unexpected / known-gap issues against the manifest.
- `<host>/fix-issues-by-status.json`, `fix-history.json`, `fix-recycle-bin.json`: what the fixers did.
- `<host>/rescan-issues.json`: what is still detected after fixing.
- `<host>/mediadash-errors.json`: MediaDash's own Errors-tab diagnostics.
- `<host>/jellyfin-mediadash.log`: the host's recent MediaDash log lines (plus `jellyfin-on-error.log` if a step failed).

A summary line `RUN ERROR during <step>` means MediaDash or its API misbehaved during that step;
the host is still rolled back afterwards.

Typical iteration: change code → `testenv.ps1 -Hosts linux -Types <the type you touched>` →
read `summary.md` → repeat → finish with a full `testenv.ps1` across all hosts before you call it done.

## Reading results honestly

- **MISSING** — a fixture MediaDash should have flagged and didn't. A regression unless you changed
  the detection rule on purpose (then update the fixture and manifest, see below).
- **UNEXPECTED** — an issue on a file the manifest says is clean or a different type. Usually a
  false positive.
- **KNOWN GAP** — an expectation MediaDash is known not to meet today. Each has its reason in
  `artifacts/testenv/manifest.json`. If you fix one, the comparison starts matching it; then remove
  the `KNOWN GAP:` prefix from that note in `make-fixtures.sh`.
- **Expected failures** — fix outcomes that are correct for this environment (no subtitle provider,
  no Calibre, unrepairable zero-byte file). Listed under `fixExpectations` in the manifest.
- **FAILED / STILL DETECTED after fix / queued but not attempted** — real fix problems.

Never "fix" a test by weakening the manifest to match wrong behaviour. If you believe an
expectation is wrong, say why in your summary instead of editing it silently.

## Changing fixtures or host setup

- Fixtures are generated by `tools/testenv/make-fixtures.sh` (one deterministic tree plus
  `manifest.json`). Use `expect` for detection expectations and `fixfails` for expected fix failures.
- After changing fixtures, the test config or a host's Jellyfin version, rebuild the snapshots:
  `./tools/testenv/prepare-golden.ps1` (~12 min; all three baselines must show 0 missing and
  0 unexpected before you rely on them).
- `deploy-plugin.ps1` is what testenv uses to put a build on a host; you rarely need it directly.

## Debugging on a host

Use `-KeepState`, then `ssh mdtest-linux` / `ssh mdtest-docker` / `ssh mdtest-windows`
(key auth is set up; the Windows shell is PowerShell).

| | Plugins | Jellyfin logs | Metadata / trickplay |
|---|---|---|---|
| linux   | `/var/lib/jellyfin/plugins` | `/var/log/jellyfin` | `/var/lib/jellyfin/metadata`, `/var/lib/jellyfin/data/trickplay` |
| docker  | `/srv/jellyfin/config/plugins` (container: `jellyfin`) | `/srv/jellyfin/config/log` | `/srv/jellyfin/config/metadata`, `.../data/trickplay` |
| windows | `C:\ProgramData\Jellyfin\Server\plugins` | `C:\ProgramData\Jellyfin\Server\log` | `C:\ProgramData\Jellyfin\Server\metadata`, `...\data\trickplay` |

Jellyfin's web UI is at `http://<host>:8096` (`.150` linux, `.151` docker, `.152` windows). The
next `testenv.ps1` run rolls the host back, so anything you change by hand is temporary.

## Hard rules

- **Only touch the three `mdtest-*` hosts.** Production Jellyfin and the rest of the homelab share
  the same Proxmox server and network. Never SSH to, scan, or call the API of any other host.
- Credentials live in `%USERPROFILE%\.mediadash-testenv\` (`pve.env`, `jellyfin.env`,
  `windows.env`). Never print their values, copy them into the repo, or commit them.
  `artifacts/` is git-ignored; keep it that way.
- The Proxmox token can only power-cycle, snapshot and roll back the `mediadash-test` pool. Do not
  try to widen it, and do not delete or rename the `base`, `configured` or `golden` snapshots
  except through `prepare-golden.ps1`.
- MediaDash analytics are switched off in the test config. Keep them off: test runs must never
  reach the public install stats.
- Leave hosts rolled back and stopped when you finish (the default unless you passed `-KeepState`).
  If a run reports `cleanup failed`, tell the user: that host may need rolling back to `golden` by hand.
