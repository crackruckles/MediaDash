# MediaDash — Jellyfin Plugin Project Plan

**The one plugin a Jellyfin library owner needs to keep their library tidy, complete, and playable.** MediaDash is the housekeeping layer over Jellyfin: everything a self-hoster would otherwise chase across a handful of scripts and second-tab utilities — duplicate copies, unplayable files, oversized encodes, wrong-language tracks, misplaced files, missing subtitles — surfaced on one dashboard and fixed safely on your schedule.

**Design commitment:** MediaDash fixes what Jellyfin already knows about. It does not go looking for new media. Anything a user has to *want* rather than *own* stays out of scope. The mission is "make the library you have work perfectly," not "get you more of a library."

**Non-goals:**

- Media acquisition — torrent/usenet, indexer integration, arr-style automation.
- Metadata authoring — Jellyfin's built-in editor already handles user-curated fields; MediaDash surfaces gaps and triggers refreshes, it doesn't overwrite what the user chose.
- Multi-server orchestration or cross-library dedup.
- Upscaling, HDR-to-SDR conversion, or any transform that destroys artistic intent.

**This plugin is released publicly to the Jellyfin community.** Two consequences run through every decision below:

1. **Zero machine-specific assumptions.** No hardcoded paths, usernames, drive letters, OS assumptions, or defaults that only make sense on the developer's machine. Everything environment-dependent (library paths, ffmpeg location, data folders) must come from Jellyfin's APIs (`IApplicationPaths`, `IServerApplicationPaths`, encoding options) or plugin configuration. Must work identically on Windows, Linux, macOS, and Docker installs (mind path separators, case sensitivity, permissions). Language defaults must not assume English — the first-run wizard asks.
2. **The UI must be intuitive enough for a non-technical user.** A person who has never read the docs should be able to install it, walk through the first-run wizard one feature at a time, and understand every screen. Concrete standards in §6.

---

## 1. Tech stack & constraints

- C# class library targeting **net9.0** — one binary covers both **Jellyfin 10.11+** and **Jellyfin 12.0+**. Reference `Jellyfin.Controller` and `Jellyfin.Model` NuGet packages (pinned to the 10.11 line) with `<ExcludeAssets>runtime</ExcludeAssets>`; the manifest advertises `targetAbi` entries for both host lines. Cross-version API differences that touch the `User` entity (moved namespaces from `Jellyfin.Data.Entities` to `Jellyfin.Database.Implementations.Entities`, plus `IUserManager.Users` property → `GetUsers()` method) are bridged via reflection in `Scanners/StaleContentScanner.cs` so the same DLL loads on both hosts without a `MissingMethodException`.
- Scaffold from the official template: https://github.com/jellyfin/jellyfin-plugin-template (solution layout, `build.yaml`, `.vscode` debug tasks, GPLv3 license).
- Plugin GUID: generated once with `New-Guid`, never changed.
- Media analysis via **ffprobe**, transcoding via **ffmpeg** — use the binaries Jellyfin already bundles (resolve path from `IServerConfigurationManager` / `EncodingOptions` rather than requiring a separate install).
- Subtitle downloading via **Jellyfin's own `ISubtitleManager`** — no bundled provider; MediaDash inherits whatever the admin has configured under Dashboard → Metadata → Subtitles.
- All state (scan results, fix queue, history) persisted in a **SQLite** DB in the plugin's data folder — not in plugin XML config. Config XML holds settings only.

## 2. Architecture

