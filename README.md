<div align="center">

<img src="logo.png" width="160" alt="MediaDash logo"/>

# MediaDash

**Keep your Jellyfin library lean and playable.**

Finds duplicate copies, broken files, oversized encodes and unwanted language tracks — then fixes them safely, on your terms.

[![CI](https://github.com/crackruckles/MediaDash/actions/workflows/ci.yaml/badge.svg)](https://github.com/crackruckles/MediaDash/actions/workflows/ci.yaml)
[![Release](https://img.shields.io/github/v/release/crackruckles/MediaDash?label=release&color=00a4dc)](https://github.com/crackruckles/MediaDash/releases/latest)
[![Jellyfin](https://img.shields.io/badge/jellyfin-10.11%2B-aa5cc3)](https://jellyfin.org)
[![License](https://img.shields.io/badge/license-GPLv3-blue)](LICENSE)

<img src="docs/overview.png" width="850" alt="MediaDash overview dashboard"/>

</div>

---

## Install (30 seconds)

1. In Jellyfin: **Dashboard → Plugins → Repositories → +** and paste:

   ```
   https://raw.githubusercontent.com/crackruckles/MediaDash/main/manifest.json
   ```

2. Open **Catalog**, find **MediaDash**, click **Install**, restart Jellyfin.
3. Open **Dashboard → My Plugins → MediaDash** and answer three setup questions. Done.

Requires Jellyfin **10.11+**.

## What it does

| | Finds | Fixes |
|---|---|---|
| 🗂 **Duplicate copies** | Same movie/episode twice (by TMDb/IMDb/TVDb id, or name + year) | Deletes the worse copy — you choose what "worse" means |
| 🚫 **Files that won't play** | Broken and unreadable files — every file is *test-played* at its start, middle and end | Removes them, after re-checking they're really broken |
| 📦 **Files wasting space** | Anything above your resolution/bitrate ceiling | Re-encodes to your chosen codec + container (GPU-accelerated if you want) |
| 💬 **Unwanted subtitles** | Embedded tracks & external files in languages you don't keep | Lossless remux — no quality loss |
| 🔊 **Unwanted audio** | Extra audio tracks outside your language list | Lossless remux — never touches a file's only audio track |

Every fix type runs independently: **Off · Detect only · Ask me first · Automatic**.

<div align="center">
<img src="docs/issues.png" width="850" alt="Issues tab with one-click actions"/>
</div>

## Built to be trusted with your media

- 🛡 **Dry-run is on by default** — fix runs only log what they *would* do until you say otherwise
- ♻️ **Recycle bin, not deletion** — removed files are recoverable for 30 days with one-click Restore
- ✅ **Verify before swap** — a re-encoded file replaces the original only after it passes probe verification (duration, streams)
- 🔒 **Hard limits** — never touches files outside your libraries, never removes a file's last audio track, checks free disk space before encoding
- 😴 **Polite** — scheduled runs wait until nobody is watching and the server has been idle for 15 minutes

<div align="center">
<img src="docs/history.png" width="850" alt="History with space-saved graph and restore"/>
</div>

## Highlights

- **Three-question setup** — pick your language, quality ceiling, and auto-vs-review. Everything else has safe defaults.
- **Hardware-accelerated re-encoding** (optional) — uses the AMF/NVENC/QSV/VideoToolbox encoder you already configured in Jellyfin, with automatic per-file software fallback.
- **Smart test-play cache** — thorough playability checks only re-run on files that changed.
- **Plain language everywhere** — "Safe to delete — a better copy exists", not "duplicate group loser".
- Scan & fix schedules live in Jellyfin's own **Scheduled Tasks** dashboard.

<div align="center">
<img src="docs/settings.png" width="850" alt="Settings with per-type fix modes"/>
</div>

## FAQ

**Will it delete something I can't get back?**
Not unless you choose both permanent delete *and* turn off dry-run. Out of the box everything removed sits in the recycle bin for 30 days.

**Why isn't a broken file fixed automatically?**
Broken files can't be repaired — MediaDash flags them so *you* decide. Even in full-automatic mode, removing broken files always waits for your approval.

**A track has no language tag — will it be removed?**
Never. Untagged tracks are always kept, because deleting a track whose language is unknown isn't safe.

**Does re-encoding lose my subtitles?**
Not with the default MKV output. MP4 output skips subtitle tracks (the format's support is too patchy) — the setting says so.

## Development

```
dotnet build Jellyfin.Plugin.MediaDash.sln
dotnet test
```

Deploy locally: copy `bin/Debug/net9.0/Jellyfin.Plugin.MediaDash.dll` to your server's `plugins/MediaDash/` folder and restart. Test fixtures: `tools/make-fixtures.sh <dir>` · full docker cycle: `tools/integration-test.sh`.

## License

[GPLv3](LICENSE)
