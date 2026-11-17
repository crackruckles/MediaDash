# Media Mover Repair Implementation Plan

> **For agentic workers:** Use `superpowers:executing-plans` to implement task-by-task when execution is requested. Steps use checkbox syntax. This document authorizes no deployment, publication, GitHub mutation or production operation.

**Goal:** Move complete media items into Jellyfin-compatible layouts without mixing identities, losing companions, bypassing consent/protection, or leaving changes without recovery and undo; exclude linked aliases from duplicate findings.

**Architecture:** Both mover scanners produce one versioned inventory/identity plan, and both fixers execute that exact plan through one recoverable move service. A small durable operation store records progress before filesystem mutation and survives failures independently of History. Existing library/protection/policy checks, SQLite factory, outcome handling, restore conventions and refresh service remain the surrounding infrastructure.

**Tech Stack:** C#/.NET 9, existing Jellyfin abstractions, Microsoft.Data.Sqlite, existing dashboard JavaScript, xUnit and current PowerShell/Python/Node harnesses. No new package dependency.

**Spec:** The consolidated contract and evidence matrix below are the specification for this plan. Detailed local evidence: [original grouping/link report](C:/dev/mediadash-grouping-investigation-20261023/REPORT.md), [mover compliance report](C:/dev/mediadash-mover-spec-investigation-20261023/REPORT.md), [identity cases](C:/dev/mediadash-mover-spec-investigation-20261023/IDENTITY-SCENARIOS.md), [additional safety cases](C:/dev/mediadash-mover-spec-investigation-20261023/ADDITIONAL-SCENARIOS.md), [current results](C:/dev/mediadash-mover-spec-investigation-20261023/results.json). The contract/tests in this plan must remain usable if the external investigation folders are unavailable.

## Global constraints

- Preserve the deliberately dirty/concurrent checkout. No reset, clean, stash, amend, unrelated refactor, automatic commit, push or release. Each task ends with a reviewed diff/test checkpoint; commit only when authorized.
- Never touch production `192.168.1.117`, real media or an existing Jellyfin service while implementing/testing without explicit authorization. Live acceptance uses owned fixture hosts only.
- Retain current issue type names, settings, XML compatibility tombstones, library selection and disposal choices. Do not reset configuration or globally change the removed DetectOnly migration.
- Target the current net9 plugin and supported Jellyfin 10.11/12 paths. OS/filesystem-specific acceptance is explicit; a Windows run does not certify Linux/NAS.
- Reuse `LibraryGuard`, `ProtectionService`, `FixPolicyEvaluator`, `MediaDashConnectionFactory`, `FileHasher`/SHA-256 streaming conventions, `FixResultRecorder`, `FixLibraryRefresher` and the restore claim pattern. Do not rewrite unrelated repair/recycle workflows.
- A move frees **0 bytes**; copying/moving bytes is not savings. No original or sole recovery copy may be deleted on uncertain evidence.
- Filesystem operations across several paths are not one atomic transaction. Persist progress, preserve recoverability and report partial work honestly.
- No planned test or simulated failure counts as actual cross-volume/server certification. Keep NOT RUN/SKIPPED/INCOMPLETE distinct from PASS.

## Consolidated contract

