# Reported-issue push gates

This is a tests-and-gates change, not a repair of the reported product defects. A failing desired-behavior test is an open blocker. Do not skip it, reverse its assertion, filter it out or substitute a green audit that proves the bug still exists.

Run the mandatory gate from the repository root:

```powershell
python -B tools/reported_issue_gate.py
```

It runs the offline .NET suite, the three dashboard test scripts, and gate self-tests, then verifies the source-bound acceptance records. Any native command failure, missing/empty result, skipped test, missing passing issue coverage, source change during testing, or missing/stale/failing acceptance evidence returns exit code 1. Logs and a summary go to a new ignored `artifacts/reported-issues/<run>/` directory.

CI runs this gate on Windows and Linux, including documentation-only changes. The release script runs it before publishing. `tools/test-harnesses.ps1` uses it for its product check; `-SelfTestsOnly` remains a harness check and does not certify push safety.

## Local push enforcement

```powershell
pwsh -NoProfile -File tools/install-push-gate.ps1
```

The installer preserves any existing custom hook path or different pre-push hook and refuses to overwrite it. The installed hook rejects dirty source, tracked files hidden with assume-unchanged/skip-worktree flags, and outgoing branches/tags that do not resolve to the tested HEAD. It reruns the mandatory gate before Git sends objects. Install it in every new clone. Git's `--no-verify`, changing hooks or a separate client can bypass local enforcement; remote enforcement additionally requires these CI checks to be required in GitHub branch/ruleset settings. This task does not change GitHub settings.

## Coverage and remaining acceptance

`tools/reported-issues/requirements.json` maps all ten report families to required passing automated tests and a real-world acceptance scenario. Existing approval, artwork, original-language and history tests remain part of the full suite. New tests cover loaded history, persisted legacy XML, approved metadata admission, Spanish-original retention, native decoder failures, artwork owner-lookup failure, output-header classification, companion partial failure, progress without database/drive reads, and refused rows disappearing as completed fixes.

The real UNC test is intentionally external: no Linux runner or local-drive test can certify Windows NAS behavior. On Windows, point `MEDIADASH_TEST_UNC_ROOT` at an existing owned writable UNC directory with at least 2 KB free, then run:

```powershell
dotnet test Jellyfin.Plugin.MediaDash.Tests -c Release --filter FullyQualifiedName~ReportedUncAcceptanceTests
```

The test creates one uniquely named 1 KB file, exercises both real guard APIs, verifies its bytes and removes only that file. Missing prerequisites fail instead of skipping. The offline gate excludes `Category=ReportedIssueExternal` explicitly and remains blocked until corresponding acceptance evidence exists; exclusion is not acceptance.

The primary #92 media failures and the exact #87/Reddit reports remain unreproduced. Synthetic tests are safeguards, not evidence that those exact reports are resolved. Large-library timing, actual host restart/config migration, metadata ownership, native decoder host versions, NAS encode/restore and mover restart/Undo likewise need owned live fixtures.

## Recording acceptance evidence

For 1.0.8.2, the evidence index records the decision to defer live acceptance for #81 progress, #87, #89, and #92. The deferral applies only to the indexed source fingerprint. The gate reports those cases as deferred and still requires their offline regressions and every other acceptance record.

On 27 November 2026 the owner approved shipping 1.0.8.2 with 11 more cases deferred: #80, #81-83, #86, #88, #43, the Reddit report, and the `media-live-workflows`, `media-fate`, `media-matroska`, `media-hevc` and `media-epub` cases. `media-host-matrix` and `media-docker-smoke` were run against the final candidate. The extra cases are accepted only while `build.yaml` says 1.0.8.2 and the deferral record carries `"version": "1.0.8.2"` (`RELEASE_DEFERRABLE_CASES` in `tools/reported_issue_gate.py`); remove that entry after the release so the next version needs full evidence again.

After the release (which added the recurring-issue check and was published with the gate bypassed by the owner), `media-host-matrix` (testenv run 20261127-194923) and `media-docker-smoke` were re-run against the released source plus the Linux test fix (commit dfb6a37), and the 1.0.8.2 deferral record was re-pointed at that fingerprint.

After running the scenario in `requirements.json` against the final candidate, obtain its code fingerprint:

```powershell
python -B tools/reported_issue_gate.py --fingerprint
```

The fingerprint covers tracked and nonignored source/test/tool/config files, with text line endings normalized for Windows/Linux. Documentation and acceptance records are excluded so evidence can be committed after testing; changing product code, tests, gates or CI invalidates previous evidence. Do not reuse historical source hashes or claim a synthetic test is the reporter's reproduction.

