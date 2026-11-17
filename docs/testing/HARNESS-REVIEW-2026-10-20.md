# Test harness review - 2026-10-20

## Verdict

**Do not publish this checkout on this evidence.** The harnesses had real correctness and safety gaps, many of which are now corrected and verified. Offline checks, real repair acceptance on Jellyfin 10.11 and 12, the common-format matrix, and live recycle/restore pass. The comprehensive matrix retains a genuine artwork repair failure. Positive acceptance coverage for every registered scanner/fixer is still incomplete; the inventory below records the gaps explicitly.

These results apply to a frozen candidate DLL with SHA-256 `8A1CBB6091E5E222371751D52E334178511BFF30BF0E4FCCA50EB9972D52C9DF`. Both isolated Windows hosts loaded exactly those bytes. The concurrent checkout is dirty and may continue changing; these results do not certify later edits. No product C# change was made by this harness review.

## Fresh evidence

Evidence paths are relative to the repository unless indicated otherwise.

| Check | Result / evidence |
|---|---|
| Release .NET tests immediately before deployment | **1,614 passed, 0 failed/skipped**; `artifacts/harness-live-20261020/pre-live/pre-live.trx` |
| Offline harness gate | 11 suites passed; `artifacts/harness-live-20261020/final-verified/summary.json` |
| Updated comprehensive framework contracts | **48 passed**, including cross-time-zone timestamps and refusal of unowned approvals; `artifacts/harness-live-20261020/framework-final.log` |
| Python contracts | **37 passed**, including Jellyfin 12 Authorization precedence; `artifacts/harness-live-20261020/final-verified/python.log` |
| Fixture generation | 15 repair cases; 89 common formats including real RAR/7z/MOBI; legacy nine-case fixture/validator prerequisites and comprehensive base/scratch generation passed |
| Jellyfin 10.11.11 repair acceptance | **304 passed, 0 failed/blocked**, exit 0; `artifacts/harness-live-20261020/repair-03/summary.json` |
| Jellyfin 10.11.11 common formats | **443 passed, 0 failed**, 89 fixtures, exit 0; `artifacts/harness-live-20261020/formats-01/summary.json` |
| Jellyfin 10.11.11 recycle endpoints | **34 passed, 0 failed**, child and cleanup exit 0; `artifacts/harness-live-20261020/recycle-02/live-recycle.log` |
| Comprehensive acceptance | **107 passed, 1 product failure, 0 errors/skips**, exit 1; `tools/comprehensive-test/test-results/live-harness-20261020-04/` |
| Browser smoke | All seven visible tabs captured by the live comprehensive run; desktop/light screenshots in its `artifacts/ui-*.png` |
| Jellyfin 12 startup and authenticated routes | Active plugin; System/Info, Plugins, configuration page, Status and CSS asset all HTTP 200; `artifacts/harness-live-20261020/server12-probes.json` |
| Jellyfin 12 repair acceptance | **304 passed, 0 failed/blocked**, exit 0; `artifacts/harness-live-20261020/repair-v12-01/summary.json` |
| Linux/Docker integration and CI smoke | Not executed: Docker unavailable on this host. Bash syntax and offline integration-probe contracts passed |

Earlier failed runs remain as diagnostic evidence. In particular, the earlier eight failing approval unit tests were resolved by concurrent product work before the final 1,614-test pre-deployment run. They are not the current result. The initial comprehensive failures included fixture and stale expectation defects; the rerun distinguishes those from the retained product failure.

The repair matrix now proves preservation after a failed repair, blocking Delete review after rescan, refusal to delete without consent, fresh explicit Delete approval, exact recycle provenance, and restoration. Successful repair outputs pass full audio/video decode, stream inventory checks and complete Jellyfin HTTP-body SHA-256 checks. Each repair ladder method is exercised, including corrupt audio in both stream positions. Disabled repair methods refuse safely. Partial salvage success remains a separate coverage gap.

## Confirmed harness defects corrected

