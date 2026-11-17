# Ordered repair follow-up — 29 October 2026

The local fixes remove the three previously failing reported-issue regressions while preserving successful cleanup workflows. **This is partial implementation and local validation, not issue closure or release certification. DO NOT PUSH / PUBLISH.** Durable mover recovery/Undo, stronger orphan-lifetime evidence, reporter media, remote NAS/service-account/quota checks and large-library scan timing remain open.

Latest #86 implementation and validation: [persisted orphan confirmation](ISSUE-86-CONFIRMATION-2026-10-29.md), including 1,805 passing offline tests on both Windows and Ubuntu. The earlier gate results below retain their original candidate fingerprint.

Latest continuation: [move containment, refresh and timing follow-up](ISSUES-CONTINUATION-2026-10-29.md). Fresh checks passed **1,835 Windows offline tests**, **1,838 Linux tests including three actual cross-filesystem move cases**, and **five generated-media/UNC tests with each FFmpeg bundle**. These are scoped local results; durable journaling/Undo and bounded folder indexing remain open.

Latest isolated mover implementation: [durable moves, explicit previews, Undo and 31 October continuation](ISSUES-MOVER-2026-10-30.md). The canonical user-data candidate passed 2,050 analyzed Windows tests, 279 Linux tests and fresh complete grouping/Files Move/Rename/Undo workflows on both 10.11.11 and 12.0.0, including watched state, new favorites, native log consistency and full playback/hash/decode checks. Both owned hosts are restored and stopped. The later ownership candidate passed 2,054 Windows tests, 283 Linux tests and five actual SMB alias cases. New crash and Move/Restore alias checks are in progress; the media-kind/fault/platform/browser matrix, bounded indexing, final review and safe integration remain incomplete. This implementation is not transferred into the original checkout.

The earlier offline continuation performed no commit, push, GitHub mutation, plugin deployment, Jellyfin server start/restart or production-media mutation. The subsequently authorized local-host tests are recorded separately below. The existing concurrent dirty tree was preserved. Generated media tests invoke the current plugin services and bundled FFmpeg directly under the .NET test runner; they do not certify the installed plugins or either Jellyfin host ABI.

Latest authorized live acceptance: [30 October isolated-host workflows](ISSUES-LIVE-2026-10-30.md). The frozen candidate completed controlled issue workflows on 10.11.11 and the available 12.0.0 distribution, plus one-million-row History and 1,000-file cold/warm scan checks. Both owned servers are stopped and cleanup is verified. Raw logs retain earlier retry/setup failures; these are scoped results, not universal closure. New mover implementation is isolated in `artifacts/remaining-issue-repairs-worktree` and is not covered by those frozen-candidate results.

## Guardrails must preserve working workflows

The 1.0.8.0 source at immutable commit `9b006379794daa0def3ee0eac23c0f2e8e7593ec` rejects `DetectOnly` for every fix intent, including explicit approvals. That supports the approval/execution mismatch diagnosis, **not** the claim that every configuration or run fails. Current legacy XML migration, approval/executor/history and policy tests are retained. No global safety bypass was added.

## Work in the requested order

