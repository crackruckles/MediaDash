# MediaDash push preparation, 5 November 2026

## Current checkpoint — source-deletion refusal, 6 November

The selected policy is **refuse moves that cannot guarantee safe source deletion**. The repair is integrated into the primary tree at source fingerprint `66c2758eab0a10309a04ff00e0d46eb77d3ff55779db91fe40920dfa80cfc299`; native profiles used isolated Release DLL SHA-256 `97c60024328408db7230dcc92121c7076a560640707fde755b3372fafa00d3aa`. Ten changed/added/deleted files were transferred after original-byte checks, with a backup and zero conflicts. The primary Release build passed with zero warnings/errors after restoring missing analyzer packages. [Integration](../../artifacts/push-preparation-20261106-refusal-native/primary-integration.json) · [Original bytes](../../artifacts/push-preparation-20261106-refusal-native/primary-original-bytes.zip) · [Build](../../artifacts/push-preparation-20261106-refusal-primary-build-retry.log).

**DO NOT PUSH OR PUBLISH.** Supported move/recycle routes no longer invoke the two unsafe recovery-deletion primitives. This closes those product routes through refusal, not through proof that native unlink or new-stream exclusion has become universally safe. Their direct failing diagnostics remain preserved and must not justify re-enabling copy/delete. Reporter, platform, container and committed acceptance remain incomplete. No commit, push, shared-host deployment, or NAS write was performed.

Copy/delete moves now show a blocker in preview and are rechecked for every entry before journal acquisition, including older approvals. Actual movement uses strict native same-filesystem rename with no implicit copy fallback; a device change after preflight fails safely. The old staging/source-capture/source-deletion execution path was removed. Recycle and restore use the same rule; unsupported recycling refuses before creating a batch. Valid same-filesystem moves, recycle/restore and Undo remain available. **Cross-filesystem moves, recycle/restore, Windows namespace changes requiring a copy, and legacy cross-filesystem Undo are unsupported. Configure the recycle bin on the source filesystem.** Existing recovery payloads/journals are retained for review.

| Current-source implementation and validation | Result and boundary |
| --- | --- |
| Full analyzed Windows offline suite | 2,121 passed; zero failed/skipped |
| Full analyzed Linux offline suite | 2,121 passed; zero failed/skipped |
| Dashboard, gate self-tests and harness suites | 61, 19 and all 11 passed |
| Focused mover/recycle suite | 341 passed |
| Windows 10.11.11 and 12.0.0 native workflows | 49 checks each: refusal without journal/destination, accessible new ADS, valid NFO move/Undo and favorites |
| Actual Linux Jellyfin 12.2 native workflow | 70 checks: distinct ext4/tmpfs refusal, accessible native writable-FD bytes, valid same-filesystem playback/restart/Undo, favorite and metadata retention |
| Scoped review | No important finding in the changed move/recycle/restore routes; production deletion-primitive callers removed |
| Mandatory push gate | Exit 1 for committed/external acceptance only; no source-drift problem in the accepted retry |

The current report binds 168 native checks and 81 hashed artifacts. Windows 12 reports 12.0.0 from the earlier rc3 archive; Linux uses a separately downloaded official 12.2 self-contained archive with recorded size/hash. Linux is Ubuntu WSL with the existing FFmpeg 7.1.4, not the reporter's Debian 13/.NET/FFmpeg environment. All owned hosts stopped, settings/libraries/queues/protection were restored, and the owned empty tmpfs fixture root was removed. [Current scoped evidence](reported-issues-evidence/cross-volume-refusal-local-20261106.json) · [Windows gate](../../artifacts/push-preparation-20261106-cross-volume-refusal/artifacts/refusal-final-gate-retry/summary.json) · [Linux suite](../../artifacts/push-preparation-20261106-cross-volume-refusal/artifacts/refusal-final-linux-retry.log) · [Harnesses](../../artifacts/push-preparation-20261106-cross-volume-refusal/artifacts/refusal-final-harnesses/summary.json) · [Windows native run](../../artifacts/push-preparation-20261106-refusal-native.log) · [Linux native run](../../artifacts/push-preparation-20261106-linux12-refusal-native.log).

Tests for the removed copy/stage/delete capability were archived and replaced with atomic identity/content, collision, cancellation, new-import and ADS checks. Consent tests now exercise the boundary before atomic rename; the obsolete restore source-text assertion became a behavioral file/directory restore test. The net test count changed from 2,122 to 2,121. Failed red cases, obsolete expectations, the initial CRLF Linux runner/source-drift attempt and its quarantined artifact paths, native setup/binding attempts, and the initial no-restore build remain recorded; they are not counted as final acceptance.