1. **False green results:** skipped required coverage produces INCOMPLETE/nonzero. Selected phases with no results are rejected. Failed preflight/setup stops dependent phases. Missing fixture prerequisites fail instead of logging an unverifiable pass.
2. **Live isolation:** explicit opt-in, loopback URL validation, active-data-directory identity, no redirects/proxy credential forwarding, owned path containment, and filesystem-link refusal protect shared media. Global Fix refuses approvals outside the run, including blank paths.
3. **Cleanup and recovery:** settings, queue and task triggers are snapshotted and read back after restoration. Only recorded owned libraries, scratch files, issue approvals and newly created recycle entries are removed. Cleanup failures remain errors; active tasks retain isolation rather than losing inputs.
4. **Backups:** independent copies and manifest SHA-256 checks replace hardlinks. Restoration validates both path containment and resulting bytes. Existing run/output directories cannot silently reuse stale evidence.
5. **Current-run attribution:** task waits require a fresh completed execution; history checks require IDs newer than the current fix baseline. Duplicate and combined tests require current source-specific recycle entries, original hashes, decode and exact restoration.
6. **Real prerequisites:** duplicate variants are independently indexed with matching local NFO identity; the quality/audio combined fixture is genuinely 4K with unwanted audio. ASS fixtures contain removable font blocks with consistent newlines. Trickplay sprites are decodable padded JPEGs.
7. **Managed artwork:** setup uploads an image through Jellyfin for an owned movie, reads its actual metadata path, then corrupts only that owned image. User sidecars are separately preserved. Detection and repair are now real assertions, exposing the product bug below.
8. **Endpoints and collections:** Issues is a bare JSON array. PowerShell REST arrays are normalized before pipeline filtering, fixing task/issue enumeration. API errors are not converted into empty success responses; PowerShell 7 error bodies are handled.
9. **Dry-run and UI:** dry-run uses an available owned font issue and verifies a fresh preview plus unchanged bytes. It no longer assumes a restored duplicate immediately becomes eligible again. UI checks require visible content and save screenshots for all seven tabs.
10. **Python fixtures and failure workflow:** empty/duplicate manifests, changed hashes, path escapes and linked source trees fail before live setup. Failed repair expectations now match preserved-file Delete review. Case cleanup clears only owned approvals. JSON warning casing is handled.
11. **Recycle expectations:** provenance checks now require the actual History origin and history ID. Timestamp freshness compares UTC instants, avoiding the false failure caused by Perth local time versus UTC ticks. Restore collisions verify both recovered and pre-existing file hashes.
12. **Linux integration:** bounded startup/index/scan waits, JSON parsing, fresh completion, required issue counts, loopback binding and unique container ownership replace unbounded/text-only checks. Auth replaces the bootstrap Authorization header with its token, fixing Jellyfin 12 authentication precedence.
13. **Retired unsafe entry points:** malformed-row and migration wrappers now run production .NET regression tests on disposable databases. They no longer mutate an active server DB or treat the historical schema-v9 SQL mirror as current.
14. **One offline entry point and CI:** `tools/test-harnesses.ps1` retains child exits and logs. Python, PowerShell and Bash syntax checks are wired into CI; decoder shims work on Windows/Linux. Remote CI was not run from this review.

See [the full harness inventory](../../tools/TEST-HARNESSES.md) for entry points, dependencies and operational scripts excluded from the gate.

## Retained product failure: managed artwork cannot pass the library gate

The fixture's owned movie is in an enabled test library. Jellyfin stores its uploaded primary image under the isolated host's `metadata/library/.../poster.jpg`. ArtworkScanner detects the zero-byte image, but FixTask filters approval by the image path against media library locations. No ArtworkFixer attempt occurs.

Observed status: `Attempted=0`, `SkippedLibraryDisabled=1`, with `NoWorkReason` explaining that the approved issue is outside enabled libraries. The corrupt poster remains. Evidence is retained under the comprehensive run's `failures/ArtworkFixer_removes_corrupt_0-byte_poster/`.