1. **Identity:** preserve separate movie items and TV series. Reboots use existing disambiguation, provider identity and movie/series premiere years; season release year is not series identity. Provider IDs are optional. Conflicting or insufficient evidence blocks automatic movement before the first mutation.
2. **Organization:** one item directory per movie; series/season/specials hierarchy for TV. Preserve valid existing folder names, all supported provider-tag delimiters, versions, editions and multipart filename relationships. A numeric/Part/Roman/colon title does not establish multipart or collection identity. Avoid automatic franchise nesting; a shared collection must never replace per-film containers.
3. **Ownership:** a verified dedicated container moves whole, including unknown user files, empty subdirectories, metadata, artwork, external streams, extras, themes, local trickplay and exclusion markers. A shared directory requires explicit ownership resolution against every item/path in it. Never steal a longer-prefix title's companion. Ambiguous generic/shared assets block the operation and are shown for review.
4. **Other existing media kinds:** preserve album/disc/lyrics/artwork, book/audiobook/chapter metadata, comic metadata and owned photo sidecars when routing those already-supported kinds. If ownership cannot be established, refuse before mutation; never fall back to flattening just the primary. STRM is a pointer payload; do not fetch/move its remote target.
5. **Consent/scope:** bind the approved plan to item, paths, payload inventory, target settings and policy. Check all source/destination paths, enabled-library scope, descendant/individual protection, exclusion-scope changes and cancellation immediately before mutation and before source removal. Unknown action/version, path mismatch or changed inventory requires a new preview/approval.
6. **Collision/concurrency:** preflight every file/directory destination and the entire batch. Reject self/descendant moves, overlapping operations and same-identity target conflicts. Never overwrite or silently merge. The execution check remains necessary after preview.
7. **Recovery:** copy across volumes into uniquely owned staging, verify payloads, publish without overwrite, then remove unchanged sources. Keep original/recovery on any failure. Persist an operation before work and each step transition; interrupted/ambiguous work becomes NeedsReview, never an automatic replay or cleanup target.
8. **Undo:** reverse the recorded complete operation only after validating current identities, scope and original-path availability. No overwrites, forced swaps or guessed inversion of history text. Preserve user edits and unrelated new imports. Mark restored paths to prevent immediate automatic reapplication.
9. **Integration:** invalidate/rebuild path-bound findings after moves instead of changing `Issue.Path` while leaving stale JSON references. Notify Jellyfin and refresh both affected libraries once per run. Exclude active/recovery paths from orphan cleanup and conflicting mutations.
10. **Links:** exclude file symlinks and linked ancestors from duplicate candidates/keepers, rechecking at fix time. Reject automatic moves of containers containing unsupported links. Treat hardlinks/other same-physical-file aliases separately from independent copies; do not claim their logical length as reclaimable disk space.

