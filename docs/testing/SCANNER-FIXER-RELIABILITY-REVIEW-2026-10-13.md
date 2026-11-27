# MediaDash scanner and fixer reliability review — 13 October 2026

What this repo does: MediaDash inspects Jellyfin libraries and queues repairs, conversions, moves, and cleanup. Some operations replace or delete user files, so uncertainty must preserve the source. The assumed load is one Jellyfin server with large local/NAS libraries, background imports, metadata updates, and configuration changes during long jobs.

Reviewed the current dirty tree at `C:\dev\mediadash`: all **18 registered scanners and 15 registered fixers**, their dispatch/persistence flow, and relevant probing/converter/replacement helpers. This is a source and isolated-probe review, not live Jellyfin certification. Existing work was preserved; no product source was edited.

Evidence labels: **Probe** means a saved isolated program reproduced the helper or protocol defect. **Sequence probe** reproduces the exact file-operation sequence, rather than invoking the whole fixer. **Source** means the concrete failure case follows from the current code but was not exercised through a running Jellyfin host.

## Must fix

1. **Sorter cleanup can delete the only remaining copy.** [MediaSorterFixer.cs:221](../../Jellyfin.Plugin.MediaDash/Fixers/MediaSorterFixer.cs). **Sequence probe.**
   - **What this is:** Cross-volume sorting moves the source into a temporary destination, then renames it to the final filename.
   - **Problem:** Once the first move succeeds the source is gone. If the final rename fails, such as a destination appearing during the copy, every relevant catch calls `TryDeleteStaging`, which deletes the remaining source content.
   - **Fix:** Copy into staging, verify it and the source identity, promote without overwriting, then dispose of the source. Preserve a recovery file on any failure after source disposal; never clean up the sole survivor.
   - **If skipped:** A failed move can permanently lose media. Add an injected final-rename-failure test and a real cross-volume fixture.

2. **Orphan-folder revalidation overlooks newly added music, books, comics, and pictures.** [OrphanCleanupFixer.cs:240](../../Jellyfin.Plugin.MediaDash/Fixers/OrphanCleanupFixer.cs). **Probe.**
   - **What this is:** The fixer rechecks an old media-free-folder finding before recursively disposing of it.
   - **Problem:** The scanner uses `MediaExtensions`, but the fixer uses only `VideoExtensions`. A FLAC/EPUB/image imported after detection does not stop deletion of the folder.
   - **Fix:** Use the scanner's shared all-media set for the fix-time check. Keep the conservative refusal on read failure.
   - **If skipped:** A stale folder issue can remove newly imported non-video media. Test each supported media family appearing between scan and fix.

3. **Folder operations can bypass protection on children.** [FixExecutor.cs:135](../../Jellyfin.Plugin.MediaDash/ScheduledTasks/FixExecutor.cs), [OrphanCleanupFixer.cs:189](../../Jellyfin.Plugin.MediaDash/Fixers/OrphanCleanupFixer.cs), [MediaGrouperFixer.cs:152](../../Jellyfin.Plugin.MediaDash/Fixers/MediaGrouperFixer.cs), [TrickplayOptimizeFixer.cs:74](../../Jellyfin.Plugin.MediaDash/Fixers/TrickplayOptimizeFixer.cs). **Source.**
   - **What this is:** The executor checks protection on the issue's parent path before dispatching a folder operation.
   - **Problem:** A rule protecting a child does not match its parent. Orphan deletion and grouping do not call the existing descendant check; trickplay conversion does not check individual sprites. A fixer-only child rule, or one added after scanning, can therefore be bypassed.
   - **Fix:** Reuse `IsAnyDescendantProtected` before moving/deleting a tree, and check each file before a folder-wide rewrite. Apply the checks at commit time too.
   - **If skipped:** Never-modify rules can still allow protected files to move, disappear, or change. Test a child rule with an unprotected queued parent.

