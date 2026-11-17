# Issue #86 — persisted orphan confirmation

29 October 2026. The next unresolved priority was #86's short-lived owner-absence gap. The local implementation now requires a persisted folder observation and a later successful Jellyfin library scan before real orphan-metadata disposal. **Local implementation and automated validation passed; live host acceptance and release certification remain open. DO NOT PUSH / PUBLISH.**

No commit, push, GitHub mutation, deployment, Jellyfin start/restart or production-media mutation was performed. The concurrent dirty checkout was preserved. The targeted end-of-fixer-run refresh TODO remains item 7, last and lowest priority; that behavior was not changed here.

## Behavior

1. MediaDash scans the configured metadata root and records an observation for an eligible orphan. Existing exact root/shard/GUID binding, enabled scope, absent-owner and cache-only-content checks still apply.
2. Approval remains available. Preview preserves bytes and queue state without requiring a confirming host scan.
3. A real cleanup attempt stays pending until Jellyfin's **Scan Media Library** task successfully completes a run that **started after** the finding was detected. An earlier, overlapping, failed, cancelled, aborted, future-dated or unrelated task result cannot confirm it. Missing/unreadable task evidence also defers cleanup.
4. The owner must still be absent, and the original directory and images must match the persisted observation. Real cleanup then honors the configured permanent/recycle disposal setting. No automatic full library scan, quarantine override, delay setting or new dependency was added.
5. A legacy or changed observation needs the existing **Undo** approval action, a new MediaDash scan and approval, then a confirming Jellyfin scan. The endpoint/rescan path is tested through to successful cleanup.

For a new candidate: **MediaDash scan → approve/preview → Jellyfin Scan Media Library → Run fixes**. The same queued approval is retained while waiting. History restore does not require another confirming scan and retains its existing provenance, scope and protection checks.

## Implementation and scope

- `Data/OrphanMetadataObservation.cs` stores a versioned signature in existing issue details. It binds the detection timestamp, directory physical identity and modification state, sorted filenames, file physical identities, sizes, modification times and image bytes. No database schema change is needed. Equal-size edits with restored timestamps and replaced directories invalidate the observation.
- `OrphanCleanupScanner` captures observations at detection. The suggestion explains the host-scan requirement. A queued finding survives later MediaDash scans, so another scan cannot silently replace the approved observation or manufacture an older detection time.
- `LibraryGuard.HasConfirmingMetadataScan` uses the injected task manager and existing `TimeProvider` registration. It requires the `RefreshLibrary` worker to be idle and its matching result to be successful with valid timestamps. Jellyfin 10.11's worker persists this task result and key: [upstream implementation](https://raw.githubusercontent.com/jellyfin/jellyfin/v10.11.8/Emby.Server.Implementations/ScheduledTasks/ScheduledTaskWorker.cs). Fresh behavior on an actual Jellyfin 12 host was not certified.
- `OrphanCleanupFixer` validates authorization before the full read and again after it. Returning owners, a new refresh, changed configuration/protection, cancellation and changed directory identity prevent disposal. Preview remains sticky when settings change during the attempt.
- All retained child states and the exact directory entry set are rechecked after the full hash and after the final authorization pass. The final check does not read every image's bytes again.
- The Unix identity helper previously passed the multiword GNU `stat` format as separate arguments, making the probe fail. It now uses `ProcessStartInfo.ArgumentList` for the format and path. This shared repair also reaches duplicate, artwork and restore safety callers. Owned Linux directories and filenames containing spaces, quotes and backslashes are covered.

The observation hashes only eligible candidate metadata images, not media-library payloads. It adds reads proportional to those candidates; production NAS performance has not been measured.

## Failure evidence and review

Before implementation, **14 of 15** new confirmation tests failed as expected. A separate timestamp-backdating regression also failed before its binding check. Independent review then found stale authorization during hashing, an earlier-child mutation window and the Linux `stat` invocation defect.

Six late-state regressions failed before correction. The final matrix exercises changes at both the first and second task-result reads, including the pass after hashing. A child-edit regression changes an earlier image during final authorization and verifies that all bytes survive. The Linux identity regression was observed failing on Ubuntu before the argument fix, then passing afterward. Review follow-up found no remaining concrete Important issue or universal valid-run blocker in the changes; review itself did not execute tests or live hosts.

Failure artifacts are under `Jellyfin.Plugin.MediaDash.Tests/TestResults/`: `issue86-confirmation-red.trx`, `issue86-backdated-red.trx` and `issue86-late-state-red.trx`. Linux failure and passing results are copied under `artifacts/issue86-confirmation-linux-20261029/`.

The first full Linux run had 14 source-contract test failures because the isolated source copy omitted the solution file used by their repository-root lookup. The solution marker was copied, with no product-code or test-assertion changes; the full run was repeated successfully. That initial failure result is retained under `linux-full/`, alongside the final `linux-full-final/` result.

## Final validation

Candidate fingerprint: `28a940809a41ac3b5abe4d76c0b7f99149b935dd5826aa1e1504142d4cd630eb`.

| Check | Observed result |
| --- | --- |
| Full Windows offline .NET suite | **1,805 passed, 0 failed, 0 skipped** |
| Full Ubuntu 26.04 LTS / WSL offline .NET suite | **1,805 passed, 0 failed, 0 skipped** |
| Dashboard checks | **45 passed, 0 failed/skipped** |
| Gate self-tests | **19 passed** |
| Existing #80, #81/#83, #88 and three previously failing reported-issue regressions | Included and passed in both full .NET runs |
| Changed tracked product source whitespace check | Passed |
| Complete reported-issue acceptance gate | **Exit 1 — DO NOT PUSH** |

Windows gate evidence: [summary](../../artifacts/reported-issues/1686bc5fe5f948d4b5c08c091b72009d/summary.json), [.NET result](../../artifacts/reported-issues/1686bc5fe5f948d4b5c08c091b72009d/reported-issues.trx), [UI checks](../../artifacts/reported-issues/1686bc5fe5f948d4b5c08c091b72009d/ui.log), [gate self-tests](../../artifacts/reported-issues/1686bc5fe5f948d4b5c08c091b72009d/gate-self-tests.log).

Linux evidence: [raw results and source-copy verification](../../artifacts/issue86-confirmation-linux-20261029/). The isolated copy is `/home/crackruckles/mediadash-confirmation-dr8uovkr` in Ubuntu. It contains only copied source/build outputs and generated test fixtures; no live Jellyfin instance was involved. The two project source trees were checked byte-for-byte against the Windows checkout. These are local ignored artifacts, not committed acceptance attestations.

The complete gate reports no .NET/UI failure or source drift. It remains blocked by the uncommitted acceptance index and required candidate-specific acceptance records. Prior live reports belong to their own frozen candidates and are not reassigned to this fingerprint.

## Limits and remaining order

Completed host-task evidence plus persisted source binding is stronger than repeated immediate null lookups. It does not lock Jellyfin's database and filesystem together: an uncoordinated external writer can still race the final checks and disposal. A host scan reporting success is not proof of every possible external importer's state. No atomic cross-process deletion guarantee is claimed.

Fresh supported-host workflow acceptance, real remote NAS/service-account/permission/quota behavior, and production performance remain unverified. The opt-in real-media external tests were not rerun because this change adds metadata confirmation and repairs filesystem identity; they are excluded explicitly from the offline suites, not counted as passing acceptance.

Next in the original order is #92's remaining reporter-equivalent MKV repair work, followed by #43/Reddit durable companion recovery and Undo. The exact reporter inputs and larger recovery work remain open; this change does not close those issues or authorize a release.
