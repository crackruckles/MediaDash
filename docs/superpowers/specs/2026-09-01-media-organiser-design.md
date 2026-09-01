# MediaDash Media Organiser — Design Spec

**Status**: Draft — pending user review
**Date**: 2026-09-01
**Replaces**: `MediaGrouperScanner` + `MediaGrouperFixer` (Ungrouped), `MediaSorterScanner` + `MediaSorterFixer` (Misplaced)
**Ship gate**: 100% real-world tested + fuzzed + user-signed off before release notes claim "working"

---

## 1. Goal

Replace the existing MediaGrouper (Ungrouped) and MediaSorter (Misplaced) scanners with a single unified **Media Organiser** that ensures every identified Movie / TV Episode / Anime Episode in the user's enabled Jellyfin libraries sits at its canonical Jellyfin-conventional path (correct folder AND correct filename). Files Jellyfin hasn't identified are surfaced to the Errors tab with an inline manual-rename affordance rather than guessed at.

---

## 2. Scope & boundaries

### In scope

- Movies + TV Episodes + Anime Episodes in libraries the user has enabled (`PluginConfiguration.EnabledLibraries`).
- Files Jellyfin has confidently identified (has provider IDs / year for movies; series identity + season + episode for TV).
- Atomic move + rename + folder creation.
- Sidecar migration (subtitles, `.nfo`, artwork) alongside the primary file.
- History entry per rename; existing Restore-from-History logic reverses the move.
- Errors-tab surface for unidentified files, one entry per file, each with an inline `Rename` button that opens a modal to enter identification manually.

### Out of scope for this ship