4. **Failed playability repair falls into deletion without a fresh safety gate.** [PlayabilityFixer.cs:194](../../Jellyfin.Plugin.MediaDash/Fixers/PlayabilityFixer.cs), [PlayabilityFixer.cs:219](../../Jellyfin.Plugin.MediaDash/Fixers/PlayabilityFixer.cs). **Source.**
   - **What this is:** After revalidation, the fixer attempts several repairs and deletes/recycles the source when the ladder returns null.
   - **Problem:** The fallback uses the earlier config and brokenness verdict. If an import replaces the source while an unsuccessful rung runs, or protection/dry-run changes, the fallback can dispose of the new source. Insufficient repair disk space also returns null and takes this deletion route.
   - **Fix:** Capture identity before repair and require unchanged identity, current policy/protection, cancellation clearance, and a still-broken verdict before fallback disposal. Treat inability to attempt repair as deferred rather than failed repair.
   - **If skipped:** Infrastructure failure or a stale verdict can become removal of a recoverable/newly healthy file. Test source replacement, policy changes, and insufficient repair space.

5. **Replacement staging checks only for nonzero size.** [VerifiedReplacementService.cs:199](../../Jellyfin.Plugin.MediaDash/Fixers/VerifiedReplacementService.cs). **Probe.**
   - **What this is:** Verified output is moved into a recovery filename before the source is disposed of.
   - **Problem:** The comments promise a length comparison, but the code only rejects `recoveryLength <= 0`. The fault probe reduced verified output to one byte during staging; the service committed it successfully and deleted the original.
   - **Fix:** Capture verified output length before staging and require the staged length to match. Reverify staged content when the move crosses volumes and stronger fidelity evidence is needed.
   - **If skipped:** Nonempty partial staging output can replace good media. Add a named-stage truncation fault test.

6. **Source and policy checks precede potentially long staging.** [VerifiedReplacementService.cs:137](../../Jellyfin.Plugin.MediaDash/Fixers/VerifiedReplacementService.cs), [VerifiedReplacementService.cs:189](../../Jellyfin.Plugin.MediaDash/Fixers/VerifiedReplacementService.cs). **Probe.**
   - **What this is:** Source identity and policy are checked before the output is promoted to its recovery path.
   - **Problem:** Staging can involve a cross-volume copy. The source can change during that operation, yet disposal does not recheck it. A fault callback replacing the source during staging was followed by deletion of that new source and commitment of the old output.
   - **Fix:** Repeat source identity and current-policy checks after staging, immediately before disposal; preserve the staged output on refusal. This narrows the race without claiming to make concurrent filesystem mutation fully atomic.
   - **If skipped:** Long staging can invalidate the safety decision. Test source and policy changes inside the staging seam.

7. **NFO and artwork deletion trusts stale corruption findings.** [NfoFixer.cs:51](../../Jellyfin.Plugin.MediaDash/Fixers/NfoFixer.cs), [ArtworkFixer.cs:66](../../Jellyfin.Plugin.MediaDash/Fixers/ArtworkFixer.cs). **Source.**
   - **What this is:** These fixers remove metadata previously identified as corrupt.
   - **Problem:** They do not call their scanner's validator again. Jellyfin can replace an empty NFO/poster with a valid one between detection and approval; the fixer still deletes it. Artwork also clears the item's image record first.
   - **Fix:** Reuse `NfoScanner.EvaluateFile` and `ArtworkScanner.EvaluateFile` before mutation. Return NoChange when healthy, and distinguish read/permission failure from confirmed corrupt content.
   - **If skipped:** Valid regenerated metadata or custom metadata can be removed. Test corrupt → valid at the same path between scan and fix.

