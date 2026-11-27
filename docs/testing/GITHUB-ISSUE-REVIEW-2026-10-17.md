# GitHub issue review — 17 October 2026

Reviewed all **24 open issues** in `crackruckles/MediaDash` against the current local checkout, including recent issue comments and the complete duplicate-report discussion (#3). GitHub was read-only. Existing uncommitted work was preserved; no commit, push, release, deployment or server restart was performed.

## Fixed in this pass

### #43: companions left behind when grouping a loose video

`MediaGrouperFixer` moved the video with `File.Move` but never ran the companion-file handling already used by `MediaSorterFixer`. A failing filesystem regression reproduced a successful video move leaving matching subtitles, NFO metadata and artwork at the original location.

Both fixers now share that existing mover. It moves matching subtitle formats (including paired IDX/SUB), metadata, named artwork and trickplay indexes. Generic artwork in a shared source directory and unrelated files stay in place. Each companion's source and destination must pass `LibraryGuard`; destination files are never overwritten. Filename matching follows the platform's existing case convention. Listing or moving a companion can fail without misreporting the already completed primary move as failed; the Errors diagnostics identify companions left behind.

This addresses the loose-file companion defect in #43. It does **not** add grouping undo, restore previous layouts, change series-folder selection, or automatically relocate generic artwork from a shared directory. Folder moves continue to carry their whole directory contents. A companion collision or access failure leaves the original companion at its old path for manual resolution.

### #65: setup overwriting an unlimited recycle-bin cap

The Settings save path already preserved zero, but the setup wizard's disk check still replaced it with `SuggestedPauseCapGb`. A regression reproduced **0 becoming 162** after that check. Removed the implicit assignment, so rerunning setup preserves both unlimited and finite caps. The low-space check and independent fix-time free-space floor remain in place.

## Issue-by-issue disposition

“Existing” means code was present before this pass. Passing the local suites does not establish that every reporter's server is fixed.

| Issue | Local assessment |
| --- | --- |
| [#81 — Run fixes now does nothing](https://github.com/crackruckles/MediaDash/issues/81) | Existing approval and no-work fixes explain Detect only/disabled settings and reject ineligible approvals. Approval, dispatch and no-work tests passed. See `FIXERS-NOT-RUNNING-2026-10-17.md` for the earlier isolated live evidence. |
| [#82 — Redundant external subtitles](https://github.com/crackruckles/MediaDash/issues/82) | New feature request. Existing language cleanup does not establish that embedded and external subtitles are equivalent. No automatic deletion added. |
| [#43 — Grouping layout and companion files](https://github.com/crackruckles/MediaDash/issues/43) | **Partial fix this pass:** loose-file companions now move. Existing reboot/year tests passed. Folder-layout complaints and grouping undo remain unresolved. |
| [#80 — Unresponsive / immediate no-work runs](https://github.com/crackruckles/MediaDash/issues/80) | Existing history-query/index, UI load-error and task/approval changes are present; suites passed. The reported Jellyfin 12.2 service-provider/memory-pool disposal failure has not been independently reproduced. |
| [#70 — Blacked-out menu](https://github.com/crackruckles/MediaDash/issues/70) | Existing local theme CSS covers the Issues language selector. No fresh browser/theme audit in this pass. |
| [#59 — Timestamp permission error](https://github.com/crackruckles/MediaDash/issues/59) | Existing replacement/timestamp handling and changelog address permission failures after a successful media change. No affected SMB/NFS host reproduced here. |
| [#57 — Generic fix-run failure](https://github.com/crackruckles/MediaDash/issues/57) | Existing diagnostics distinguish several pause/no-work outcomes. The report lacks an exception or specific file; no further root cause proven. |
| [#56 — HandBrake M4V remux error](https://github.com/crackruckles/MediaDash/issues/56) | Existing MP4 subtitle muxer compatibility and explicit data-loss-consent guards are covered by passing tests. No reporter sample exercised here. |
| [#55 — Corrupt MPEG source ffmpeg failure](https://github.com/crackruckles/MediaDash/issues/55) | Existing corruption classification and repair workflow are covered by passing tests. These do not guarantee salvage of the reported recording. |
| [#73 — Manually select re-encode](https://github.com/crackruckles/MediaDash/issues/73) | Existing Files context actions and manual issue endpoint implement manual selection; no new feature needed for triage. |
| [#77 — Swedish/Swahili confusion](https://github.com/crackruckles/MediaDash/issues/77) | Existing picker regressions passed for Swedish names/codes and refusing the partial `Sw` prefix. Backend matching tests also passed. |
| [#71 — Optional modified-date update](https://github.com/crackruckles/MediaDash/issues/71) | Feature remains pending. No new timestamp setting added. |
| [#72 — Docker import disk saturation](https://github.com/crackruckles/MediaDash/issues/72) | Existing scan coordination, chunked hashing and best-effort Linux I/O-priority mitigations are present. Realtime imports use a separate path; no affected Docker/Linux load reproduction. Remains open. |
| [#66 — Separate anime TV/movie destinations](https://github.com/crackruckles/MediaDash/issues/66) | Pending feature. Current configuration still has a single `AnimeTargetPath`. |
| [#67 — Untagged-track reporting](https://github.com/crackruckles/MediaDash/issues/67) | Planned reporting feature; not implemented here. Current cleanup keeps untagged tracks. |
| [#64 — Japanese kept-language flag](https://github.com/crackruckles/MediaDash/issues/64) | Current scanner and fixer share removal selection, with normalized language matching. The reported item/configuration was not reproduced; no new correction or resolution claim. |
| [#65 — Unlimited recycle bin](https://github.com/crackruckles/MediaDash/issues/65) | **Fixed remaining setup overwrite this pass.** Settings already saved zero; wizard now preserves it too. Does not establish correct capacity reporting on every bind mount. |
| [#62 — Temporary media extensions](https://github.com/crackruckles/MediaDash/issues/62) | Existing `.mediaDash.tmp` tail and muxer mapping tests passed; no new naming change needed. |
| [#54 — Provider IDs during grouping](https://github.com/crackruckles/MediaDash/issues/54) | Existing canonical file-rename option preserves provider IDs, and year-tagged series-folder handling accepts provider suffixes. General grouping ID naming remains a separate feature gap. |
| [#46 — Combined queued-only Issues view](https://github.com/crackruckles/MediaDash/issues/46) | Pending UI feature. Queued rows are visible, but there is no dedicated combined queue view. |
| [#37 — Delete watched media](https://github.com/crackruckles/MediaDash/issues/37) | Pending feature. Stale detection and manual deletion do not implement watched-age automatic cleanup. |
| [#27 — Extract subtitles](https://github.com/crackruckles/MediaDash/issues/27) | Pending feature; no extraction pipeline added. |
| [#30 — Resolution per library](https://github.com/crackruckles/MediaDash/issues/30) | Pending feature; quality settings remain global. |
| [#3 — Misidentified duplicates](https://github.com/crackruckles/MediaDash/issues/3) | Existing duplicate-key/confidence safeguards and the Futurama-specials regression are covered by passing tests. The thread includes a reporter-confirmed TV naming/metadata correction; arbitrary libraries are not certified by that result. |

## Verification

- Before production edits: the new companion regression failed because matching files remained at the source; the new unlimited-cap regression failed with `162 !== 0`.
- `dotnet test Jellyfin.Plugin.MediaDash.Tests --no-restore -c Release -v quiet`: **1,617 passed, 0 failed, 0 skipped**, including eight new filesystem tests. This compiled the Release plugin with its normal analyzers.
- `node --test tools/comprehensive-test/ui-regression-self-tests.js tools/comprehensive-test/ui-lifecycle-self-tests.js tools/comprehensive-test/ui-issue-fixes-self-tests.js`: **43 passed, 0 failed, 0 skipped**. The four new wizard checks execute its actual disk-check function in the existing VM-test style; they are not a browser run.
- Added the new wizard test file to the existing Windows/Linux CI command. Remote CI has not run.
- `git diff --check`: passed, with existing line-ending conversion notices.

No existing Jellyfin installation was updated. The filesystem tests use disposable text-content fixtures to verify moves and preservation; no media stream is rewritten by these changes. No fresh live Jellyfin, Linux/macOS, network-share, physical-disk saturation or symbolic-link execution test was performed. Windows could not create a file symlink without additional privilege; per-companion library checks are present, but that branch is not claimed live-tested.

Local tests passed. This is not a public-release readiness certification of the wider dirty checkout.