- Music, Audiobooks, Books, Comics, Pictures (existing behavior retained).
- Watching a source / Downloads folder (mediasorter's source→library flow — deliberately never in scope).
- Direct TMDB / TVDB calls to enrich missing metadata. Jellyfin already tried — we don't second-guess.
- Multi-season merges (e.g. re-numbering a season because a provider changed the ordering) — Jellyfin handles this itself.
- Deleting files. Organiser only moves + renames; existing OrphanCleanup handles genuine orphans.

### Invariants (locked; enforced by tests)

1. Never moves a file outside its Jellyfin library root (`LibraryGuard.IsInsideLibrary` on source and target).
2. Never overwrites an existing file at the target path.
3. Never touches a file Jellyfin didn't identify with sufficient confidence (Section 4.2).
4. Rename is atomic per file — either fully done + history recorded, or nothing changed.
5. Sidecars either all move with the primary, or the primary rolls back.
6. **Jellyfin userdata (watched state, play position, favorite, rating, playlist membership) is preserved across every move** via `ILibraryManager.UpdateItemAsync` so item identity stays continuous.
7. **Touched library roots get a targeted refresh after the fix batch** — `ILibraryMonitor.ReportFileSystemChanged` per changed folder plus `ILibraryManager.ValidateMediaLibrary` per affected root.

---

## 3. Architecture

Approach A from brainstorming: **new unified `MediaOrganiser` scanner + fixer**, retire the old two.

```
┌─────────────────────────┐
│  MediaOrganiserScanner  │  iterates BaseItem[] filtered by EnabledLibraries
│  (IScanner)             │  emits IssueType.Ungrouped where item.Path != canonical
└──────────┬──────────────┘
           │ Issue { Path, DetailsJson={target, kind, itemId, providerIds} }
           ▼
┌─────────────────────────┐
│  MediaOrganiserFixer    │  atomic move + rename + sidecar + Jellyfin item update
│  (IFixer)               │  writes History entry with source→target
└──────────┬──────────────┘
           │
           ▼
┌─────────────────────────┐
│  FixTask end-of-batch   │  ValidateMediaLibrary per touched root
└─────────────────────────┘
```

Pure functions extracted for exhaustive testing:
- `CanonicalNaming.SanitiseComponent(string) → string` — filesystem-safe path component
- `CanonicalNaming.MoviePath(moviesRoot, name, year, ext) → string`
- `CanonicalNaming.EpisodePath(tvRoot, seriesName, season, episode, endEpisode?, ext) → string`
- `CanonicalNaming.AnimePath(animeRoot, seriesName, season, episode, endEpisode?, ext) → string`

The rest of the code composes these.

---

## 4. Detection logic

### 4.1 Scanner walk

The scanner receives `IReadOnlyList<BaseItem>` from `ScanTask`. It:

1. Reads target roots from config: `MoviesTargetPath`, `TvTargetPath`, `AnimeTargetPath`. If none configured, emits no issues (idle no-op).
2. Filters items to `Movie`, `Episode` (with or without Anime genre tag) whose `Path` is inside an enabled library.
3. For each item, extracts identification fields (Section 4.2), classifies media kind (Section 4.3), computes canonical target (Section 5).
4. If canonical target ≠ current path AND target path does not already exist AND source is inside a library: emits `Issue { Type=Ungrouped, Path=item.Path, DetailsJson={action, target, mediaKind, itemId, providerIds} }`.
5. If item cannot be identified: emits a `MediaOrganiser.Unidentified` diagnostic to the Errors tab instead (Section 6).

### 4.2 Identification requirements

| Kind          | Required fields                                                                 |
|---------------|---------------------------------------------------------------------------------|
| Movie         | `Name`, `ProductionYear`, at least one `ProviderId`                             |
| Episode       | `SeriesName`, `ParentIndexNumber` (season), `IndexNumber` (episode)             |
| Anime Episode | Same as Episode + Anime genre tag (`MediaSorterScanner.HasAnimeGenre`)          |

Anything missing any required field is unidentified.

### 4.3 Media kind classification

Priority order — first match wins:

1. If `AnimeTargetPath` configured AND item has Anime genre → **Anime**.
2. If item is `Movie` AND `MoviesTargetPath` configured → **Movie**.
3. If item is `Episode` AND `TvTargetPath` configured → **TV**.
4. Otherwise → skip (target root not configured for this kind).

### 4.4 Collision handling

If the computed target already exists (some other file at that path), the scanner **does not emit** — it logs a `MediaOrganiser.Collision` diagnostic. Auto-emitting would create a queued issue whose fix can never succeed; better to surface the manual conflict once and let the user resolve.

---

## 5. Canonical naming rules

Following Jellyfin's documented conventions (`https://jellyfin.org/docs/general/server/media/`).

### 5.1 Movie

```
{MoviesRoot}/{SafeName} ({Year})/{SafeName} ({Year}).{ext}
```

- `SafeName` = `SanitiseComponent(item.Name)`.
- `Year` = `item.ProductionYear`.
- Extension preserved verbatim from source (lowercased).
- Example: `/media/movies/Blade Runner (1982)/Blade Runner (1982).mkv`.

### 5.2 TV Episode

```
{TvRoot}/{SafeSeries}/Season {NN}/{SafeSeries} S{NN}E{NN}.{ext}
```

- `SafeSeries` = `SanitiseComponent(episode.SeriesName)`.
- `NN` = zero-padded 2-digit season / episode number.
- **Multi-episode files**: if `episode.IndexNumberEnd` is set and > `IndexNumber`, filename becomes `{SafeSeries} S{NN}E{NN}-E{NN}.{ext}` (Jellyfin's recognised range form).
- **Specials** (season 0): folder is `Season 00`, filename `Show S00E01.mkv`.
- Example: `/media/tv/Breaking Bad/Season 03/Breaking Bad S03E07.mkv`.

### 5.3 Anime Episode

Same shape as TV, different root:

```
{AnimeRoot}/{SafeSeries}/Season {NN}/{SafeSeries} S{NN}E{NN}.{ext}
```

### 5.4 Sanitisation rules (`SanitiseComponent`)

- Strip Windows-reserved chars `< > : " / \ | ? *` → replace with hyphen `-`.
- Strip control chars (`\x00-\x1F`).
- Collapse consecutive whitespace to a single space.
- Trim trailing dots and spaces (Windows disallows these on directory names).
- Refuse to produce an empty string — fall back to `_untitled` if all input stripped.
- Refuse Windows-reserved DOS names (CON, PRN, AUX, NUL, COM1-COM9, LPT1-LPT9) — append `_` if the input matches.
- Preserve Unicode letters, digits, punctuation that aren't in the reserved set (émoji, CJK, cyrillic all fine).
- Pure function — no I/O, deterministic, testable in isolation.

### 5.5 What is NOT in the filename

Deliberately omitted for the 100% bar:

- Episode titles (subject to change on metadata refresh)
- Quality tags (`1080p`, `x265`)
- Release group tags (`-GROUP`)
- Codec tags
- Audio/subtitle language codes on the primary file (sidecar `.en.srt` naming stays as-is)

Rationale: the canonical target must be deterministic given `(item.Id, config)`. Anything that changes across metadata refreshes would cause re-detection loops.

---

## 6. Unidentified-file UX

### 6.1 Errors-tab surface

For every file that reaches the scanner but fails identification, emit one Diagnostic:

- **Source**: `MediaOrganiser.Unidentified`
- **Message** (Movie):
  ```
  {path}: MediaDash couldn't organise this file because Jellyfin hasn't identified it.
  Jellyfin expects movies at 'Movie Name (Year)/Movie Name (Year).ext' inside your Movies library.
  Click Rename to enter the title and year manually.
  ```
- **Message** (Episode):
  ```
  {path}: MediaDash couldn't organise this file because Jellyfin hasn't identified the show, season, and episode.
  Jellyfin expects TV at 'Show Name/Season 01/Show Name S01E01.ext' inside your TV library.
  Click Rename to enter the details manually.
  ```

Diagnostics are already deduped by source + message hash server-side, so re-scanning doesn't multiply entries.

### 6.2 Inline manual-rename button

The Errors tab already renders per-source buttons (precedent: the "Merge into current bin" button on legacy-batch rows). The rendering layer switches on source prefix to inject the `Rename` button on any `MediaOrganiser.Unidentified` entry.

### 6.3 Manual-rename modal

Opens on Rename click. The media kind is passed from server to client via a new `Metadata` string field on `DiagnosticEntry` (JSON: `{"kind":"Movie"}` or `{"kind":"Episode","seriesHint":"..."}`). Requires a small, backwards-compatible extension to the Diagnostics DTO. Modal adapts to kind:

| Field         | Movie | Episode |
|---------------|-------|---------|
| Title         | ✓     | ✓ (series name) |
| Year          | ✓     | ✗       |
| Season number | ✗     | ✓       |
| Episode number | ✗    | ✓       |
| Episode-end (multi-episode) | ✗ | optional |
| Kind toggle (Movie / TV / Anime) | ✓ (defaults per message) |
| Preview: computed target path | ✓ | ✓ |

### 6.4 Manual-rename endpoint

- **Route**: `POST /MediaDash/Organise/ManualRename`
- **Body**: `{ sourcePath, kind, title, year?, season?, episode?, episodeEnd? }`
- **Behaviour**: computes canonical target from the user-provided fields (same `CanonicalNaming` functions), applies steps 3–13 of the fixer flow (Section 7). Response: `{ success, target?, error? }`.
- **Auth**: same admin-only bracket as every other MediaDash mutation endpoint.

The manual-rename endpoint is the **only** code path that bypasses the "must be Jellyfin-identified" gate. The user is providing the identification manually, so we honor it. Jellyfin's own identifier can re-match the file post-rename via the canonical folder structure.

---

## 7. Fixer flow

`MediaOrganiserFixer.FixAsync(issue, progress, cancellationToken)`:

1. **Re-check source** — `File.Exists(issue.Path)` and item still identified. Bail cleanly if not.
2. **Recompute target** — config or metadata may have changed since scan. Bail if computed target now matches source (nothing to do).
3. **LibraryGuard** — `IsInsideLibrary(source)` and `IsInsideLibrary(target)`. Bail with clear error if either fails.
4. **Collision check** — `File.Exists(target)` → fail with "target already exists" (stale failure — surfaces as `MediaOrganiser.Collision` diagnostic; the `IsStaleFailure` heuristic in FixTask marks it Fixed on first failure to stop the retry loop).
5. **Cross-volume free-space check** — reuse pattern from existing `MediaSorterFixer`.
6. **Dry-run gate** — return `FixResult.DryRun(actionText)` if `config.DryRun`.
7. **Capture timestamps** — `srcCreatedUtc`, `srcModifiedUtc` (WIP timestamp preservation pattern).
8. **Create target directory** — `Directory.CreateDirectory(targetDir)`.
9. **Physical move**:
   - Same-volume: `File.Move(source, target)` (atomic).
   - Cross-volume: `File.Move(source, stagingPath)` then `File.Move(stagingPath, target)`. Staging path uses `.mediadash.tmp.{guid}` suffix so `SweepOrphanSidecars` cleans up on crash.
10. **Restore timestamps** — `File.SetCreationTimeUtc` / `SetLastWriteTimeUtc` on target. Best-effort.
11. **Update Jellyfin item identity** — critical for invariant #6:
    ```csharp
    item.Path = target;
    await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken);
    ```
    Keeps `item.Id` stable so all userdata rows (watched, played, favorite, rating, playlist) stay attached. If `UpdateItemAsync` throws, log a `MediaOrganiser.MetadataUpdateFailed` diagnostic and continue — the file is at the canonical path; worst case Jellyfin's own scan re-associates it on the next library validation (Section 7.1) and userdata reattaches by `ProviderId` match. Do NOT rollback the physical move on this failure — the file is already at its correct destination. This is the one intentional exception to invariant #4: physical move + history entry are both durable; the Jellyfin metadata sync is best-effort with a repair path.
12. **Move sidecars** — reuse `MoveSidecars` helper (extracted from `MediaSorterFixer` into a shared `SidecarMover` utility). Sidecar failures logged but don't fail the primary move.
13. **Notify Jellyfin per file** — `_libraryMonitor.ReportFileSystemChanged(source)` and `_libraryMonitor.ReportFileSystemChanged(target)`.
14. **Repoint queued sibling issues** — `_db.RelocateIssuePaths(source, target)`.
15. **Write History entry** — `{ IssueId, Path=source, Action="organised {source} → {target}", RecyclePath=target, WasDryRun=false, Success=true }`. Storing target in `RecyclePath` lets the existing Restore-from-History logic reverse the move (Restore treats it as "move back").

### 7.1 End-of-batch library refresh

Added to `FixTask.ExecuteAsync` after the main loop:

```csharp
var touchedRoots = organiserSuccesses.Select(TargetLibraryRoot).Distinct(...);
foreach (var root in touchedRoots)
{
    try
    {
        await _libraryManager.ValidateMediaLibrary(new Progress<double>(), recursive: true, cancellationToken);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Post-organise library refresh failed on {Root}", root);
    }
}
```

Non-fatal on failure — Jellyfin's own scheduled scan picks it up eventually. The point is to make the user's next dashboard visit already reflect reality.

### 7.2 Restore-from-History

Existing Restore button in the History tab already looks for `RecyclePath` on the entry. For organiser entries, `RecyclePath` holds the new location and `Path` holds the original — Restore reverses the move (target → source). Requires updating the Restore endpoint to recognise "organiser" action prefix; then reuses the same move-with-guards logic.

---

## 8. Config & migration

### 8.1 New config field

```csharp
// PluginConfiguration.cs
public FixMode OrganiserFixMode { get; set; } = FixMode.DetectOnly;
```

Default `DetectOnly` — surfaces issues without moving anything, until the user opts in on the Fixes tab.

### 8.2 Boot migration

On plugin startup (`Plugin.RegisterServices` or equivalent), one-shot check:

- If legacy `UngroupedFixMode` OR `MisplacedFixMode` was `Automatic`, set `OrganiserFixMode = DetectOnly`. Do **not** carry over Automatic — the new fixer renames as well as moves, which is more aggressive than either predecessor.
- If legacy was `DetectOnly` or `Off`, set `OrganiserFixMode` to the same value.
- If legacy was already migrated (flag `_organiserMigrated = true` in config), no-op.
- Save config once, log an INFO line: `"Media organiser: migrated from Ungrouped/Misplaced. Set to DetectOnly by default — enable manually on the Fixes tab after reviewing the plan."`

### 8.3 Deprecated code paths

- `IssueType.Ungrouped` — repurposed for the new organiser (keeps DB rows valid).
- `IssueType.Misplaced` — deprecated. Existing rows preserved for history; no new emission. Old scanner + fixer classes deleted.
- `MediaGrouperScanner`, `MediaGrouperFixer` — deleted.
- `MediaSorterScanner` — deleted.
- `MediaSorterFixer` — sidecar-moving helper extracted to shared utility, then class deleted.

### 8.4 UI changes

- Fixes tab: existing "Ungrouped media" card relabelled to "Media organiser". Description updated.
- Fixes tab: existing "Misplaced files" card removed. Any users with a saved custom FixMode see the migration line in Diagnostics.
- Settings → Libraries: existing Movies / TV / Anime target-path fields stay put — same config drives the new scanner.

---

## 9. Testing strategy (the 100% bar)

Three layers, all required green before shipping.

### 9.1 Layer 1 — Unit + property tests

Pure functions in `CanonicalNaming`:

- `SanitiseComponent`: table-driven for every reserved char on Windows and POSIX, every reserved DOS name, control chars, unicode, extremely long strings.
- **Property**: sanitised output never contains any of `< > : " / \ | ? *`, never empty, never ends in `.` or ` `, never matches a reserved DOS name.
- **Property**: idempotent — `Sanitise(Sanitise(x)) == Sanitise(x)`.
- `MoviePath` / `EpisodePath` / `AnimePath`: table-driven for representative inputs.
- **Property**: `ComputeTargetPath(alreadyOrganisedItem)` returns its current path (no drift).
- Multi-episode range formatting: `S01E01-E02`, `S01E10-E15`, `S00E01-E02` (specials range).

### 9.2 Layer 2 — Fuzz tests

xUnit theories with a `Random` generator seeded per-test:

- Random Unicode + special-char titles (including surrogate pairs, RTL text, combining diacritics).
- Random years (0 to 9999, negative, missing).
- Random episode numbers (0, 1, 99, 999, negative, missing).
- **Assertions**:
  - Never throws (any input handled gracefully).
  - Output path is always a valid path on the current OS (attempt `Path.GetFullPath` — must not throw).
  - Output path is deterministic (same input → same output across N iterations).
  - Output path is always under the provided root (never escapes via `..` or absolute path injection).

Fuzz batch: 10,000 iterations per property, seeded for reproducibility.

### 9.3 Layer 3 — Real-world corpus + integration

**Real-world corpus** (assembled by dispatching a research agent to scrape r/jellyfin, r/plex, and Jellyfin GitHub issues for common filename patterns):

- Target: 500+ real filenames with expected canonical output.
- Categories: TV episode naming variants (S01E01, 1x01, 101, S1E1, dotted, spaced), movie naming variants (year in title, year at end, no year, remake year suffix), anime naming variants (fansub group tags, alternative titles), edge cases (extras, specials, multi-episode, samples).
- Stored in test resources; loaded via `[Theory]` `[MemberData]`. Each entry asserts the transform matches.

**Integration on the running Jellyfin dev server**:

- Fresh fixture library: 20+ real files across Movies (5), TV (10 spanning 2 seasons), Anime (5), plus 3 external subtitle sidecars and 2 `.nfo` files.
- Set watched state on 5 items, favorites on 3, ratings on 2 (via Jellyfin API before the run).
- Run scanner + fixer end-to-end.
- **Assertions**:
  - Every file at canonical path.
  - Userdata preserved: watched flag on the 5 → still true; favorites on the 3 → still true; ratings on the 2 → still equal.
  - Post-run library validation completes; dashboard reflects new paths.
  - Zero duplicate items in the Jellyfin DB.
  - Restore-from-History reverses every move cleanly (loop: organise → assert new paths → restore → assert original paths, userdata intact throughout).

**Cross-volume test**:

- Fixture library on volume A, target on volume B (via junction or second drive if available on the dev box).
- Assert: staging file (`.mediadash.tmp.{guid}`) cleaned up after success.
- Kill process mid-move (SIGKILL / `taskkill /f`) — verify next fix run's orphan sweep cleans the staging file.

**Chaos test**:

- Kill Jellyfin mid-run at each of steps 8, 9, 11.
- Restart. Verify: no partial state (either fully moved + fully updated in DB, or nothing changed). Any inconsistency = test failure.

### 9.4 Ship gate

The release commit that flips `MediaOrganiser` from experimental to shipped cannot be made until:

1. All three test layers green in CI.
2. A manual smoke test run against the user's actual library on the dev machine, with the user watching. Rename plan reviewed by user before Fix Now clicked. Post-run userdata verified by user in Jellyfin UI (watched/favorite/rating state intact).
3. User signs off explicitly: "ship it".

No premature "it works" claim before all three.

---

## 10. Open questions

None currently. The user has asked to work autonomously — decisions defaulted per Section 3–7.

Sub-questions expected to surface during implementation (not blocking spec):

- Exact `ItemUpdateType` flag to pass to `UpdateItemAsync` — may need `ImageUpdate` too if artwork moves.
- How `ILibraryManager.ValidateMediaLibrary` interacts with a scan already scheduled — cancel-cascade behaviour to be observed on dev server.
- Whether `RelocateIssuePaths` correctly repoints queued Playability / SubtitleFonts issues on the same file — already tested in the existing WIP but worth an integration check.

---

## 11. Non-goals reiterated

To keep future scope-creep pressure honest:

- **No source→library flow.** Downloads folders are out.
- **No custom filename templates.** Jellyfin canonical only.
- **No TMDB/TVDB direct calls.** Jellyfin's identifier is the source of truth.
- **No music / audiobook / book / comic / picture reorganisation** in this ship.
- **No cross-library moves** (movie flagged as TV — user resolves manually via Errors tab).
