# Fixers not running: GitHub triage and local correction

Checked 17 October 2026. Local source changes tested in a new disposable loopback-only Jellyfin host. No deployment to an existing server, GitHub comment, commit, push or release.

## User reports

Screened all 77 repository issues and 199 issue comments for fixer/approval/no-work reports, then read the relevant threads. This included both open and closed issues.

| Report | Evidence | Conclusion |
| --- | --- | --- |
| [#81, reporter's latest comment](https://github.com/crackruckles/MediaDash/issues/81#issuecomment-6218243652) | Dry-run was off. Types set to Detect only did nothing; changing them to Ask me first made fixes work. | Confirms an approval/execution policy mismatch. |
| [#80, follow-up after 1.0.8.1](https://github.com/crackruckles/MediaDash/issues/80#issuecomment-6127995035) | Buttons respond after the hotfix, but repeated fix runs complete in zero seconds without plugin errors. | Task registration/dispatch works on that server. Consistent with an empty or blocked queue; its exact settings are unavailable, so the same cause is not proven. |
| [#57](https://github.com/crackruckles/MediaDash/issues/57) | Generic unexpected pipeline failure. | Insufficient exception evidence to assign the same cause. |
| [#59](https://github.com/crackruckles/MediaDash/issues/59), [#56](https://github.com/crackruckles/MediaDash/issues/56), [#19](https://github.com/crackruckles/MediaDash/issues/19), [#39](https://github.com/crackruckles/MediaDash/issues/39) | Timestamp permission failure, incompatible ffmpeg output codec/header, a locked file, and output-duration verification rejection. | These reached actual fixer work; they are distinct from the immediate no-op. Not claimed resolved by this patch. |
| [#76](https://github.com/crackruckles/MediaDash/issues/76) | Reporter describes media disposal despite DetectOnly. | Read-only mode must remain a safety boundary. Do not restore the older behavior by allowing every existing queued row to bypass it. |

The [#43 grouping report](https://github.com/crackruckles/MediaDash/issues/43#issuecomment-6157116014) remains separate. Its companion-file/undo defects are not fixed here.

## Root cause and correction

`FixPolicyEvaluator` refuses DetectOnly, Off and muted fixers during execution. Single-row approval and both bulk approval routes previously accepted those same types and returned success. The UI then promised that queued items would run. The new endpoint tests reproduced all three mismatches before changes (7 failures).

The controller now uses the existing evaluator for type-level approval eligibility and returns a readable conflict for blocked approval. Issues include `FixBlockedReason`, so detected rows offer Settings and existing blocked queued rows explain the blocker. Settings are not changed automatically; current queued rows remain intact. Switching the relevant type to Ask me first and saving requires no manual rescan. The existing configuration-change service automatically rescans affected types and can replace a Detected issue ID; refresh Issues before approving. Execution retains its independent library, protection, consent and current-settings checks.

Bulk approval counts only eligible detected rows, retains the per-row warning-consent gate, and reports partial approval or errors. Warning rendering also handles the backend's explicit lowercase JSON field names, so needs-review rows are visibly explained and excluded from the bulk selection.

Starting fixes now reports a missing or busy task instead of false success. Jellyfin's `ITaskManager.Execute` returns a task representing the whole run: immediate dispatch faults return an error, while later faults are observed and shown through Diagnostics and the current-run note without holding the HTTP request open. Failed dispatch clears the one-shot manual bypass. The client handles start/retry/approval failures and reads both fetch and XHR error bodies while retaining page-lifetime suppression.

The existing local #80 no-work summary changes were retained and tested. They explain zero-work runs rather than discarding queued work or inferring media corruption.

## Verification

- Regression tests cover DetectOnly/Off/muted approval, reevaluating eligibility after a mode change, both bulk routes, existing queue explanations, mixed-library consent preservation, warning consent, dismissal, missing/busy tasks, synchronous and asynchronous dispatch failures. The controller test does not model the separate configuration-change rescan service; the live test below covers that behavior.
- A fixture integration test runs approval through the real FixTask planner, executor and history recorder with an instrumented dry-run fixer. The owned source file is unchanged, the issue stays queued after preview, and changing back to DetectOnly prevents a subsequent dispatch.
- UI tests execute blocked-row rendering, updated eligibility after a settings refresh, bulk filtering, approval rejection, startup failure, error-body reading, and stale-page suppression.
- `dotnet test Jellyfin.Plugin.MediaDash.Tests --no-restore -c Release -v quiet`: **1,609 passed, 0 failed, 0 skipped**. This builds the Release plugin with its normal analyzers before running the suite.
- `node --test tools/comprehensive-test/ui-regression-self-tests.js tools/comprehensive-test/ui-lifecycle-self-tests.js`: **39 passed, 0 failed** (38 regression tests plus the lifecycle self-test harness).
- `git diff --check`: passed; only existing line-ending conversion notices were printed.

## Isolated live follow-up

The current Release plugin was installed into a newly created Windows Jellyfin **12.0.0** host at `127.0.0.1:18181`, using its bundled FFmpeg **8.1.2**, generated media, a dedicated data directory and a disposable account. Source and installed DLL SHA-256 both matched `6A29A117C467459DD36734F49C75960D3C0C9498EAB7FDD50BC217F9008465E3`. The plugin UI reports local assembly version `0.0.0`; this is build evidence, not release certification.

**41 live checks passed, zero failed**, covering:

- A real scan of generated video with English and Spanish audio, plus rejection of all three approval routes under DetectOnly.
- A blocked run leaving the source and history unchanged; switching to Ask me first, refreshing the issue after the automatic rescan, and approving successfully.
- Dry-run recording the exact issue/path in History, preserving source bytes and retaining the approval.
- Actual TrackFixer execution removing only the Spanish track, preserving decoded video frames and producing a fully decodable file.
- An original recovery copy with matching SHA-256, complete Jellyfin stream-response hashing against the fixed file, and a clean rescan.
- Restore recovering the original byte-for-byte and passing full decode; fixture libraries, approvals and configuration cleaned up afterward.

A separate browser check verified the DetectOnly explanation and Fix settings action, changed the owned fixture to Ask me first, saved, approved from Issues, clicked Run fixes now, and observed the new preview in History. Database attribution confirmed that preview belonged to the exact UI fixture issue and path; its source hash remained unchanged.

The live harness's held-queue restoration was also updated for the stricter approval API: restore approvals under dry-run, excluded-library settings and approvable modes before restoring the original configuration. Informational Stale approvals are refused before setup changes because they cannot be reapproved. **23 harness tests passed**, and a live hold/restore check recovered the UI fixture's exact approval ID/path without changing media or history. The final backend wording change passed **28 targeted approval/no-work tests** and a Release publish.

Evidence retained locally:

- `C:/dev/mediadash-fix-e2e-20261017/evidence-final/summary.json` and `results.json`: 41-check run, with history, status, configuration and ffmpeg logs alongside.
- `C:/dev/mediadash-fix-e2e-20261017/identity-final.json`: loaded/source DLL identity.
- `C:/dev/mediadash-fix-e2e-20261017/ui-evidence/detect-only.jpg`: visible blocker and Settings action.
- `C:/dev/mediadash-fix-e2e-20261017/queue-restore-evidence/results.json`: browser preview attribution and held-queue restoration.
- `C:/dev/mediadash-fix-e2e-20261017/ui-evidence/results.json`: final fixture cleanup and source preservation.

Limitations: no affected Ubuntu/Debian Jellyfin 12.2 server reproduction or production deployment. The real-media test exercises the shared approval path and audio-track fixer; it does not certify every fixer or establish that all historical failures share this cause. The separate #43 grouping defects remain unresolved here.
