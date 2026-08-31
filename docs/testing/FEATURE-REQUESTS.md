# Feature Requests

User-facing feature requests. Drawn from the `enhancement`-tagged
issues on <https://github.com/crackruckles/MediaDash/issues> plus a
few maintainer notes that are features rather than bugs.

Bugs live in `ISSUES-WORKTHROUGH.md` alongside the PR plan. This file
tracks **what to build**, not **what to fix**.

Format per entry:
- **Ask** — one sentence, in the user's words where possible
- **Rationale** — why users want it
- **Sketch** — how it plausibly fits the codebase
- **Depends on** — prerequisite PRs / features
- **Priority** — P1 (many users blocked) / P2 (nice, requested) / P3 (interesting, wait)
- **Effort** — S (< 1 day), M (1-3 days), L (> 3 days)
- **Status** — Proposed / In progress / Shipped / Declined
- **PR pointer** — link to the fix PR from `ISSUES-WORKTHROUGH.md` if
  it's already planned

---

## Fixer capabilities (biggest cluster)

### F-REQ-1 · Combined subtitle + audio remux (#28, #32)

- **Ask** — "Subtitle removal and unwanted-audio removal should be a
  single ffmpeg pass, not two on the same file."
- **Rationale** — On large libraries, remuxing the same 20 GB file
  twice for two different track ops is redundant I/O. Users see it as
  a straightforward efficiency win.
- **Sketch** — new `Fixers/CombinedTrackFixer` builds one ffmpeg
  invocation with all `-map -0:a:<n>` / `-map -0:s:<n>` selectors.
  `FixTask.cs:267` groups queued issues by path before the loop.
- **Depends on** — nothing prerequisite. Independent.
- **Priority** — **P1** (blocks the non-English use case for large
  libraries).
- **Effort** — M.
- **Status** — In progress (maintainer comment on both issues).
- **PR pointer** — **PR-2** in `ISSUES-WORKTHROUGH.md`. Same PR as
  file-date preservation.

### F-REQ-2 · Preserve source `DateCreated` / `DateModified` on fixer output (#31)

- **Ask** — "New file after strip should carry the original date so
  Jellyfin's 'Recently Added' doesn't get polluted."
- **Rationale** — Radarr and Sonarr both do this. Users treat "recent"
  as a curation signal — a housekeeping pass shouldn't nuke it.
- **Sketch** — capture `FileInfo.CreationTimeUtc` +
  `LastWriteTimeUtc` BEFORE the ffmpeg pass, apply with
  `File.SetCreationTimeUtc` after the swap. See
  `Fixers/TrackFixer.cs:178` and `Fixers/TranscodeFixer.cs:229`.
  A new `Fixers/OutputFinalizer.SwapAndPreserveStamps` helper keeps
  it one line at each call site.
- **Depends on** — nothing.
- **Priority** — **P1** (matches user expectation from other tools).
- **Effort** — S.
- **Status** — In progress (maintainer comment on issue).
- **PR pointer** — **PR-2**.

### F-REQ-3 · Throttle re-encoding / muxing I/O (#34)

- **Ask** — "MediaDash re-mux maxes out my HDD's transfer rate; give
  me a knob to slow it down."
- **Rationale** — Users on single-platter HDDs get their whole system
  stalled while a fixer runs.
- **Sketch** — new config field `FixerMaxIoBandwidthMBps` (int,
  default 0 = unlimited). `FfmpegExecutor.cs:233-248` already sets
  `BelowNormal` priority; extend with a Windows `Job Object` I/O
  rate-limit or a per-write throttle around the output stream. On
  Linux, wrap the ffmpeg call in `ionice -c 3`.
- **Depends on** — PR-2 (combined pass) preferably ships first — if
  each file only gets one pass, throttling matters less.
- **Priority** — **P2** (workaround: run fixes overnight).
- **Effort** — M (Windows platform work is non-trivial).
- **Status** — Proposed.
- **PR pointer** — deferred beyond PR-2.

### F-REQ-4 · Per-library re-encode target resolution (#30)

- **Ask** — "I want Movies at 1080p and TV at 720p — set target per
  library, not globally."
- **Rationale** — Storage-conscious users don't apply the same
  ceiling to shows as to movies.
- **Sketch** — `PluginConfiguration.PerLibraryOverride[libraryId] =
  { MaxHeight?, MaxBitrateMbpsAt1080p?, TargetContainer?,
  ReencodeFileTypes? }` — every field nullable so it falls back to
  the global. Settings tab renders one card per registered library.
  Config JSON round-trip stays flat via a
  `Configuration/PluginConfiguration.PerLibraryOverride.cs` DTO.
- **Depends on** — **PR-7** in workthrough (surfaces the three
  missing config fields — same shape work).