Both the initial queue filter and execution-time library recheck in `ScheduledTasks/FixTask.cs` need an owner-aware solution. Any fix must validate the owning item's enabled library, the exact managed image path and current protection policy. Blanket permission for the metadata directory would weaken safety. This review leaves the failing acceptance test intact and makes no product permission bypass.

## Registered-surface completeness

There are 18 registered scanners and 15 registered fixers. The harness is a partial acceptance matrix, not universal coverage.

| Surface | Observed coverage / remaining gap |
|---|---|
| DuplicateScanner / DuplicateFixer | Real separate-item detection, current recycle entry, original hash and restore passed |
| SuspiciousFileScanner / SuspiciousFileFixer | Owned inert executable/script fixtures exercised |
| MediaSorterScanner / MediaSorterFixer | Negative placement check; positive move/collision lifecycle incomplete |
| MediaGrouperScanner / MediaGrouperFixer | Doctor Who proposal/collision regression exercised; positive safe grouping lifecycle incomplete |
| StaleContentScanner | Informational query; aged/played/unplayed positive and exclusion fixtures incomplete |
| TranscodeLogScanner | Deterministic positive failed/heavy-transcode log acceptance fixture absent |
| NfoScanner / NfoFixer | Malformed metadata detection and format fix/restore exercised |
| ArtworkScanner / ArtworkFixer | Real managed-image detection; repair blocked by the product library gate described above |
| SubtitleFontScanner / SubtitleFontFixer | Unused embedded-font detection/pruning and current dry-run preview exercised |
| OrphanCleanupScanner / OrphanCleanupFixer | Orphan sidecar detection and removal exercised |
| TrickplayOptimizeScanner / TrickplayOptimizeFixer | Real padded sprite conversion and rescan exercised |
| AudioLanguageScanner / SubtitleLanguageScanner / TrackFixer | Language fixtures, combined cleanup, decode, recycle and restore exercised |
| MissingSubtitleScanner / MissingSubtitleFixer | Real provider/download/manual-choice lifecycle absent |
| QualityScanner / TranscodeFixer | High-bitrate conversion and quality-plus-audio companion lifecycle exercised |
| EmbeddedCoverArtScanner / EmbeddedCoverArtFixer | Common-format embedded-cover fixture; not every cover/metadata-preservation boundary |
| PlayabilityScanner / PlayabilityFixer | Five repair methods, failed-repair review/delete, healthy/excluded controls, exact restore; see repair COVERAGE.md for remaining fault injection |
| FormatCompatibilityScanner / FormatCompatibilityFixer | 89-format conversion/archive/DRM/refusal matrix exercised |

UI smoke is not a full tab x theme x viewport x keyboard/accessibility audit. Full HTTP-body/decode proof is not real-client seeking/playback proof. Configuration persistence does not prove every setting's scheduled runtime behavior. Linux, crash/ENOSPC/permission injection, cross-volume/network recovery and further cancellation/policy races remain release gaps.

## Cleanup and final state

Only the two owned loopback instances were deployed and stopped: Jellyfin 10.11.11 PID 16564 on port 8099 and Jellyfin 12.0.0 PID 5332 on port 8101. Both had zero libraries, zero queued approvals and all three workers idle before shutdown. Configuration matched each saved snapshot; comprehensive task triggers matched their saved values. The unrelated original Jellyfin PID 16152 remained listening on port 8098.

All eight base files in each of the three comprehensive backup sets were restored and then independently verified: no missing or modified originals. Repair/format acceptance also verified exact original restoration. Logs, failure DB snapshots, generated media, backups and isolated host directories are retained for inspection.

Evidence: `artifacts/harness-live-20261020/cleanup-before-stop.json`, `cleanup-stopped.json`, and `comprehensive-fixtures*-verify.log`. Final harness-only verification: 11 suites passed, including 48 framework checks and 37 Python contracts, under `final-verified/`.

The local harness fixes are reviewable. **Do not push or publish the full dirty checkout as a validated release:** the managed-artwork product failure and the unexecuted/absent acceptance scenarios above remain open.
