# MediaDash — ponytail debt ledger

Every `ponytail:` marker in the shipping code + supporting scripts. Grouped by whether the shortcut is still justified for 1.0.

Docs/plan mentions (`docs/superpowers/**`) excluded from the ledger — they're historical spec notes, not code debt.

## Still-justified (leave for 1.0)

`Jellyfin.Plugin.MediaDash/Compat/SkiaSharpBridge.cs:34` — by-name SkiaSharp load. **Ceiling:** covers test runners and hosts that don't pre-load. **Upgrade:** unchanged; Jellyfin ships SkiaSharp for the foreseeable.

`Jellyfin.Plugin.MediaDash/ScheduledTasks/ScanTask.cs:75` — IncludeItemTypes widened to Audio/AudioBook/Book/etc. **Ceiling:** matches the v0.9 scanner expansion. **Upgrade:** revisit when Jellyfin adds new BaseItemKinds we want to scan.

`Jellyfin.Plugin.MediaDash/Scanners/ArtworkScanner.cs:55` — URL check instead of `IsLocalFile`. **Ceiling:** `IsLocalFile` doesn't exist in 10.11.8 ItemImageInfo. **Upgrade:** switch when the minimum Jellyfin ABI moves past that point.

`Jellyfin.Plugin.MediaDash/Configuration/configPage.html:4611` — skip re-render when the visible list is unchanged. **Ceiling:** cheap diff on a 3s poll. **Upgrade:** only if the diff hash ever misses a real change users notice.

`Jellyfin.Plugin.MediaDash/Configuration/configPage.html:5990` — only-first-visible wins in a per-chip toggle callback. **Ceiling:** matches how filter chips currently render. **Upgrade:** revisit if filter chips ever need multi-select behaviour.

`Jellyfin.Plugin.MediaDash/Configuration/configPage.html:6950` — fetch full open-issue list, filter client-side. **Ceiling:** fine at current issue counts. **Upgrade:** move filter to the server endpoint if issue lists routinely exceed a few thousand rows.

`Jellyfin.Plugin.MediaDash/Configuration/configPage.html:7908` — 500ms polling on user-initiated bin empty. **Ceiling:** matches user-visible latency. **Upgrade:** switch to server-push if bins routinely take minutes.

`Jellyfin.Plugin.MediaDash/Fixers/ArtworkFixer.cs:63` — rely on Jellyfin's scheduled scan to re-fetch. **Ceiling:** avoids IDirectoryService/IFileSystem scaffolding not otherwise used. **Upgrade:** only if users complain about re-fetch latency.

`Jellyfin.Plugin.MediaDash/Probing/FfprobeService.cs:105` — sentinel for whole-file decode on short clips. **Ceiling:** short clips where regional sampling collapses. **Upgrade:** revisit if scanner produces false positives on short files.

`Jellyfin.Plugin.MediaDash/Scanners/SuspiciousFileScanner.cs:25` — hand-curated extension list. **Ceiling:** covers the malware families we care about. **Upgrade:** append extensions as new families appear.

`Jellyfin.Plugin.MediaDash/Probing/SmartHealthProbe.cs:21` — long TTL on SMART cache. **Ceiling:** SMART changes slowly; 10-min lag vs hammering the disk on every 3s poll. **Upgrade:** shorten only if a real drive-failure incident is missed.

`Jellyfin.Plugin.MediaDash/Fixers/MediaGrouperFixer.cs:126` — Directory.Move within-library-root only. **Ceiling:** grouping stays inside a single library. **Upgrade:** add cross-volume copy+delete if users request grouping across roots.

`Jellyfin.Plugin.MediaDash/Scanners/DuplicateScanner.cs:183` — kind-gate + cast for Movie access. **Ceiling:** clean skip if Jellyfin v12+ moves the type. **Upgrade:** switch when the type stabilizes across ABIs.

`Jellyfin.Plugin.MediaDash/Scanners/DuplicateScanner.cs:208` — same as above for Audio.

`Jellyfin.Plugin.MediaDash/Scanners/QualityScanner.cs:44` — same as above for Video.

`Jellyfin.Plugin.MediaDash/Scanners/QualityScanner.cs:236` — linear bitrate model for savings estimate. **Ceiling:** good enough for the UI's ballpark number. **Upgrade:** replace with a codec-efficiency-aware model if users complain about accuracy.

`Jellyfin.Plugin.MediaDash/Scanners/StaleContentScanner.cs:194` — reflection-based bridge over the User API. **Ceiling:** avoids compile-time dependency on either 10.11 or 12.0 User shape. **Upgrade:** delete when minimum ABI moves past the divergence.

`Jellyfin.Plugin.MediaDash/Scanners/MediaGrouperScanner.cs:324` — iterate stem-strip until stable. **Ceiling:** bounded iteration count. **Upgrade:** none — the bounded loop IS the safety.

`Jellyfin.Plugin.MediaDash/Fixers/LibraryGuard.cs:57` — refuse-any-reparse-point conservative stance. **Ceiling:** hostile-multi-tenant threat model. **Upgrade:** per-target resolution if a real deployment needs symlinks inside a library.

`Jellyfin.Plugin.MediaDash/Scanners/AudioLanguageScanner.cs:87` — assume 128 kbps when track has no bitrate. **Ceiling:** savings estimate accuracy. **Upgrade:** drop when Jellyfin reliably reports bitrate on every audio stream.

`tools/release.ps1:78` — string surgery in manifest.json instead of parse+reserialize. **Ceiling:** stable manifest.json schema. **Upgrade:** rewrite as parse+reserialize when the schema ever changes.

## Rot-risk (no clear trigger)

None. Every marker carries a clear ceiling + upgrade path.

## Summary

**21 markers, 0 with no trigger.**

Every remaining `ponytail:` comment names its ceiling and the condition to revisit. No silent rot; the debt ledger is clean.

**For 1.0**: no marker requires action. Every shortcut is either (a) driven by external ABI stability that hasn't moved, (b) a deliberate UI perf trade-off, or (c) a conservative security stance we WANT to keep. Ship as-is.
