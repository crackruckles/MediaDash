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

- C# class library targeting **net9.0** (Jellyfin 10.11+). Reference `Jellyfin.Controller` and `Jellyfin.Model` NuGet packages with `<ExcludeAssets>runtime</ExcludeAssets>`; pin versions to the server version being targeted (mismatched versions cause "NotSupported").
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
│   └── MissingSubtitleScanner.cs    # no subs in any wanted language
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
2. **PlayabilityScanner** — ffprobe every file; flags: probe failure, zero/negative duration, no video stream, container/codec combos Jellyfin can't direct-play or transcode, truncated files. "Thorough" mode (default on) test-plays start + middle + end via ffmpeg; results are cached for unchanged files.
3. **QualityScanner** — user-set ceiling: max resolution (default 1080p), max video bitrate (default 8 Mbps @1080p, scaled by resolution), preferred codec (default HEVC). Files above any ceiling are flagged with estimated savings (`currentSize − estimatedSize`). Skip files already at/below ceiling or within a configurable tolerance (default 15%) to avoid churn. HDR content skipped by default.
4. **SubtitleLanguageScanner** — flags embedded subtitle tracks and external `.srt`/`.ass` files whose language isn't in the allowed list. Untagged (`und`) tracks are always kept.
5. **AudioLanguageScanner** — flags files with audio tracks outside the allowed list. Never suggests removing the ONLY audio track, and never removes the last allowed track even if it means keeping a disallowed one (safety invariant).
6. **MediaSorterScanner** — a movie physically located under a TV library, or a TV episode under a Movies library. Uses Jellyfin's own classification (`BaseItemKind`) or a filename-heuristic fallback (`SxxExx` / `NxN` patterns) per user choice.
7. **MissingSubtitleScanner** — Video items with no subtitle track (embedded or external) in any wanted language. Only runs when at least one subtitle language is configured; only meaningfully fixable when the admin has set up a subtitle provider in Jellyfin.

Scan results are incremental: `FfprobeService` caches probe output keyed on `(path, size, mtimeUtc)` so unchanged files are skipped on re-scan.

## 4. Fix engine

- Each fix type has an independent mode: **Off / Detect only / Manual approve / Automatic**.
- Each *removing* fix type has an independent disposal: **Recycle bin** (default, plugin-managed trash folder, configurable retention, one-click restore) or **Permanent**. Media sorter (moves) and missing-subs (adds) have no disposal.
- `FixTask` (scheduled, default nightly, off-peak) drains the queue: automatic-mode issues go straight in; manual-mode issues wait for approval from the UI.
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

## 10. Release process

`build.yaml` → `manifest.json` → GitHub Releases. `tools/release.ps1 -Version X.Y.Z -Changelog "..."` builds, zips, uploads to GitHub Releases, re-downloads the uploaded asset and writes its MD5 into `manifest.json` — so the manifest checksum can't drift from the released zip. `targetAbi` tracks the minimum supported Jellyfin server; bump it deliberately, not by default.