8. **Cover-art cleanup assumes different track images are duplicates.** [EmbeddedCoverArtScanner.cs:128](../../Jellyfin.Plugin.MediaDash/Scanners/EmbeddedCoverArtScanner.cs), [EmbeddedCoverArtFixer.cs:161](../../Jellyfin.Plugin.MediaDash/Fixers/EmbeddedCoverArtFixer.cs). **Source.**
   - **What this is:** One folder cover is extracted and embedded images can then be stripped from every audio file.
   - **Problem:** The scanner probes only the first file and the fixer never compares artwork. An album/audiobook with intentionally different track/chapter covers loses those distinct images while the issue describes them as duplicate copies.
   - **Fix:** Compare each extracted image with the shared cover before stripping. Preserve differing artwork and require explicit consent for any broader removal.
   - **If skipped:** Unique artwork is lost, permanently when original disposal is permanent. Test two tracks with distinct embedded covers.

9. **Cover stripping verifies stream count but not audio completeness.** [EmbeddedCoverArtFixer.cs:344](../../Jellyfin.Plugin.MediaDash/Fixers/EmbeddedCoverArtFixer.cs). **Source.**
   - **What this is:** The rewritten audio is checked before the shared replacement protocol runs.
   - **Problem:** A readable output with the same number of audio streams passes even if it contains only the first seconds. No duration or retained-audio-content comparison protects the swap.
   - **Fix:** Compare source and output audio duration and retained stream identity, using frame/packet evidence when metadata is inconclusive. Refuse an unverifiable rewrite.
   - **If skipped:** Truncated audio can replace the source. Test a readable shortened output with unchanged stream count.

10. **Subtitle-font and trickplay overwrites lack source and late-policy checks.** [SubtitleFontFixer.cs:150](../../Jellyfin.Plugin.MediaDash/Fixers/SubtitleFontFixer.cs), [TrickplayOptimizeFixer.cs:209](../../Jellyfin.Plugin.MediaDash/Fixers/TrickplayOptimizeFixer.cs). **Source.**
    - **What this is:** These fixers write deterministic temporary filenames and overwrite the source directly.
    - **Problem:** A subtitle edited after parsing or a sprite regenerated during encoding can be overwritten with stale output. Protection/dry-run/fixer changes during processing are not rechecked before the overwrite; SubtitleFontFixer also ignores cancellation.
    - **Fix:** Capture identity before reading, check identity/cancellation/current policy at commit, and use an owned unique temporary path. Verify output before promotion; preserve recovery where the original is valuable.
    - **If skipped:** Concurrent subtitle edits and regenerated images can be lost. Test source mutation and cancellation/policy changes before promotion.

11. **Invalid UTF-8 subtitles are accepted and rewritten with damaged text.** [AssSubtitleFile.cs:75](../../Jellyfin.Plugin.MediaDash/Fixers/AssSubtitleFile.cs). **Probe.**
    - **What this is:** The font optimiser claims to handle UTF-8 and refuse unsupported encodings.
    - **Problem:** `Encoding.UTF8.GetString` silently substitutes invalid bytes. A legacy-encoded `Café` fixture was accepted and saved with a replacement character, so a font-only operation can damage dialogue.
    - **Fix:** Decode with strict UTF-8 error handling and refuse unsupported encoding, preserving the existing UTF-16 refusal.
    - **If skipped:** Dialogue text can be permanently garbled. Add invalid UTF-8 and valid Unicode round-trip tests.

## Should fix

12. **Detect-only duplicate findings suppress other scanners.** [ScanTask.cs:387](../../Jellyfin.Plugin.MediaDash/ScheduledTasks/ScanTask.cs), [ProbingScannerBase.cs:69](../../Jellyfin.Plugin.MediaDash/Scanners/ProbingScannerBase.cs). **Source.**
    - **What this is:** A per-run doomed set avoids probing files presumed destined for deletion.
    - **Problem:** Every duplicate finding is marked doomed regardless of mode, confidence, approval, dismissal, or successful persistence. Detect-only/manual/low-confidence duplicates can therefore hide playability, quality, and language issues on files that will remain.
    - **Fix:** Restrict the shortcut to actually authorised, validated removals, or remove it from detection. Apply protection filtering before deriving that set.
    - **If skipped:** Detection silently misses problems. Test detect-only and below-threshold duplicates alongside a real playability/language finding.