```
Jellyfin.Plugin.MediaDash/
├── Plugin.cs                        # BasePlugin<PluginConfiguration>, IHasWebPages
├── PluginServiceRegistrator.cs      # IPluginServiceRegistrator — DI wiring
├── Configuration/
│   ├── PluginConfiguration.cs       # settings model (see §5)
│   └── configPage.html              # embedded dashboard UI (see §6)
├── ScheduledTasks/
│   ├── ScanTask.cs                  # IScheduledTask — runs every enabled scanner
│   └── FixTask.cs                   # IScheduledTask — executes approved/auto fixes
├── Scanners/
│   ├── IScanner.cs
│   ├── ProbingScannerBase.cs        # shared per-file evaluation loop
│   ├── DuplicateScanner.cs
│   ├── PlayabilityScanner.cs
│   ├── QualityScanner.cs
│   ├── SubtitleLanguageScanner.cs
│   ├── AudioLanguageScanner.cs
│   ├── MediaSorterScanner.cs        # movies in TV folder / vice versa
│   ├── MissingSubtitleScanner.cs    # no subs in any wanted language
│   └── StaleContentScanner.cs       # unwatched past a threshold (detect-only)
├── Fixers/
│   ├── IFixer.cs
│   ├── DuplicateFixer.cs            # delete/trash losing copy
│   ├── TranscodeFixer.cs            # re-encode via ffmpeg
│   ├── TrackFixer.cs                # strip unwanted audio & sub tracks (remux)
│   ├── PlayabilityFixer.cs          # remove unplayable — re-verified at fix time
│   ├── MediaSorterFixer.cs          # move file to correct library folder
│   ├── MissingSubtitleFixer.cs      # download via ISubtitleManager
│   └── RecycleBin.cs                # trash folder + retention purge
├── Data/
│   ├── MediaDashDb.cs               # SQLite: issues, fix_queue, history, file_probe_cache
│   ├── Issue.cs, IssueType.cs, IssueStatus.cs, IssueSummary.cs, HistoryEntry.cs
├── Api/
│   └── MediaDashController.cs       # ControllerBase — REST endpoints for the UI
└── Probing/
    └── FfprobeService.cs            # runs ffprobe, caches results by path+size+mtime
```

Key Jellyfin integration points: `ILibraryManager` (enumerate items/paths), `ISubtitleManager` (search + download subtitles from configured providers), `IScheduledTask` (both tasks appear in the standard Scheduled Tasks dashboard), `IPluginServiceRegistrator` for DI wiring, `ControllerBase` for the API, `IHasWebPages` for the config/dashboard page.

## 3. Scanners

Each scanner emits `Issue` rows: `{id, type, itemId, path, details(json), suggestedFix, sizeSavings, status: detected|queued|fixed|dismissed}`. Scanners inherit `ProbingScannerBase` (shared per-file loop) unless they need a whole-library view.

1. **DuplicateScanner** — groups items by provider IDs (TMDb/TVDB/IMDb) via `ILibraryManager`, falling back to normalized name+year (movies) / series+season+episode (TV). Within a group, ranks copies by a "keeper" policy (configurable order: resolution > codec preference > bitrate > file size). Suggests deleting the losers. Never compares across different editions unless "treat editions as duplicates" is enabled.
2. **PlayabilityScanner** — ffprobe every file; flags: probe failure, zero/negative duration, no video stream, container/codec combos Jellyfin can't direct-play or transcode, truncated files. "Thorough" mode (default on) test-plays start + middle + end via ffmpeg; results are cached for unchanged files. Beyond exit code, thorough mode scans stderr for `File ended prematurely` / `Truncating packet` (ffmpeg quirk: emits these but exits 0), cross-checks container `bit_rate × duration` against actual file size, and compares the last `time=HH:MM:SS.ms` from ffmpeg's `-stats` output against what was requested. Together these catch files that "sort of play" — files ffprobe accepts as valid but that stop short during actual decode.
3. **QualityScanner** — user-set ceiling: max resolution (default 1080p), max video bitrate (default 8 Mbps @1080p, scaled by resolution), preferred codec (default HEVC). Files above any ceiling are flagged with estimated savings (`currentSize − estimatedSize`). Skip files already at/below ceiling or within a configurable tolerance (default 15%) to avoid churn. HDR content skipped by default.
4. **SubtitleLanguageScanner** — flags embedded subtitle tracks and external `.srt`/`.ass` files whose language isn't in the allowed list. Untagged (`und`) tracks are always kept.
5. **AudioLanguageScanner** — flags files with audio tracks outside the allowed list. Never suggests removing the ONLY audio track, and never removes the last allowed track even if it means keeping a disallowed one (safety invariant).
6. **MediaSorterScanner** — a movie physically located under a TV library, or a TV episode under a Movies library. Uses Jellyfin's own classification (`BaseItemKind`) or a filename-heuristic fallback (`SxxExx` / `NxN` patterns) per user choice.
7. **MissingSubtitleScanner** — Video items with no subtitle track (embedded or external) in any wanted language. Only runs when at least one subtitle language is configured; only meaningfully fixable when the admin has set up a subtitle provider in Jellyfin.
8. **StaleContentScanner** — media that has been on the server past `StaleThresholdDays` (default 365) AND has no play record within that window across any user account. Both conditions must hold, so freshly-imported items aren't flagged immediately. Detect-only: MediaDash doesn't ship a stale-content fixer because pruning old-but-unwatched media is a subjective call. `SizeSavings` is populated so the Overview "Space you could reclaim" total reflects the stale bytes.