- **Priority** — **P2**.
- **Effort** — M.
- **Status** — Roadmap (maintainer said "not that much work, added to
  list").
- **PR pointer** — **PR-7b** (per-library overrides, follow-up to
  PR-7 config completeness).

### F-REQ-5 · Per-library (or per-folder) allowed audio languages (#22)

- **Ask** — "My anime folder should only keep JPN/KOR audio; my
  German shelf only DEU; my American shelf only ENG."
- **Rationale** — Global `AllowedAudioLanguages=[eng]` is too coarse
  for mixed libraries.
- **Sketch** — same `PerLibraryOverride` layer as F-REQ-4, with
  `AllowedAudioLanguages` nullable-per-library. UI: dropdown or
  multi-select per library card. Optionally, `PerFolderOverride`
  (map<absolute-folder-path, config>) for finer control — the
  commenter's `SurRealGames` suggestion.
- **Depends on** — F-REQ-4 (shares the override infrastructure).
- **Priority** — **P2** (multi-locale libraries are common).
- **Effort** — M.
- **Status** — Roadmap (maintainer: "for the future, not for a while").

### F-REQ-6 · Extract embedded subs to sidecar files (#27)

- **Ask** — "Add an option in Files-wasting-space to extract subs
  into separate `.srt` / `.ass` sidecars instead of leaving them
  embedded — cleaner, editable, direct-play compatible."
- **Rationale** — Some players (LG WebOS, older Roku) don't handle
  embedded subs but do handle sidecars. Also lets Bazarr / other
  tools manage them.
- **Sketch** — new fixer type `SubtitleExtractionFixer` runs
  `ffmpeg -i input.mkv -map 0:s -c:s copy input.<lang>.srt`. Emits
  a new `IssueType.EmbeddedSubtitles` when a video has embedded
  subs AND a user config prefers sidecars. Fixer path chunks per
  subtitle track so a file with 3 subs produces 3 sidecars.
- **Depends on** — new IssueType + new fixer registered.
- **Priority** — **P2**.
- **Effort** — M.
- **Status** — Committed for v1.0.8 (maintainer: "will add to the
  next feature update, but not until bug reports slow down").

### F-REQ-7 · Advanced subtitle rules — SDH/HI prioritisation (#18)

- **Ask** — "Detect `.hi.srt` / `.sdh.srt` / `.cc.srt` sidecars +
  in-track SDH/CC flags. Let me decide: keep SDH, keep standard,
  keep both, keep only if it's the sole track."
- **Rationale** — Hearing-impaired subs are an accessibility need;
  MediaDash currently treats all `.en.srt` the same.
- **Sketch** — extend `Scanners/SubtitleLanguageScanner.cs` + the
  `LanguageHelper` to parse `.hi` / `.sdh` / `.cc` filename
  suffixes and match `Disposition.HearingImpaired` /
  `.VisualImpaired` from ffprobe. New config fields
  `SubtitleSdhPreference` (enum: Prefer / KeepBoth / DropIfPaired
  / DropAlways) and `SubtitleTypeAwareOrphanCleanup` (bool, gates
  Orphan detection).
- **Depends on** — nothing.
- **Priority** — **P1** (accessibility) — most of it already
  shipped in v1.0.7.
- **Effort** — M.
- **Status** — Partially shipped (v1.0.7 detects, follow-up per
  `znarfm` comment: "have a per-language 'missing subs' policy so
  native-language sub isn't demanded on every file").
- **PR pointer** — follow-up **PR-7c** — per-language missing-subs
  whitelist. Deferred beyond PR-7.

### F-REQ-8 · Auto-delete watched media after N days (#37)

- **Ask** — "MediaCleaner-plugin-style feature: after a file is
  watched, delete it in N days. Bundled into MediaDash."
- **Rationale** — Users would like one plugin instead of two.
- **Sketch** — new `IScanner`
  `WatchedAndAgedScanner` → new `IssueType.WatchedAndAged`. Gate on
  strict conditions: **played by ALL users** with playState.Played
  ≥ N days ago, opt-in per library, dry-run flags visible in
  history. Never on by default.
- **Depends on** — **F-036** (Stale scanner uses `DateCreated`, not
  mtime — the same infra will need fixing). See PR-4 in workthrough.
- **Priority** — **P3** (maintainer wary of auto-delete: "I'm pretty
  hesitant to make it auto delete"). Ship opt-in with big warnings.
- **Effort** — M.
- **Status** — Proposed with reservation.

---

## Issues / queue / visibility

### F-REQ-9 · Remove-from-queue button on issue card (#35)

- **Ask** — "Let me un-approve an issue I added to the fix queue
  by accident."
- **Rationale** — Users report accidents — big libraries mean bulk
  approve; one wrong click and a real file is queued for
  destruction.
- **Sketch** — the `POST /MediaDash/Issues/{id}/Revert` endpoint at
  `MediaDashController.cs:409` already does this (Queued/Dismissed
  → Detected). Frontend just needs a button on Queued issues in
  the Issues panel of `configPage.html`. Optional: alias the
  endpoint as `POST /Issues/{id}/Unqueue` for cleaner semantics.
- **Depends on** — nothing.
- **Priority** — **P1** (users report accidents; every day this
  isn't shipped is another user with a nuked file).
- **Effort** — S.
- **Status** — In progress (maintainer comment: "next build").
- **PR pointer** — **PR-8**.

### F-REQ-10 · "Why?" — explain each Playability flag (#36)

- **Ask** — "Tell me *why* a file failed playability. Permission
  issue? Corrupted? Sometimes I click 'Try to play' and it plays."
- **Rationale** — Users don't know if it's their setup or the
  file. `SuggestedFix` alone is not enough context.
- **Sketch** — pure frontend change in `configPage.html`. The
  Playability issue's `DetailsJson.Reason` (`"decode-error"`) and
  `.Detail` (the ffmpeg tail) already carry the info; the issue
  card just needs a `<details><summary>Why?</summary>...</details>`
  block that renders both. Same shape works for every issue type
  where `DetailsJson.Reason` exists (CorruptNfo,
  SubtitleLanguage, MalwareRisk).
- **Depends on** — nothing.
- **Priority** — **P2**.
- **Effort** — S.
- **Status** — Proposed. Maintainer mentioned a recycle-bin revamp
  underway with similar spirit.
- **PR pointer** — **PR-9**.

---

## Copy diagnostics (cross-cutting)

### F-REQ-11 · One-click "Copy diagnostics" button on Errors tab

- **Ask** — implicit across #6 / #26 / #33 / #38 / #39 — every user
  who tries to file a bug says "these instructions don't match the
  UI, there's no Copy button".
- **Rationale** — Users copy diagnostics from the "Report an issue"
  flow currently (which opens a new GitHub tab). Users who don't
  want to file publicly can't get the payload out.
- **Sketch** — one button next to `[Report an issue]` in
  `configPage.html` Errors tab that runs
  `navigator.clipboard.writeText(diagnosticsPayload)` on the same
  payload the report flow prepares.
- **Depends on** — nothing.
- **Priority** — **P2** (affects every future bug report).
- **Effort** — S.
- **Status** — Proposed.
- **PR pointer** — **PR-6** (bundled with diagnostics work).

---

## Feature ideas from user reports that aren't yet issues

Distilled from the audit + closed-issue reads. Not on the maintainer's
tracker but load-bearing for adoption.

### F-REQ-12 · Bin cap that actually stops writes (extends closed #29)

- **Ask** — "Warn me at X GB is display-only right now. Give me a
  cap that refuses further recycle writes."
- **Rationale** — Closed #29 fixed the banner but not the cap. #24
  and #17 hit the same wall.
- **Sketch** — `Fixers/RecycleBin.MoveToBin` at
  `RecycleBin.cs:151` checks `Info.SizeBytes + fileSize` against
  `PluginConfiguration.RecycleBinCapGb`. If exceeded and no
  fallback path configured, refuse the move and emit a "bin full"
  fix-failure history entry.
- **Depends on** — **PR-1** (bin data-model unification — need the
  correct `SizeBytes`).
- **Priority** — **P0** — bin-fills-boot-drive still causes crashes.
- **Effort** — S.
- **Status** — Proposed.

### F-REQ-13 · Scan preview: "this run will touch libraries [X, Y]"

- **Ask** — implicit from #13 ("music library nuked" — user hadn't
  meant to enable it).
- **Rationale** — Users get burned when a scanner walks a library
  they didn't opt-in. Preview prevents surprise.
- **Sketch** — new endpoint `GET /MediaDash/Scan/PreviewScope`
  returns `{ libraries: [{Id, Name, Kind, Enabled, Scanners: [...]}] }`
  computed from `PluginConfiguration.EnabledLibraries` +
  each scanner's `Skip*` config. UI: modal shown when the user
  clicks Scan, listing what will be touched, with a "cancel"
  option.
- **Depends on** — **PR-3** (scanner-enablement gating fix — needs
  the truth about which library is enabled).
- **Priority** — **P1** (blast-radius prevention).
- **Effort** — S.
- **Status** — Proposed.
- **PR pointer** — bundle with **PR-3**.

### F-REQ-15 · Preserve original-language audio track from metadata (Reddit — u/Tobias-Drundridge)

- **Ask** — "Poll the NFO / TMDB / IMDB for the film's original language
  and always keep that audio track, even if the user doesn't speak it —
  subtitles-only viewers want the original performance."
- **Rationale** — Substantial subset of users watch dub-averse. Global
  `AllowedAudioLanguages=[eng]` currently deletes the original French
  from a French film if the user only reads English.
- **Sketch** — `AudioLanguageScanner.cs` already has metadata access
  via `BaseItem.ProviderIds`. Add a new config field
  `PreserveOriginalLanguageAudio` (bool, default false). When true and
  the scanner is about to remove a track, resolve the item's original
  language via one of (in order): `BaseItem.OriginalTitle`-adjacent
  metadata provider result, TMDB `original_language`, IMDB primary
  language. If the track's language tag matches the resolved original,
  exclude it from the remove list even if not in
  `AllowedAudioLanguages`. Non-video items skip this (no concept of
  original language).
- **Depends on** — nothing.
- **Priority** — **P2** (matches user expectation from other tools;
  affects non-English original content).
- **Effort** — M (metadata provider resolution + cache for the lookup
  since `MediaSourceInfo` doesn't carry it directly).
- **Status** — Proposed.

### F-REQ-16 · Duplicate section: list all copies, choose keeper, preview each (Reddit — u/Commercial-Camp-8052)

- **Ask** — "In the duplicate copies section, list all the files, the
  suggestion for which one to keep, and let me pick a different one.
  Also let me play each one before deciding."
- **Rationale** — Auto-pick heuristics (higher bitrate, larger, newer)
  don't always match user preference — sometimes the smaller file is a
  hand-encoded golden copy. Users want to override.
- **Sketch** — `DuplicateScanner` already emits an `Issue` with a
  `DetailsJson` payload naming the paired paths. UI change only:
  `configPage.html` Issues panel renders each Duplicate issue as an
  expandable card listing every path in the pair/group, radio-select
  for the keeper, and a `<button>` per row that opens Jellyfin's own
  item player (`/web/index.html#!/details?id={ItemId}`) in a new tab.
  Backing endpoint `POST /MediaDash/Issues/{id}/SetKeeper` records the
  chosen path — the fixer at fix-time uses `DetailsJson.ChosenKeeper`
  if set, otherwise falls back to the heuristic.
- **Depends on** — nothing (existing scanner output already carries
  the paths).
- **Priority** — **P1** (users report accidentally losing preferred
  copies; every day this ships late is a rebuilt library).
- **Effort** — M (frontend + one endpoint + one config field on
  DuplicateIssue payload).
- **Status** — Proposed.

### F-REQ-14 · NVMe SMART via `MSFT_PhysicalDisk` (extends #23, #38)

- **Ask** — implicit from #23 — "NVMe drives don't report via
  ATA-SMART."
- **Rationale** — MediaDash's current SMART probe silently fails on
  NVMe and spams the Errors tab. Users on modern hardware are
  affected disproportionately.
- **Sketch** — extend `Probing/SmartHealthProbeWmi.cs` to detect
  NVMe interface via `MSFT_PhysicalDisk.BusType = 17` (NVMe), and
  read `HealthStatus` (int → OK/Warning/Unhealthy) +
  `Temperature` + `Wear`. Fall back to ATA path only for SATA.
- **Depends on** — nothing (independent of `SmartHealth` cache
  work in PR-6).
- **Priority** — **P2**.
- **Effort** — M (WMI query behaviour differs per Windows build;
  test on 10.0.26200 which is what this dev box runs).
- **Status** — Proposed.
- **PR pointer** — bundle with **PR-6**.

---

## Declined (kept here so they don't come back as PRs)

### Offload transcoding to a remote machine (#25 — closed)

Maintainer scope: MediaDash is a Jellyfin plugin, not a distributed
compute framework. If a user wants remote encode, they can point
Jellyfin's transcode-path at a network share and run ffmpeg
elsewhere. Not implementing.

### `.strm` link support (#4 — closed)

Fixed by adding `.strm` to a skip list, not by supporting streaming.
Playability scanner does not resolve VOD links; users with `.strm`
files should ensure MediaDash is configured to ignore them.

---

## How to use this file

1. When a new enhancement issue lands on GitHub, add an F-REQ-N
   entry here. Number is monotonically increasing; never reuse.
2. When shipping, flip `Status` to `Shipped` and add a
   `Shipped in: vX.Y.Z` line. Leave the entry so the roster stays
   traceable.
3. When declining, move to the "Declined" section with a one-line
   rationale.
4. Priority + effort are the maintainer's call, not the reporter's
   — reset them to match the release plan.

## Cross-references

- Fix roster: `ISSUES-WORKTHROUGH.md`
- Audit findings that underpin some of these features:
  `FINDINGS.md` (F-013, F-016, F-030, F-036, F-084, F-086)
- Raw issue snapshot: `issues/gh-issues-raw.json`
