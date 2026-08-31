<div align="center">

<img src="logo.png" width="160" alt="MediaDash logo"/>

# MediaDash

**The one plugin a Jellyfin library owner needs.**

Duplicates, broken files, oversized encodes, wrong-language tracks, misplaced files, missing subtitles, corrupt artwork, executables where media should be, stale unwatched content — surfaced on one dashboard and fixed safely, the moment your server goes idle.

[![CI](https://github.com/crackruckles/MediaDash/actions/workflows/ci.yaml/badge.svg)](https://github.com/crackruckles/MediaDash/actions/workflows/ci.yaml)
[![Release](https://img.shields.io/github/v/release/crackruckles/MediaDash?label=release&color=00a4dc)](https://github.com/crackruckles/MediaDash/releases/latest)
[![Jellyfin](https://img.shields.io/badge/jellyfin-10.11%20%7C%2012.0-aa5cc3)](https://jellyfin.org)
[![License](https://img.shields.io/badge/license-GPLv3-blue)](LICENSE)

<img src="docs/overview.png" width="850" alt="MediaDash overview dashboard"/>

</div>

---

> [!NOTE]
> MediaDash is a **third-party plugin**, not officially affiliated with the Jellyfin project. It's not yet in the official Jellyfin plugin catalog — install from the community repository URL below.

## Install (30 seconds)

1. In Jellyfin: **Dashboard → Plugins → Repositories → +** and paste:

   ```
   https://raw.githubusercontent.com/crackruckles/MediaDash/main/manifest.json
   ```

2. Open **Catalog**, find **MediaDash**, click **Install**, restart Jellyfin. (If MediaDash doesn't appear, hit the refresh icon next to the repository — Jellyfin caches catalog manifests for 6 hours.)
3. Open **Dashboard → My Plugins → MediaDash** — the first-run wizard walks you through each feature, one step at a time.

Requires Jellyfin **10.11+** or **12.0+**. One binary covers both — the manifest advertises `targetAbi` for each host line, and the plugin bridges the `IUserManager` / `User` entity changes between 10.11 and 12.0 via reflection so the same install works everywhere.

## Uninstall

Jellyfin's "Uninstall" button removes the files but leaves the repository URL registered, so the **Update Plugins** scheduled task will silently reinstall MediaDash on its next tick. Remove both to uninstall for real:

1. **Dashboard → Plugins → Repositories** — delete the row pointing at `raw.githubusercontent.com/crackruckles/MediaDash/main/manifest.json`.
2. **Dashboard → Plugins → My Plugins → MediaDash → Uninstall**.
3. Restart Jellyfin.

Step 1 is the one everyone misses. Skip it and step 2 alone appears to work — the plugin folder is marked `deleteOnStartup`, the DLL unloads on next restart — but the still-registered repo tells Jellyfin's auto-updater the plugin is available, and it downloads a fresh copy on the next scheduled run. On Windows, the running Jellyfin process holds the DLL open during step 2, which is why the folder can't be removed until the restart in step 3 — that's expected, not a bug.

## What it does

| | Finds | Fixes |
|---|---|---|
| 🗂 **Duplicate copies** | Same movie, episode, song (MusicBrainz + artist/album/title/duration), audiobook or book (ISBN + name) twice | Deletes the worse copy — you choose what "worse" means |
| 🚫 **Files that won't play** | Broken / unreadable video — every file is *test-played* at its start, middle and end. Books (EPUB/PDF/MOBI/AZW3) and comics (CBZ/CBR/CB7) get integrity probes too | Removes them, after re-checking they're really broken |
| 📦 **Files wasting space** | Anything above your resolution / bitrate ceiling. Detect-only audio ceiling for MP3 > 320 kbps and AAC > 256 kbps (lossless codecs skipped) | Re-encodes to your chosen codec + container (GPU-accelerated, per-GPU selectable) |
| 💬 **Unwanted subtitles** | Embedded tracks + external files in languages you don't keep | Lossless remux — no quality loss |
| 🔊 **Unwanted audio** | Extra audio tracks outside your language list | Lossless remux — never touches a file's only audio track |
| 📥 **Missing subtitles** | Videos with no subtitle in any language you keep | Downloads via Jellyfin's configured providers (OpenSubtitles etc.) |
| 🚚 **Misplaced files** | A movie under TV, a TV episode under Movies, an audiobook under Books, a comic under Music, etc. (Movies / TV / Anime / Music / Audiobooks / Books / Comics / Pictures) | Moves it into the right library folder |
| 🎨 **Corrupt artwork** | Zero-byte, truncated or unreadable poster / backdrop files inside Jellyfin's metadata folders | Deletes the broken image so Jellyfin's own metadata pipeline re-fetches on the next scan (never touches artwork alongside your media) |
| ⚠️ **Executables and scripts** | `.exe`, `.msi`, `.bat`, `.ps1`, `.sh`, `.lnk` and other non-media files sitting inside library folders — almost always malware bundled with pirated releases | Moves them to the recycle bin |
| ⏳ **Stale content** | Media nobody has played in your configured window (default 365 days) — video, audio, audiobook, book | Detect-only — surfaces the list so you can decide whether to prune |
| 🔥 **Heavy / failed transcodes** | Files Jellyfin has had to transcode on the fly recently, and files whose last live transcode attempt failed | One-off re-encode with MediaDash's settings so future plays direct-play |
| 🗑 **Orphaned debris** | Empty folders, subtitle sidecars, trickplay folders and Jellyfin metadata folders whose parent no longer exists | Removes the orphan; re-checks at fix time that no companion has reappeared |
| 📄 **Corrupt NFO** | Zero-byte, malformed, or unrecognised-root `.nfo` sidecars that stop Jellyfin from reading metadata | Deletes the broken sidecar; Jellyfin re-fetches on the next scan |
| 🖼 **Trickplay space savings** | Scrub-bar preview thumbnails still stored as raw JPG | Re-encodes to WebP renamed `.jpg` — clients keep serving them exactly the same |
| 🎵 **Duplicated embedded cover art** | Music / audiobook folders where every track carries its own copy of the same cover but the folder has no shared `cover.jpg` | Extracts once and (optionally) strips the redundant per-file copies |
| 📁 **Ungrouped media** | Loose files or oddly-named folders that should sit under a per-title (or per-franchise) parent folder | Moves them into a folder named after the identified title |
| 🔤 **Subtitle fonts** | Embedded fonts inside `.ass` sidecars that no style actually references | Rewrites the sidecar without them — contents-only edit, still a valid subtitle |

Every fix type runs independently: **Off · Detect only · Ask me first · Automatic**. Stale content and audio-quality checks are detect-only.

<div align="center">
<img src="docs/issues.png" width="850" alt="Issues tab with one-click actions"/>
</div>

## Community impact

<!-- STATS:START -->
_Refreshes on the 1st of each month from installs with community stats enabled (on by default; untick in Settings → Safety to opt out). Reports use a **month-rotated anonymous ID** — no persistent install UUID is stored on disk or in the payload. See [docs/PRIVACY.md](docs/PRIVACY.md) for exactly what's collected._

| | Lifetime | This month |
|---|---:|---:|
| **Storage reclaimed** | 1.1 TB | 0.0 B |
| Duplicate copies removed | 217 | 0 |
| Broken files removed | 356 | 0 |
| Oversized files re-encoded | 2,140 | 0 |
| Unwanted subtitles stripped | 39 | 0 |
| Unwanted audio stripped | 144 | 0 |
| Misplaced files moved | 0 | 0 |
| Missing subtitles downloaded | 0 | 0 |
| Stale files retired | 0 | 0 |
| Corrupt artwork repaired | 0 | 0 |
| Suspicious files quarantined | 0 | 0 |
| Ungrouped media grouped | 0 | 0 |
| Trickplay thumbnails re-encoded | 0 | 0 |
| Subtitle fonts stripped | 0 | 0 |
| Orphaned debris removed | 0 | 0 |
| Corrupt NFO deleted | 0 | 0 |
| Heavy transcodes pre-encoded | 0 | 0 |
| Failed transcodes recovered | 0 | 0 |
| Embedded cover art consolidated | 0 | 0 |
| **Reporting installs** | 21 lifetime | 6 this month |
<!-- STATS:END -->

## Built to be trusted with your media

- 🛡 **Dry-run is on by default** — fix runs only log what they *would* do until you say otherwise
- ♻️ **Recycle bin, not deletion** — removed files are recoverable for 30 days with one-click Restore
- ✅ **Verify before swap** — a re-encoded file replaces the original only after it passes probe verification (duration, streams). The encoded copy is staged to a sidecar *before* the original is disposed, so a failed final rename never leaves you with both files gone
- 🔒 **Hard limits** — never touches files outside your libraries, never removes a file's last audio track, never moves a file outside a library root, checks free disk space before encoding
- 😴 **Fires only when idle** — the fix task wakes up every 15 minutes and skips whenever anyone is watching or has been active in the last 15 minutes. Queued fixes drain the moment the server goes idle — no more waiting for a nightly window

For safe deployment, library directories must not be writable by untrusted local users or unrelated containers. MediaDash rejects paths outside configured libraries and refuses existing symlinks/junctions, but portable pathname checks cannot make destructive filesystem operations race-free when another process can rename library directories concurrently.

On upgrade, legacy batches in a custom recycle-bin root are adopted only when MediaDash's fix history references an item inside them. Any other timestamp-shaped folder is left untouched and reported in Errors; after verifying that MediaDash created it, an administrator can adopt it by creating an empty `.mediadash-owned-v1` file inside that batch folder.

<div align="center">
<img src="docs/history.png" width="850" alt="History with space-saved graph and restore"/>
</div>

## Highlights

- **Runs on Jellyfin 10.11 *and* 12.0** — one .NET 9 binary covers both host lines. The plugin bridges the `User` / `IUserManager` entity changes and the `VirtualFolderInfo.ItemId` shape drift between the two ABIs via reflection, so scoped-library filters work on either.
- **Opportunistic fix scheduling** — no more picking a nightly time. The fix task fires every 15 minutes and skips whenever anyone is watching or was active in the last 15 minutes, so approved fixes drain the instant the server frees up.
- **Full media-type coverage** — every scanner (Duplicate, Playability, Quality, Sub/Audio, Stale, Misplaced) now covers Movies, TV, Music, Audiobooks and Books alongside video. Music duplicates group by MusicBrainz recording ID (or artist + album + title + duration); books group by ISBN.
- **Book & comic integrity probes** — pure stdlib EPUB / PDF / MOBI / AZW3 checks, plus CBZ / CBR / CB7 archive validation via SharpCompress. Catches container-corrupt e-books and comics ffprobe never sees.
- **Corrupt artwork fixer** — deletes zero-byte / truncated / decode-failing poster and backdrop files inside Jellyfin's metadata cache so Jellyfin's own metadata pipeline re-fetches on the next scan. Never touches artwork you placed alongside your media.
- **Media sorter with per-type routing** — flag a movie sitting under TV, an audiobook under Books, a comic under Music, and move it to the right library. Each kind (Movies / TV / Anime / Music / Audiobook / Book / Comic / Picture) has its own optional target folder.
- **Feature-at-a-time first-run wizard** — walks each scanner and its settings one step at a time; every knob is also on the Settings tab, and the wizard is re-openable from Settings → Maintenance.
- **Live system stats on Overview** — CPU / RAM / per-GPU utilisation, Windows and Linux, with an AMD APU `gpu_metrics` fallback for Rembrandt / Phoenix iGPUs where the plain busy-percent counter is broken.
- **Hardware-accelerated re-encoding** — uses the AMF / NVENC / QSV / VideoToolbox encoder Jellyfin already knows about, with a preferred-GPU picker and automatic per-file software fallback.
- **Subtitle downloading via your Jellyfin providers** — MediaDash surfaces missing subs; the download itself uses whatever provider you already configured in Jellyfin (no new API keys to manage).
- **Deep playability check** — beyond ffprobe headers, MediaDash test-plays the start (and end, and middle for long files), scans ffmpeg's stderr for truncation markers, cross-checks container bitrate × duration against actual file size, and compares decoded seconds against what was requested. Catches files that "sort of play" — ones ffprobe reports as valid but that stop short during actual playback.
- **Smart test-play cache** — thorough playability checks only re-run on files that changed.
- **Errors tab surfaces every silent failure** — anywhere a scanner or fixer would previously swallow an exception (permission denied, transient I/O, ffmpeg gone missing, SkiaSharp not loaded) now records a one-line diagnostic you can act on.
- **Recycle-bin size banner** — a visible reminder on Overview when the bin exceeds 10 GB, so it doesn't quietly eat a chunk of your library drive.
- **Files tab** — scoped file browser inside your library folders (rename / move / delete, admin only, deletes go to the recycle bin).

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

**How does MediaDash download subtitles?**
It uses Jellyfin's own subtitle providers — the ones you'd configure at Dashboard → Metadata → Subtitles. If you haven't set any up, missing-subs fixes fail with a specific message telling you why. No new keys, no new provider — same OpenSubtitles / etc. account you already have.

**Does re-encoding lose my subtitles?**
Not with the default MKV output. MP4 output skips subtitle tracks (the format's support is too patchy) — the setting says so.

**Does the plugin download or acquire media?**
No. MediaDash is explicitly *not* an arr-style acquisition tool. It cleans, verifies and completes the library you already have. See [PLAN.md](PLAN.md) for the deliberate scope.

## Development

See [CONTRIBUTING.md](CONTRIBUTING.md) for build steps, style, safety invariants, and the release process. Code of conduct: [Contributor Covenant](CODE_OF_CONDUCT.md).

## Reporting a bug

Errors tab → **Report an issue** (top of the tab, with the bug icon) → the tracker opens in a new tab; paste the clipboard contents into the report. The dump includes plugin/Jellyfin/OS/runtime versions and every recorded error.

## License

[GPLv3](LICENSE) — required because Jellyfin's shared libraries are GPLv3, and a plugin compiled against them inherits that licence.
