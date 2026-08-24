# Privacy

MediaDash runs entirely on your Jellyfin server. **No data leaves your server unless the community-stats toggle is on.** New installs opt in by default; untick it in the first-run wizard or in **Settings → Safety** to opt out at any time.

## What runs where

| | Local | Sent off-server |
|---|---|---|
| Scans, fixes, ffprobe, re-encodes | ✅ Always local | Never |
| Fix history and the recycle bin | ✅ Local SQLite in `%LOCALAPPDATA%/jellyfin/data/mediadash/` (Windows) or `/var/lib/jellyfin/data/mediadash/` (Linux) | Never |
| Community stats (this document) | | ✅ Only if you opt in |

Subtitle downloads use the providers you already configured in Jellyfin (e.g. OpenSubtitles). MediaDash doesn't ship its own subtitle service.

## Community stats (opt-out)

The **"Share anonymous stats with the community board"** toggle is on by default (the first-run wizard shows it ticked, and it's also in **Settings → Safety**). While on, the plugin sends one HTTP POST after every fix run to a small Supabase backend that aggregates numbers across all opted-in installs. Untick to opt out — no further stats are sent. The aggregated totals appear on the README of this repo, refreshed on the 1st of every month.

### Exactly what's sent

Every field. Nothing else:

| Field | Example | Purpose |
|---|---|---|
| `install_id` | `f9a7c2ee-…` (a **month-rotated hash** — see below) | Deduplicate your install's row per month. Rotates on the 1st of every month so nothing links reports across months. Not tied to any account. |
| `plugin_version` | `0.8.0` | See adoption of new releases. |
| `jellyfin_version` | `10.11.8` | See which Jellyfin versions the community is running. |
| `month` | `2026-07-01` | Bucket for aggregation. |
| `duplicate_fixed` | `47` | Count of successful, non-dry-run duplicate fixes this month. |
| `playability_fixed` | `3` | Count of broken files removed this month. |
| `quality_fixed` | `12` | Count of oversized files re-encoded this month. |
| `subtitle_fixed` | `9` | Count of unwanted-subtitle strips this month. |
| `audio_fixed` | `5` | Count of unwanted-audio strips this month. |
| `misplaced_fixed` | `1` | Count of misplaced-file moves this month. |
| `missing_subs_fixed` | `4` | Count of subtitle downloads this month. |
| `stale_fixed` | `2` | Count of stale files retired this month (untouched past the stale threshold). |
| `corrupt_artwork_fixed` | `6` | Count of corrupt / truncated poster / backdrop / thumb files repaired this month. |
| `suspicious_fixed` | `0` | Count of suspicious files (executables / scripts inside media folders — potential malware) quarantined this month. |
| `ungrouped_fixed` | `8` | Count of loose files nested under per-title parent folders this month. |
| `large_trickplay_fixed` | `540` | Count of scrub-bar preview thumbnails re-encoded from JPG to WebP this month. |
| `subtitle_fonts_fixed` | `3` | Count of `.ass` sidecars stripped of unreferenced embedded fonts this month. |
| `orphaned_debris_fixed` | `17` | Count of orphaned subtitle / trickplay / metadata folders removed this month. |
| `corrupt_nfo_fixed` | `2` | Count of malformed `.nfo` sidecars deleted this month. |
| `heavy_transcode_fixed` | `9` | Count of files pre-encoded to a compatible codec so future plays direct-play. |
| `failed_transcode_fixed` | `1` | Count of files re-encoded after a failed live transcode attempt. |
| `embedded_cover_art_fixed` | `230` | Count of music / audiobook tracks whose embedded cover was consolidated into a shared folder image. |
| `bytes_freed` | `12345678900` | Sum of bytes freed by all successful fixes this month. |

### How `install_id` is derived (Time-Bounded Rotational Hash)

Starting with plugin version **1.0.6**, MediaDash **no longer stores a persistent per-install UUID**. On every report the plugin computes:

```
install_id = SHA-256( "mediadash-analytics-tbrh-v1" | Jellyfin.SystemId | YYYY-MM )
             → first 16 bytes → UUID-shaped string
```

- The **Jellyfin `SystemId`** is a per-install identifier Jellyfin already generates for its own use. It is **never sent** — only its SHA-256 with the current year-month is.
- The **year-month** rotates the ID on the 1st of every month. A given install produces the same `install_id` for every report inside a month (so the backend can deduplicate to one row), but a **different** `install_id` starting on the 1st. The two months' rows can't be linked without knowing the SystemId, which nobody outside your server has.
- The **plugin-scoped salt** (`"mediadash-analytics-tbrh-v1"`) prevents any other plugin that might also hash the same SystemId from correlating with our IDs.

**Consequences for what we can learn from the data:**

- ✅ Within-month deduplication (one row per install per month) — works.
- ✅ Approximate distinct-install count each month — works.
- ❌ Cross-month retention ("how many July installs are still active in August") — **no longer possible, by design**.

Previous versions (≤ 1.0.5) stored a persistent `AnalyticsInstallId` UUID in the plugin config. On upgrade, that field is cleared and never read again; you don't have to do anything.

### What's NEVER sent

- ❌ File paths, filenames, or folder names
- ❌ Media titles, show names, movie names
- ❌ Library names or Jellyfin server names
- ❌ Your Jellyfin `SystemId` — only its salted, month-hashed derivative
- ❌ Usernames, email addresses, IP addresses (writes happen from your server, so the IP is your server's; nothing correlates it to `install_id` at rest)
- ❌ System info beyond the plugin + Jellyfin version — no OS name, no CPU/GPU, no MAC address, no disk paths
- ❌ Dry-run counts (they don't affect real files, so they'd inflate the totals misleadingly)
- ❌ Failure counts or error messages
- ❌ Anything from tabs other than the community-impact card

### How the backend protects the numbers

- Direct table writes are denied to the public anon key — the only write path is a single SECURITY DEFINER function that validates and clamps every field.
- Per-field caps: max 1M fixes per type per month, max 100 TiB freed per month. Anything outside those bounds is silently discarded.
- Monotonic clamp: each field can only grow within a month, so a bug or a rogue submitter can't push counts down.
- Public views only expose SUMs across all installs — no per-install rows are readable via the anon key.

### Opting out

Community stats are on by default for new installs. To opt out, untick **Settings → Safety → Share anonymous stats with the community board** (also available on the last step of the first-run wizard) and save. From that point:

- No further stats are sent.
- Nothing needs to be cleared from your config — no persistent install ID is stored under 1.0.6+. Any legacy `AnalyticsInstallId` from a pre-1.0.6 config is wiped on opt-out for good measure.
- Rows previously submitted with your `install_id` for the current month remain in that month's aggregated totals — they can't be reversed out without re-computing the hash for that month, which nobody outside your server can do (they'd need your Jellyfin `SystemId`). This is by design; aggregation is one-way.

Because `install_id` rotates every month, you don't even need to prove ownership to "delete last month's row" — it's already unlinkable from anything you can produce this month. If you want to force-remove a specific month's contribution before the rotation, open an issue with the derived `install_id` you observed in that month's request payload; that's the only way anyone can identify it.

### Backend details (for the curious)

- Host: [Supabase](https://supabase.com/), free tier
- Project ref: `mcgpyjtcqyrffydpfdrd`
- Region: `ap-southeast-2` (Sydney)
- Anon publishable key: shipped in the plugin binary (safe by design — see above for what it can actually do)
- Schema: two tables (`installs`, `monthly_stats`) + three read-only views (`public_lifetime`, `public_current_month`, `public_monthly_series`), plus the `report_stats` write function. Full DDL is in the plugin's Supabase migration history — open an issue if you'd like a copy pasted in.