Remaining: reporter-equivalent #87/#92 media/probe/settings, writable NAS/service-account/quota acceptance, mounted ISO and remaining disc/platform/interruption coverage, both supported container startup checks, complete final review, an explicitly authorized candidate commit and committed acceptance. The NAS stays read-only. The recovery-policy question is resolved. Older checkpoints and acceptance records below remain historical; none were relabeled as current-source full-family evidence. The legacy #43 acceptance description still mentions cross-volume moves; final release review must reconcile that wording with the explicitly selected refusal policy without waiving the remaining supported-move scenarios.

## Previous checkpoint — provider deferral, 6 November

The provider-sidecar repair is integrated into the primary tree: source fingerprint `ccc304c77cdee26f4770e1e995619361d1a7a2e38e7f80e9b78b00198861f4a7`. Four files were transferred after checking original bytes, with a backup and zero conflicts. The owned native profiles used isolated Release DLL SHA-256 `e89b68564fd93ba9128212879ba50074c89db212e25fa79ddcfc887658c8aba0`. The primary Release solution build passed with zero warnings/errors. [Integration record](../../artifacts/push-preparation-20261106-provider-native-final/primary-integration.json) · [Original bytes](../../artifacts/push-preparation-20261106-provider-native-final/primary-original-bytes.zip) · [Build](../../artifacts/push-preparation-20261106-provider-primary-build.log).

**DO NOT PUSH OR PUBLISH.** The Linux recovery unlink and Windows new-alternate-stream deletion races remain open. External/platform coverage and committed acceptance are incomplete. No commit, push, shared-host deployment, or NAS write was performed.

Move refresh now completes bounded native membership indexing, then defers provider metadata refresh when local media-folder metadata writing or metadata savers are enabled, or their options are unavailable. Destination catalog validation and watched/favorite-state reconciliation still run. The result explicitly says metadata is pending and a Jellyfin scan may write sidecars and make Undo unavailable. Failed user-state restoration keeps a separate pending watched-state warning based on durable journal markers. Normal fixer refresh and move refresh in libraries without local writes remain enabled. This deliberately preserves immediate Undo while leaving provider metadata pending; it does not claim a general read-only contract for third-party providers or protect against subsequent user/native sidecar changes.

| Current-source implementation and validation | Result and boundary |
| --- | --- |
| Full analyzed Windows offline suite | 2,122 passed; zero failed/skipped |
| Full analyzed Linux offline suite | 2,122 passed; zero failed/skipped |
| Dashboard, gate self-tests and harness suites | 61, 19 and all 11 passed |
| Real NFO remote-thumb/local-saver workflow | 43 native checks per host; both passed: no sidecar writes, exact immediate Undo, favorites retained |
| Native hosts | Windows 10.11.11 and 12.0.0 from the 12.0-rc3 archive; both stopped after restoration |
| Scoped review | One pending-user-state message defect reproduced and repaired; follow-up found no remaining important concern within the four-file scope |
| Mandatory push gate | Exit 1: committed acceptance index/evidence absent; no complete acceptance-family certification |

The current-source report binds 86 native checks and 50 hashed evidence artifacts. Initial opt-in test invocation and a build/testhost collision are retained as failed verification attempts; neither is counted as acceptance. The final offline gates use the required external-test exclusion. Earlier 1,650 native checks and six acceptance records below belong to the preceding source and have not been relabeled as current-source full-family evidence. [Current scoped evidence](reported-issues-evidence/provider-deferral-local-20261106.json) · [Windows gate](../../artifacts/push-preparation-20261106-provider-guard/artifacts/provider-final-gate/summary.json) · [Linux suite](../../artifacts/push-preparation-20261106-provider-guard/artifacts/provider-final-linux.log) · [Harnesses](../../artifacts/push-preparation-20261106-provider-guard/artifacts/provider-final-harnesses/summary.json) · [Native run](../../artifacts/push-preparation-20261106-provider-native-final.log).

Remaining work: resolve the two cleanup races under an explicit recovery policy; obtain reporter-equivalent #87/#92 inputs and writable NAS/service-account acceptance; finish mounted ISO playback, remaining disc/platform/interruption coverage, supported-container startup, complete final review and an explicitly authorized committed candidate gate. The NAS remains read-only. Recovery-policy and external-input questions remain pending; no choice or writable-NAS consent is inferred from “continue.”