| Order | Issue | Current local result | Remaining work |
| --- | --- | --- | --- |
| 1 | #86 metadata deletion | Persisted directory/image observations and a successful host scan started after detection now gate real metadata disposal. Queued approval survives the wait; valid permanent/recycle cleanup and restoration pass. Live authorization and all child states are rechecked after hashing. Linux identity probing is repaired. | Fresh default/custom-root and all/selected-library recycling/restore acceptance now passes on both available local hosts, including restart and changed-content refusal. Other operating environments and atomic exclusion of external writers are not certified. See the confirmation follow-up for evidence and limits. |
| 2 | #92 MKV failures | Output-header errors containing generic invalid-data text no longer suppress normal error diagnostics as source corruption. Specific demux/decode corruption signatures still win. Both failure classes are exercised with real FFmpeg and unchanged-original checks. | Exact reporter MKVs, full inventories, codec-specific stderr and matching Debian/Jellyfin runtime remain unavailable. Controlled failures prove preservation/reporting, not a codec repair of the reporter files. |
| 3 | #43 and Reddit disappearance | Complete inventories, explicit previews, typed invalidation, durable recovery and separate Undo are implemented in the isolated worktree. Fresh owned 10.11.11/12.0.0 native grouping and Files Move/Rename/Undo preserve bytes, companions, watched state and new favorites; full playback/hash/decode and native log checks pass. Actual Linux cross-filesystem execution and owned Windows SMB alias checks pass. | The newest ownership/crash/Move-Restore alias candidate still needs matching native acceptance. Browser, complete family/non-video native workflows, remaining OS faults, bounded indexing, final review and integration remain open. Earlier frozen-candidate evidence stays separate. Exact Reddit reproduction and remote NAS certification remain unavailable. |
| 4 | #87 original audio | Controlled metadata/flag and metadata-failure coverage retained. A generated Spanish-original file completes detection, approval, actual cleanup, rescan, recycle and exact-byte restoration. Video, Spanish audio and retained-subtitle packet hashes are unchanged. | Exact Rafa verification remains open without reporter stream flags and Jellyfin item/series metadata. Offline coverage uses controlled inherited Spanish metadata; the fresh live file carries an actual Spanish original flag. Neither establishes that the reporter metadata is correct. |
| 5 | #89 UNC/NAS | Real space guards, re-encode, retained stream inventory, full decode, original recycle hash and exact-byte restoration pass through an owned loopback SMB path. | The current interactive process is not the Jellyfin service account. Remote NAS permissions, quotas, service credentials and production LXC remain unverified. |
| 6 | #81 20 TB slowdown | INFO phase timings now separate item materialization, preparation, each scanner and result persistence, including completed/failed/cancelled outcomes. Source tracing still identifies full duplicate hashing and per-audio decode work. Fresh installed-host measurements separately verify one million History rows and 1,000 generated audio/artwork scans: 33.27/0.48 s cold/warm on 10.11, 35.22/0.71 s on the available 12 distribution. | A corrected 1,000-file resource run measured 31.19s with empty plugin cache and 0.56s warm; host CPU averaged 12.57% and peaked at 32.8% during the uncached run, observed Jellyfin-tree memory peaked at 423 MiB. This small fixture is not 20 TB acceptance. Dynamic CPU/RAM/disk-pressure backoff and pinned-system behavior remain unimplemented/unverified. See [resource evidence](SCANNER-RESOURCES-2026-10-30.md); optimize a measured large-library bottleneck next. |
| 7 — lowest priority | Targeted refresh after fixer runs | **Partial implementation.** Changed existing files now receive deduplicated individual-item refreshes with forced stream probing; unchanged/refused/dry-run attempts do not refresh. Partial changes survive later failure/cancellation. Moved/deleted locations resolve to affected existing parents, with deduplicated native notifications. Refresh failures cannot strand fix-run state. | Native Jellyfin folder-change notifications still recurse: a root-level move/deletion may walk the library. Bounded folder indexing is still required, including existing fixer notifications. Individual-file stream metadata is now verified live after an actual UNC encode. Bounded old/new folder indexing and unrelated-sibling checks remain open. See the continuation report for the confirmed upstream behavior and acceptance limits. |

## Reddit reference