Scan results are incremental: `FfprobeService` caches probe output keyed on `(path, size, mtimeUtc)` so unchanged files are skipped on re-scan.

## 4. Fix engine

- Each fix type has an independent mode: **Off / Detect only / Manual approve / Automatic**.
- Each *removing* fix type has an independent disposal: **Recycle bin** (default, plugin-managed trash folder, configurable retention, one-click restore) or **Permanent**. Media sorter (moves) and missing-subs (adds) have no disposal.
- `FixTask` runs on a 15-minute opportunistic interval and defers via the idle check while anyone is watching or has been active in the last 15 minutes. Automatic-mode issues go straight in; manual-mode issues wait for approval from the UI.
- **Transcode fix:** ffmpeg re-encode to the ceiling, hardware encoder (NVENC / AMF / QSV / VideoToolbox) used when available with automatic per-file software fallback. Output → temp file → ffprobe verify (duration within 2s, streams present) → swap in → original disposal per config.
- **Track strip fix:** remux with `ffmpeg -map` excluding disallowed tracks, `-c copy` (lossless). Same temp→verify→swap flow.
- **Duplicate fix:** move losing file(s) to disposal target; trigger library refresh on the affected item.
- **Playability fix:** re-verify at fix time (a scanner-flagged file that plays fine now is never removed).
- **Media sorter fix:** `File.Move` into the configured target folder inside a Jellyfin library; `LibraryGuard` refuses any target outside a library root.
- **Missing subtitles fix:** `ISubtitleManager.SearchSubtitles` per wanted language → download the first hit via `DownloadSubtitles`. Failure messages surface the specific reason (no providers configured, no matches, network error).
- Concurrency: max 1 transcode at a time by default (configurable); pause when Jellyfin reports active playback if "avoid interfering with playback" is on (default on).
- Every action logged to `history` with before/after size and a restore reference.

### Safety invariants (non-negotiable, enforce in code + tests)

- Never touch a file outside configured library paths.
- Never remove the last audio track or the last video stream.
- Never replace a file whose transcode/remux failed verification.
- Never move a file to a target outside a Jellyfin library root.
- Dry-run mode: global toggle that logs what *would* happen; ships with dry-run ON by default for the first run.
- Free-space check before transcoding (need ~2× file size headroom).

## 5. Configuration model (PluginConfiguration)

Enabled libraries (default all); per-fix-type mode + disposal; recycle bin path + retention days; quality ceiling (resolution, bitrate table, codec, tolerance %, HDR skip, encoder preset); re-encode source file types + target container; hardware encoder toggle + preferred GPU index; allowed audio languages + allowed subtitle languages; duplicate keeper policy order + treat-editions-as-duplicates toggle; thorough playability check on/off; media sorter target paths + source (Jellyfin metadata vs filename); rename-after-transcode toggle; max concurrent transcodes; pause-during-playback; dry-run; `FirstRunDone` (gates the wizard).

## 6. UI (configPage.html — embedded plugin page in Jellyfin web)

Single-page dashboard, plain JS + Jellyfin's built-in `emby-*` web components (matches native look, no build step).

**Intuitiveness standards (release-blocking, not nice-to-have):**

- **First-run wizard** — one feature at a time, in the order: Welcome → Libraries → Duplicates → Broken files → Oversized → Languages → Media sorter → Safety → Done. Progress dots, back / skip / continue, per-step save. Triggered by `!FirstRunDone`; survives plugin updates because config XML is preserved.
- Plain language everywhere: "Files wasting space" not "QualityScanner issues"; "Safe to delete — a better copy exists" not "duplicate group loser". No codec/bitrate jargon on primary surfaces; details available behind an expand.
- Every destructive button states its consequence inline ("Moves 3 files (4.2 GB) to MediaDash's recycle bin — recoverable for 30 days").
- Every setting has a one-line description of what it does and what the default means; risky settings (permanent delete, full-auto mode) require an explicit confirmation.
- Empty states explain what will appear and how to trigger it.
- Progress feedback for long operations with item counts, not spinners alone.
- Live system-performance card (CPU / RAM / GPU) at the top of Overview — task-manager-style, host- and per-GPU-aware.
- Follows Jellyfin dashboard styling (dark/light themes, mobile-responsive).
- All UI strings in one place, structured for future localization.

