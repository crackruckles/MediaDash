# Changelog

## 0.1.0 (unreleased)

Initial version.

- Five scanners: duplicate copies, unplayable files, oversized encodes, unwanted audio languages, unwanted subtitle languages
- Fix engine with per-type modes (Off / Detect only / Ask me first / Automatic) and per-type disposal (recycle bin / permanent)
- Plugin-managed recycle bin with retention purge and one-click restore
- Safety: dry-run on by default, verify-before-swap, never touches files outside libraries, never removes the last audio track, free-space check before re-encoding
- Dashboard with Overview, Issues (approve/dismiss), History (restore) and Settings tabs
- Three-question first-run setup
- Configurable re-encode source file types and target container/codec
- Server-idle gate: scheduled scans and fixes wait until nobody is playing media or has used Jellyfin in the last 15 minutes (toggle in Settings; manual runs bypass it)
- Thorough playability check now test-plays the start, middle and end of every file (cached per file), and unplayable files can be approved for removal like any other issue — with a fresh is-it-really-broken re-check at fix time
- Visual dashboard: issue donut chart, per-type savings bars, cumulative space-saved graph, sliders for bitrate/tolerance/retention
