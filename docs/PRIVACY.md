# Privacy

MediaDash runs entirely on your Jellyfin server. **No data leaves your server unless you opt in to community stats**, and that toggle is off by default.

## What runs where

| | Local | Sent off-server |
|---|---|---|
| Scans, fixes, ffprobe, re-encodes | ✅ Always local | Never |
| Fix history and the recycle bin | ✅ Local SQLite in `%LOCALAPPDATA%/jellyfin/data/mediadash/` (Windows) or `/var/lib/jellyfin/data/mediadash/` (Linux) | Never |
| Community stats (this document) | | ✅ Only if you opt in |

Subtitle downloads use the providers you already configured in Jellyfin (e.g. OpenSubtitles). MediaDash doesn't ship its own subtitle service.

## Community stats (opt-in)

If you turn on **"Share anonymous stats with the community board"** — either during the first-run wizard or in **Settings → Safety** — the plugin sends one HTTP POST after every fix run to a small Supabase backend that aggregates numbers across all opted-in installs. The aggregated totals appear on the README of this repo, refreshed on the 1st of every month.

### Exactly what's sent

Every field. Nothing else:

| Field | Example | Purpose |
|---|---|---|
| `install_id` | `f9a7c2ee-…` (random UUID generated on your machine when you opt in) | Deduplicate your install's row per month. Not tied to any account — regenerated if you opt out and back in. |
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
| `bytes_freed` | `12345678900` | Sum of bytes freed by all successful fixes this month. |

### What's NEVER sent

- ❌ File paths, filenames, or folder names
- ❌ Media titles, show names, movie names
- ❌ Library names or Jellyfin server names
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

**Settings → Safety → Share anonymous stats with the community board.** Untick and save. From that point:

- No further stats are sent.
- Your `install_id` is cleared from the plugin config on your server.
- Rows previously submitted with your old `install_id` remain in the aggregated totals — they can't be reversed out without knowing the ID that was cleared, which is by design (aggregation is one-way).

If you want your prior contribution deleted from the DB, open an issue with the install_id you had. Because that ID was never tied to any account, you're the only one who knows it.

### Backend details (for the curious)

- Host: [Supabase](https://supabase.com/), free tier
- Project ref: `mcgpyjtcqyrffydpfdrd`
- Region: `ap-southeast-2` (Sydney)
- Anon publishable key: shipped in the plugin binary (safe by design — see above for what it can actually do)
- Schema: two tables (`installs`, `monthly_stats`) + three read-only views (`public_lifetime`, `public_current_month`, `public_monthly_series`), plus the `report_stats` write function. Full DDL is in the plugin's Supabase migration history — open an issue if you'd like a copy pasted in.
