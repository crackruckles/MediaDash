# Changelog

Release notes for every published version are on GitHub Releases: https://github.com/crackruckles/MediaDash/releases

The Jellyfin plugin catalog also shows the changelog for each version — open **Dashboard → Plugins → Catalog** in your Jellyfin server, or read `manifest.json` in this repo.

## Highlights so far

- **0.7.3** — New *Stale content* scanner (detect-only): flags media nobody has played in a configurable window (default 365 days). Jellyfin 12.0 compatibility: one binary works on both 10.11 and 12.0 via a reflection bridge over the `IUserManager` / `User` entity changes; manifest advertises both target ABIs. Playability scanner gains three new checks — stderr marker scan for "File ended prematurely" / "Truncating packet" (ffmpeg emits these but exits 0), container-bitrate-vs-actual-size sanity check, and decoded-time vs requested-segment comparison. Together they catch files that "sort of play" — ones ffprobe accepts but that stop short during actual playback.
- **0.7.x pre-0.7.3** — Configurable fix schedule (Settings → Safety), UI localisation (en source + de/es/fr/it/nl/pt-BR/ru/zh-CN machine-translated seeds), opt-in community stats board, Settings tab redesign, mobile-responsive pass.
- **0.6.0** — New *Missing subtitles* fix type (downloads via Jellyfin's configured providers). Multi-step first-run wizard walking each feature one at a time. Hardware GPU picker beside the encoder toggle. AMD APU (Rembrandt / Phoenix) GPU% now reads from `gpu_metrics` when `gpu_busy_percent` is pinned at 0. Queued issues count toward "Space you could reclaim".
- **0.5.x** — Media sorter (misplaced files), History tab filter chips, first-run library-access check, recycle-bin cross-volume warning, hardware encoder + preferred GPU, Errors tab retry, canonical rename after re-encode, ffprobe cache, HDR-skip default.
- **0.4.x** — Multi-GPU system stats card (NVIDIA / Windows PDH / Linux sysfs), Files tab, per-fix disposal, permission-error surfacing, thorough playability check, thumbnails.
- **0.1 – 0.3** — Five original scanners (dupes, playability, quality, subs, audio), dry-run + recycle bin, verify-before-swap, three-question first-run.

## Reporting issues

Use the **Copy diagnostics** button on the Errors tab — it copies plugin/OS/Jellyfin versions and every visible error to your clipboard in a format that pastes cleanly into a new GitHub issue.
