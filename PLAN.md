# MediaDash — Jellyfin Plugin Project Plan

A Jellyfin plugin that keeps media libraries lean and playable: it scans for duplicates, unplayable files, oversized encodes, and wrong-language subtitle/audio tracks, then fixes them — each fix type independently configurable as automatic or review-first. Managed entirely from a dashboard page inside the Jellyfin web UI.

**Goals:** save HDD space, guarantee every file plays, be dead simple to use.
**Non-goals (v1):** media downloading/acquisition, metadata editing, multi-server support, upscaling.

**This plugin will be released publicly to the Jellyfin community.** Two consequences run through every decision below:

1. **Zero machine-specific assumptions.** No hardcoded paths, usernames, drive letters, OS assumptions, or defaults that only make sense on the developer's machine. Everything environment-dependent (library paths, ffmpeg location, data folders) must come from Jellyfin's APIs (`IApplicationPaths`, `IServerApplicationPaths`, encoding options) or plugin configuration. Must work identically on Windows, Linux, macOS, and Docker installs (mind path separators, case sensitivity, permissions). Language defaults must not assume English — first-run setup asks.
2. **The UI must be intuitive enough for a non-technical user.** A person who has never read the docs should be able to install it, answer 2–3 first-run questions, and understand every screen. Concrete standards in §6.

---

## 1. Tech stack & constraints

- C# class library targeting **net8.0** (Jellyfin 10.x). Reference `Jellyfin.Controller` and `Jellyfin.Model` NuGet packages with `<ExcludeAssets>runtime</ExcludeAssets>`; pin versions to the server version being targeted (mismatched versions cause "NotSupported").
- Scaffold from the official template: https://github.com/jellyfin/jellyfin-plugin-template (solution layout, `build.yaml`, `.vscode` debug tasks, GPLv3 license).
- Plugin GUID: generate once with `New-Guid` and never change it.
- Media analysis via **ffprobe**, transcoding via **ffmpeg** — use the binaries Jellyfin already bundles (resolve path from `IServerConfigurationManager` / `EncodingOptions` rather than requiring a separate install).
- All state (scan results, fix queue, history) persisted in a **SQLite** DB in the plugin's data folder — not in plugin XML config. Config XML holds settings only.

## 2. Architecture

```
Jellyfin.Plugin.MediaDash/
├── Plugin.cs                     # BasePlugin<PluginConfiguration>, IHasWebPages
├── PluginServiceRegistrator.cs   # IPluginServiceRegistrator — DI wiring
├── Configuration/
│   ├── PluginConfiguration.cs    # settings model (see §5)
│   └── configPage.html           # embedded dashboard UI (see §6)
├── ScheduledTasks/
│   ├── ScanTask.cs               # IScheduledTask — runs all enabled scanners
│   └── FixTask.cs                # IScheduledTask — executes approved/auto fixes
├── Scanners/
│   ├── IScanner.cs
│   ├── DuplicateScanner.cs
│   ├── PlayabilityScanner.cs
│   ├── QualityScanner.cs
│   ├── SubtitleLanguageScanner.cs
│   └── AudioLanguageScanner.cs
├── Fixers/
│   ├── IFixer.cs
│   ├── DuplicateFixer.cs         # delete/trash losing copy
│   ├── TranscodeFixer.cs         # re-encode via ffmpeg
│   ├── TrackFixer.cs             # strip/remove unwanted audio & sub tracks (remux)
│   └── RecycleBin.cs             # trash folder + retention purge
├── Data/
│   ├── MediaDashDb.cs            # SQLite: issues, fix_queue, history, file_probe_cache
│   └── Models.cs
├── Api/
│   └── MediaDashController.cs    # ControllerBase — REST endpoints for the UI
└── Probing/
    └── FfprobeService.cs         # runs ffprobe, caches results by path+size+mtime
```

Key Jellyfin integration points: `ILibraryManager` (enumerate items/paths), `IScheduledTask` (both tasks appear in the standard Scheduled Tasks dashboard), `IPluginServiceRegistrator` + `IHostedService` if background state is needed, `ControllerBase` for the API, `IHasWebPages` for the config/dashboard page.

## 3. Scanners (all in v1)

Each scanner emits `Issue` rows: `{id, type, itemId, path, details(json), suggestedFix, sizeSavings, status: detected|queued|fixed|dismissed}`.

