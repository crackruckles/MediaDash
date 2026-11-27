# Issues table continuation — 29 October 2026

The local continuation repairs additional move safety defects, replaces unconditional end-of-run library refreshes with individual-file refreshes and affected-folder notifications, and adds scan-phase timing. **The issues table is not complete. DO NOT PUSH / PUBLISH.** Durable move journaling, complete Undo, approval-bound payload planning, bounded folder indexing and reporter/environment acceptance remain open.

No commit, push, GitHub mutation, plugin deployment, Jellyfin start/restart or production-media change was performed. Existing concurrent changes were preserved. GitHub #92 and comments for #92/#87 were refreshed read-only: there are still no reporter comments supplying the missing media/stream inventories. This is a statement about those fetched threads, not about files the user may hold elsewhere.

## Repairs and their limits

### #43 / disappearance: move containment

The shared cross-device helper now captures uncached SHA-256 payload evidence and physical identities, including empty directories. It verifies source and copied payload before publication, verifies the published payload before capturing the source under a unique recovery path, and rechecks captured objects before disposal. Unknown volume identity uses verified copying. Sorter, grouper file moves, companion file moves and the Files Move endpoint now route cross-volume files through that helper; relying on an EXDEV exception from `File.Move` was insufficient because .NET can silently copy/delete a file across volumes.

Recorded members alone may be removed from staging/recovery. Directories are removed bottom-up **without recursive deletion**. A concurrent new child therefore remains for review. Cleanup also verifies staging ownership; it preserves replacement payloads and a staging copy that may be the only survivor. Cancellation and failures preserve surviving payloads and name recovery paths in diagnostics.

Protection covers companions and manual same-volume moves. The sort/grouper refuse an empty resolved library selection instead of treating it as universal authorization. A longer primary title owns its matching artwork before a shorter title can claim it. Shared move checkpoints re-read actual queued approval, current policy, enabled libraries and protection; dismissal, disabling a fixer, protection changes and cancellation during copying stop source disposal.

New tests first failed for same-size changed source/copy, new entries after copying, changed published copies, sole-survivor cleanup, foreign staging replacement, a child added to captured recovery, longer-title companion ownership, protected companions and an empty enabled-library match. A misleading old test that could pass via an ordinary successful same-volume move was removed. The replacement tests execute the real helper and explicit failure checkpoints.

Independent review reproduced the captured-recovery child-loss race with a separate controlled harness. That finding led to inventory-bound, non-recursive disposal. Review also identified bypassing callers and stale approval checks; those were corrected. Final source review reported no further actionable safety finding in the changed code. It did not certify the deferred mover contract or run the final suite itself.

**Still open:** the complete versioned identity/ownership planner, approval-bound file inventories, durable manifests/claims, startup reconciliation and complete Undo. A late companion failure can still leave a partial bundle requiring manual recovery. These changes do not establish the exact Reddit cause. Uncoordinated external writers can still race path-based final checks; atomic cross-process exclusion is not promised. Cross-volume permissions/attributes and Windows cross-drive/NAS behavior are not certified by the Linux fixture tests.

### End-of-run refresh / #84

`FixLibraryRefresher` captures candidate paths before each actual attempt. Successful changes and detected partial changes contribute targets to the run; unchanged failures, refusals, no-change results and dry runs contribute none. File identity, existence, size and modification time help identify partial changes during exceptions/cancellation. Completed changes still contribute refresh work when a later attempt or recording fails.

Existing files receive a deduplicated `QueueRefresh` for the individual item, using `FullRefresh` and `ForceSave` without replacing all metadata/images. This preserves forced stream probing when source timestamps are retained. The plugin does not await remote metadata providers while holding the fix-run state. Missing/moved paths resolve to existing parents; folder notifications are deduplicated. Library lookup failure retains a known owning item, and a failed notification or queue request cannot prevent processing another target. Run state is completed in a nested `finally`.

**This is partial implementation of the targeted-refresh row.** Jellyfin's native folder-change path calls `ChangedExternally`, queues a folder refresh, then recursively validates children. A root-level deletion/move can therefore still trigger a recursive library scan. Removing the plugin's unconditional collection-folder refresh does not satisfy the remaining bounded-folder-indexing requirement. Existing fixer notifications can enter the same native path. Current-source host tests for metadata, indexing and unrelated siblings remain required.

