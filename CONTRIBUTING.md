# Contributing to MediaDash

Thanks for helping! MediaDash is a small, opinionated Jellyfin plugin. A few notes to make contributions land smoothly.

## Building

Requires the .NET 9 SDK.

```
dotnet build Jellyfin.Plugin.MediaDash.sln
dotnet test
```

Deploy locally for iteration: copy `Jellyfin.Plugin.MediaDash/bin/Debug/net9.0/Jellyfin.Plugin.MediaDash.dll` into your Jellyfin server's `plugins/MediaDash/` folder and restart. Full `.vscode` build-and-copy task in the [official plugin template](https://github.com/jellyfin/jellyfin-plugin-template) works verbatim.

## Test fixtures

`tools/make-fixtures.sh <dir>` generates a small set of ffmpeg-synthesised files (duplicate movie, truncated file, oversized encode, multi-language tracks, misplaced file, no-subtitle file, clean control). Point a Jellyfin library at the output directory and run the scanners against known-good inputs.

## Safety invariants — do not relax

Read PLAN.md §4 before touching a fixer. These invariants must hold in every code path and have unit tests:

1. Never touch a file outside configured library paths.
2. Never remove the last audio track or the last video stream.
3. Never replace a file whose transcode/remux failed verification.
4. Never move a file to a target outside a Jellyfin library root.
5. Free-space check before transcoding (need ~2× file size headroom).

Bug fixes for these are the highest-priority PRs.

## Style

- One class per file. Scanners implement `IScanner`; fixers implement `IFixer` — see PLAN.md §2.
- Follow the shipped `jellyfin.ruleset` (warnings-as-errors). If a rule is bogus for a specific line, `SuppressMessage` with a one-line justification.
- Plain-language UI copy over jargon; every destructive button states its consequence inline.
- No hardcoded paths, drive letters, usernames, or English-only defaults.

## Releasing (maintainers only)

```
./tools/release.ps1 -Version X.Y.Z -Changelog "One line summary."
```

The script publishes, zips, uploads to GitHub Releases, re-downloads the asset to verify, then writes the verified MD5 into `manifest.json`. **Do not hand-edit manifest checksums or hand-upload releases** — the drift guarantee lives in the script.

## Scope

MediaDash cleans, verifies and completes the library you already have. Deliberately out of scope:

- Media acquisition (torrent, usenet, indexer integration).
- Metadata authoring (Jellyfin's own editor covers this; we surface gaps).
- Multi-server orchestration.
- Upscaling / HDR-to-SDR / anything that destroys artistic intent.

If a proposal fits one of the above, it belongs in a different plugin.