Commit a sanitized result artifact under `docs/testing/reported-issues-evidence/`, containing the actual environment and named assertions/results:

```json
{
  "caseId": "issue-89",
  "sourceDigest": "<candidate fingerprint>",
  "environment": "<actual Windows/Jellyfin/FFmpeg/service-account/NAS environment>",
  "checks": [
    { "name": "<assertion actually executed with its observed result>", "passed": true, "skipped": false }
  ]
}
```

Save artifacts as UTF-8 with LF line endings (enforced by `.gitattributes`). Reference each in `docs/testing/reported-issues-evidence.json` under `cases[caseId]`, with `artifact` set to its repository-relative path and `sha256` set to its SHA-256. Both the index and artifacts must exist in committed HEAD with matching content. All ten issue cases and all media acceptance cases are required. Empty, failed, skipped, mismatched, changed or missing artifacts fail the gate. Review the attestations against raw logs, fixture hashes and cleanup evidence. Never fill a record merely to unblock a push.

## Final media checks

Run these against the intended candidate on owned test systems. Record the installed plugin DLL hash, Jellyfin and FFmpeg versions, and client versions where applicable. For each case, use the exact `requiredChecks` names from `tools/reported-issues/requirements.json` in the evidence artifact. The push hook and release script verify the records without starting hosts or downloading third-party media.

| Case | Run and retain |
|---|---|
| `media-host-matrix` | Prepare the expanded fixtures on the disposable hosts with `tools/testenv/prepare-golden.ps1` and run `tools/testenv/testenv.ps1` across Linux, Docker, and Windows. Retain the manifest, indexed paths, scan and fix results, and rollback/shutdown evidence. Verify that the unknown extension remains unindexed and unchanged. |
| `media-live-workflows` | Follow the [repair harness instructions](../../tools/repair-test/README.md) for `run_formats.py` and `run_live.py` on both Jellyfin versions. Retain full decode, retained-stream, HTTP delivery, history, recycle, refusal and exact restoration results. |
| `media-docker-smoke` | Run the [real Docker smoke workflow](../../.github/workflows/smoke.yaml) for the candidate on Jellyfin 10.11 and 12. Retain both container startup and plugin-load results. |
| `media-fate` | Select [FFmpeg FATE](https://www.ffmpeg.org/fate.html) samples with published references that exercise supported decode or conversion paths. Record the sample hashes, FATE revision and reference verdicts. Use Jellyfin's FFmpeg for the candidate decode and exercise MediaDash on owned copies, checking output streams, refusal and source preservation. |
| `media-matroska` | Run the cue-less seeking and header variation vectors from the [Matroska test suite](https://www.matroska.org/downloads/test_suite.html), plus a known damaged control. Record seeks and MediaDash verdicts separately. |
| `media-hevc` | Select Main and Main 10 bitstreams and decode references from [ITU-T H.265.1](https://www.itu.int/ITU-T/recommendations/rec.aspx?id=13665). Compare decoded outputs to their references using the candidate host's FFmpeg. |
| `media-epub` | Convert an owned supported source through MediaDash with Calibre available. Run [EPUBCheck](https://w3c.github.io/epubcheck/docs/) on the actual output and verify readable content and exact source restoration. |

Pin external sample URLs, revisions and SHA-256 values in the run evidence. Check redistribution terms before committing third-party media. Keep hosted reference streams separate from owned files used for destructive tests.

No product fixes, commits, origin pushes, public releases or Jellyfin deployments are authorized by a passing test or by installing this hook.

## Verification on 27 October 2026

- Full offline .NET suite: **1,684 passed, 11 failed, 0 skipped** (1,695 executed). All failures are the new desired-behavior tests: legacy `DetectOnly` XML loading, four managed-metadata admission cases, two native decoder failures, owner lookup failure, output-header misclassification, companion partial-success reporting and refused rows marked complete.
- Dashboard tests: **43 passed**. Gate self-tests: **19 passed**, including a real rejected push to an owned temporary remote, hook preservation, hidden-index flags, and acceptance artifacts absent from outgoing HEAD. Full Python harness contracts: **56 passed**.
- PowerShell syntax, release source guard self-tests and whitespace checks passed; existing line-ending notices remain.
- External UNC acceptance: explicitly failed for missing `MEDIADASH_TEST_UNC_ROOT`; no NAS or live Jellyfin was accessed. All ten external acceptance records remain unverified.
- Current local pre-push hook is installed. Final gate output: `artifacts/reported-issues/913b78fd696d483d8f8b600e8ae4554d/summary.json`, exit **1 / DO NOT PUSH**. No product repairs, user-repository commits, origin pushes, deployments or releases occurred.