## Previous checkpoint — review repairs and discs, 6 November

The review repair is integrated into the primary tree: source fingerprint `575b881b68693b1a6cc7182c865c286916b41a251ac654fcc3799ae2958a96ab`. Fifteen files were transferred after checking every original byte, with a backup and zero conflicts. The owned native profiles use isolated Release DLL SHA-256 `f3c38106ae7cb5a640d27cf460644b36ce6b5da14a71ae6d14ffa1f3dc6a9f01`. The primary Release build also passed with zero warnings/errors. [Integration record](../../artifacts/push-preparation-20261106-review-live/primary-integration.json) · [Original bytes](../../artifacts/push-preparation-20261106-review-live/primary-original-bytes.zip) · [Primary build](../../artifacts/push-preparation-20261106-review-primary-build.log).

**DO NOT PUSH OR PUBLISH.** Review found additional cleanup races that have executable failing reproductions. They remain blockers even though the automated suites pass. The mandatory gate returned 1 because committed acceptance evidence is absent. No commit, push, production deployment, or NAS write was performed.

The integrated repairs bind explicit Files previews to their dry-run mode without invalidating automatic-fix approvals, bind duplicate disposal to observed keeper/loser identities, reconcile positively confirmed missing managed artwork, support read-only Windows copy/Undo, and retain Windows sharing exclusions for the main and currently recorded alternate streams during verified recovery deletion. Move-only native refresh avoids forcing NFO saves; normal fixer refresh retains its full provider behavior. The native provider-sidecar boundary below remains open.

| Current-source verification | Result |
| --- | --- |
| Full analyzed Windows offline suite | 2,116 passed; zero failed/skipped |
| Full analyzed Linux offline suite | 2,116 passed; zero failed/skipped |
| Dashboard and gate self-tests | 61 and 19 passed |
| Harness suites | All 11 passed |
| Owned native mode consent, EPUB/comic/audiobook move, restart, playback and Undo | 116 named checks per host; both passed |
| Missing-artwork native scan/approve/fix/rescan | 35 checks per host; both passed; prior native image bytes restored |
| Broader fixer/legacy/metadata/artwork/History cohort | 443 checks per host; both passed and restored |
| Exact-ID preview/refusal/rescan continuation | 40 checks per host; both passed |
| Current-source native Linux ext4/tmpfs restart/Undo | 66 checks passed; exact owned tmpfs root removed |
| Rendered 1,000-movie progress cohort | 50 checks passed; 1,000 fixture hashes checked before removal |
| Authored Blu-ray playback/move/restart/Undo | 82 checks per host; both passed with equal decoded video/audio hashes |
| Authored DVD ISO catalog/download/move/restart/Undo | 51 checks per host; both passed; mounted ISO playback remains unverified |

The current candidate has 1,384 passing named native checks in the non-disc cohorts and 247 hashed evidence artifacts. Six fresh source-matched local acceptance records now cover #80, #81/#83, #81 progress, #86, #88, and the exact-ID disappearance controls; they are referenced by the uncommitted acceptance index. No #43, #87, #89, or #92 complete-family attestation was fabricated. [Consolidated current-source evidence](reported-issues-evidence/review-repairs-local-20261106.json).

The subsequent disc continuation adds 266 passing checks and 91 hashed artifacts, bringing the current-source native total to 1,650. Generated Blu-ray BDMV folders played through the native MPEG-TS endpoint before move, after move, after restart, and after Undo; complete decoded video/audio hashes matched the authored title at each stage. Full folder inventories, notes, empty directories, favorites, History and rescan passed on both hosts. Authored DVD ISO folders passed native indexing, full download-body hashes, move, restart and exact Undo on both hosts. ISO mounting/playback, other disc structures and client negotiation remain unverified. Both owned hosts stopped after settings, libraries, queues and protection were restored. [Scoped disc evidence](reported-issues-evidence/disc-scoped-20261106.json) · [Driver and limits](../../artifacts/push-preparation-20261106-bluray/README.md).