13. **Incomplete scans can erase previously detected issues.** [ScanTask.cs:402](../../Jellyfin.Plugin.MediaDash/ScheduledTasks/ScanTask.cs), [IssueStore.cs:48](../../Jellyfin.Plugin.MediaDash/Data/IssueStore.cs). **Source.**
    - **What this is:** Successful scanner returns replace prior detected rows, often across the whole issue type.
    - **Problem:** Several scanners return partial/empty lists after skipped paths, inaccessible roots, missing dependencies, or user-API failure. The persistence layer treats these as authoritative clean results. Whole-scanner exceptions preserve old rows, but locally handled failures do not.
    - **Fix:** Carry completed scope/inconclusive paths into persistence and replace only successfully examined paths. A dependency failure should preserve that scanner's old detections.
    - **If skipped:** Issues disappear during infrastructure trouble and reappear later. Test an unavailable root and stale-user-API failure with existing detected rows.

14. **Some recursive walkers follow links and can revisit the same tree.** [EmbeddedCoverArtScanner.cs:232](../../Jellyfin.Plugin.MediaDash/Scanners/EmbeddedCoverArtScanner.cs), [OrphanCleanupScanner.cs:210](../../Jellyfin.Plugin.MediaDash/Scanners/OrphanCleanupScanner.cs). **Source.**
    - **What this is:** Embedded-cover discovery and parts of orphan/trickplay scanning use recursive enumeration without the reparse-point exclusion used elsewhere.
    - **Problem:** A junction/symlink pointing to an ancestor can cause repeated traversal, excessive I/O, deep recursion, or failure. The fixer's LibraryGuard does not protect scanner traversal.
    - **Fix:** Skip reparse points consistently with the existing NFO/suspicious-file enumeration pattern. Keep filesystem iteration exceptions inside the enumeration loop.
    - **If skipped:** One linked directory can disrupt a whole scan. Test a controlled link cycle on Windows and Linux.

15. **Disk-walking scanners bypass the unreachable-root filter.** [ScanTask.cs:292](../../Jellyfin.Plugin.MediaDash/ScheduledTasks/ScanTask.cs), [EmbeddedCoverArtScanner.cs:80](../../Jellyfin.Plugin.MediaDash/Scanners/EmbeddedCoverArtScanner.cs), [NfoScanner.cs:69](../../Jellyfin.Plugin.MediaDash/Scanners/NfoScanner.cs). **Source.**
    - **What this is:** ScanTask drops library items beneath roots that fail its five-second health check.
    - **Problem:** NFO, suspicious-file, orphan, embedded-cover, and format filesystem passes independently enumerate configured roots and ignore that filtered item list. They can immediately stat/revisit the same hung share, defeating the task's protection. Synchronous blocked filesystem calls are not interrupted by a cancellation-token check.
    - **Fix:** Feed the task's usable root set into disk scanners, or apply the same shared root-health exclusion before their walks. Preserve findings for excluded roots as in #13.
    - **If skipped:** A NAS outage can still hang a scan. Validate with an isolated unavailable share, not production media.

16. **Hearing-impaired subtitle scan and fix rules disagree.** [MissingSubtitleScanner.cs:54](../../Jellyfin.Plugin.MediaDash/Scanners/MissingSubtitleScanner.cs), [MissingSubtitleFixer.cs:140](../../Jellyfin.Plugin.MediaDash/Fixers/MissingSubtitleFixer.cs). **Source.**
    - **What this is:** Hearing-impaired mode asks for SDH/CC subtitles.
    - **Problem:** The scanner requires HI tracks, but the fixer falls back to a non-HI hit and reports success. The next scan reports missing subtitles again. The fixer deliberately skips its already-present check, so repeated approvals can consume quota/download again.
    - **Fix:** Agree on whether HI is required or preferred; enforce the same rule in both paths. Recheck current subtitle availability before search/download and late policy after the provider search.
    - **If skipped:** Users get repeat issues and wasted provider quota. Test a provider offering only non-HI hits and subtitles added after detection.