User-provided project thread, saved on 29 October 2026: [MediaDash Plugin on r/jellyfin](https://www.reddit.com/r/jellyfin/s/GfFB9aPKiq). Canonical URL: [the MediaDash Plugin thread](https://www.reddit.com/r/jellyfin/comments/1v518up/mediadash_plugin/); [newest comments first](https://www.reddit.com/r/jellyfin/comments/1v518up/mediadash_plugin/?sort=new).

This is the source thread for the Reddit-disappearance work in item 3. The retrieved thread includes JayGerard's report of briefly flashing progress, disappearing Issues entries, unchanged files and an empty recycle bin on Linux Mint. That report remains evidence of symptoms, not proof of the underlying cause or a successful repair. The individual comment permalink timed out when fetched separately; the report was readable in the main thread. Other comments and historical maintainer replies are context to verify against current source and controlled tests.

## #86 scope and positive coverage

The shared `LibraryGuard` still checks the configured custom root, exact shard/GUID directory, item identity, links, enabled-library scope and owner absence. The new flat-content rule recognizes Jellyfin's internal artwork names, including indexed backdrop/screenshot names and the existing image formats. Unknown provider/plugin layouts stay intact for review. Upstream naming reference: [Jellyfin ImageSaver](https://raw.githubusercontent.com/jellyfin/jellyfin/v10.11.8/MediaBrowser.Providers/Manager/ImageSaver.cs).

Deletion-content eligibility is separate from recovery authorization. A recovered original may still land beside an occupied GUID directory containing unrelated new files, without moving or overwriting those files. An independent review caught this overbroad restore restriction; a regression was observed failing, the rule was separated, and the test passed.

The tests prove both directions: refusal of unrelated contents, repository disagreement/failure, returning owners and active refresh; and successful preview/approval, permanent disposal, recycling and restoration for valid metadata. Existing protection, source-replacement, link, custom-root and configuration-change tests remain in coverage. Disposal preference and global dry-run are preserved.

Initial new failure evidence: `Jellyfin.Plugin.MediaDash.Tests/TestResults/issue86-safety-red.trx` (8 failed cases), `issue86-recovery-red.trx`, and `issue86-library-refresh-red.trx`. The focused green run contains 97 passing tests with no failures/skips. The final full gate also includes these checks. This does not replace fresh live acceptance on supported hosts.

## Real media checks

New opt-in tests: [ReportedMediaWorkflowAcceptanceTests.cs](../../Jellyfin.Plugin.MediaDash.Tests/ReportedMediaWorkflowAcceptanceTests.cs). Missing prerequisites fail rather than skip. The standard offline gate excludes the existing `ReportedIssueExternal` category explicitly; these checks were run separately.

- **#92 output header:** generate an MP4 payload with a timecode data track under a `.mkv` filename. Its actual Matroska rebuild fails to write a supported output. The original remains byte-identical and fully decodable, approval stays queued, savings are zero, and a diagnostic is bound to the exact fixture path. This controlled container/extension mismatch is not asserted to be the reporter cause.
- **#92 retained inventory:** generate a healthy MKV, inject an inconsistent cached subtitle-language inventory, then invoke the actual track fixer/executor/recorder. Strict retained-track verification refuses replacement; original hash and queued approval survive. The stale-inventory injection is named explicitly and is not reporter-media evidence.
- **#87 success:** generate H.264 video, English/Spanish AAC and a hearing-impaired Spanish subtitle. Controlled inherited original language is Spanish. Actual cleanup removes only English audio. Hashes of the retained packet payloads, retained stream metadata/dispositions, full decode, rescan, original recycle bytes and force restoration are asserted.
- **#89 success:** use actual `TranscodeFixer` and replacement/recycle/history/restore services on an owned ordinary UNC path. The original and replacement differ, expected streams survive, replacement fully decodes, recycled original hash matches and restoration is exact.

Owned SMB parent: `\\localhost\C$\dev\mediadash\artifacts\issue89-owned-unc-20261029`. No share or system service was created or changed. Each test creates and removes its own unique directory; the parent is empty after the runs. This exercises actual Windows SMB path handling on the local host, not a remote NAS.

FFmpeg bundles exercised:

| Bundle | Actual FFmpeg version | Test runner |
| --- | --- | --- |
| Local Jellyfin 10.11.11 distribution | `7.1.4-Jellyfin` | .NET 9 / Windows |
| Local Jellyfin 12 distribution | `8.1.2-Jellyfin` | .NET 9 / Windows |

Run these explicitly against an owned UNC parent:

```powershell
$env:MEDIADASH_TEST_FFMPEG = '<absolute path to Jellyfin ffmpeg.exe>'
$env:MEDIADASH_TEST_UNC_ROOT = '<existing owned writable \\server\share\test-folder>'
dotnet test Jellyfin.Plugin.MediaDash.Tests --filter 'FullyQualifiedName~ReportedMediaWorkflowAcceptanceTests|FullyQualifiedName~ReportedUncAcceptanceTests'
```

The tests restore the prior in-process plugin singleton, clear only fixture-related diagnostics, close test database handles and remove only their generated directories. No shared server configuration/queue is involved.

## Earlier gate and remaining acceptance

Final candidate fingerprint: `9bae230c4307be026a4354fed453b606f8197f766264fa10d6c71e4516d89b50`.

| Verification | Observed result |
| --- | --- |
| Full offline .NET suite, including #80, #81/#83 and #88 | **1,767 passed, 0 failed, 0 skipped** |
| All three original reported-issue failures | **Passed**: output-header classification, companion collision completion, refused-move visibility |
| Dashboard checks | **45 passed, 0 failed/skipped** |
| Gate self-tests | **19 passed** |
| Real-media/UNC checks with FFmpeg 7.1.4-Jellyfin | **5 passed, 0 failed/skipped** |
| Real-media/UNC checks with FFmpeg 8.1.2-Jellyfin | **5 passed, 0 failed/skipped** |
| Owned UNC fixture parent after both runs | **Empty** |
| Whitespace check for changed tracked source/test/gate files | **Passed** |
| Complete acceptance gate | **Exit 1 — DO NOT PUSH** |

Final gate: [summary](../../artifacts/reported-issues/1a6cb652280140478bb460143bc98d48/summary.json), [.NET results](../../artifacts/reported-issues/1a6cb652280140478bb460143bc98d48/reported-issues.trx), [UI output](../../artifacts/reported-issues/1a6cb652280140478bb460143bc98d48/ui.log), [gate self-tests](../../artifacts/reported-issues/1a6cb652280140478bb460143bc98d48/gate-self-tests.log). It reports no product-test failure or source drift; its blockers are the uncommitted acceptance index and unverified candidate-specific acceptance cases. This task does not fabricate records or commit the user's tree to satisfy that check.

Real-media evidence: [7.1.4-Jellyfin bundle run](../../Jellyfin.Plugin.MediaDash.Tests/TestResults/ordered-real-media-v10-ffmpeg-final.trx) and [8.1.2-Jellyfin bundle run](../../Jellyfin.Plugin.MediaDash.Tests/TestResults/ordered-real-media-v12-ffmpeg-final.trx). Raw artifacts are local ignored files, not committed acceptance attestations.

The final loaded-history measurements were **8.9 ms** for 80,000 history rows / 1,000 requested issue IDs, and **32.4 ms** for 1,000,000 rows / 2,000 IDs. These verify the #80 lookup path on this machine; they are not measurements of a 20 TB media scan. The #81 hypotheses above still require per-phase timing, cold/warm cache comparison, file counts/sizes, selected scanners and I/O/CPU measurements on an owned representative corpus.

Independent read-only review found no universal refusal of valid operations. The restore/content separation finding and a misleading preflight diagnostic were corrected. The reviewer did not run tests or live hosts. Refresh/owner checks remain snapshots and mover recovery remains manual; those limitations are not waived by the green regression suite.

Prior #80/#81/#83/#88 and #86 live reports remain historical evidence for their own frozen candidates. Their hashes and acceptance claims are not reassigned to this changed candidate. The larger [media mover plan](../superpowers/plans/2026-10-23-media-mover-repair.md) remains the source of unfinished recovery/Undo work.