The two Windows native hosts report 10.11.11 and 12.0.0; the latter runtime came from the `jellyfin_12.0-rc3-amd64.zip` archive. This is not Jellyfin 12.2 reporter-environment acceptance. All owned hosts stopped and configuration, libraries, approvals, and protection were restored. Failed diagnostic journals remain in their owned profiles for inspection. [Automated gate](../../artifacts/push-preparation-20261105-review-repairs/artifacts/review-final-gate-20261106/summary.json) · [Linux suite](../../artifacts/push-preparation-20261105-review-repairs/artifacts/review-linux.log) · [Harness summary](../../artifacts/push-preparation-20261105-review-repairs/artifacts/review-final-harnesses-20261106/summary.json) · [Native family run](../../artifacts/push-preparation-20261106-review-live.log) · [Missing-artwork retry](../../artifacts/push-preparation-20261106-review-missing-artwork-retry.log) · [Broader native cohort](../../artifacts/push-preparation-20261106-review-core-live.log) · [Exact-ID continuation](../../artifacts/push-preparation-20261106-review-refusal.log).

The current 1,000-movie browser cohort required 775.07 seconds for native Jellyfin indexing before measuring MediaDash. The measured MediaDash cold scan took 43.99 seconds and the warm scan 0.47 seconds; progress requests peaked at 29.13 ms and 26.49 ms. Chrome recorded 13 rendered samples, successful progress responses, completion clearing, and no page errors. Each native host also served 20 requests for 2,000 issues against one million synthetic History rows; averages/peaks were 70.33/110.98 ms on 10.11 and 78.96/135.94 ms on 12. Other owned tests were active; these are observations, not a controlled throughput improvement or 20 TB/adaptive-resource certification. [Rendered progress run](../../artifacts/push-preparation-20261106-review-browser.log).

Remaining blockers:

- **Linux recovery unlink:** a native writable file descriptor survives rename and unlink; subsequent written bytes have no accessible recovery path. The executable diagnostic remains red. [Reproducer and evidence](../../artifacts/push-preparation-20261105-review-repairs/artifacts/cleanup-native-writer-repro/README.md).
- **Windows new alternate stream:** a writer can create a previously unrecorded stream after enumeration. Existing-stream exclusion does not cover this. A read-only fence was experimentally bypassed through ordinary metadata attribute changes, so it was not presented as a fix. [Reproducer and evidence](../../artifacts/push-preparation-20261105-review-repairs/artifacts/cleanup-new-ads-repro/README.md).
- **Native provider sidecars — reproduced:** an owned NFO referencing a loopback JPEG, with local metadata saving enabled and future mtime, caused native refresh to add `folder.jpg` and rewrite `movie.nfo`. The subsequent native Undo was refused. Approved originals were backed up, all current bytes were preserved, configuration was restored, the host stopped, and the owned folder was returned to its original path for inspection; the failed journal remains for review. The desired-Undo assertion remains red. Upstream source confirms that validation mode still runs local NFO providers, whose remote-image imports can write artwork and trigger metadata savers; disabling forced saves is insufficient. [Provider boundary investigation](../../artifacts/push-preparation-20261106-nfo-thumb-retry/PROVIDER-BOUNDARY.md) · [Native diagnostic](../../artifacts/push-preparation-20261106-nfo-thumb-retry/v10/final-evidence/diagnostic.json) · [Failing assertion](../../artifacts/push-preparation-20261106-nfo-thumb-expected-undo-red.log) · [Fixture recovery](../../artifacts/push-preparation-20261106-nfo-thumb-retry/v10/final-evidence/failed-undo-fixture-recovery.json).
- **External acceptance:** refreshed GitHub #87 and #92 still have no equivalent reporter probe/media inventory. #89 has a reporter-tested narrower NAS patch, but writable service-account/quota and reporter-host acceptance for this candidate remain absent. The earlier NAS read-only constraint remains in force.
- Mounted ISO playback, remaining disc/platform/interruption coverage, complete final review, an explicitly authorized candidate commit, the committed-source gate, and both supported container startup checks remain open. This Windows machine exposes only C: as a usable mounted volume; physical Windows cross-volume acceptance is not established. Docker remains unavailable.

Two inputs are pending before the remaining work can be considered complete: reporter-equivalent #87/#92 data and writable service-account NAS access, and the recovery policy for source originals when exclusive deletion cannot be proved. Retaining originals until explicit cleanup preserves recoverability but does not immediately free the source disk; refusing unsupported moves avoids that storage cost but limits supported operations. Neither choice is inferred from elapsed time or from green tests.

## Earlier candidate record