Tabs:

1. **Overview** — welcome-card wizard when unset, then headline savings / per-type cards / drives / system stats / "Scan now" & "Run fixes now" buttons.
2. **Issues** — filterable per-type list with per-row Approve / Dismiss and bulk approve-all-of-type.
3. **History** — completed fixes, space saved over time (area chart), Restore for anything in the recycle bin.
4. **Files** — scoped file browser for the configured libraries (rename / move / delete inside library boundaries).
5. **Errors** — swallowed exceptions from scanners/fixers, with per-run retry.
6. **Settings** — everything in §5, grouped by section (Safety / Languages / What to fix / Quality / Libraries / Advanced / Recycle bin / Maintenance).

API endpoints (`/MediaDash/...`, `[Authorize(Policy = "RequiresElevation")]`): `GET /Status`, `GET /Issues?type=&status=&openOnly=`, `POST /Issues/{id}/Approve|Dismiss`, `POST /Scan`, `POST /Fix`, `GET /History`, `POST /History/{id}/Restore`, `GET /RecycleBin`, `POST /RecycleBin/Empty`, `GET /Errors`, `POST /Errors/Retry`, `GET /LibraryAccessCheck`, `GET/POST /Files/*`.

## 7. Build order

Shipped (v0.1 → v0.5.x):

1. Scaffold, DI, SQLite layer, `FfprobeService` + cache.
2. Five original scanners (dupes, playability, quality, subs, audio).
3. Read-only API + Overview / Issues UI.
4. Fix engine (RecycleBin → TrackFixer → DuplicateFixer → TranscodeFixer → PlayabilityFixer) with dry-run.
5. Approve flow + FixTask + History + one-click Restore.
6. First-run wizard (multi-step, feature at a time).
7. Media sorter (scanner + fixer).
8. Live system stats (CPU / RAM / GPU), including AMD APU `gpu_metrics` fallback.
9. Missing-subtitle scanner + `ISubtitleManager` fixer.
10. Files tab, Errors tab, per-fix-type disposal, hardware GPU picker.

## 8. Whole-library housekeeping roadmap

The "one plugin you need" ambition is delivered by continuing to fold in library-owner chores that today live in one-off scripts. Each item ships as another `IScanner`/`IFixer` pair reusing the existing infrastructure (issue lifecycle, dry-run, disposal, wizard step, Overview card).

Prioritized by value × safety:

1. **Missing metadata** — items with no poster, no backdrop, no overview, missing year or provider IDs. Fix: trigger Jellyfin's own metadata refresh with a specific replacement strategy per gap.
2. **Missing chapter markers** — video files with no chapter table. Fix: generate via silence-detection or fixed intervals (user picks).
3. **Corrupt / stale artwork** — 404'd remote images, orphaned local artwork files. Fix: re-fetch or clean up.
4. **Duplicate subtitle files** — two `.srt` for the same language, or embedded + external duplicates.
5. **Series holes** — TV shows missing episodes between existing ones (aired but absent). Detect-only; deliberately no acquisition path.
6. **Naming drift** — files whose on-disk name no longer matches the plugin's canonical template. Fix: rename in place (already partly implemented as an opt-in post-transcode step).
7. **Orphaned recycle-bin entries** — files whose original library is gone. Fix: prompt to purge or migrate.

Any addition MUST honour §4 safety invariants and must fit the "surface what Jellyfin already knows" scope commitment — nothing that requires an external service the user hasn't already configured for Jellyfin proper.

## 9. Test fixtures

Tiny synthetic files generated by `tools/make-fixtures.sh`: a 4K H.264 high-bitrate clip (quality hit), same movie in two files (duplicate), a truncated file (playability), a clip with eng+fra+deu audio (audio strip), a clip with unwanted sub tracks (sub strip), a movie under a TV path (misplaced), a movie with no subtitle track (missing subs), a clean file (no issues).

## 9a. Known bugs (release-blocking for v0.9)

Surfaced by the cross-ABI smoke test on 2026-07-29 (SuspiciousFileScanner E2E on v10.11.11 + v12.0.0).

