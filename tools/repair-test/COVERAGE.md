# Repair harness coverage audit

Original audit: 2026-09-27. See [the 2026-10-20 harness review](../../docs/testing/HARNESS-REVIEW-2026-10-20.md) for current harness corrections and fresh verification limits. Scope: playability repair plus shared test-framework reliability. The full organiser, duplicate, track, transcode and UI workflows still have their own comprehensive-test phases. This audit does not certify all of those workflows.

## Live scenarios in `run_live.py`

| Scenario | Required evidence |
|---|---|
| Healthy file and Unicode/brackets/ampersand/dollar filename | Indexed, no playability issue, unchanged bytes and sidecars |
| Duplicate basename in another directory | Only the damaged full path is repaired; healthy namesake intact |
| Light tail truncation | Quick remux selected alone; current history, original recycled, repaired streams decode |
| Large tail truncation | Failed repair preserves bytes; rescan retains blocking Delete review; no deletion without fresh approval; approved Delete recycles the exact original |
| Video packet corruption | Video re-encode selected alone, playable MKV output, stream inventory |
| Broken first audio track with second healthy track | Drop-stream method selected alone; bad audio removed, video unchanged |
| Broken second audio track | Scanner must detect it before the drop-stream flow can run |
| Sole broken audio track with healthy video | Audio re-encode selected alone; video content preserved |
| Forced container coercion | Only container-coerce enabled, correct new extension and indexed output |
| Zero bytes, truncated header | Preserved after failed repair, fresh Delete consent, exact recycling/restoration; no repair saved-byte credit |
| Unrecognisable junk | Safe refusal if probe cannot establish media validity; original intact |
| Every repair method disabled | Safe refusal, original preserved, no implicit deletion or repair credit |
| Valid MP4 payload with MKV extension | Rename only, identical content, no repair credit |
| Existing rename target | Create conflict after indexing; preserve both original and target |
| Excluded-library broken file | No playability issue and unchanged bytes |
| Dry run across actionable fixtures | Exactly attributable history, no file/bin changes or repair-counter changes |
| Repair lifecycle | Exact recycled original, full decode of every audio/video stream, stream counts, complete Jellyfin HTTP-body hash, refresh/rescan |
| Restore lifecycle | Restore each recycled original through API and compare original SHA-256 |
| Isolation/teardown | No foreign history, original settings and held queue restored, own libraries/issues removed, no abandoned staging/recovery files |

Each of the five repair switches is exercised independently. This catches a fixture being salvaged by an earlier method and incorrectly counted as coverage for a later one. It does not exercise all 32 switch combinations or every default-ladder interaction.

## Existing code-level edge coverage

These tests are useful complementary evidence; fake file operations and assertions on code structure are not live fault injection.

| Boundary | Existing test files |
|---|---|
| Source changed/deleted during encode, output absent/empty, source/output alias | `VerifiedReplacementTests.cs` |
| Failed verification, destination file/directory collision | `VerifiedReplacementTests.cs` |
| Policy revoked after encoding | `VerifiedReplacementTests.cs`, `FixPolicyRevalidationTests.cs` |
| Promotion/disposal/final-rename failure; cancellation before and after disposal | `VerifiedReplacementTests.cs` |
| Recovery file survives orphan sweeper | `PlayabilityFixerRepairTests.cs`, `SwapRecoveryHelperTests.cs` |
| Recycle original even when general disposal is permanent | `PlayabilityFixerRepairTests.cs` |
| Missing/unresolvable drive, capacity arithmetic overflow | `FreeSpaceGuardTests.cs`, `PlayabilityFixerRepairTests.cs` |
| Duration-loss slack, short audio versus regional sampling | `PlayabilityFixerRepairTests.cs`, `PlayabilitySamplingTests.cs` |
| Indeterminate probe must not dispose | `PlayabilityFixerRevalidationResultTests.cs` |
| Restore collisions, suffixed paths, directories, protections | `Restore*Tests.cs`, `DirectoryRestoreTests.cs` |
| Protection masks, path boundaries and case behavior | `ProtectionServiceTests.cs`, `LibraryGuardTests.cs` |
| Repair label parsing | `RepairSummaryTests.cs`; label parsing alone does not verify SQL aggregation |

## Required additional environment/fixture gates

These are outstanding coverage, not implied passes:

The old 55-percent tail-truncation fixture exercises failed-repair review on the current build, not successful partial salvage. A separate deterministically repairable duration-loss fixture is required to certify partial-repair wording and saved-byte aggregation live.

| Priority | Gate | What would establish it |
|---|---|---|
| P0 | Actual ENOSPC, locked/read-only media, recycle permission denied | Constrained disposable volume and owned fixtures; source/recovery survival at every failure boundary |
| P0 | Process/server crash during staging, promotion, recycling and final rename | Isolated Jellyfin instance with controlled fault hooks; restart, recover and verify bytes |
| P0 | Live policy/library/protection changes while encoding; scan/fix races | Long-running fixture with controlled barriers, no timing-only guesses |
| P0 | Cross-volume and disconnected network recycle/restore | Two disposable filesystems/volumes plus injected connection failure |
| P1 | Same-path MKV repairs and complete default ladder combinations | Dedicated fixtures verifying rung/action/output and every material configuration interaction |
| P1 | Exact 60-second/30-second sampling boundaries; damage only between sampled windows | Durations below/equal/above thresholds and known damaged packet positions |
| P1 | No-audio, audio-only, multiple video, subtitles/attachments/chapters, HDR and unusual codecs | Per-stream survival/content and metadata expectations, client playback evidence |
| P1 | Symlinks/junctions/hardlinks, UNC, path casing, long names, alternate separators | Owned filesystem matrix on Windows and Linux; verify resolved scope and refusal semantics |
| P1 | Malformed/legacy DB rows, schema upgrades, 30-day boundary, timezone and large totals | Isolated DB instances; exact aggregation, no production-data mutation |
| P1 | Browser UI, seeking, playback on actual clients, GPU fallback | Available browser/device/GPU runtime, assertions against UI and playback state |
| P1 | Linux/Docker, Jellyfin 12, different ffmpeg versions | Same owned-fixture lifecycle matrix on each supported host/version |
| P2 | Huge files/libraries, long runs, cancellation/retry and repeated scans | Resource and persistence assertions on dedicated stress fixtures |

As of 2026-10-20, `torture-malformed-rows.ps1` invokes isolated production regression tests; its active-server database injection path is retired. It is not live HTTP endpoint evidence. Container startup smoke tests establish plugin loading only.

## Findings from this audit

- Fixed harness false-pass paths: empty fixture sets, absent original hashes, stale task completion, default issue filtering that hid queued rows, ambiguous output candidates and missing current-run history attribution.
- Replaced the legacy live script's shared-bin cleanup/global-library setup with generated scoped fixtures, saved settings and checked teardown.
- Confirmed live: corruption confined to the second audio stream can be missed by scanning even though that stream fails direct decode. The runner preserves this as a failure.
- Confirmed in source: `GetRepairSummary` filters `action LIKE 'repaired %'`, excluding successful `partially repaired ...` rows. Live partial-repair counter assertions expose this instead of accepting the undercount.

Final live counts and any additional findings are recorded in the dated run artifacts and appended below after completion. No product fix is bundled into this harness audit.