The remaining sections preserve evidence for source `5bb602fa9950197fa215d8e5b21b8d52a22399c07dbf0f6c3c200d8cca4a2d95`. They are historical after the review repair and do not certify the changed source above.

The current repair is integrated into the primary working tree, with the existing concurrent changes preserved. A live test found a whole-folder move that preserved its files but left the moved comic or audiobook absent from Jellyfin's recursive library results. The new destination catalog row existed without its metadata. Explicitly refreshing the audiobook restored its visibility. MediaDash now refreshes the destination items recorded in the approved plan before clearing indexing guidance. Explicit folder refreshes cover their descendants once; separately selected items still refresh.

**DO NOT PUSH yet.** The automated checks pass, and six acceptance families now have records for this source. Four families remain incomplete, and the acceptance index is absent from committed HEAD. Committing the current records alone would not satisfy the four missing scenarios. No commit, push, public release, or shared Jellyfin deployment was performed.

## Candidate and repair provenance

- Source fingerprint: `5bb602fa9950197fa215d8e5b21b8d52a22399c07dbf0f6c3c200d8cca4a2d95`.
- DLL used by the owned live profiles: SHA-256 `f225958b511271b532254ed3c9f04afe8e525d4610614f3020bec6806eac4e80`, built in the isolated repair workspace.
- The isolated workspace began as an exact copy of candidate `4045428334a7552f9d9afb435efff70ce81e5fe20308911afce7746a1e2c7e5e`. Three regressions failed because no payload metadata refresh occurred. A fourth test exposed duplicate metadata work for an explicitly selected folder and its child. All four pass after the repair. [Original failure](../../artifacts/push-preparation-20261105-native-target-red-detail.log) · [Four-case transition](../../artifacts/push-preparation-20261105-native-target-combined.log) · [Focused verification](../../artifacts/push-preparation-20261105-native-target-focused.log).
- An independent review found no issues in the four-file incremental change. This was a scoped repair review, not a review of every pre-existing change in the dirty tree.
- Integration checked the original bytes of each affected file, saved a backup, transferred four files with zero conflicts, and verified that the primary fingerprint exactly matched the tested isolated source. [Integration record](../../artifacts/push-preparation-20261105-native-target-integration.json) · [Original bytes](../../artifacts/push-preparation-20261105-native-target-integration-backup.zip).

