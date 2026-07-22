# MediaDash — Jellyfin Plugin

Read PLAN.md first. It is the source of truth for scope, architecture, and build order. Work through §7 (Build order) step by step; do not skip ahead to fixers before scanners are verified.

## What this is

A Jellyfin plugin (C#, net8.0 class library) that scans libraries for duplicates, unplayable files, oversized encodes, and wrong-language audio/subtitle tracks, then fixes them on a schedule. Full spec in PLAN.md.

**This is a public community release, not a personal tool.** Two consequences:

- **Portability:** no hardcoded paths, drive letters, usernames, or OS/locale assumptions anywhere. Resolve all environment-dependent values through Jellyfin APIs (`IApplicationPaths`, encoding options) or plugin config. Must run on Windows, Linux, macOS, and Docker. Use `Path.Combine`, never string-concatenated separators; assume case-sensitive filesystems.
- **UI intuitiveness is release-blocking:** plain language, no jargon on primary surfaces, consequences stated on every destructive button, safe defaults, 2–3-question first-run setup. Standards in PLAN.md §6 — treat them as acceptance criteria.

## Reference

- Official plugin template & docs: https://github.com/jellyfin/jellyfin-plugin-template — scaffold from this.
- Pin `Jellyfin.Controller` / `Jellyfin.Model` NuGet versions to the user's installed server version (ask if unknown) and set `<ExcludeAssets>runtime</ExcludeAssets>` on both, or the plugin shows as NotSupported.

## Build & test

```
dotnet build Jellyfin.Plugin.MediaDash.sln /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary
dotnet test
```

Deploy for local testing: copy `bin/Debug/net8.0/publish/*` to `%LOCALAPPDATA%\jellyfin\plugins\MediaDash\` and restart the server (see template README §6 for .vscode automation).

## Hard rules — safety invariants

These must hold in every code path and have unit tests. Do not relax them for convenience:

1. Never modify or delete a file outside the configured library paths.
2. Never remove a file's last audio track or its video stream.
3. Never replace an original until the new file passes ffprobe verification (duration within 2s, expected streams present).
4. All destructive operations respect the per-fix-type disposal setting (recycle bin vs permanent) and the global dry-run toggle. Dry-run defaults ON.
5. Check free disk space (≥2× source size) before any transcode.

## Conventions

- One class per file; scanners implement `IScanner`, fixers implement `IFixer` (see PLAN.md §2 layout).
- State lives in SQLite in the plugin data folder; settings live in `PluginConfiguration` XML. Don't mix them.
- Use Jellyfin's bundled ffmpeg/ffprobe (path from server encoding options) — never assume a system install.
- UI is a single embedded `configPage.html` using Jellyfin's `emby-*` components; no JS build step, no frameworks.
- API controllers require elevation (`RequiresElevation` policy).
- All UI strings centralized for future localization; no English-only assumptions in language defaults (first-run setup asks).
- Follow the template's `jellyfin.ruleset` / analyzers; treat warnings as errors.
- License: GPLv3 (required by Jellyfin NuGet linkage).
- Release via plugin repository: `build.yaml` → zip + `manifest.json`, semantic versioning, `targetAbi` = minimum supported Jellyfin version (see PLAN.md §7 step 9). Cut releases with `tools/release.ps1 -Version X.Y.Z -Changelog "..."` — it builds, zips, uploads to GitHub Releases, then re-downloads the uploaded asset and writes that MD5 into `manifest.json`, so the manifest checksum can't drift from the released zip. Do NOT hand-edit `manifest.json` checksums or hand-upload releases.

## Verification checklist per phase

After each PLAN.md §7 step: build clean, plugin loads without errors in the Jellyfin dashboard, run `tools/make-fixtures.sh` test library through a scan, confirm expected issue counts before moving on. Fixer phases additionally: run in dry-run first, diff expected vs planned actions, then run real and confirm restore from recycle bin works.