Upstream behavior: [FileRefresher](https://raw.githubusercontent.com/jellyfin/jellyfin/v10.11.11/Emby.Server.Implementations/IO/FileRefresher.cs), [ProviderManager](https://raw.githubusercontent.com/jellyfin/jellyfin/v10.11.11/MediaBrowser.Providers/Manager/ProviderManager.cs), [Folder.ValidateChildren](https://raw.githubusercontent.com/jellyfin/jellyfin/v10.11.11/MediaBrowser.Controller/Entities/Folder.cs).

### #81: slowdown evidence

INFO logs now time item materialization, preparation, each scanner and scanner-result persistence separately. Scanner records include elapsed milliseconds, candidate count, finding count and completed/failed/cancelled outcome. These measurements preserve the existing scan order, hashes and decode checks; they are diagnostic instrumentation, not a claimed scan-speed improvement.

No representative 20 TB cold/warm corpus was supplied. Compare these phases, selected scanners, file counts/sizes and host I/O/CPU on an owned corpus before selecting an optimization. The unit tests validate phase attribution and cancellation/failure reporting; they do not establish large-library throughput.

## Fresh verification

| Check | Observed result |
| --- | --- |
| Initial Windows offline baseline | 1,805 passed, 0 failed/skipped |
| Final Windows offline suite | **1,835 passed, 0 failed/skipped** |
| Source-bound Release .NET suite | **1,835 passed, 0 failed/skipped** |
| Final Ubuntu / WSL suite, including real cross-filesystem dispatch | **1,838 passed, 0 failed/skipped** |
| Real FFmpeg 7.1.4-Jellyfin media + loopback UNC workflows | **5 passed, 0 failed/skipped** |
| Real FFmpeg 8.1.2-Jellyfin media + loopback UNC workflows | **5 passed, 0 failed/skipped** |
| Dashboard regression checks | **45 passed, 0 failed/skipped** |
| Python harness self-tests | **56 passed** |
| Offline harness suite groups | **11 passed, 0 failed** |
| Mandatory acceptance gate | **Exit 1 — DO NOT PUSH** |
| Changed tracked-file whitespace check | Passed |

The Linux run uses the identical portable plugin/test DLLs copied from the Windows build, with the two source trees and solution copied into the expected test layout. This exercises the actual Linux runtime and filesystem APIs, not a Linux source compilation. The three additional cases dispatch real sort, group and Files moves from the owned `/tmp` fixture onto the distinct `/dev/shm` filesystem. Sort/group checks include subtitle companions and zero savings; the Files endpoint intentionally moves only the selected file, with complete media-bundle semantics still in the deferred plan.

The first Linux binary-only layout caused 21 source-contract test failures because source files/solution were absent. The corrected isolated source layout passed the complete run; those setup failures were not converted into passes or excluded. An earlier shell-launch quoting error was corrected by running a saved script with explicit temporary-path validation. No server or system configuration was changed.

Both FFmpeg runs invoke current plugin services with generated fixture media, including #92 header/inventory refusal, #87 Spanish-original retained packet hashes and exact restoration, and #89 actual UNC transcode/recycle/decode/restore. These remain controlled tests, not reproduction of the exact reporter MKVs, Rafa's metadata, a remote NAS service identity, or the Jellyfin 12.2 ABI.

Owned SMB parent: `\\localhost\C$\dev\mediadash\artifacts\issues-continuation-owned-unc`. It is empty after both runs. No share/service was created or altered. Linux test workspace: see the saved workspace record below; only copied source/build outputs and generated fixtures are used.

Verified Debug DLL SHA-256 values, matching Windows and Linux:

```text
f17a327cfa23d663240a03376ef169939daea48c86dec9b5d3543214912721d6  Jellyfin.Plugin.MediaDash.dll
cfa357c64e356bc2899bb635c9065a502ec5316de27ff45b770d551fd9f2b667  Jellyfin.Plugin.MediaDash.Tests.dll
```

Evidence:

- [Windows final .NET results](../../Jellyfin.Plugin.MediaDash.Tests/TestResults/issues-continuation-final.trx)
- [Linux results](../../artifacts/issues-continuation-linux/issues-continuation-linux.trx), [DLL hashes](../../artifacts/issues-continuation-linux/sha256.txt), [owned workspace](../../artifacts/issues-continuation-linux/workspace.txt)
- [FFmpeg 7.1.4 media/UNC](../../Jellyfin.Plugin.MediaDash.Tests/TestResults/issues-continuation-real-media-v10.trx), [FFmpeg 8.1.2 media/UNC](../../Jellyfin.Plugin.MediaDash.Tests/TestResults/issues-continuation-real-media-v12.trx)
- [Harness summary](../../artifacts/issues-continuation-harness/summary.json)
- [Read-only reporter refresh](../../artifacts/issues-continuation-reporter-refresh.json)
- [Source-bound Release gate](../../artifacts/issues-continuation-gate/summary.json)

These raw artifacts are local ignored evidence. They do not replace committed, candidate-specific acceptance records or authorize release.

Final gate source fingerprint: `56777e04af0538d17710e2af69ab57a7a4d3cdff0238f13d0f994ee256f2d0fd`. The gate's Release suite, UI checks and self-tests pass with no source drift or product-test failure reported. It remains blocked because the acceptance index is not committed in `HEAD` and every required candidate-specific acceptance case remains unverified. This continuation does not commit the dirty tree or fabricate attestations to satisfy that gate.

## Remaining work order

1. Finish the existing [mover plan](../superpowers/plans/2026-10-23-media-mover-repair.md): immutable payload/identity approval plans, durable journal/claims, restart reconciliation and complete safe Undo. Do not certify #43/Reddit from the containment checks.
2. Run fresh #86 host acceptance on owned supported Jellyfin hosts, including the confirming host scan and restoration; current offline safety tests pass on both OSes.
3. Obtain the actual #92 inventories/files and #87 inherited metadata/stream flags; controlled fixtures cannot establish their reporter-specific causes.
4. Verify #89 remote NAS credentials/permissions/quotas under the intended service identity and real Windows cross-drive/network recovery.
5. Measure #81 cold/warm phase timing on an owned representative corpus, then optimize the measured bottleneck with retained safety coverage.
6. Replace recursive native folder-change handling with bounded indexing/refresh of changed folders/items, and verify unrelated siblings on live hosts. Until this is implemented, the lowest-priority targeted-refresh row is partial.

**Issue closure and safe-to-push/safe-to-publish verdicts remain withheld.** Existing dirty work and older acceptance fingerprints were not reassigned to this candidate.