17. **Font scanning matches subtitles belonging to other videos.** [SubtitleFontScanner.cs:152](../../Jellyfin.Plugin.MediaDash/Scanners/SubtitleFontScanner.cs). **Probe.**
    - **What this is:** Sidecars are found using `basename + "*" + extension`.
    - **Problem:** `Film.mkv` matches `Film 2.eng.ass` and `FilmExtended.ass`. The optimiser associates the wrong item with those subtitles and can process them under the wrong scan/protection scope.
    - **Fix:** Require the subtitle stem to equal the video stem or follow it with a dot, matching the existing orphan-sidecar pairing convention.
    - **If skipped:** Unrelated subtitles can be offered for changes. Test exact, dotted-language, sequel, and longer-title filenames.

18. **Book/comic probes do not reliably distinguish corrupt payloads from protected content.** [BookProbeService.cs:121](../../Jellyfin.Plugin.MediaDash/Probing/BookProbeService.cs), [ComicProbeService.cs:164](../../Jellyfin.Plugin.MediaDash/Probing/ComicProbeService.cs). **Two probes.**
    - **What this is:** Archive probes underpin playability findings and converted-output acceptance.
    - **Problem:** EPUB checks read the mimetype but only inspect compressed lengths for the remaining entries. A corrupt chapter payload passed. CBZ treats any invalid-data exception reading the first image as encryption; an unencrypted invalid-deflate fixture was reported as password-protected. Later comic pages are not read.
    - **Fix:** Validate required EPUB structure and bounded payload reads; inspect real encryption metadata before labelling DRM. Check comic payloads with cancellation and decompression limits rather than trusting directory listings.
    - **If skipped:** Broken books can be declared healthy and corrupt comics get misleading advice. Retain these two probes as regression fixtures and add a damaged later-page case.

19. **Partial cover/trickplay success hides files needing retry.** [EmbeddedCoverArtFixer.cs:114](../../Jellyfin.Plugin.MediaDash/Fixers/EmbeddedCoverArtFixer.cs), [TrickplayOptimizeFixer.cs:159](../../Jellyfin.Plugin.MediaDash/Fixers/TrickplayOptimizeFixer.cs), [TrickplayOptimizeScanner.cs:344](../../Jellyfin.Plugin.MediaDash/Scanners/TrickplayOptimizeScanner.cs). **Source.**
    - **What this is:** Folder operations report success when some work finishes and some individual files fail.
    - **Problem:** A cover file created before failed strips makes future cover scans/fixes short-circuit. A successful trickplay-folder history timestamp suppresses older JPGs, including failed conversions. The remaining work can become invisible indefinitely.
    - **Fix:** Track failures separately from deliberate no-benefit skips. Let existing covers support retrying unfinished strips, and suppress only actually completed/no-benefit sprites rather than every file older than folder success.
    - **If skipped:** Temporary failures leave permanently unfinished optimisation. Test one success plus one failure, then rescan and retry.

20. **Cancellation after a committed repair can lose its history record.** [PlayabilityFixer.cs:1016](../../Jellyfin.Plugin.MediaDash/Fixers/PlayabilityFixer.cs), [FixTask.cs:662](../../Jellyfin.Plugin.MediaDash/ScheduledTasks/FixTask.cs). **Source.**
    - **What this is:** After replacement commits, PlayabilityFixer probes duration loss using the caller's cancellation token before returning a result to the recorder.
    - **Problem:** Cancellation in that post-commit probe throws, so the executor never returns the applied outcome and history/status persistence never happens. The repaired file and recycled original exist but their successful operation can be missing from history.
    - **Fix:** Make optional post-commit measurement best-effort and ensure the committed result always reaches the recorder. Cancellation must stop new work without erasing a completed effect.
    - **If skipped:** Users can see queued/stale issues after successful repair and lose normal restore/history visibility. Inject cancellation immediately after commit and assert recorded paths/status.

## Coverage

