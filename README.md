# MediaDash — Jellyfin Plugin

A media processing dashboard built into Jellyfin. Gives admins a single view of:

- **Re-encode queue** — live progress, per-worker status, files remaining
- **Track stripping** — log of audio/subtitle tracks removed and space saved
- **Duplicate detection** — IMDB-grouped duplicates with one-click deletion
- **Active streams** — who is watching what right now, with progress
- **System metrics** — CPU (per-core), GPU, RAM, NVMe temps, disk I/O

All data sources (log files, flag files, service names, media paths) are configurable via the Jellyfin plugin settings page, so the plugin works with any setup.

---

## Install via repository

1. In Jellyfin go to **Dashboard → Plugins → Repositories**
2. Click **Add** and paste:
   ```
   https://raw.githubusercontent.com/crackruckles/jellyfin-plugin-mediadash/main/manifest.json
   ```
3. Go to **Catalog**, search for **MediaDash**, click **Install**
4. Restart Jellyfin
5. Go to **Dashboard → Plugins → MediaDash → Settings** and configure your paths

---

## Manual install

1. Download `mediadash_x.x.x.x.zip` from [Releases](../../releases)
2. Extract into your Jellyfin plugins directory:
   - Linux: `/var/lib/jellyfin/plugins/`
   - Windows: `%ProgramData%\Jellyfin\Server\plugins\`
3. Restart Jellyfin

---

## Configuration

All settings are in **Dashboard → Plugins → MediaDash → Settings**.

| Setting | Description | Example |
|---|---|---|
| Media Root Path | Root of media storage (disk stats) | `/mnt/media` |
| Reencode Log Path | Log written by your re-encoder script | `/var/log/reencode_bdremux.log` |
| Strip Log Path | Log written by your strip-tracks script | `/var/log/strip_tracks.log` |
| Dupes Report Path | JSON report from your find_dupes script | `/var/log/find_dupes_report.json` |
| Encode Status Dir | Directory of per-worker slot JSON files | `/tmp/encode_status` |
| Reencode State File | JSON tracking processed files | `/var/lib/reencode/processed.json` |
| Pause Flag Path | Touch to pause, delete to resume | `/tmp/reencode_pause` |
| Force Flag Path | Touch to force-resume past quiet hours | `/tmp/reencode_force` |
| Reencode Service | systemd service to start encoder | `reencode-bdremux` |
| Strip Service | systemd service to start stripper | `strip-tracks` |
| Dupes Scan Script | Path to find_dupes.py | `/root/scripts/find_dupes.py` |
| Reencode Process Name | Substring in cmdline to detect encoder | `reencode_bdremux` |
| Strip Process Name | Substring in cmdline to detect stripper | `strip_tracks` |
| Quiet Hours Start/End | Hours when encoding is paused | `8` / `16` |
| Video Extensions | Extensions scanned for queue count | `.mkv,.mp4,.avi,...` |
| Skip Codecs | Codecs already done (not counted) | `hevc,av1` |

---

## How it works

The plugin embeds the dashboard HTML inside the compiled DLL as a resource. Jellyfin serves it at its own web interface — no separate port or web server required. API endpoints are registered as standard Jellyfin REST controllers, protected by Jellyfin's built-in admin authentication.

The plugin reads log files and status JSON written by your external processing scripts. It does not run ffmpeg or manage media directly — it observes and controls the scripts you already have running via flag files and systemd.

---

## Requirements

- Jellyfin 10.11.x
- Linux host (uses `/proc`, `/sys/class/hwmon`, `/proc/diskstats` for metrics)
- Your own processing scripts writing the configured log/status files

---

## Building from source

```bash
dotnet build Jellyfin.Plugin.MediaDash/Jellyfin.Plugin.MediaDash.csproj \
  --configuration Release -o build/
```

Requires .NET 8 SDK.

---

## Releasing a new version

Push a tag:
```bash
git tag v1.0.1
git push origin v1.0.1
```

GitHub Actions will build, zip, compute the MD5 checksum, update `manifest.json`, create a release, and attach the zip. Your users' Jellyfin instances will see the update in the plugin catalog automatically.