Official layout references: [Movies](https://jellyfin.org/docs/general/server/media/movies/), [Shows](https://jellyfin.org/docs/general/server/media/shows/), [provider identifiers](https://jellyfin.org/docs/general/server/metadata/identifiers/), [NFO](https://jellyfin.org/docs/general/server/metadata/nfo/), [Music](https://jellyfin.org/docs/general/server/media/music/), [Books](https://jellyfin.org/docs/general/server/media/books/), [scan exclusions](https://jellyfin.org/docs/general/server/media/excluding-directory/). Recovery/undo/consent are MediaDash requirements; the naming guides do not specify a filesystem transaction protocol.

## Review focus

- An importer replaces a file with the same name, length or timestamps, or adds a child during a copy: preserve the new content and require review (Tasks 3/5).
- Destination collision, policy change or process death between staging/publish/cleanup: at least one verified copy survives; no replay (Tasks 1/4/5).
- A protected or excluded descendant, linked folder, disabled destination or concurrent manual action: reject the whole affected operation (Tasks 2/3/5/7).
- Case-sensitive/normalizing filesystems, long/reserved names, editions and provider-tag variants: distinct items stay distinct (Tasks 2/3/8).
- History/database failure or edited/missing destination before Undo: the journal remains discoverable and user edits stay untouched (Tasks 4/6/8).

## Evidence matrix and task ownership

Current baseline: **52 mover reproductions**, plus the separate real-junction duplicate reproduction. **296 selected existing tests pass** despite the recorded defects. Reproduction assertions currently expect defects; convert these into desired-behavior regressions rather than importing their success expectations.

| Group | Cases to retain in regression coverage | Owner |
| --- | --- | --- |
| Companion completeness | same-stem subs/NFO/art, generic metadata/art, external audio, extras/themes, local trickplay, unknown files, collisions/locked companions | 3/5 |
| TV identity | 2005/2020 reboots, later-season dates, IDs only, no IDs, missing metadata, supported tag delimiters, mixed folders | 3 |
| Movie identity | remakes with bare metadata titles, same year/different IDs, separate Part films, actual split-file movie, versions, collections, missing IDs/years | 3 |
| Safety | sole staging survivor, cancelled calls, replaced source, new children, protected descendants, disabled target, target config drift, .ignore changes | 1/3/5 |
| Persistence | path/JSON mismatch, relocated rows targeting new imports, DB failure after mutation, restart/duplicate execution | 4/5/6 |
| Entry points | scheduled sorter/grouper, Files Move/Rename/bulk operations, optional post-transcode rename, music/books and remaining supported types | 7 |
| Original duplicate issue | real file link, directory link, all-link group, hardlink, ordinary independent-copy control, stale queued aliases | 2 |
| Hardware/host gates | Linux case rules, cross-drive/NAS, ENOSPC/ACL/dropout, crash/undo, Jellyfin indexing/playback/watch-state | 8 |

## File structure and interfaces

New files have one owner each:

- `Fixers/MediaMovePlan.cs`: internal versioned plan plus nested entry/snapshot records. A plan owns top-level move actions; each directory action owns a recursive immutable inventory. Do not execute both a directory action and its children as independent moves.
- `Fixers/MediaMovePlanner.cs`: identity/layout/ownership and full-operation preflight, used by scanners and explicit manual requests.
- `Data/MediaMoveStore.cs`: durable operation manifests, guarded state transitions and overlapping-path claims. Use the existing SQLite factory.
- `Fixers/MediaMoveService.cs`: execution, recovery reconciliation and complete undo; no naming decisions.
- `Probing/PhysicalFileIdentity.cs`: small platform identity/link-count helper shared by alias detection and move snapshots. One internal value type can be nested here; no dependency package or generic filesystem framework.
- Focused tests: `MediaMovePlanningTests.cs`, `MediaMoveExecutionTests.cs`, `MediaMoveRecoveryTests.cs`, `MediaMoveIntegrationTests.cs`, `DuplicateAliasTests.cs`. Reuse existing fixture/proxy/fault-test patterns rather than another permanent harness project.

Plan surface (internal unless an existing DI convention needs the class public):

```csharp
// MediaMovePlan: Version=1, IssueId, ItemId, Type, ApprovalFingerprint,
// PolicyStamp, Entries, BlockingReasons. Entry: Source, Destination,
// IsDirectory, Inventory. Snapshot: RelativePath, IsDirectory, Length,
// LastWriteUtcTicks, PhysicalIdentity, Sha256.
Task<MediaMovePlan> MediaMovePlanner.BuildAsync(
    IssueType type, BaseItem item, IReadOnlyList<BaseItem> peers,
    PluginConfiguration config, CancellationToken ct);
Task<MediaMovePlan> MediaMovePlanner.BuildExplicitAsync(
    string source, string destination, bool rename,
    PluginConfiguration config, CancellationToken ct);
Task<FixResult> MediaMoveService.ExecuteAsync(
    Issue issue, MediaMovePlan approved, CancellationToken ct);
Task<FixResult> MediaMoveService.UndoAsync(string operationId, CancellationToken ct);
Task MediaMoveService.ReconcileAsync(CancellationToken ct);
```

`PolicyStamp` covers configured targets, classification mode, enabled scope and relevant policy. `ApprovalFingerprint` covers immutable identity, sources/destinations, inventories and plan version. Snapshot capture must catch same-size/mtime replacement: use stable physical identity and uncached streamed hashes for the files being approved/moved. Reuse hashing conventions but do not trust the size/mtime cache as fresh destructive-proof evidence. Hash only candidate move payloads, with progress/cancellation, not every file in the library.

Store surface: `string? TryBegin(MediaMovePlan plan)` returns an operation ID or null on active overlap; `MediaMoveStore.Operation? Read(string id)` returns operation ID/state/owner/manifest/history link/detail in one nested record; `bool Update(string id, string expectedState, string state, string manifestJson, string? detail)` is conditional; `bool HasActiveConflict(string path)` checks source/destination/staging ancestors and descendants; `IReadOnlyList<MediaMoveStore.Operation> GetNeedsReview()` exposes incomplete work. Keep private SQL in the store; mutation/state and queue/history links use the existing DB transaction owner where needed.

## Task 1: Contain the known destructive failure paths

**Modify:** `Fixers/MediaSorterFixer.cs`, `Fixers/MediaGrouperFixer.cs`, `ScheduledTasks/FixExecutor.cs`. **Tests:** new execution tests plus `FixExecutorTests.cs`, `GroupingCompanionTests.cs`.

**Consumes:** existing guards/policy and `ProtectionService.IsAnyDescendantProtected`. **Produces:** a safe transitional mover until Task 5 replaces the duplicated execution code.

- [ ] Add failing tests: `PromotionFailurePreservesSourceOrVerifiedRecovery`, `CancellationBeforeMutationLeavesAllPathsUntouched`, `ProtectedDescendantBlocksQueuedFolderMove`, `DisabledDestinationIsRefused`, `DetailsSourceMustMatchApprovedPath`.
- [ ] Run the filtered tests and prove they fail on current behavior.
- [ ] Change cross-volume sorting to copy -> verify -> no-overwrite publish -> recheck unchanged source/current policy -> delete source. Never run cleanup on a sole survivor. Report the surviving recovery path on failure.
- [ ] Add cancellation and full-path/descendant scope checks before mutation. Whitelist move actions and bind details to the approved source. Keep existing dry-run/permission/timestamp handling.
- [ ] Run `dotnet test Jellyfin.Plugin.MediaDash.Tests/Jellyfin.Plugin.MediaDash.Tests.csproj --filter 'FullyQualifiedName~MediaMoveExecution|FullyQualifiedName~FixExecutor|FullyQualifiedName~GroupingCompanion'` and inspect the diff. This checkpoint is containment, not certification of companion completeness.

## Task 2: Fix duplicate aliases and establish reusable physical identity

**Modify:** `Scanners/DuplicateScanner.cs`, `Fixers/DuplicateFixer.cs`, `Fixers/LibraryGuard.cs` only if a shared query must be exposed, `PluginServiceRegistrator.cs`, affected constructor tests. **Create:** physical identity helper and `DuplicateAliasTests.cs`.

**Produces:** `PhysicalFileIdentity.TryRead(string path, out PhysicalFileIdentity.Value value)` returning volume/device, file ID/inode and link count; failure is explicitly inconclusive, not an independent-file identity. Use OS handle metadata on Windows/Linux; platform failure refuses an unsafe alias decision. Retain library reparse guards.

- [ ] Add failing tests for direct links, linked parents, groups made only of aliases, a keeper swapped to a link after scanning, hardlinked paths to the same physical file, and an ordinary independent-copy positive control. Missing symlink privilege is incomplete platform coverage.
- [ ] Exclude links before grouping/probing/hashing, never merely re-append them as losers. Collapse/skip same-physical-file aliases; the conservative default is to exclude multi-link candidates from disposal decisions. Do not advertise logical link length as recoverable savings.
- [ ] Recheck both loser and keeper identity/link status at disposal; invalidate stale queued alias findings without changing unrelated approvals. Keep other duplicate confidence/edition policy intact.
- [ ] Run `dotnet test Jellyfin.Plugin.MediaDash.Tests/Jellyfin.Plugin.MediaDash.Tests.csproj --filter 'FullyQualifiedName~Duplicate|FullyQualifiedName~LibraryGuard'`; record which real link types ran on each OS.

## Task 3: Build complete, identity-safe plans

**Create:** move plan/planner and `MediaMovePlanningTests.cs`. **Modify:** `Scanners/MediaGrouperScanner.cs`, `Scanners/MediaSorterScanner.cs`, `Scanners/MediaFileHelper.cs`, `PluginServiceRegistrator.cs`.

**Consumes:** physical identity, guards, protection and current config. **Produces:** the version-1 plans/fingerprints defined above; scanners serialize them in existing `DetailsJson` and show every blocker.

- [ ] Convert all identity/ownership cases in the matrix to desired assertions. Pin `Show (2005)` and `Show (2020)` separation, a 2020 season staying in a 2005 series, same-year/different-ID films, and bracket/brace/parenthesized provider tags.
- [ ] Pin separate Hobbit films versus one split-file film, merged alternates/editions, true collections with differing titles, no-ID fallbacks and ambiguous/mixed folders. Every independent film retains its own container; remove automatic stripped-title franchise inference from the default organizer.
- [ ] Inventory every payload and empty directory in a verified dedicated container. In shared folders resolve companions against all primaries/versions/parts; `Film-Other` owns its own art. Do not blindly move generic root art. Block unresolved ownership before emitting a runnable plan.
- [ ] Define dedicated ownership concretely: never a library root; every primary media payload must resolve to the same movie item/declared version/part set, the same series, album, or book. Recognized extras can belong to that owner; a different owner or an unexplained primary media file blocks the whole-container fast path. Use the actual series item's source path when it is a valid ancestor of the episodes, rather than assuming each episode's immediate parent is its series. Unknown non-media files remain with a verified dedicated owner. Exact video-prefix ownership takes precedence over a shorter title prefix; unresolved overlaps are blockers.
- [ ] Cover audio/subtitle language/flag suffixes, NFO/image naming scopes, extras/themes, local trickplay, disc directories and unknown dedicated-container files. For music/books/comics/photos use their existing item/container metadata; refuse automatic routing of ambiguous containers instead of flattening.
- [ ] Preserve valid existing layouts. Creating/renaming a movie container must preserve the exact common prefix needed by versions and their companions; do not append an ID to a folder while leaving incompatible filenames behind. Keep filenames unchanged when already valid.
- [ ] Preflight all destinations, source/destination library roots, active claims, child protection, unsupported links, target type conflicts, reserved/long names and batch overlaps. Preserve `.ignore` when moving a whole owner; when shared ancestor exclusion scope changes, require review rather than introducing a custom gitignore parser.
- [ ] Run `dotnet test Jellyfin.Plugin.MediaDash.Tests/Jellyfin.Plugin.MediaDash.Tests.csproj --filter 'FullyQualifiedName~MediaMovePlanning|FullyQualifiedName~MediaGrouper|FullyQualifiedName~MediaSorter|FullyQualifiedName~MediaFileHelper'` and verify repeated planning stabilizes.

## Task 4: Add the durable journal and migration

**Create:** `Data/MediaMoveStore.cs`, recovery tests. **Modify:** `Data/MediaDashSchema.cs`, `Data/MediaDashDb.cs`, `Data/HistoryEntry.cs`, `Data/HistoryStore.cs`, `Data/HistoryRowReader.cs`, DI registration and schema/malformed-row tests.

**Produces:** one `media_move_operations` table with operation ID, issue/item identity, versioned manifest JSON, state, owner token, history link, timestamps and diagnostic detail. Serialize per-entry progress/staging ownership in the manifest. States: `Prepared`, `Executing`, `Completed`, `Undoing`, `Undone`, `NeedsReview`. Only current-owner conditional updates advance a state. Completed manifests persist for undo; active/NeedsReview manifests retain path claims.

- [ ] Add failing fresh/upgrade/repeated-migration and malformed-manifest tests; preserve all existing settings, history and held approvals.
- [ ] Add the table and nullable history move-operation link with schema version **12 from current 11**. If another concurrent change already advances the schema, rebase the migration and choose the next unused version; never replace another migration.
- [ ] Acquire overlapping path claims and create the durable Prepared manifest in one SQLite write transaction. Compare ancestor/descendant source, destination and staging paths using filesystem case rules. No filesystem mutation occurs if journal preparation fails.
- [ ] On restart, classify incomplete manifests as NeedsReview before accepting conflicting work. Do not auto-move, delete staging, release claims or infer success from path existence alone. Reconcile the crash-after-filesystem/before-journal window using stored identities/hashes.
- [ ] Invalidate legacy queued mover details lacking version/fingerprint: return to Detected with a blocking explanation and require a new plan/approval. Preserve unrelated queue types and XML config.
- [ ] Run schema/recovery tests, including two simultaneous claims, repeated startup and corrupt JSON. Verify a missing History row does not hide a pending operation.

## Task 5: Execute and recover the approved operation

**Create:** `Fixers/MediaMoveService.cs`; execution/recovery tests. **Modify:** both mover fixers to delegate, `Fixers/FixResult.cs`, `Fixers/FixReason.cs`, DI. Remove their old best-effort companion mutation path only when all callers migrate.

**Consumes:** immutable plan and durable store. **Produces:** existing typed FixResult with optional `MoveOperationId`; add `MoveNeedsReview` reason without inventing a parallel outcome system.

- [ ] Add failing named fault tests at journal preparation, first/last copy, publish, source cleanup, rollback and progress persistence. Test source replacement/new child/config/protection/cancellation inside these seams, not only before Execute is called.
- [ ] Revalidate the approved fingerprint and entire batch; acquire claims and journal first. Fast-path a same-volume whole-container rename only when its inventory and scope are unchanged. Unknown/inconclusive mount identity uses the verified-copy path.
- [ ] Cross-volume: copy to unique owned staging, stream-verify all files and inventory, flush before promotion, publish without overwrite, then freshly verify sources/current policy/cancellation before each source removal. Rename each source into uniquely owned recovery on its own filesystem before disposal, validate that captured object again, and never delete a new import recreated at the old path. Keep recovery until completion is durable. Hardlinks/relative links that cannot preserve supported semantics are blocked for review. Never delete a changed source, foreign staging or sole verified survivor.
- [ ] Revalidate after a same-volume container rename too. If an importer raced the final preflight, do not report completion or continue other entries: safely restore the complete captured container when the original is vacant, otherwise retain it as NeedsReview. Do not promise atomic exclusion of external processes; account for the race in recovery tests.
- [ ] Before/after every filesystem step persist enough state to reconcile the result. On a partial failure stop; roll back only unchanged owned destinations into vacant originals. Otherwise retain copies, claims and NeedsReview. No automatic retry of a partially applied bundle.
- [ ] Use a narrow internal named-checkpoint hook for deterministic fault injection, following existing fault-test conventions. Do not build a general filesystem framework or repurpose the transcoding replacement protocol as a whole-folder mover.
- [ ] Success requires the complete final inventory. Moves return BytesFreed=0. Preview writes no operation/staging/filesystem changes. Preserve timestamps and supported metadata; cross-volume permissions/attributes need explicit tests and a truthful refusal where they cannot be preserved.
- [ ] Run all mover planning/execution/recovery tests. Remove the sorter's destructive staging cleanup and void best-effort helper once no migrated caller depends on them.

## Task 6: Integrate consent, issue invalidation, History and complete Undo

**Modify:** `ScheduledTasks/FixExecutor.cs`, `FixTask.cs`, `FixPlanBuilder.cs`, `FixResultRecorder.cs`, `FixLibraryRefresher.cs`, `Data/IssueStore.cs`, `Fixers/FixResultPolicy.cs`, `Fixers/LibraryGuard.cs`, `Scanners/OrphanCleanupScanner.cs`, `Fixers/OrphanCleanupFixer.cs`, `Fixers/RestoreService.cs`, `Configuration/ConfigChangeInvalidation.cs`, `ScheduledTasks/ConfigChangeRescanner.cs`, `Api/MediaDashController.cs`, `Api/HistoryDto.cs`, `Api/IssueDto.cs`, `Api/StatusQueryService.cs`, `Configuration/scripts/tabs/issues-tab.js`, `history-tab.js`, `scripts/api-client.js`, relevant `Configuration/i18n/*.json`.

**API:** `GET /MediaDash/Moves` lists visible operation state/recovery detail; `POST /MediaDash/Moves/{operationId}/Undo` calls `UndoAsync`. Use existing admin authorization. History adds `MoveOperationId`, `MoveState`, `CanUndoMove`; recycle Restore semantics stay intact.

- [ ] Add failing tests for old Path/new JSON references, the recreated-old-source import chain, policy edits after approval, duplicate path plans, DB/history failure after publish, double Undo and edited destinations.
- [ ] Bind approvals to the displayed versioned plan. Changed targets, identities, inventory or policy reset mover approval with a readable reason. A stale/refused/partial move must not be marked Fixed merely to suppress retries; keep visible blockers and active operation claims. Automatic queueing must not bypass them.
- [ ] Replace mover path-only relocation with a typed invalidation transaction: update safe path references and invalidate/re-scan any path-bound details whose meaning changed (including duplicate keeper references and probe caches). Do not regex-rewrite arbitrary JSON or preserve approvals for a materially different operation.
- [ ] Persist the operation/history link with truthful progress. If History fails after the mutation, show the durable operation in Moves/History recovery UI; never replay it as an untouched issue. Active/recovery paths are excluded from orphan scans/fixes, `LibraryGuard.SweepOrphanSidecars`, scheduled conflicting fixers and recycle Restore targets. Journal ownership takes precedence over a staging filename matching a generic cleanup pattern.
- [ ] Implement Undo as a newly claimed reverse operation from the saved manifest, validating every identity and original destination before starting. Occupied originals, modified/missing destinations, disabled libraries and protected children refuse safely. Record reverse progress, survive interruption and mark restored paths to prevent immediate automatic reapplication.
- [ ] Show the entire preview inventory, unresolved shared files, blocked/stale reasons, real completion/partial state and Undo availability. Keep user wording plain; expose technical details only in existing disclosures.
- [ ] Reuse `FixLibraryRefresher` for all source/destination libraries and actual item refresh; notify after coherent completion/recovery changes. Verify watch state and IDs on live hosts rather than promising preservation from a filesystem rename alone.
- [ ] Run mover integration, queue-policy, config-invalidation, history, refresh, restore and Node UI regression tests; retain existing recycle restore tests unchanged.

## Task 7: Route the remaining existing entry points and media kinds

**Modify:** `Api/FileBrowserController.cs`, `Api/MoveOrCopyRequest.cs`, `Api/RenameRequest.cs`, `Configuration/scripts/tabs/files-tab.js`, `Fixers/TranscodeFixer.cs`, `Fixers/RenameTemplate.cs`, relevant tests. Copy/upload are not redefined as media moves.

- [ ] Add failing tests for Files Move/Rename/bulk overlap with companions, existing destination collision, active operation conflict, optional post-transcode rename, album lyrics/discs, audiobook chapters, OPF/comic metadata and photo sidecars.
- [ ] Known media requests use `BuildExplicitAsync` and the same preview/execute/undo path. A generic non-media file still moves only its explicitly selected entry, using the same journal/claim protection. Directory moves inventory the complete tree; bulk parent/child selections become one operation rather than double movement.
- [ ] Extend request DTOs with the approved plan token/fingerprint. The UI previews the related paths before execution. Missing/stale tokens for a multi-entry media move return a readable conflict with a fresh preview; document this API behavior instead of silently executing a different bundle for older clients. Files Delete/Copy/Upload keep their current meaning but refuse writes/deletes that overlap an active move claim.
- [ ] Optional post-transcode canonical rename becomes a separate complete move operation for the verified output and its companions; its undo reverses that rename, not the preceding transcode. If a safe plan cannot be formed, keep the verified output under its current name and report the skipped rename. Preserve the stored setting and its default.
- [ ] Prove existing opt-in routing never flattens an album/book or detaches metadata. Preserve dedicated containers; show a blocker for uncertain/shared ownership. Do not introduce a new generic “move everything in the source parent” fallback.
- [ ] Run full mover integration/UI tests and the existing file-browser/transcode-rename/format tests. Inspect every File.Move/Directory.Move caller touched by this module; unrelated repair temp promotions remain outside this refactor.

## Task 8: Acceptance, documentation and publication gates

**Modify:** tests above; `tools/repair-test/run_live.py`, `run_formats.py`, their self-tests, `tools/comprehensive-test/phases/03-fixers.ps1`, `09-reported-bugs.ps1`, `tools/repair-test/COVERAGE.md`, `docs/testing/ROADMAP-TEST-MATRIX.md`, `CHANGELOG.md`, and the relevant README/settings help. Reuse the existing harness entry points; do not retain standalone audit applications as the regression suite.

- [ ] Run `dotnet test Jellyfin.Plugin.MediaDash.Tests/Jellyfin.Plugin.MediaDash.Tests.csproj -c Release`, then `pwsh -NoProfile -File tools/test-harnesses.ps1`. Missing tools/cases are incomplete/nonzero, not green.
- [ ] Run fault tests for kill/restart at every journal/filesystem boundary, ENOSPC, permission denial, NAS interruption, altered staging/source, concurrent moves, edited/occupied Undo targets, malformed manifests and history outage. Verify every fixture payload/unknown file survives at an accounted-for source, destination or recovery path.
- [ ] Run real same-volume and cross-volume fixtures on Windows and a case-sensitive Linux filesystem; include file/directory links, relative links, hardlinks, long/case/Unicode collisions, alternate mounts and readonly targets. Simulated DriveInfo failures do not satisfy these gates.
- [ ] Use owned Jellyfin 10.11 and 12 hosts to prove configuration -> detection -> complete preview -> approval -> move -> library refresh -> rescan/index -> history -> playback/external streams -> Undo -> rescan/playback. Include reboots, remakes, separate Part films, true multipart, editions/versions, no IDs, shared assets, .ignore and each enabled non-video route.
- [ ] Verify active DLL/source hashes and current-run operation IDs; compare all media/companion SHA-256 inventories, watch-state behavior and unknown user files. Confirm a second unchanged scan proposes no more nesting or regrouping and that orphan cleanup cannot dispose of in-flight/recovery content.
- [ ] Restore fixture-host settings/queues/libraries and verify fixture cleanup ownership. Mark live/OS/hardware gaps explicitly. No production deployment is part of this task.
- [ ] Update documentation around per-item organization, optional IDs, ambiguity blockers, complete Undo and legacy-history limits. Old text-only history cannot safely gain automatic Undo retrospectively; require a verified reconstruction/manual recovery.
- [ ] Self-review the contract/evidence matrix against Tasks 1-8, all constructor/DI callers, migration/JSON/XML compatibility, export/package contents and UI localization. Report Safe-to-push/Do-not-push and safe-to-publish/do-not-publish separately; incomplete required acceptance is **do-not-publish**.

## Execution order and completion criteria

Implement **1 -> 2 -> 3 -> 4 -> 5 -> 6 -> 7 -> 8** in the current session or an explicitly chosen isolated checkout. Do not delegate without authorization. The early tasks are reviewable safety checkpoints, not public-release milestones.

The module is fixed only when complete bundles and independent identities are preserved; stale/ambiguous operations cannot mutate; every partial move is recoverable; Undo is safe and complete; link aliases do not become disposable duplicates; and the required real-host/filesystem matrix passes. Green helper tests alone are insufficient.

Planning status: complete. Implementation, full-suite/hardware/live acceptance and release have not started. Latest investigation evidence: 52 scenario reproductions and 296 selected existing tests, with the boundaries stated above.