| Scanner/fixer family | Paths examined and result |
|---|---|
| Duplicates | Group keys, editions, ranking, hash/confidence vetoes, keeper viability, disposal/sidecars; shared scan suppression issue #12. |
| Playability | Video/audio/book/comic classification, decode/interleave, reason-specific revalidation, repair ladder, rename, replacement and fallback; #4, #18, #20. |
| Quality / heavy / failed transcodes | Quality eligibility, HDR/attached-picture checks, log aggregation, track retention, transcode verification and policy; shared #5, #6, #12, #13. |
| Audio / subtitle language | Shared removable-track calculation, muxer incompatibility, retained-track verification, combined consent and external-file guards; shared #5, #6, #12, #13. |
| Missing subtitles | Presence/language/HI decision, provider search, download and quota handling; #16. |
| Sorter / grouper | Classification, targets, collision and library checks, main/sidecar moves, issue-path relocation; #1, #3. |
| NFO / artwork | Scope, XML/image validation, disposal and image-record invalidation; #7, #13, #15. |
| Suspicious files | Enabled-root walk, reparse exclusion, extension recheck, disposal; shared #13, #15. |
| Orphans | All-media detection, subtitle/trickplay pairing, metadata ownership, live checks and recursive disposal; #2, #3, #13–15. |
| Trickplay | Both storage layouts, JPG identification, history cutoff, conversion and overwrite; #3, #10, #14, #19. |
| Embedded covers | Folder discovery, first-file probe, extraction, audio stripping, replacement and partial outcomes; #8–9, #14–15, #19. |
| Subtitle fonts | Sidecar discovery, ASS parse/reference analysis, save and overwrite; #10–11, #17. |
| Format compatibility | Indexed/unindexed/folder scanning, library and consent gates, converter selection, AV/book/comic output checks and replacement; shared #5–6, #13, #15, #18. Audiobook merge keeps source folders, but complete-duration/inventory verification would strengthen it. |
| Stale content | User compatibility bridge, exclusions and last-play arithmetic; shared #13. Detect-only, no registered fixer. |

No new isolated family-specific defect is asserted for every family: shared findings affect multiple families. Existing useful protections include tri-state interleave handling, source snapshots in major media fixers, retained-track checks in TrackFixer/TranscodeFixer, destination collisions, source-library guards, recovery filenames, and transactional history/status writes.

## Validation and recommended order

- Existing test suite: **1,560 passed, zero failed/skipped**, using `dotnet test Jellyfin.Plugin.MediaDash.Tests/Jellyfin.Plugin.MediaDash.Tests.csproj --no-restore --verbosity quiet`. Duration reported by runner: 52 seconds.
- Saved isolated harness: **eight reproductions passed**, using `dotnet run --project C:/dev/mediadash-scanner-review-20261013/probes/ReviewProbes.csproj --no-restore --verbosity quiet`.
- Harness checks #1, #2, #5, #6, #11, #17, and both cases in #18. Its assertions prove the unsafe present behavior; they are reproduction checks, not tests proving remediation.
- No product source/configuration changes, deployment, restart, GitHub mutation, or production-host access. Harness creates and removes only its own temporary fixtures.

Fix #1–7 first, then #8–11. Address scan coverage (#12–15) and repeatability (#16–20) afterward. Reuse the existing policy, protection, replacement, and pairing helpers; a new scanner/fixer framework is unnecessary.

One smaller improvement was left out of the numbered findings: FFmpeg-based format converters launch their own processes rather than using the shared executor, so they bypass its low-impact hints and bounded diagnostics. Reusing that executor would improve consistency without another dependency.

Verdict: useful safety foundations, but confirmed failure-path defects remain; address the destructive paths before treating these fixers as robust or certifying a release.

Not checked: live Jellyfin UI/playback/restore, actual cross-volume sorter execution, real NAS outage behavior, Linux/macOS runs, complete converter fidelity, or every line of unrelated recycle/restore/API implementations. Fault probes establish protocol behavior, not the frequency of real filesystem faults. This review is not a safe-to-push/publication certification.