1. **DuplicateScanner** — groups items by provider IDs (TMDb/TVDB/IMDb) via `ILibraryManager`, falling back to normalized name+year (movies) / series+season+episode (TV). Within a group, ranks copies by a "keeper" policy (configurable order: resolution > codec preference > bitrate > file size). Suggests deleting the losers. Never compares across different editions unless "treat editions as duplicates" is enabled.
2. **PlayabilityScanner** — ffprobe every file; flag: probe failure, zero/negative duration, no video stream, container/codec combos Jellyfin can't direct-play or transcode, truncated files (probe `duration` vs container metadata mismatch). Optional deep check (decode first+last 30s with `ffmpeg -v error`) behind a "thorough" toggle since it's slow.
3. **QualityScanner** — user-set ceiling: max resolution (default 1080p), max video bitrate (default 8 Mbps @1080p, scaled by resolution), preferred codec (default H.265/HEVC). Files above any ceiling are flagged with estimated savings (`currentSize − estimatedSize`). Skip files already at/below ceiling or within a configurable tolerance (default 15%) to avoid churn.
4. **SubtitleLanguageScanner** — flags embedded subtitle tracks whose language isn't in the allowed list (default: user's chosen language + `und`). External `.srt`/`.ass` files in disallowed languages flagged separately. Suggested fix: strip tracks / delete external files.
5. **AudioLanguageScanner** — flags files with multiple audio tracks where at least one is outside the allowed list. Never suggests removing the ONLY audio track, and never removes the last allowed track even if it means keeping a disallowed one (safety invariant).

Scan results are incremental: `FfprobeService` caches probe output keyed on `(path, size, mtimeUtc)` so unchanged files are skipped on re-scan.

## 4. Fix engine

- Each fix type (duplicates / transcode / subtitle-strip / audio-strip) has an independent mode: **Off / Detect only / Manual approve / Automatic** (user requirement: configurable per fix type).
- `FixTask` (scheduled, default nightly, off-peak window configurable) drains the queue: automatic-mode issues go straight in; manual-mode issues wait for approval from the UI.
- **Transcode fix:** ffmpeg re-encode to the ceiling (e.g. `-c:v libx265 -crf 23 -preset medium`, hardware encoder used if Jellyfin has one configured), audio copied, all allowed subs/audio mapped. Output to temp file → verify with ffprobe (duration within 2s of original, streams present) → swap in, original goes to recycle bin (or is deleted, per disposal setting).
- **Track strip fix:** remux with `ffmpeg -map` excluding disallowed tracks, `-c copy` (fast, lossless). Same temp→verify→swap flow.
- **Duplicate fix:** move losing file(s) to recycle bin or delete, per disposal setting; then trigger a library refresh on the affected item.
- **Disposal setting (per fix type, user requirement):** *Recycle bin* (plugin-managed trash folder, configurable path, purged after N days — default 30) or *Permanent delete*. Recycle bin preserves relative paths for one-click restore.
- Concurrency: max 1 transcode at a time by default (configurable); pause when Jellyfin reports active playback sessions if "avoid interfering with playback" is on (default on).
- Every action logged to `history` with before/after size and a restore reference.

### Safety invariants (non-negotiable, enforce in code + tests)
- Never touch a file outside configured library paths.
- Never remove the last audio track or the last video stream.
- Never replace a file whose transcode/remux failed verification.
- Dry-run mode: global toggle that logs what *would* happen; ship with dry-run ON by default for the first run.
- Free-space check before transcoding (need ~2× file size headroom).

## 5. Configuration model (PluginConfiguration)

Settings: enabled libraries (default all), scan schedule, fix schedule/window; per-fix-type mode (Off/Detect/Manual/Auto) and disposal (Recycle/Permanent); recycle bin path + retention days; quality ceiling (resolution, bitrate table, codec, tolerance %); re-encode source file types (extension list, empty = all) and target container (mkv/mp4) — user requirement 2026-07-19; allowed audio languages, allowed subtitle languages (ISO 639-2 lists, sensible single default the user picks in a first-run setup card); duplicate keeper policy order; thorough playability check on/off; max concurrent transcodes; pause-during-playback; dry-run.

## 6. UI (configPage.html — embedded plugin page in Jellyfin web)

Single-page dashboard, plain JS + Jellyfin's built-in `emby-*` web components (matches native look, no build step).

