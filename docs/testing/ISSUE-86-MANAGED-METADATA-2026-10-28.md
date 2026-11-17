# Issue #86 — managed metadata authorization and recovery

28 October 2026. The remaining orphan-metadata refusal is fixed locally. Managed artwork and orphan metadata were validated on isolated Jellyfin **10.11.11** and **12.0.0**, using a custom metadata root outside the media library. Both owned hosts are stopped. No commit, push, release, existing-server restart or production-media mutation was performed.

## Behavior and scope

The queue previously compared every orphan path against enabled media-library roots. A valid orphan under Jellyfin's managed metadata root therefore never reached its specialized fixer. Detection and deletion also guessed the metadata root from DataPath, ignoring a configured custom metadata location.

- Detection, approval and execution now use the configured `InternalMetadataPath`. Only its exact `library/<first-two-GUID-digits>/<GUID>` item directory can qualify; the issue's nonempty item ID must match that directory. Metadata roots, shard roots, mismatched IDs, descendants and guessed alternate roots are refused.
- Missing-item metadata is a **global cleanup resource**. Its old media library cannot be established once the owner no longer resolves. The explicit metadata toggle and approval policy govern it, and at least one configured library must be enabled. The settings label explains this scope. Any resolving owner, including an owner in a disabled library, prevents orphan treatment. Media-side orphan kinds retain their library-path authorization.
- Lookup failures cannot establish an orphan. Path/link checks run after owner lookup; directory identity, current settings, enabled scope, cancellation and protected descendants are checked before disposal. A returned owner or refused operation keeps queued approval visible.
- History recovery requires the original issue/path binding, a currently valid metadata destination, an owned directory entry in the current recycle root, matching origin provenance and unchanged recovery identity. Source/ancestor/descendant links are refused. Force recovery preserves the displaced directory as its own issue-bound history entry and respects protected descendants.
- Recycling now propagates origin-manifest write failures before moving source content. This shared safety change prevents successful disposal from producing recovery content that lacks the provenance required by restore.

## Frozen candidate

Source digest: `46ea17d7e7f9f6b730fb4ea539c1e9da5a8304d59a1a7d403069cc0fe605fdc3`.

Installed DLL SHA-256 on both hosts: `534D3029C2D70EEB90CBD2A4D3AB50F5D8A2FE20C061FD15BD0074D08DDCBEEB`.

Active plugin status, actual ProgramDataPath and installed DLL hashes were checked for each owned host. The older #80/#81/#83/#88 report remains evidence for its separately identified frozen candidate; those prior live measurements were not silently reassigned to this digest.

## Automated validation

- **225 focused tests passed, zero failed/skipped**, including 28 `OrphanMetadataSafetyTests` cases and existing artwork, approval, orphan scanner, library guard and recycle coverage. Result: `Jellyfin.Plugin.MediaDash.Tests/TestResults/issue86-targeted.trx`.
- Both original #86 orphan regressions changed from failed to passed. The combined tests exercise real stores, controller approval, FixTask/executor, preview, recycling, rescan and History restore under all/selected libraries and custom roots.
- New boundary tests were observed failing before their fixes: cancellation; guessed-root deletion; metadata detection/approval; settings changing during owner lookup; late directory links; restore through a replaced shard; protected force replacement; unrelated or wrong-origin recovery sources; recovery-source replacement; swallowed manifest write failure.
- Final whole-tree suite: **1,747 executed, 1,744 passed, 3 failed, 0 skipped**. UI **45/45** and gate self-tests **19/19** passed. Evidence: `artifacts/reported-issues/8f0bbd8619784b63b6773df15e6c0fef/`.
- Remaining failures: `Issue43_CompanionCollisionMustNotReportACompletedMove`, `Issue92_OutputHeaderFailureMustNotBeClassifiedAsSourceCorruption`, and `Reddit_RefusedMoveStaysVisibleAndDoesNotClaimACompletedFix`.
- Independent bounded security review found configuration/path-ordering, recovery-source binding and provenance-persistence gaps. Each was addressed with a failing regression followed by a passing check. Final source follow-up found no remaining concrete Important issue; the reviewer did not independently execute tests or live hosts.

## Live acceptance

Evidence root: `artifacts/issue-86-live-20261028/`. The previously approved test hosts were reused sequentially on loopback, with exact owned data-directory checks. Each host used one generated four-second H.264/AAC movie and generated metadata fixtures. Its server MetadataPath was temporarily set to an owned custom root, then the original server XML was restored byte-for-byte after shutdown.

| Check | Jellyfin 10.11.11 | Jellyfin 12.0.0 |
|---|---|---|
| Custom-root orphan appears eligible in browser Issues | Pass | Pass |
| All-library orphan scan, single approval and preview | Pass | Pass |
| Selected-library orphan scan, bulk approval and preview | Pass | Pass |
| Exact issue/path preview history; full directory hashes and queue preserved | Pass | Pass |
| Protected descendant prevents real disposal | Pass | Pass |
| Real recycle, matching full directory hashes and rescan | Pass | Pass |
| Force restore of original; displaced replacement remains recoverable | Pass | Pass |
| Occupied-path recovery preserves both original and recovered hashes | Pass | Pass |
| Actual recycle destination failure reports failed history and preserves source/queue | Pass | Pass |
| Managed artwork under custom root, all and selected libraries | Pass | Pass |
| Disabled artwork owner preserves bytes and queued approval | Pass | Pass |
| Actual artwork recycle and exact-byte History restore | Pass | Pass |
| Generated movie unchanged and fully decodable | Pass | Pass |
| Initial settings, protection rules and empty library list restored | Pass | Pass |
| Owned process exited, port released and original server XML restored | Pass | Pass |

![Eligible custom-root orphan on Jellyfin 12](../../artifacts/issue-86-live-20261028/v12/orphan-eligible.png)

Each version folder contains `process.json`, `server.json`, `results.json`, original settings, workflow snapshots, full hashes, exact-ID history, artwork records, failed-recycle history, browser evidence and `cleanup.json`. V10 records 105 passing checks and V12 records 102. Small owned fixtures and recovery records remain as evidence; temporary libraries are removed and no approval remains queued.

One v10 setup attempt assumed a null optional JSON field would be present; the server omits null fields. That harness failure is retained in `results.json`, then the eligibility check passed using the correct optional-field handling. A later harness process hit Windows' default text encoding while reading its own Unicode JSON; UTF-8 was made explicit before any workflow action. These were harness errors, not waived product failures.

## Limits and verdict

Owner-lookup races, source replacement, forged paths, native link boundaries and manifest-write faults have automated fault coverage; they were not all induced on live hosts. The live recycle failure was induced on both hosts. Linux/NAS, production permissions and real disk-exhaustion workflows were not certified. Missing or legacy recovery provenance fails closed and may require manual recovery.

**#86's scoped implementation and local acceptance are complete. Whole-tree verdict: DO NOT PUSH / publish.** Three unrelated regressions remain, and the mandatory gate still rejects uncommitted/incomplete acceptance evidence across the other issue families. No passing whole-tree certification or release claim is made.