1. **`EnabledLibraries` filter drops every library on v12** — `/Library/VirtualFolders` on Jellyfin 12 returns entries with no `ItemId` field: `[{"Name":"MediaDash Test","Locations":[...],"CollectionType":"movies"}, ...]`. Compile-time reference to `VirtualFolderInfo.ItemId` doesn't throw on v12 (the property still exists in the 10.11 SDK we compile against) — it just returns null at runtime. Users with a configured library scope will see "0 issues" across the board after upgrading to v12.

   **Backend callsites (three, all filter `.Where(f => list.Contains(f.ItemId, ...))`):**
   - `Scanners/SuspiciousFileScanner.cs` line 81 — `EnabledLibraries` scope
   - `ScheduledTasks/ScanTask.cs` line 97 — `EnabledLibraries` scope
   - `Scanners/StaleContentScanner.cs` line 79 — `StaleExcludedLibraryIds` scope

   **Frontend surface:** `Configuration/configPage.html` reads `f.ItemId` from Jellyfin's native `/Library/VirtualFolders` endpoint in multiple places (lines ~3028, 3120, 3193, 3304-3388). Without a stable ID from the server, the library-selection checkboxes render with `data-id="undefined"` on v12 and save an empty list — indistinguishable from "all libraries", but with no way to actually scope.

   **Fix design (small, contained):**
   1. ✅ **Done 2026-07-29** — New helper `Scanners/VirtualFolderIdentity.cs`: static `GetId(VirtualFolderInfo)` returns `f.ItemId` when non-empty, else reflection-cached lookup of `Id` (Guid) → `ToString("N")`. Single reflection resolve at load, `PropertyInfo` cached in a static.
   2. ✅ **Done 2026-07-29** — Unit test `Jellyfin.Plugin.MediaDash.Tests/VirtualFolderIdentityTests.cs`: 3 facts covering ItemId-set, both-empty, empty-string. 16/16 tests pass.
   3. ✅ **Done 2026-07-29** — Three backend callsites swapped to `VirtualFolderIdentity.GetId(f)`:
      - `Scanners/SuspiciousFileScanner.cs:81`
      - `ScheduledTasks/ScanTask.cs:97`
      - `Scanners/StaleContentScanner.cs:79`
   4. ✅ **Done 2026-07-30** — New `GET /MediaDash/Libraries` endpoint (`Api/MediaDashController.cs` + new DTO `Api/LibraryInfo.cs`) returning `[{ItemId, Name, CollectionType, Locations}]` where `ItemId` routes through `VirtualFolderIdentity.GetId`. DTO field names mirror Jellyfin's own `VirtualFolderInfo` JSON shape so the frontend swap was URL-only — three callsites in `Configuration/configPage.html` (`ApiClient.getJSON(ApiClient.getUrl('Library/VirtualFolders'))` → `api('Libraries')`) at the Settings-load, Stale-excluded, and Wizard-load blocks. No `f.ItemId` / `data-id` churn needed.
   5. ✅ **Done 2026-07-30** — Added §4a "Scoped-scanner assertion" and a new row for `VirtualFolderInfo.ItemId` to `tools/verify-cross-abi.md` (release gate now blocks on it).

   6. ✅ **Done 2026-07-31** — Reworked `VirtualFolderIdentity.BuildIdLookup(ILibraryManager)` to enumerate `CollectionFolder` BaseItems and build a `Name → Id.ToString("N")` map. Match key: **library name only** (`CollectionFolder.Path` is Jellyfin's internal `root/default/<Name>` shortcut, not the on-disk media location, so path-matching never aligns with `VirtualFolderInfo.Locations` — Jellyfin refuses duplicate library names so name alone is a unique key in practice). `GetId(folder, lookup?)` short-circuits on native `ItemId` for v10 (no wasted lookup work) and falls back to the lookup on v12. 6 unit-test facts. Live-verified on v12: `/MediaDash/Libraries` now returns all 6 libraries with populated ItemIds matching the config XML; scoped scan reports MalwareRisk = 1 with `EnabledLibraries` populated.

   **Non-goals:** don't migrate existing saved `EnabledLibraries` GUIDs — v10 stored `ItemId`, v12 stores `Id.ToString("N")` which are the *same* Guid in different notations, so an upgrade path across ABIs is a follow-up if it turns out they diverge (verify by dumping both sets against the same library once fix is in).

**Ruled out during triage (2026-07-29):** "DLL loads as `MediaDash 0.0.0.0`" during smoke test — this is by design. `Directory.Build.props` intentionally defaults to `0.0.0.0` for dev builds; `tools/release.ps1` passes `/property:Version=X.Y.Z` at release time so shipped DLLs stamp correctly. Not a regression.

## 9b. Settings tab overhaul

User-reported (2026-07-30) grab-bag of Settings-tab issues to land as a single pass. Each item is one edit; the whole card should be reviewed for consistency at the end. Group work into three landings — layout first (visible wins), then Maintenance additions (new backend + button), then data-driven pickers (needs library scan).

**All four landings ✅ completed 2026-07-31. Live-verified on both Jellyfin 10.11.11 and 12.0.0** — every new endpoint (`GET /MediaDash/Libraries`, `GET /MediaDash/Genres`, `POST /MediaDash/Scan/Suspicious`, `GET /MediaDash/PostUpgradeCleanup/Status`) returns correct data on both ABIs; full ScanTask still runs cleanly on both; MalwareRisk detection remains 1 on the fixture suspicious file; v12 migration button gates itself off on Jellyfin 10.

**Landing 1 — layout, alignment, structural fixes** (`Configuration/configPage.html` only, no C#):
1. **Broken link "Retention and bin location live in Recycle bin"** — currently anchor-targets a Recycle-bin sub-section on Settings but the target is empty on the current layout, so the browser scrolls to blank space. Fix: after promoting Recycle bin to its own tab (item 4 below), rewrite this link to `#mdTabRecycleBin` or the tab-activation JS handler. Verify the link works both on initial load and after tab-switching.
2. **"Thorough playability check" checkbox overflow** — checkbox label wraps outside its container to the left. Root cause is likely the label using a wider intrinsic width than the two-column card layout allocates. Fix: either constrain the label with `max-width: 100%; overflow-wrap: anywhere;` or restructure the row to one-column when the label is longer than the input.
3. **Max-resolution slider vs. bitrate input misaligned** — slider is narrower than the `MaxBitrateMbpsAt1080p` input above it, and the "Mbps" unit label needs left-centered vertical alignment against the input's column. Standardise slider width to `100%` of its column; align "Mbps" with `display: inline-flex; align-items: center` next to the input, not on its own line.
4. **Uniform control sizing across the Settings tab (release-blocking polish)** — audit every `<select>`, `<button>`, `<input type="text|number|range">` on the Settings surface, standardise:
   - **Height**: 40px (or Jellyfin's `emby-input` intrinsic height, whichever matches native dashboard) via a single `.mdControl { height: 40px; box-sizing: border-box; }` class applied to all three.
   - **Width for form controls** (dropdowns, sliders, text inputs, number inputs): 100% of their column, with a max-width cap so single-line cards don't stretch to 800px.
   - Buttons keep intrinsic width but the same height rule.
   - Land this after all other layout changes so the rule sweeps a stable DOM.
5. **Reorder cards — Libraries at top of Settings** — currently `#mdLibrariesCard` sits near the bottom. Move it directly under the sticky page header, before the fix-type cards. Update the anchor nav chips (if any) so the ordering matches.
6. **Recycle bin gets its own tab** — extract every Recycle-bin surface currently inside the Settings tab (retention days, bin path, "empty bin now", per-item restore list) into a new top-level tab: **Overview / Issues / History / Files / Errors / Settings → + Recycle bin**. Insert between Files and Errors. Remove the Recycle-bin section from the Settings tab entirely (leave a single link pointing to the new tab for muscle-memory users). Keep API surface unchanged (`GET /MediaDash/RecycleBin`, `POST /MediaDash/RecycleBin/Empty`, per-item Restore).

**Landing 2 — Maintenance section additions** (backend + frontend):
7. **"Start virus scan" button in Maintenance** — new button that fires just the `SuspiciousFileScanner` (skips the other nine). Two options:
   - **A** (preferred, cleaner): new endpoint `POST /MediaDash/Scan/Suspicious` that resolves the registered `SuspiciousFileScanner` from DI, calls `ScanAsync` with the current library items, and writes results via `_db.ReplaceDetectedIssues`. Roughly 25 lines in `Api/MediaDashController.cs` mirroring the shape of the existing `POST /MediaDash/Scan`.
   - **B** (cheaper, uglier): trigger the full ScanTask but with a per-scanner allowlist config toggled just-in-time. Rejected — leaks a global toggle for one button.
   Frontend: button in Maintenance card, calls `api('Scan/Suspicious', 'POST')`, shows toast on completion, refreshes Issues tab if it's currently visible.
8. **"Start v12 migration" button in Maintenance** — new button that fires the existing `PostUpgradeCleanup` service (already wired into DI per `PluginServiceRegistrator.cs`). Same shape as item 7 — new `POST /MediaDash/Migration/V12` endpoint if one doesn't exist. Button disabled with a tooltip explaining why when host version reports `< 12.0.0.0` (read from `IServerApplicationHost.ApplicationVersion` — already available in the controller). Gate the button both visually (`disabled` attribute) and server-side (`if (host < 12) return BadRequest(...)`) so a determined user with dev-tools can't fire it on 10.11.

**Landing 3 — data-driven pickers** (needs new endpoints, biggest change):
9. **Languages: dropdown of Jellyfin-native language list** — replace the two freeform CSV text inputs (Allowed audio / Allowed subtitles) with multi-select `<select>` (or `emby-checkbox` list) sourced from Jellyfin's own `GET /Localization/Cultures` endpoint. That endpoint returns `[{Name, DisplayName, TwoLetterISOLanguageName, ThreeLetterISOLanguageName}]`. Save the ThreeLetterISOLanguageName codes (matches current `AllowedAudioLanguages` / `AllowedSubtitleLanguages` schema — no migration needed). Frontend: replace the text inputs with the picker; keep the underlying config keys.
10. **Genres-to-skip: dropdown sourced from library genres** — currently `StaleExcludedGenres` is a freeform CSV. Replace with multi-select populated from a new `GET /MediaDash/Genres` endpoint that returns the distinct genres present in the user's libraries (`_libraryManager.GetGenres()` on 10.11 / equivalent on 12 if it moved). Cache the response client-side per settings-load. If the user's saved genres include something the library no longer has, show it in the picker anyway with a "(no items)" hint so they can keep or remove.
11. **Misplaced-files scanner: settings for comics, music, pictures** — currently `MediaSorterFixer` only knows Movies / TV / Anime target folders. Extend `PluginConfiguration` with `ComicsTargetPath`, `MusicTargetPath`, `PicturesTargetPath` (paths inside a Jellyfin library, guarded by `LibraryGuard`). Extend `MediaSorterScanner.cs` classification: use `BaseItemKind.Book` + comic-file-extension heuristic for comics, `BaseItemKind.Audio`/`AudioBook` for music, `BaseItemKind.Photo` for pictures. Wizard step 6 (Media sorter) grows three new path inputs. `PLAN.md` §3 point 6 needs an update too.

**Landing 4 — polish** (Settings tab tooltip pass):
12. **Tooltip pass across Settings** — most controls today rely on a small `<div class="fieldDescription">` under the label. Standardise: every non-obvious control gets a one-line `title=""` attribute on the label OR a `?` info icon after the label that reveals the description on hover/focus. Explicit list of controls that need tooltips: Duplicate keeper preset, Quality tolerance %, Skip HDR, Thorough playability, Rename after transcode, Duplicate min age, Stale threshold, Stale excluded libraries, Stale excluded genres, Pause during playback, Scheduled fix time, Max concurrent transcodes, Community stats opt-in, Recycle bin retention, Preferred GPU, Software encode preset, Media sort source, Anime target path, Books/Music/Pictures target paths (new from landing 3). Keep tooltip text ≤ 100 chars each — it's a hover hint, not documentation.

**Test plan for the whole §9b:**
- Settings-tab visual review at 1080p and 1440p on both dark and light themes.
- Every button/dropdown/input measured for consistent height and width (browser devtools).
- Save+reload settings, confirm nothing regressed.
- Fresh install first-run wizard still passes (nothing in `mdWizStep*` referenced here).
- Cross-ABI: Recycle-bin tab, virus-scan button, v12-migration button all functional on both Jellyfin 10.11 and 12 (with the migration button disabled on 10.11).

## 10. Release process

`build.yaml` → `manifest.json` → GitHub Releases. `tools/release.ps1 -Version X.Y.Z -Changelog "..."` builds, zips, uploads to GitHub Releases, re-downloads the uploaded asset and writes its MD5 into `manifest.json` — so the manifest checksum can't drift from the released zip. `targetAbi` tracks the minimum supported Jellyfin server; bump it deliberately, not by default.