**Intuitiveness standards (release-blocking, not nice-to-have):**

- First-run setup: 2–3 questions max (preferred language, quality ceiling, auto vs review). Everything else gets safe defaults. User should reach a useful Overview within 60 seconds of install.
- Plain language everywhere: "Files wasting space" not "QualityScanner issues"; "Safe to delete — a better copy exists" not "duplicate group loser". No codec/bitrate jargon on primary surfaces; details available behind an expand.
- Every destructive button states its consequence inline ("Moves 3 files (4.2 GB) to MediaDash's recycle bin — recoverable for 30 days").
- Every setting has a one-line description of what it does and what the default means; risky settings (permanent delete, full-auto mode) carry an explicit warning and require confirmation to enable.
- Empty states explain what will appear and how to trigger it ("No issues yet — run your first scan").
- Progress feedback for long operations (scan/transcode) with item counts, not spinners alone.
- Follows Jellyfin dashboard styling exactly (dark/light themes, mobile-responsive like native pages).
- All UI strings in one place, structured for future localization (community will ask for translations).

Tabs:

1. **Overview** — headline cards: potential space savings, issue counts by type, last scan time, "Scan now" / "Run fixes now" buttons.
2. **Issues** — filterable table (type, library, status), per-row Approve / Dismiss / details, bulk approve-all-of-type. This is the manual-approve queue.
3. **History** — completed fixes, space saved to date, Restore button for recycle-bin items.
4. **Settings** — everything in §5, grouped simply; first-run banner walks through language + quality ceiling in ~3 clicks.

API endpoints (`/MediaDash/...`, `[Authorize(Policy = "RequiresElevation")]`): `GET /status`, `GET /issues?type=&status=`, `POST /issues/{id}/approve|dismiss`, `POST /issues/approve-bulk`, `POST /scan`, `POST /fix`, `GET /history`, `POST /history/{id}/restore`, `GET/POST /config`.

## 7. Build order (all v1, but sequenced so each step is testable)

1. **Scaffold** — template clone, rename to `Jellyfin.Plugin.MediaDash`, GUID, builds and loads in a Jellyfin instance, empty config page appears.
2. **Foundations** — SQLite layer, `FfprobeService` + cache, `ScanTask` shell that enumerates libraries via `ILibraryManager`.
3. **Scanners** — implement all five against the Issue model; verify counts against a test library seeded with known-bad files.
4. **API + UI read-only** — controller endpoints + Overview/Issues tabs showing real scan data.
5. **Fix engine** — RecycleBin, then TrackFixer (safest, `-c copy`), then DuplicateFixer, then TranscodeFixer (riskiest last). Dry-run mode first, real execution behind it.
6. **Approve flow + FixTask scheduling + History/restore.**
7. **Settings UI + first-run setup + polish.**
8. **Hardening** — unit tests for keeper-policy ranking, language matching, safety invariants; integration test script that builds a docker Jellyfin + fixture media files and runs a full scan/fix cycle — run it on both a Linux (Docker) and Windows Jellyfin instance before release.
9. **Community release** — `build.yaml` + GitHub Actions producing the plugin zip and repository `manifest.json` (users install by adding the repo URL in Dashboard → Plugins → Repositories); semantic versioning with `targetAbi` set to the minimum supported Jellyfin version; README with screenshots, install steps, FAQ, and a prominent "what it will/won't touch" safety section; CHANGELOG; issue templates.

## 8. Test fixtures (generate with ffmpeg in a script, commit the script not the files)

Tiny synthetic files: a 4K H.264 high-bitrate clip (quality hit), same movie in two files (duplicate), a truncated file (playability), a clip with eng+fra+deu audio (audio strip), a clip with unwanted sub tracks (sub strip), a clean file (no issues). Script: `tools/make-fixtures.sh`.

## 9. Risks / open questions for the build

- Jellyfin version support: develop against the developer's installed server version, but define a minimum supported version (`targetAbi`) for release — aim to support the current stable 10.x line, not just one machine's install.
- Hardware transcode flags vary by platform; v1 can fall back to software (libx265) if reading Jellyfin's encoding options proves messy.
- After replacing a file, trigger `ILibraryManager` refresh on the item so Jellyfin re-probes; verify watched-status/metadata survive (path is unchanged, so it should).
- 10.11.x template README shows net9.0 examples; confirm target framework against the installed server's plugin ABI before scaffolding.
