# Issues progress - 31 October 2026

Implementation and local validation status, not release-readiness scores. The implementation remains in the isolated `artifacts/remaining-issue-repairs-worktree`; review and integration into the original concurrent tree are still pending. No commit, push, GitHub change, production update or real-library write occurred.

| Issue | Current progress | Remaining work |
| --- | --- | --- |
| #86 metadata deletion | Persisted observations and a later successful native scan gate disposal. Controlled cleanup, refusal, recycle and restore workflows pass. | Broader environments and external-writer races. |
| #92 MKV failures | Controlled output-header and retained-inventory failures preserve originals, queued consent and useful diagnostics. | Reporter MKVs, full probes and matching Debian/Jellyfin runtime. This is not a verified codec repair of the reporter files. |
| #43 / disappearing issues | Complete plans, durable claims/recovery and Undo implemented. Native group/Files workflows pass on 10.11.11 and 12.0.0; actual browser Rename/Move/History Undo pass on 12. | Remaining media-family/platform matrix, bounded indexing, final review and integration; exact reporter reproduction. |
| #87 original audio | Controlled Spanish-original cleanup, retained packet hashes, rescan and exact original restoration pass. | Reporter stream flags and inherited Jellyfin metadata. |
| #89 UNC/NAS | Owned loopback-SMB space, re-encode, decode, recycle and restoration workflows pass. | Remote NAS quotas/ACLs, service account and production LXC acceptance. |
| #81 scan performance | Phase timings, cache/resource measurements, unavailable-root preservation and bounded outstanding root probes implemented. | Representative large-library checks. Dynamic CPU/RAM/disk-pressure backoff remains absent. |
| #83 approved fixes doing nothing | DetectOnly migration, approval, dry run and actual cleanup tested in controlled native workflows. | Broader reporter configurations. |
| #88 artwork | Healthy/stale/locked/unverifiable image refusal and recoverable cleanup/restoration tested. | Reporter Linux/Skia/Jellyfin environment. |
| #80 responsiveness | Indexed History lookup, UI failure handling and loaded-history checks pass in their recorded cohorts. | Reporter-specific service-provider/memory-pool disposal failure remains unreproduced. |
| #84 targeted refresh - lowest priority | Changed-file stream refresh is verified; unchanged/refused/dry-run attempts do not refresh. | Bounded folder indexing and unrelated-sibling verification remain open. |

Latest source: `0631ee0042c4507e59a2219472807cd8d4820c1fec04d908466fae34e27859fc`; Release DLL: `3099e392bb78395cad79955f64bbb98c856ccadda56830d931275689963df06e`. [Source archive identity](../../artifacts/remaining-issue-repairs-worktree/artifacts/mover-browser-json-evidence-identity.json).

Matching checks: **2,060 analyzed Windows tests; 335 Linux tests; 61 dashboard checks; 11 harness suites; 29 Windows SMB/process-kill cases; three actual Linux OS-fault/healthy-control cases; six real-media/UNC cases with each bundled FFmpeg version.** Missing prerequisites fail rather than skip. OS faults use generated payloads, not codec fixtures; the actual ENOSPC tmpfs was private and unmounted afterward.

Fresh exact-DLL 10.11.11 native group/Files Move/Rename/Undo, full playback/hash/decode, watched/favorite preservation and strict native-log checks pass. Fresh 12.0.0 browser Rename and Move both preview all three paths, complete successfully and Undo through History. A source edit after preview produces a fresh-review message with no move or new operation. Complete hashes, native indexing, full playback/decode and watched/favorite preservation pass afterward; the independent destination movie is unchanged.

The 12.0.0 whole-log check remains failed for exactly five pre-login `/socket` token errors during connection to a new server identity. All five precede successful browser authentication. The authenticated workflow window has zero request errors, and the entire log has zero user-data reattachment or placeholder errors. The classification preserves `wholeLogStrictPassed=false`; it does not convert the strict failure into a pass. Earlier browser/helper failures remain in their original logs.

Both hosts are restored and stopped. Original settings, libraries, approvals and fixture hashes are restored, and no active move claims remain. [Browser/native evidence](../../artifacts/remaining-issue-repairs-worktree/artifacts/mover-owned-host-browser-json-20261031), [stale-preview proof](../../artifacts/remaining-issue-repairs-worktree/artifacts/mover-owned-host-browser-json-20261031/v12/evidence/browser-stale-preview.png), [restored move records](../../artifacts/remaining-issue-repairs-worktree/artifacts/mover-owned-host-browser-json-20261031/v12/evidence/browser-history-restored.png).

The earlier issue-specific live evidence is separately bound to its frozen candidate in [the 30 October report](ISSUES-LIVE-2026-10-30.md). Do not reassign those results to a later build. See [the mover ledger/report](ISSUES-MOVER-2026-10-30.md) and [scanner resource limits](SCANNER-RESOURCES-2026-10-30.md). **DO NOT PUSH OR PUBLISH.**