The changes are in [MediaMoveService](../../Jellyfin.Plugin.MediaDash/Fixers/MediaMoveService.cs), [NativeLibraryRefresher](../../Jellyfin.Plugin.MediaDash/ScheduledTasks/NativeLibraryRefresher.cs), and their regression tests. The upstream [Jellyfin folder implementation](https://raw.githubusercontent.com/jellyfin/jellyfin/v10.11.11/MediaBrowser.Controller/Entities/Folder.cs) distinguishes membership validation from child metadata refresh; the repair preserves that scoped indexing boundary.

## Current automated results

| Check | Result | Evidence |
| --- | --- | --- |
| Primary Windows Release suite | 2,088 passed; zero failed or skipped | [Integrated gate](../../artifacts/push-preparation-20261105-integrated-gate/dotnet.log) |
| Ubuntu/WSL Release suite | 2,088 passed; zero failed or skipped | [Linux run](../../artifacts/push-preparation-20261105-native-target-linux.log) |
| Focused mover and native-refresh suite | 146 passed | [Focused run](../../artifacts/push-preparation-20261105-native-target-focused.log) |
| Dashboard | 61 passed | [Dashboard run](../../artifacts/push-preparation-20261105-integrated-gate/ui.log) |
| Push-gate self-tests | 19 passed | [Gate checks](../../artifacts/push-preparation-20261105-integrated-gate/gate-self-tests.log) |
| Offline harness suites | All 11 passed, including release-source guards | [Harness summary](../../artifacts/push-preparation-20261105-native-target-harnesses/summary.json) |
| Linux external filesystem/crash controls | 27 passed; both owned private mounts unmounted | [External run](../../artifacts/push-preparation-20261105-native-target-linux-external.log) |

The Linux external controls cover actual ENOSPC, permission denial, healthy move/Undo, cross-filesystem crash boundaries, and the two- and three-times free-space thresholds. They execute production move code with a controlled catalog fixture; they do not themselves establish native Jellyfin cross-volume playback acceptance. A separate native Linux workflow is recorded below. Historical Windows external counts from the earlier candidate are not claimed as rerun here.

The complete primary gate returned exit 1 because the acceptance index is not committed in HEAD. Its automated checks passed. [Mandatory gate summary](../../artifacts/push-preparation-20261105-integrated-gate/summary.json) · [Current review audit and artifact hashes](reported-issues-evidence/push-preparation-final-audit-20261105.json).

## Owned Jellyfin acceptance

Fresh Jellyfin 10.11.11 and 12.0.0 profiles ran the current DLL through preview, actual legacy XML migration/restart, approval preservation, real fixes, rescan, playback, recycle, and exact restoration. Both repeated default/custom-root orphan confirmation and refusal controls, recoverable artwork and infrastructure failures, controlled Spanish-original retention, both MKV failure controls, loopback SMB encode/restore, and movie grouping/Undo. Settings, libraries, approvals, and protection were restored before shutdown. [Current live workflow log](../../artifacts/push-preparation-20261105-native-target-live.log).

Additional current-DLL workflows verified:

- EPUB, comic, and audiobook complete-folder moves on both hosts: primary and sidecar hashes, empty directories, favorites, full native HTTP delivery, restart, and exact Undo. Audiobook output also fully decoded.
- JPEG photo-folder moves on both hosts in a `homevideos` library with `EnablePhotos`: XMP/user files, empty directories, favorites, native delivery, restart, and exact Undo. Earlier PNG/photo-library prerequisites failed and are retained; they do not count as acceptance. [Photo 10.11](../../artifacts/push-preparation-20261105-photo-proof-v10.log) · [Photo 12](../../artifacts/push-preparation-20261105-photo-proof-v12.log).
- Ambiguous generic shared artwork blocked relocation without changing either movie or the artwork. A valid same-folder rename and complete Undo still worked on both hosts.
- Exact issue IDs survived preview, companion-collision refusal, failed History with zero savings, and rescan on both hosts.
- A FLAC album on 10.11 and TV series on 12 preserved exact payloads and user state through folder rename and Undo. Unrelated catalog timestamps and bytes remained unchanged after 70 seconds with native monitoring enabled. The operations did not require a full library scan.

The first v10 extension reused bookkeeping for load fixtures that had already been removed by an earlier successful trim. Its cleanup failed visibly. A separate recovery restored configuration, libraries, and queues and stopped the host. The failure remains in the raw log. [Initial extension and album checks](../../artifacts/push-preparation-20261105-native-target-extra.log) · [Cleanup recovery](../../artifacts/push-preparation-20261105-native-target-extra-recover.log) · [12.0.0 extension and TV checks](../../artifacts/push-preparation-20261105-native-target-extra-v12.log).

[Scoped mover evidence](reported-issues-evidence/mover-families-scoped-20261105.json) records these results without marking the complete mover acceptance family finished. The continuations below add STRM, specific Windows metadata/refusal controls, playable DVD folders on both Windows hosts, and native Linux 10.11 cross-filesystem playback/Undo. ISO/Blu-ray variants, writers during copy/commit, remaining platform metadata/interruption boundaries, and Windows/12.0 cross-volume acceptance remain open.

### STRM and Windows metadata continuation

Fresh owned profiles on Jellyfin 10.11.11 and 12.0.0 ran the same source fingerprint and DLL recorded above, with 81 passing named checks per host (162 total). STRM complete-folder preview, move, native indexing, favorite retention, host restart, Undo, linked History with zero savings, and rescan passed on both hosts. The STRM referenced generated video served by an owned loopback HTTP server outside the libraries. Complete native playback responses matched the backing video's SHA-256 before the move, after the move, after restart, and after Undo. Sidecars, user notes, and the empty directory survived exactly.

Both hosts refused a stale approval after an owned writer changed a manifest member between preview and apply, preserving the new bytes without a move journal. A fresh approval then completed the move and exact Undo. Both also refused an approved move while an exclusive native file handle was open, preserving the payload and journal; releasing the handle allowed the unchanged plan to complete. These cover changes and locks before apply, not a writer during copy or commit.

A separate generated MKV on each host preserved its protected DACL, owner/group, NTFS alternate-stream bytes, read-only/archive attributes, creation time, and modification time through a same-volume complete-folder move and Undo. Native indexing, full HTTP playback-body hashing, and full decode passed. This is specific Windows NTFS evidence; cross-volume, Linux, and remote filesystem metadata remain separate requirements.

The initial 10.11 attempt failed a harness assertion that incorrectly expected reverse operations to be `Undone`. Inspection found original moves `Undone` and reverse operations `Completed`, all with cleanup complete. The failed attempt is retained. A fresh run checks the correct journal states and repeats the workflows on both hosts. Post-shutdown verification checked all six journal rows per host, restored payload hashes, and NTFS metadata. Configuration, libraries, queues, and protection were restored; both owned Jellyfin processes and backing HTTP servers stopped. [Initial failed attempt](../../artifacts/push-preparation-20261105-strm-boundaries-v10.log) · [Fresh two-host run](../../artifacts/push-preparation-20261105-strm-boundaries-verified.log) · [Source-bound scoped evidence](reported-issues-evidence/mover-strm-ntfs-scoped-20261105.json).

### DVD and native Linux cross-filesystem continuation

The unchanged candidate passed another 228 named live checks: 81 DVD checks on each Windows host and 66 checks on native Linux Jellyfin 10.11.11 with Jellyfin FFmpeg 7.1.4.

An owned three-second PAL DVD was genuinely authored with IFO, BUP, and VOB files. Both Windows hosts indexed it as a DVD movie, moved its complete folder with user notes and an empty directory, retained its favorite, restarted, and completed exact Undo. Native MPEG-TS playback responses fully decoded before the move, after the move, after restart, and after Undo. Every decoded video and audio sample hash matched the authored VOB. Linked successful History reported zero savings, and rescan preserved all source hashes. [DVD live run](../../artifacts/push-preparation-20261105-disc-progressive.log) · [DVD source-bound evidence](reported-issues-evidence/mover-dvd-scoped-20261105.json).

The first DVD prerequisite requested a default MPEG-container/AAC combination that native FFmpeg rejected before any move. Its failure is retained; the separate successful run explicitly requests compatible MPEG-TS with video/audio stream copy through the [native progressive video endpoint](https://raw.githubusercontent.com/jellyfin/jellyfin/v10.11.11/Jellyfin.Api/Controllers/VideosController.cs). This validates the exercised server playback route, not every client negotiation or physical optical-disc workflow. ISO images were authored but not moved or played; ISO and Blu-ray acceptance remain open. [Failed prerequisite](../../artifacts/push-preparation-20261105-disc-native-v10.log).

A fresh Linux profile then indexed owned ext4 and existing tmpfs libraries. The source and destination had different measured device IDs. A complete folder containing an MKV, subtitle, user notes, and empty directory moved from ext4 to tmpfs through the native API. Native indexing, exact full HTTP playback-body hashes, full decode, favorite retention, restart, reverse cross-filesystem Undo, linked zero-savings History, and rescan passed. The file's uid/gid, 0640 mode, one user extended attribute, and an exactly representable 100 ns modification timestamp survived both directions. No custom mount or root account was used. [Linux native run](../../artifacts/push-preparation-20261105-linux-native-retry-1.log) · [Linux source-bound evidence](reported-issues-evidence/mover-native-cross-volume-linux-scoped-20261105.json).

The first Linux profile was interrupted during startup, before fixture generation or a move; it has no acceptance result. WSL had restarted when work resumed. The fresh profile completed all 66 checks. This small local ext4/tmpfs workflow does not cover Windows or 12.0 cross-volume behavior, remote NAS/service-account/quota, LXC, network interruption, large transfers, Linux ACLs/security labels/capabilities, or sub-100 ns timestamp precision. [Interrupted attempt](../../artifacts/push-preparation-20261105-linux-native.log).

Post-shutdown verification checked restored DVD and Linux payloads and durable original-`Undone`/reverse-`Completed` journals with cleanup complete. Configuration, libraries, queues, and protection were restored. Both Windows hosts and the native Linux host stopped. The exact owned empty tmpfs directory was removed; generated local fixtures and logs remain as evidence. These additions retain the same source fingerprint and DLL hash; no product source changed.

## NAS dry preview and real local-copy workflow

Following the instruction to keep the NAS in dry mode, the isolated host registered its directory only for a read-only complete move preview with `DryRun=true`. It did not run a native NAS library scan or apply a NAS move. The NAS registration was removed before enabling any real fix. Every real-fix library then resolved inside the owned local test profile.

A fresh 567,671,503-byte NAS copy matched the previously verified SHA-256 `f059380f70fdb8c16b7e40b663096ff9bbf0bd789a45288720af3c5d8f015c9d`. The local copy completed a real whole-folder move and exact Undo, then a dry quality preview and an actual re-encode from 1080p HEVC to H.264 at the configured 480p ceiling. The encoded file was 116,185,256 bytes. Its complete video/audio decode and full HTTP playback-body hash passed. Compressed AAC and subtitle packet hashes matched the original. The quality finding cleared on rescan, and recycle restore recovered the exact original file.

A final remote read matched the NAS hash, size, and modification time. The NAS source was unchanged. The local profile restored its settings, libraries, and queue and stopped. [NAS/local workflow](../../artifacts/push-preparation-20261105-nas-local-retry.log) · [Scoped sanitized evidence](reported-issues-evidence/nas-read-only-local-copy-20261105.json).

The first remote preview exceeded the harness's 30-second HTTP timeout and was canceled. Its profile was restored and stopped. The successful retry used a bounded longer read timeout. This covers read-only NAS access by the interactive account and real mutations of a local copy. It does not certify writable service-account NAS ACLs, quotas, interruption behavior, or the reporter's LXC environment. The healthy NAS file is not a reproduction of the reported MKV or Rafa failures.

## Loaded History and rendered scan progress

Each current-DLL host served 20 requests for 2,000 issues with one million synthetic History rows, returning the newest attempts. Requests averaged 64.42 ms and peaked at 132.72 ms on 10.11; 76.22 ms and 109.46 ms on 12. Synthetic rows and generated bulk files were removed after verification and host shutdown.

The current 12.0.0 browser run indexed all 1,000 generated movies before measuring MediaDash. The cold scan took 44.02 seconds and the warm scan 0.49 seconds. Progress requests peaked at 35.22 ms cold and 19.52 ms warm. Chrome observed 13 distinct rendered states, successful progress responses, and the progress display clearing on completion, with no page errors. Every generated movie produced its expected finding. Configuration, libraries, queues, and protection were restored; all 1,000 hashes were verified before removal after shutdown. [Current browser run](../../artifacts/push-preparation-20261105-browser-repaired-v12.log) · [Rendered progress](../../artifacts/push-preparation-20261105-browser-repaired/v12/final-evidence/scan-3.png) · [Completed display](../../artifacts/push-preparation-20261105-browser-repaired/v12/final-evidence/after-scan.png).

Other owned tests were active on the machine during this cohort. Process CPU figures exclude child processes. These measurements do not certify a 20 TB workload or adaptive CPU/RAM/disk-pressure handling. The earlier candidate's 10.11 browser run rendered progress but failed its no-pageerror contract on a native Jellyfin notification-permission exception; it is retained as a failed attempt and not counted as current acceptance. Earlier legacy-login helper failures are also retained separately.

## Remaining push and publication gates

| Acceptance family | Current status |
| --- | --- |
| #80, #81/#83, #81 progress, #86, #88, Reddit disappearance | Six source-matched local records with named checks and hashed raw evidence; not committed in HEAD. |
| #43 complete mover | DVD folder playback/restart/Undo passed both Windows hosts; native 10.11 Linux ext4/tmpfs playback/restart/Undo and scoped Unix metadata passed. ISO/Blu-ray, Windows/12.0 cross-volume, writers during copy/commit, and remaining platform/interruption boundaries are open. Earlier STRM, NTFS metadata, stale-preview, and exclusive-handle controls remain recorded. |
| #87 original audio | Controlled Spanish-original and failed-metadata safety passed; reporter-equivalent Rafa stream flags, inherited metadata, and saved settings remain unavailable. |
| #89 NAS | Read-only NAS preview plus real local-copy move, encode, playback, and restore passed; writable remote NAS/service-account/quota acceptance remains open. |
| #92 MKV failures | Failure controls preserve originals/queue; genuine NAS-derived HEVC-to-H.264 encode passed. Both exact reporter failure classes still need equivalent failing media and matching FFmpeg/host details. |

The next work is the remaining mover/environment and reporter-specific acceptance, review of the complete intended dirty-tree change set, an explicitly authorized clean candidate commit, and the complete source-bound gate against that committed HEAD. Docker/Podman is unavailable on Windows and WSL; startup on both supported container hosts remains a separate publication requirement. All owned Jellyfin test processes are stopped. No NAS media was modified, and nothing was committed, pushed, or published.

The earlier 89-file integration, old candidate results, and first continuation audit remain historical evidence. [Earlier report](../../artifacts/push-preparation-20261105-before-final-report.md) · [Earlier integration backup](../../artifacts/push-preparation-20261105-integration-backup.zip).
