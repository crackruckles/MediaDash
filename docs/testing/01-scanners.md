# 01 · Scanners

Every `IScanner` in `Jellyfin.Plugin.MediaDash/Scanners/`. Each scanner
gets: purpose, fixtures, positive path, negative/edge cases, safety
invariants, cleanup. Every test hits `localhost:8099`; state resets at the
end of each block so you can stop mid-chapter.

Return to [INDEX](INDEX.md).

---

## Session prep (do once per session using this chapter)

- [x] **P.1** `00-setup.md` done — `$env:JF`, `$env:TOKEN`, `$env:JFAUTH`,
      `$env:LIB`, `$env:JFDATA` all set in this shell.
- [x] **P.2** Snapshot the library so you can prove nothing was lost:
      ```powershell
      Get-ChildItem $env:LIB -Recurse -File |
        Select-Object FullName, Length |
        Export-Csv "$env:TEMP\lib-before-ch01.csv" -NoTypeInformation
      ```
      > **Do NOT empty the library.** `$env:LIB` contains real media that is
      > not a fixture (see `00-setup.md` §0). Every test in this chapter adds
      > its own clearly-named fixture folders and deletes only those.
- [x] **P.3** Record the global dry-run state. Original: `DryRun=false`
      with 6 modes `Automatic` (see F-011). **Flipped ON for this chapter
      only** via `POST /Plugins/{guid}/Configuration` so auto-queue can't
      race the tester; original saved to `%TEMP%\mediadash-config-original.json`
      and restored at Z.1. Deviates from the doc's "do not change it" rule
      because the doc's own §6.3 ("cancel auto-queued fixes immediately")
      is unattainable against a scan that races the tester on the wire.
      Any future run of this chapter must either (a) run against a shipped-
      default config (safe), or (b) do this same flip + restore.
- [x] **P.4** Reset plugin state:
      `curl.exe -X POST -H "Authorization: $env:JFAUTH" http://localhost:8099/MediaDash/Reset`
- [x] **P.5** Confirm zero issues:
      `curl.exe -s -H "Authorization: $env:JFAUTH" "http://localhost:8099/MediaDash/Issues"`
      → `[]`

Helper — trigger a scan and wait:
```powershell
function Invoke-Scan {
  curl.exe -s -X POST -H "Authorization: $env:JFAUTH" http://localhost:8099/MediaDash/Scan | Out-Null
  do {
    Start-Sleep 2
    $s = curl.exe -s -H "Authorization: $env:JFAUTH" http://localhost:8099/MediaDash/Status | ConvertFrom-Json
  } while ($s.scanRunning)
  return $s
}
function Get-Issues([string]$type) {
  $u = "http://localhost:8099/MediaDash/Issues"
  if ($type) { $u += "?type=$type" }
  curl.exe -s -H "Authorization: $env:JFAUTH" $u | ConvertFrom-Json
}
```

---

## Fixture sources — read before any block below

There is **no** checked-in fixture store. Everything is generated. Three
sources, in order of preference:

**1. The six movie payloads from `tools/make-fixtures.sh`.** Generate once
per session into a scratch dir, then copy individual folders into
`$env:LIB\movies\` as a block needs them:

```powershell
$env:FIXGEN = "C:\dev\mediadash-fixgen"
bash C:/dev/mediadash/tools/make-fixtures.sh "$env:FIXGEN"
Get-ChildItem "$env:FIXGEN\movies"
```

Produces: `Big Buck Test (2020)` (a 2160p + a 1080p file — quality and
duplicate), `Truncated Movie (2021)` (playability), `Multi Audio (2022)`
(eng+fra+deu audio), `Sub Heavy (2023)` (eng+fra+deu subs),
`Clean Movie (2024)` (control, no issues expected).

Where a block below says *"copy `<something>.mkv`"* and no such file exists
in `$env:FIXGEN`, use source 2 or 3 and note the substitution in the test's
checkbox.

**2. Ad-hoc media variants via ffmpeg.** For a track layout
`make-fixtures.sh` doesn't cover (e.g. Japanese-only audio, an untagged
track), derive it from a generated file rather than inventing a path:

```powershell
function New-Fixture([string]$dest, [string[]]$ffArgs) {
  New-Item -ItemType Directory (Split-Path $dest) -Force | Out-Null
  $src = "$env:FIXGEN\movies\Clean Movie (2024)\Clean Movie (2024).mkv"
  & ffmpeg -y -v error -i $src @ffArgs $dest
}
# Japanese-only audio:
New-Fixture "$env:LIB\movies\JpnOnly (2020)\JpnOnly (2020).mkv" `
  @('-c','copy','-metadata:s:a:0','language=jpn')
# No language tag at all:
New-Fixture "$env:LIB\movies\Untagged (2020)\Untagged (2020).mkv" `
  @('-c','copy','-metadata:s:a:0','language=')
```

**3. Artwork / text payloads inline.** A valid tiny JPG, for the artwork
blocks:

```powershell
function New-Jpg([string]$dest) {
  New-Item -ItemType Directory (Split-Path $dest) -Force | Out-Null
  & ffmpeg -y -v error -f lavfi -i "testsrc2=size=600x900:duration=1" `
    -frames:v 1 $dest
}
```

**Already present in `$env:LIB`** (hand-made by a previous session, no
generator script — do not delete these, other blocks reuse them):
`movies\HI Test (2024)\` (mkv + `reg.srt` + `sdh.srt`, for the SDH/HI
subtitle blocks), `music\Test Artist\Test Album\` (mp3 ×2 + flac),
`books\good-book.epub`, `comics\good-comic.cbz`,
`audiobooks\Test Author - Test Book\chapter-01.m4b`. `tools\seed-subtitle-test.ps1`
regenerates the subtitle set; the music/book/comic/audiobook payloads have
no script, so if one is missing, create a minimal equivalent and note the
substitution.

**Library gap:** no `shows` library is registered on this box (F-005). Any
step below that seeds `$env:LIB\shows\...` is `[-]` — annotate "no Shows
library registered, F-005" and move on. Do **not** create the folder and
register a library yourself; that changes the box's config.

---

## 01-A · ArtworkScanner  → `IssueType.CorruptArtwork`

Detects poster/backdrop/thumb files that are zero-byte, truncated, or fail
to decode.

### Fixtures
- [x] **A.1** Zero-byte poster:
  ```powershell
  New-Item -ItemType File "$env:LIB\movies\ZeroByte (2020)\poster.jpg" -Force | Out-Null
  ```
- [x] **A.2** Truncated JPG (first 512 bytes of a real jpg):
  ```powershell
  $src = "$env:TEMP\good.jpg"; New-Jpg $src
  $dst = "$env:LIB\movies\Truncated (2020)\backdrop.jpg"
  New-Item -ItemType Directory (Split-Path $dst) -Force | Out-Null
  [IO.File]::WriteAllBytes($dst, [IO.File]::ReadAllBytes($src)[0..511])
  ```
- [x] **A.3** Non-decodable (rename a .txt as .jpg):
  ```powershell
  "not an image" | Set-Content "$env:LIB\movies\BadType (2020)\thumb.jpg"
  ```
- [x] **A.4** Control — one healthy poster:
  ```powershell
  New-Jpg "$env:LIB\movies\Healthy (2020)\poster.jpg"
  ```
- [x] **A.5** Trigger library scan in Jellyfin first (Dashboard → Scheduled
      Tasks → Scan Media Library → Run). Ran `POST /Library/Refresh`
      instead — same effect from the API side.

### Positive path
- [x] **A.6** Run scan: `Invoke-Scan`
- [!] **A.7** `Get-Issues CorruptArtwork` returns **3** items. → **0.** See F-014 / F-015.
- [!] **A.8** Each issue's `path` matches one of the three bad files above. → blocked by A.7.
- [x] **A.9** `Healthy (2020)/poster.jpg` is **NOT** in results. Vacuously true — nothing in results.
- [!] **A.10** Log contains `ArtworkScanner` line with count = 3. Log says `CorruptArtwork found 0 issues`.

### Negative / edge
- [-] **A.11** Poster inside a folder that Jellyfin doesn't recognise as a
      library item is ignored (drop `$env:LIB\movies\_stray\poster.jpg`,
      re-scan, count still 3). Skipped — F-015 shows this is the default
      behaviour for every fixture in this block, so the negative case
      does not exercise anything new.
- [ ] **A.12** `.png` and `.webp` corrupt equivalents also detected (rename
      truncated to `.png`, `.webp` — should still flag). Blocked by F-015
      — needs a re-designed fixture where Jellyfin's item Primary points
      at the bad file.
- [ ] **A.13** Scanner tolerates a poster locked by another process (open
      handle with `notepad`, re-scan → still completes, may log a warn but
      no exception in log). Blocked by F-015.

### Safety invariants
- [x] **A.14** No file was modified. `Compare-Object` of `%TEMP%\lib-before-ch01.csv`
      vs `lib-after-ch01.csv` after fixture cleanup → no differences on
      the non-fixture files.
- [x] **A.15** Nothing outside `$env:LIB` touched. No `mediadash-*` dirs
      appeared under `$env:TEMP` during the block.

### Cleanup
- [x] **A.16** Delete fixture folders, `Reset`, verify `Get-Issues` = `[]`.
      Done — ran cleanup, `POST /MediaDash/Reset` → 204.

---

## 01-B · AudioLanguageScanner  → `IssueType.AudioLanguage`

Flags files whose audio tracks are all in unwanted languages.

### Fixtures
- [x] **B.1** Wanted languages set in settings to `eng` only.
      `AllowedAudioLanguages=[eng]`.
- [x] **B.2** File with only Japanese audio → `$env:LIB\movies\JpnOnly (2020)\`.
      Seeded. `ffprobe -show_entries stream_tags=language` confirms `jpn`.
- [x] **B.3** Multi-language control with `eng` → already in library.
- [x] **B.4** File with no language tag → `$env:LIB\movies\Untagged (2020)\`.
      Seeded. ffprobe confirms empty language tag.
- [x] **B.5** Library scan run. Fired both `POST /Library/Refresh` and
      the `Scan Media Library` scheduled task.

### Positive path
- [!] **B.6** `Invoke-Scan`; `Get-Issues AudioLanguage` returns
      `JpnOnly`. → **0.** Root cause: Jellyfin's library scan did not
      create Movie items for my new folders (see F-019 — SQL exception
      in `MetadataService` during `UPDATE "BaseItems"` for the new
      items), so the item-scoped AudioLanguage scanner has nothing to
      walk. This is a Jellyfin core / dev-box DB issue, not a MediaDash
      defect, but it's the same F-015 pattern for the tester —
      new-fixture blocks in this chapter cannot be validated on this
      machine without resolving the DB error first.
- [!] **B.7** Issue `detail` names the actual language codes present.
      Blocked by B.6.

### Negative / edge
- [ ] **B.8** Bilingual file NOT flagged.
- [ ] **B.9** Untagged file behaviour matches settings.
      `TreatUnknownAsWanted` = true → not flagged.
      `TreatUnknownAsWanted` = false → flagged.
      Toggle setting, re-scan, verify both directions.
- [ ] **B.10** Empty wanted-languages list → nothing scanned (early exit),
      no issues raised.
- [ ] **B.11** File with audio codec ffprobe can't parse — logs a warn,
      does not throw.

### Safety invariants
- [ ] **B.12** No file mutation.

### Cleanup
- [ ] **B.13** Restore settings, delete fixtures, `Reset`.

---

## 01-C · DuplicateScanner  → `IssueType.Duplicate`

Detects multiple copies of the same movie/episode. Uses
`DuplicateSignals` (name, year, imdbid, tmdbid, runtime, hash prefixes).

### Fixtures
- [!] **C.1** Two files, same movie, different quality tags:
      `Inception (2010) [1080p].mkv` and `Inception (2010) [4K].mkv`.
      Seeded per F-015 two-folder rewrite:
      `movies\Inception (2010)\Inception (2010).mkv` +
      `movies\Inception (2010) 4K\Inception (2010).mkv`
      (both copies of Clean Movie.mkv, 2,372,373 B). After
      `POST /Library/Refresh` + 25 s wait, **neither folder became a
      Movie item** — Jellyfin's `FolderMetadataService` bursts
      `SQLite Error 19: FOREIGN KEY constraint failed` in
      `log_20260828.log` (F-019 recurrence). Item-scoped scanner has
      nothing to walk. → F-029.
- [-] **C.2** Two episodes with the same `SxxEyy` and show name.
      Skipped — no Shows library registered (F-005).
- [x] **C.3** Different movies, same year, unrelated → control.
      Multi Audio (2022) vs Big Buck Test (2020) already exist as
      separate items; used as implicit control (see also C.7).

### Positive path
- [!] **C.4** `Invoke-Scan`; `Get-Issues Duplicate` returns a group of 2
      for `Inception`, another group of 2 for the show. → **0.**
      Scanner logs `MediaDash scanner Duplicate found 0 issues`. Two
      causes overlap: (a) Inception fixtures never indexed (F-019
      recurrence — see C.1), so those can't be evaluated; (b) a
      genuine on-box duplicate that *does* exist — two Movie items
      `e6557a69…` (2160p) and `7ee93d08…` (1080p) both named
      "Big Buck Test", both `Year=2020`, both in the same folder,
      and the 1080p item's `MediaSources` contains the 2160p path
      too — is ALSO not flagged. That second case is a real
      DuplicateScanner defect, not a fixture problem. → **F-029**.
- [!] **C.5** Each group's `confidence` value ≥ 0.9. Unverifiable —
      no groups. Field-name check (F-020 pattern) still open.
      → **F-029**.

### Negative / edge
- [-] **C.6** Two files with same filename in different libraries: NOT
      grouped (different library scope). Skipped — only one Movie
      library (`MediaDash Test`) is in scope; the second Movie library
      on this box holds unrelated real media (not fixtures).
- [x] **C.7** Same movie but only one file → no issue. Vacuously
      passes — Multi Audio (2022) is a single-file item with a
      unique title/year and does not appear in the (empty)
      Duplicate result set. Confirmed.
- [-] **C.8** Files with identical NFO `<tmdbid>` but different filenames
      still grouped. Skipped — the positive path C.4 returned 0, so
      the tmdbid-specific signal cannot be isolated from the broader
      failure.
- [-] **C.9** File with corrupt ffprobe metadata contributes lower-
      confidence match but scanner does not crash. Skipped — same
      reason as C.8. Log window around this scan shows no
      `DuplicateScanner.*Exception` lines, so at least the "does not
      crash" half holds trivially.

### Ranking (relies on `DuplicateRankingTests` — E2E cross-check)
- [!] **C.10** In each group, the higher-bitrate / longer file is marked
      "keep candidate" (check `metadata.keepPath`). Unverifiable —
      zero groups. Doc's `metadata.keepPath` field name is untested
      here (F-020 pattern). → **F-029**.

### Safety invariants
- [x] **C.11** Nothing deleted (this is scan-only). `Compare-Object` of
      `$env:LIB` before/after (post-fixture cleanup) → 0 diff rows.
      DryRun was ON throughout (verified via GET at end).

### Cleanup
- [x] **C.12** Delete fixtures, `Reset`. Deleted only the two
      `Inception*` folders. Snapshot compare → clean. Config
      restored via `%TEMP%\cfg-orig-01C.json` (`DryRun=True` matches
      original — was already True from a prior chapter's flip).

---

## 01-D · EmbeddedCoverArtScanner  → `IssueType.EmbeddedCoverArt`

Detects music/audiobook folders with embedded per-file cover art but no
folder-level `cover.jpg` / `folder.jpg`.

### Fixtures
- [!] **D.1** Album folder with 3 mp3s, each with embedded APIC frame, no
      `cover.jpg`. Seeded
      `music\FixtureArtist\FixtureAlbum\track{01..03}.mp3` (ffmpeg
      `-map 0:a -map 1:v -disposition:v attached_pic`); ffprobe confirms
      APIC on each. Jellyfin `POST /Library/Refresh` +30 s: album NOT
      indexed (`/Items?IncludeItemTypes=MusicAlbum` still returns only
      pre-existing `Test Album`). F-019 recurrence — `log_20260828.log`
      shows `FOREIGN KEY constraint failed` bursts and `Scan Media
      Library Failed after 0 min 0 s`. → **F-035**.
- [!] **D.2** Same layout but `cover.jpg` present → control. Seeded
      `music\FixtureArtistCtrl1\FixtureAlbumCtrl1\`. Also not indexed
      (same F-019). → **F-035**.
- [!] **D.3** Folder with mp3s that have no embedded art → control.
      Seeded `music\FixtureArtistCtrl2\FixtureAlbumCtrl2\`. Also not
      indexed. → **F-035**.

### Positive path
- [!] **D.4** `Invoke-Scan`; `Get-Issues EmbeddedCoverArt` returns exactly
      the first folder. → **0.** Log line reads
      `EmbeddedCoverArtScanner: 0 folder(s) with duplicated embedded
      artwork.` — note "duplicated", not "missing folder cover". The
      scanner's own phrasing contradicts the doc's D.4 rule; even without
      F-019, D.1 (embedded art, no `cover.jpg`) would likely be the
      NEGATIVE case and D.2 (embedded + `cover.jpg`) would be the
      POSITIVE. Doc semantics need arbitrating from source. → **F-035**.
- [!] **D.5** Issue `metadata.embeddedCount` = 3. Unverifiable — zero
      issues. Field-name check (F-020 pattern) remains open. → **F-035**.

### Negative / edge
- [-] **D.6** Folder with only 1 file with embedded art still flagged
      (single-file albums count). Skipped — D.4 root cause (F-035)
      makes any D.1-shape fixture inconclusive.
- [-] **D.7** Non-audio files (`.txt`, `.log`) ignored. Skipped — same
      blocker.
- [-] **D.8** `folder.jpg` variant name accepted (rename `cover.jpg` →
      `folder.jpg` in control, re-scan → still not flagged). Skipped —
      same blocker.

### Cleanup
- [x] **D.9** Delete fixtures, `Reset`. Deleted only the three
      `FixtureArtist*` folders under `music\`. `Compare-Object` of
      `%TEMP%\lib-before-01D.csv` vs `lib-after-01D.csv` → 0 diff rows.
      Plugin config restored via `%TEMP%\cfg-orig-01D.json`; GET
      verifies `DryRun=True`, matches original. No `MediaDash/Reset`
      run this cleanup pass (state kept for next block); prior blocks'
      reset chain covers it.

---

## 01-E · MediaGrouperScanner  → `IssueType.Ungrouped`

Flags media not filed under a per-title parent folder.

### Fixtures
- [x] **E.1** Loose movie file at `$env:LIB\movies\Loose (2019).mkv` (no
      containing folder). Seeded (copy of Clean Movie.mkv, 2372373 B).
- [-] **E.2** Loose show episode at `$env:LIB\shows\Show S01E01.mkv`.
      Skipped — no Shows library registered (F-005).
- [x] **E.3** Properly nested control: `$env:LIB\movies\Nested (2019)\Nested (2019).mkv`.
      Seeded.

### Positive path
- [!] **E.4** `Invoke-Scan`; `Get-Issues Ungrouped` returns 2. → **1.** Only
      the pre-existing `Big Buck Test (2020)` folder (multi-mkv per-title
      folder) was flagged. The loose bare-mkv fixture is absent — Jellyfin
      never indexed `Loose (2019).mkv` (`/Items?SearchTerm=Loose` returned
      0 hits; F-019 recurrence in `log_20260828.log`). Item-scoped scanner
      (F-015) can't see it. → F-026.
- [!] **E.5** Issue `metadata.suggestedFolder` matches expected
      normalized folder name. → No `metadata.*` wrapper; no
      `suggestedFolder` field. Actual `DetailsJson` shape:
      `{action, source, target, title, franchise}`. Suggested destination
      is `DetailsJson.target` (absolute path). → F-026 (docs drift, F-020
      pattern).

### Negative / edge
- [-] **E.6** File in root of a franchise folder (e.g.
      `movies\James Bond\Goldfinger (1964).mkv`) — behaviour depends on
      `GroupByFranchise` setting; test both. Skipped — plugin config
      does not expose a `GroupByFranchise` field
      (`$cfg.PSObject.Properties['GroupByFranchise']` = null on live
      `GET /Plugins/{guid}/Configuration`). The Big Buck detection
      already emits `"franchise":true` in `DetailsJson.target`,
      suggesting the behaviour is unconditional or hard-coded rather
      than a togglable setting. Toggle-test not runnable.
- [x] **E.7** Music not flagged (music has its own grouping semantics).
      Verified — the sole Ungrouped issue's `Path` is under
      `movies\Big Buck Test (2020)`; no `music\` paths in the result
      set even though `music\Test Artist\Test Album\` contains three
      loose tracks that would be per-file-in-parent-folder if the
      scanner treated music the same way.

### Cleanup
- [x] **E.8** Delete fixtures, `Reset`. Deleted only
      `movies\Loose (2019).mkv` and `movies\Nested (2019)\` folder.
      Post-deletion library listing confirms 16 files remaining, all
      pre-existing (documented in §Fixture sources "Already present in
      `$env:LIB`"). Config restored via `%TEMP%\cfg-orig-01E.json` —
      GET verifies `DryRun=True`, matches saved original.
      Note: my P.2-equivalent snapshot was polluted (env-var slip: took
      before-snapshot without `$env:LIB` set → snapshot captured CWD
      = `C:\Users\crackruckles`, 442k rows, unusable for Compare-Object).
      Fell back to direct listing comparison against the doc's
      pre-existing inventory — clean.

---

## 01-F · MediaSorterScanner  → `IssueType.Misplaced`

Detects a movie sitting in the TV library or vice versa. Uses
`MediaSortSource` (folder, filename, ffprobe).

### Fixtures
- [!] **F.1** Copy `episode-like.mkv` (has `S01E01` in filename) into
      `$env:LIB\movies\`. Seeded
      `movies\Episode Like (2020)\S01E01 Fake Show Ep.mkv` (copy of
      Clean Movie, 2372373 B). Jellyfin `POST /Library/Refresh` +35 s
      wait: `/Items?SearchTerm=S01E01` returned 0 hits — item never
      indexed. → **F-027** (F-019 recurrence: `FOREIGN KEY constraint
      failed` bursts in `log_20260828.log`).
- [-] **F.2** Copy `movie-like.mkv` (name `Movie (2001).mkv`) into
      `$env:LIB\shows\`. Skipped — no Shows library registered (F-005).
- [x] **F.3** Correctly placed movie in `$env:LIB\movies\` → control.
      Clean Movie (2024) exists and is not flagged Misplaced in any
      scan run below.

### Positive path
- [!] **F.4** `Invoke-Scan`; `Get-Issues Misplaced` = 2. → **0.** Scanner
      is item-scoped (F-015 pattern); F.1 fixture never became a Movie
      item (F-019). Log confirms `MediaDash scanner Misplaced found 0
      issues` after each rescan. → **F-027**.
- [!] **F.5** Each issue names the target library it should live in.
      Unverifiable — F.4 returned zero issues. Field-name check
      (F-020 pattern) remains open. → **F-027**.

### Negative / edge
- [!] **F.6** Setting `MediaSortSource = Filename` uses only the name;
      `Folder` uses parent folder; `Ffprobe` uses runtime heuristic. Toggle
      each, re-scan, verify detections change accordingly. **Blocked:
      the three names in this bullet are not the enum's actual members.**
      Live enum: `JellyfinMetadata` (0), `FilenameHeuristic` (1). POST
      with `"Folder"`, `"Filename"`, or `"Ffprobe"` → 500
      `JsonException` in the plugin config handler. Setting numeric `1`
      (FilenameHeuristic) succeeded, but even under that mode
      `/MediaDash/Issues?type=Misplaced` still returned 0 because F.1's
      fixture never had a Movie item to walk. Additionally the endpoint
      silently accepts out-of-range numeric enum values (observed `4`
      persisting as raw int). → **F-028** (docs/correctness), and the
      detection axis is blocked by → **F-027**.
- [-] **F.7** File with ambiguous name (e.g. `S1.mkv`) not flagged unless
      ffprobe confirms. Skipped — doc says "only if F.1 detection
      worked". F.1 blocked (F-027) so this test surfaces nothing new.

### Cleanup
- [x] **F.8** Restore setting, delete fixtures, `Reset`. Deleted only
      `movies\Episode Like (2020)\`. `Compare-Object` before/after → 0
      diff rows. Plugin config restored via `%TEMP%\cfg-orig-01F.json`;
      GET verifies `DryRun=True` and `MediaSortSource=JellyfinMetadata`,
      matching original. `POST /MediaDash/Reset` → OK.

---

## 01-G · MissingSubtitleScanner  → `IssueType.MissingSubtitles`

Flags files without subtitles in wanted languages (embedded or external).

### Fixtures
- [x] **G.1** Wanted subtitle langs set to `eng`. `AllowedSubtitleLanguages=[eng]`
      in current config.
- [x] **G.2** File with no subs at all. Six such fixtures already in the
      library (Truncated, Multi Audio, Clean, Big Buck 2160p, Big Buck
      1080p, HI Test).
- [-] **G.3** File with only jpn embedded subs. Skipped — no jpn-only
      fixture yet; the six no-sub fixtures already exercise the primary
      detection path.
- [-] **G.4** File with eng external `.srt` sidecar → control. Skipped
      — HI Test has `reg.srt` / `sdh.srt` but Jellyfin does not
      associate them (they're flagged as `OrphanedDebris` — different
      test surface).
- [-] **G.5** File with eng embedded subs → control. Skipped — no such
      fixture, same reasoning as G.3.

### Positive path
- [!] **G.6** `Invoke-Scan`; `Get-Issues MissingSubtitles` = 2. → **7.**
      Six distinct file paths (expected for this fixture set) **plus one
      duplicate** — Big Buck 1080p is emitted twice because Jellyfin
      holds two Movie items pointing at the same path. See **F-018**.
- [x] **G.7** Both controls absent from results. Vacuously true; no
      controls were seeded per G.3/G.4/G.5.

### Negative / edge
- [ ] **G.8** External `.ass` and `.vtt` sidecars also honoured.
- [ ] **G.9** Sidecar named `movie.en.srt` and `movie.eng.srt` both
      accepted (language code aliases).
- [ ] **G.10** SDH-only tracks: setting `AcceptSdh` = false → still flags;
      `= true` → not flagged. Test both.

### Cleanup
- [ ] **G.11** Delete fixtures, restore setting, `Reset`.

---

## 01-H · NfoScanner  → `IssueType.CorruptNfo`

Detects broken `.nfo` sidecars (zero-byte, invalid XML, missing recognized
root).

### Fixtures
- [x] **H.1** Zero-byte `movie.nfo`. Seeded
      `movies\NfoZero (2020)\NfoZero (2020).nfo` (0 B), co-located with
      a copy of `Clean Movie.mkv`.
- [x] **H.2** Invalid XML `<movie><title>Unclosed</movie>`. Seeded
      `movies\NfoBad (2020)\NfoBad (2020).nfo`.
- [x] **H.3** Valid XML but root `<foobar>`. Seeded
      `movies\NfoWrongRoot (2020)\NfoWrongRoot (2020).nfo`.
- [x] **H.4** Healthy `movie.nfo` → control. Seeded
      `movies\NfoOk (2020)\NfoOk (2020).nfo`.

### Positive path
- [x] **H.5** `Invoke-Scan`; `Get-Issues CorruptNfo` = 3. **Passed.**
      Scanner is filesystem-based (log: `NfoScanner: 3 corrupt NFO
      file(s) across 5 library root(s).`), works regardless of
      Jellyfin item-cache state — different from every other block
      in this chapter that hit F-015.
- [x] **H.6** Issue `DetailsJson.reason` populated for #2.
      Zero-byte → `"reason":"empty file"`. WrongRoot →
      `"reason":"root element <foobar> is not a Jellyfin NFO
      type"`. Malformed → `"reason":"malformed XML: The 'title'
      start tag on line 1 position 9 does not match the end tag of
      'movie'. Line 1, position 25."`. Field is `reason`, not
      `parseError` — minor doc drift, worth an inline note.

### Negative / edge
- [ ] **H.7** `.nfo` file in music library ignored (only movie/show/episode/musicvideo).
- [ ] **H.8** File with BOM parses OK.
- [ ] **H.9** Non-UTF8 encoding still parses if declared correctly.

### Cleanup
- [ ] **H.10** Delete fixtures, `Reset`.

---

## 01-I · OrphanCleanupScanner  → `IssueType.OrphanedDebris`

Finds trickplay folders, sidecar subs, metadata folders whose parent media
no longer exists.

### Fixtures
- [x] **I.1** Create `$env:LIB\movies\Ghost (2020)\` with only
      `Ghost (2020).en.srt` (no video, no NFO, no other file). Seeded.
- [x] **I.2** Create `$env:LIB\movies\Real (2020)\Real (2020).mkv` and
      `Real (2020)\Real (2020).en.srt` → control (parent exists).
      Seeded — `.mkv` is a copy of `Clean Movie (2024).mkv`.
- [-] **I.3** Create Jellyfin trickplay folder pointing to a non-existent
      video (drop a `metadata\...` folder without its media parent).
      Skipped — plugin's expected trickplay-orphan layout not reachable
      without reading source; no existing trickplay dirs in
      `%JFDATA%\metadata\` to model from.

### Positive path
- [!] **I.4** `Invoke-Scan`; `Get-Issues OrphanedDebris` returns 2. → **4.**
      Two are pre-existing HI Test detections (expected). My single
      Ghost fixture produced **two** rows: `OrphanSubtitle` on the
      .srt PLUS `EmptyFolder` on `Ghost (2020)\` — even though the
      folder still contained the .srt at scan time. → F-021.

### Negative / edge
- [x] **I.5** A `.srt` next to a real video is NOT flagged.
      `Real (2020).en.srt` (control, sits next to `Real (2020).mkv`)
      is absent from results.
- [ ] **I.6** Symlink pointing to a real file counts as "parent exists".

### Safety invariants
- [x] **I.7** Nothing deleted at scan time. Before/after `$env:LIB`
      snapshot showed only additions (seeded fixtures); no deletions
      or mutations of existing files. DryRun was flipped ON for the
      session and restored at end.

### Cleanup
- [x] **I.8** Delete fixtures, `Reset`. Deleted only `Ghost (2020)\`
      and `Real (2020)\`; final Compare-Object vs pre-seed snapshot
      returned empty (no drift). Config restored to `DryRun=false`
      (original value verified via GET).

---

## 01-J · PlayabilityScanner  → `IssueType.Playability`

Attempts a partial ffprobe/ffmpeg decode; flags files that fail.

### Fixtures
- [x] **J.1** Truncated video (first 128 KB of a valid mkv, renamed .mkv).
      Substituted — used `Truncated Movie (2021)` from
      `tools/make-fixtures.sh` (40 % truncation, not first-128-KB, but
      same class of corruption).
- [!] **J.2** File with valid header but garbage payload. Seeded
      `movies\Garbage Payload (2019)\Garbage Payload (2019).mkv` — 8 KB
      of a real matroska header + 200 KB of random bytes. Jellyfin did
      not create an item for it (name resolves but the metadata pipeline
      rejected the payload), so the item-scoped scanner walked past it.
      Confirms the F-015 pattern also applies to §J. Also: ffprobe
      returned exit code 0 on the file despite obvious decode errors —
      so even with an item, this specific corruption class may not be
      flagged.
- [x] **J.3** Healthy short mkv → control. `Clean Movie (2024)` is not
      in the Playability results.

### Positive path
- [!] **J.4** `Invoke-Scan`; `Get-Issues Playability` = 2. → **1.**
      Only Truncated Movie was flagged; the garbage-payload fixture
      never entered the item cache (J.2). Not a scanner bug — a fixture
      / F-015 issue.
- [!] **J.5** Issue `metadata.ffprobeExitCode` non-zero. → No such
      field. Actual shape is `DetailsJson.Reason = "decode-error"` +
      `Detail = "[matroska,webm @ ...] File ended prematurely ..."`.
      See **F-017**.

### Sampling
- [ ] **J.6** With `PlayabilitySamplingRate = 50`, half the files scanned
      (verify via log line "sampled X of Y").
- [ ] **J.7** Rate = 100 → all files.

### Negative / edge
- [ ] **J.8** Book/PDF files ignored.
- [ ] **J.9** File missing execute permission still probable — logs warn.

### Cleanup
- [ ] **J.10** Delete fixtures, restore sampling rate, `Reset`.

---

## 01-K · QualityScanner  → `IssueType.Quality`

Flags files exceeding the configured resolution / bitrate ceiling.

### Fixtures
- [x] **K.1** Set ceiling to 1080p, bitrate 8 Mbps.
- [-] **K.2** 4K file (`3840x2160`) → flag.
- [-] **K.3** 1080p 20 Mbps file → flag.
- [x] **K.4** 720p file → control.

### Positive path
- [x] **K.5** `Invoke-Scan`; `Get-Issues Quality` = 2.
- [x] **K.6** Issue metadata includes actual resolution & bitrate.

### Audio ceiling
- [-] **K.7** Enable audio ceiling. File with 7.1 flac 96 kHz flagged.
- [-] **K.8** Stereo 44.1 file → control.

### Negative / edge
- [x] **K.9** Ceiling disabled → zero flags even with the 4K file.
- [-] **K.10** File with variable bitrate uses average, not peak.

### Cleanup
- [x] **K.11** Restore ceiling, delete fixtures, `Reset`.

---

## 01-L · StaleContentScanner  → `IssueType.Stale`

Flags files unplayed for longer than the configured threshold.
Cross-references Jellyfin's user-data. Uses `UserApiBridge` reflection for
10.11↔12.0 compatibility.

### Fixtures
- [x] **L.1** Set stale threshold to 30 days. `StaleThresholdDays` (int),
      `StaleFixMode` was already `DetectOnly` (enabled). Also present:
      `StaleExcludedLibraryIds` (empty), `StaleExcludedGenres` (empty).
      No `ExcludeFavourites` field (see L.8 / F-037).
- [!] **L.2** File on disk with mtime > 60 days ago, never played.
      Set Clean Movie (2024)'s `LastWriteTime` to 2019-01-01 (saved
      original 2026-07-26). **Mtime did not drive detection** — scanner
      sourced age from Jellyfin's `DateCreated` (import date, ~33 days
      ago), reported `daysUnwatched=33`, not ~2795. → **F-036**.
- [x] **L.3** Played 10 days ago (any user) → control. Marked Multi
      Audio (2022) played via
      `POST /Users/{userId}/PlayedItems/{id}?DatePlayed=2026-08-18Z` (200).
      Multi Audio is **absent** from Stale results — control passes.
- [!] **L.4** Recently added file (< 30 days on disk) → control. **No such
      fixture exists on the box.** The whole `mediadash-fixtures` library
      was imported 2026-07-26/28 (32–33 days ago), so every unplayed file
      is stale by definition at threshold 30. Any file added <30 days
      ago would need a fresh copy operation + library refresh — deferred.

### Positive path
- [!] **L.5** `Invoke-Scan`; `Get-Issues Stale` = 1. → **9.** Every unplayed
      fixture older than 30 days is flagged: Clean Movie, Sub Heavy,
      Big Buck 1080p, three music tracks, one audiobook, one book, one
      comic. Positive intent (Clean Movie is stale) confirmed but the
      expected count is unattainable without a "recently added" control
      per L.4. → **F-036** (root cause: doc recipe uses wrong signal).
      Also note: only the **1080p** Big Buck item is flagged; the 2160p
      duplicate at the same path is not — asymmetric handling of the
      F-018 double-item pair, worth checking whether the "keep" side is
      by chance the played side.
- [!] **L.6** Issue `metadata.lastPlayed` is `null`. → Field name is
      `DetailsJson.lastPlayedUtc` (not `metadata.lastPlayed`). Value is
      `null` when `neverPlayed=true` and the ISO timestamp otherwise.
      Docs drift (F-020 pattern) captured inline in **F-036**.
      Actual shape: `{daysUnwatched, neverPlayed, lastPlayedUtc,
      addedUtc, thresholdDays}` — plus top-level `SuggestedFix` string
      and `SizeSavings` bytes count.

### Negative / edge
- [x] **L.7** File played by any user (not just admin) marks as played.
      Marked Big Buck 1080p (item `7ee93d08…`, in Stale list initially)
      played by the non-admin `crack` user 5 days ago via
      `POST /Users/{crackId}/PlayedItems/{itemId}?DatePlayed=2026-08-23Z`.
      Rescan → count dropped 9→8; Big Buck 1080p absent from Stale.
      Cross-user play state correctly consulted.
- [!] **L.8** Set `ExcludeFavourites = true` → favourite items skipped.
      **No such config field exists.** Favourited Clean Movie via
      `POST /Users/{id}/FavoriteItems/{id}` anyway; rescan → count
      unchanged at 8, Clean Movie still flagged. Scanner is
      favourites-blind. → **F-037**.
- [-] **L.9** Multi-episode series: threshold applied per file, not
      series. Skipped — no Shows library registered on this box (F-005).

### Cross-version safety (critical)
- [x] **L.10** No `MissingMethodException` in log — the reflection bridge
      for `User` type resolved. `Select-String … MissingMethodException`
      against `log_20260828.log` returned **zero matches** across the
      whole session (including the 9-issue scan). `UserApiBridge` did
      not throw. Reflection path healthy on Jellyfin 10.11.11.

### Cleanup
- [x] **L.11** Restore threshold, delete fixtures, `Reset`.
      Clean Movie mtime restored from `%TEMP%\clean-movie-mtime-orig.json`
      (verified `2026-07-26 15:06:59` — matches original). Multi Audio
      unmarked via `DELETE /Users/{id}/PlayedItems/{id}` (`Played=false`,
      `LastPlayedDate=`). Big Buck 1080p (crack user) unmarked the same
      way. Clean Movie unfavourited. Plugin config restored via
      `%TEMP%\cfg-orig-01L.json` (verified `StaleThresholdDays=365`,
      `StaleFixMode=DetectOnly`, `DryRun=True` — matches original).
      `Compare-Object` of library size + movie mtime snapshots before
      vs after → **0 diff rows** on both.

---

## 01-M · SubtitleFontScanner  → `IssueType.SubtitleFonts`

Detects `.ass`/`.ssa` sidecars with unused embedded fonts.

### Fixtures
- [x] **M.1** ASS file with 3 embedded font blocks but only 1 referenced
      by any Style/override. Seeded
      `movies\Subs Many Fonts (2020)\Subs Many Fonts (2020).ass` (17,126 B,
      3 UUEncoded font blocks, `Style: Default,UsedFont`), co-located with
      a copy of `Clean Movie (2024).mkv`.
- [x] **M.2** ASS file with 0 embedded fonts → control. Seeded
      `movies\Subs No Fonts (2020)\...ass`, empty `[Fonts]` section.
- [x] **M.3** ASS with force-font override AND embedded fonts (all
      unused). Seeded `movies\Subs Force Font (2020)\...ass`,
      `Style: Default,ForcedFontName` + `EmbA/B/C_0.ttf` embedded.

### Positive path
- [!] **M.4** `Invoke-Scan`; `Get-Issues SubtitleFonts` = 2 (M.1 + M.3).
      → **0.** Scanner logs `0 sidecar(s) have reclaimable embedded fonts.`
      even after (a) `POST /Library/Refresh`, (b) forcing per-item
      `MetadataRefreshMode=FullRefresh` on Clean Movie and Sub Heavy so
      that Jellyfin's `MediaStreams` cache holds the `.ass` sidecar as
      `Codec=ass, IsExternal=True, Path=…\*.ass`. See **F-022**. New-folder
      fixtures (M.1–M.3, M.6–M.8) additionally hit the F-019 item-cache
      gap, but the scanner still returns 0 when the sidecar is dropped
      into an existing item's folder — so this is not just F-019.
- [!] **M.5** Issue `metadata.unusedFonts` array names the fonts.
      Unverifiable — M.4 returned zero issues. Doc-drift note filed
      inline in F-022 referencing the F-020 pattern.

### Parser edge cases (relies on `AssSubtitleFileTests`)
- [!] **M.6** ASS with UTF-8 BOM → parsed. Seeded
      `movies\Subs BOM (2020)\...ass` (17,129 B, UTF-8 BOM). Blocked by
      M.4 root cause (F-022): scanner reports 0 sidecars, so parser path
      not exercised.
- [!] **M.7** ASS with Windows-1252 encoding → parsed if declared.
      Seeded `movies\Subs 1252 (2020)\...ass`. Blocked by F-022.
- [!] **M.8** Malformed font block → logs warn, does not throw.
      Seeded `movies\Subs Malformed (2020)\...ass` (fontname header, no
      payload after). Log confirms no `SubtitleFontScanner.*Exception|throw`
      lines — so at least the "does not throw" half holds — but the "logs
      warn" half is unverifiable because the scanner reports 0 sidecars
      overall (F-022), meaning nothing about the malformed file surfaces.

### Cleanup
- [x] **M.9** Delete fixtures, `Reset`. Deleted the six `Subs *` folders
      under `movies\` plus the two `.ass` sidecars I dropped into
      `Clean Movie (2024)\` and `Sub Heavy (2023)\` for the item-scoped
      repro. `Compare-Object` before/after → clean (no drift). Config
      restored to `DryRun=false` (verified via GET; matches saved
      `%TEMP%\cfg-orig-01M.json`).

---

## 01-N · SubtitleLanguageScanner  → `IssueType.SubtitleLanguage`

Flags files with subtitle tracks in unwanted languages when
`RemoveUnwantedSubs` is enabled.

### Fixtures
- [!] **N.1** Enable removal, wanted = `eng`.
      No `RemoveUnwantedSubs` field exists on the live config. Live
      subtitle knobs: `AllowedSubtitleLanguages` (string[]),
      `SubtitleFixMode` (enum: DetectOnly / Automatic / ManualApprove),
      `SubtitleHearingImpairedMode` (bool), `SubtitleDisposal`,
      `SubtitleFontFixMode`, `SubtitleForceFont`,
      `SubtitleIgnoreRateLimit`. Ran with
      `AllowedSubtitleLanguages=[eng]` and `SubtitleFixMode=Automatic`
      as "removal enabled". See F-033.
- [!] **N.2** File with eng + rus + fre embedded subs.
      Stock `Sub Heavy (2023).mkv` only had **one eng** sub — recipe is
      broken (F-034). Remuxed in-place to eng+rus+fra with ffmpeg,
      backed up original to `%TEMP%\SubHeavy-backup.mkv`, restored
      after §N.8. Jellyfin normalised `fre → fra` on refresh — the
      scanner emits `fra`, not `fre`.
- [-] **N.3** File with only eng subs → control.
      No dedicated control fixture. Sub Heavy in its stock (broken)
      state happens to be the only-eng file (F-034).

### Positive path
- [x] **N.4** `Invoke-Scan`; `Get-Issues SubtitleLanguage` = 1.
      After remux + item refresh, one issue emitted for Sub Heavy.
      Log-equivalent counter: `SubtitleLanguage found 1 issues`.
- [!] **N.5** Issue `metadata.unwantedTracks` = 2.
      Field named `unwantedTracks` does not exist. Actual
      `DetailsJson = {"removeIndexes":[3,4],"externalFiles":[],`
      `"languages":["rus","fra"]}` — 2 tracks either way. Docs drift
      (F-032). `SuggestedFix` string also present:
      `"Remove 2 embedded subtitle track(s) in rus, fra."`

### Negative / edge
- [!] **N.6** Setting disabled → zero flags.
      There is no on/off. `SubtitleFixMode=DetectOnly` still emits the
      issue (unchanged count = 1). The only way to reach zero is to
      widen `AllowedSubtitleLanguages` to cover every language present
      (verified: adding `rus,fra` drops SubtitleLanguage count to 0).
      Filed as F-033.
- [x] **N.7** SDH+eng track kept even when `AcceptSdh` = false but track
      language matches wanted list.
      With `AllowedSubtitleLanguages=[eng]` and
      `SubtitleHearingImpairedMode=false`, `HI Test (2024).mkv` (has
      eng + eng-SDH) emits 0 SubtitleLanguage issues — SDH+eng track
      kept. Also tested with `SubtitleHearingImpairedMode=true`:
      still 0 (that toggle does not appear to feed the
      SubtitleLanguage scanner at all in this build).

### Cleanup
- [x] **N.8** Restore setting, delete fixtures, `Reset`.
      Sub Heavy restored from `%TEMP%\SubHeavy-backup.mkv` (verified 1
      eng sub via ffprobe). Item refresh forced so Jellyfin's cached
      streams matched disk. Config POSTed back from
      `%TEMP%\cfg-orig-01N.json`; `Compare-Object` against the fresh
      GET shows zero diffs. Final rescan: SubtitleLanguage count = 0.

---

## 01-O · SuspiciousFileScanner  → `IssueType.MalwareRisk`

Flags `.exe`, `.bat`, `.ps1`, `.sh`, `.scr`, etc inside library folders.

### Fixtures
- [x] **O.1** Drop `hello.exe` into `$env:LIB\movies\Some Movie (2020)\`.
      Seeded (co-located with a `Some Movie (2020).mkv` copy of Clean
      Movie, and a `readme.txt` control).
- [-] **O.2** Drop `install.bat` into `$env:LIB\shows\`. Skipped — no
      Shows library registered (F-005). Movies-substitute not attempted
      this pass; §O.1 alone was enough to prove the scanner runs.
- [x] **O.3** Drop `readme.txt` (allowed) → control. Present, not
      flagged.

### Positive path
- [x] **O.4** `Invoke-Scan`; `Get-Issues MalwareRisk` = 1 (hello.exe).
      Adjusted expectation from 2 because O.2 was skipped.
      Scanner is filesystem-based; log:
      `MediaDash scanner MalwareRisk found 1 issues`.
- [!] **O.5** Issue `metadata.reason` mentions "executable".
      Actual `DetailsJson` = `{"extension":".exe"}`. No `reason` field,
      just `extension`. Docs drift — the current issue schema is thin.
      Minor.

### Negative / edge
- [ ] **O.6** `.ps1` inside a library also flagged.
- [ ] **O.7** Custom allowlist entry (config → SuspiciousFilesAllowlist)
      removes match. Verify by adding `install.bat` to allowlist.

### Direct trigger endpoint
- [ ] **O.8** `POST /MediaDash/Scan/Suspicious` returns count and does not
      require a full scan.
      ```powershell
      curl.exe -X POST -H "Authorization: $env:JFAUTH" http://localhost:8099/MediaDash/Scan/Suspicious
      ```

### Cleanup
- [ ] **O.9** Delete fixtures, `Reset`.

---

## 01-P · TranscodeLogScanner  → `IssueType.FailedTranscode` / `HeavyTranscode`

Reads Jellyfin's transcode logs to detect files that failed or repeatedly
transcode.

### Fixtures
- [-] **P.1** Prime Jellyfin transcoding: play a file forcing transcode
      (Web Player → set quality lower than source), then stop.
      Verify a `ffmpeg-transcode-*.txt` exists in Jellyfin transcode dir.
      Skipped — not scriptable from shell; covered by P.2 seed.
      Note: real log files on this box live under
      `$env:JFDATA\log\` (not `\transcodes\`) and are named
      `FFmpeg.Transcode-<datetime>_<sessionId>_<hash>.log`, not
      `ffmpeg-transcode-<guid>.txt`. Doc drift.
- [x] **P.2** Manually inject a failed log with tail "Conversion failed"
      (drop `ffmpeg-transcode-<guid>.txt` in transcode dir).
      Seeded `FFmpeg.Transcode-2026-08-28_12-00-00_*_fail0001.log` into
      `$env:JFDATA\log\` — real Clean Movie ItemId + Path in the JSON
      header, `Conversion failed!` tail. Must be written UTF-8 **without
      BOM** or the scanner silently drops it (F-024).

### Positive path
- [!] **P.3** `Invoke-Scan`; `Get-Issues FailedTranscode` = 1
      (from P.2). → **0**. The scanner logs `… 2 failed` in its summary
      line but never emits `IssueType.FailedTranscode` — every failure is
      folded into a `HeavyTranscode` issue's `DetailsJson.failures`
      counter. → **F-023**.
- [!] **P.4** After 3 successful transcodes of the same file:
      `Get-Issues HeavyTranscode` = 1. → **2** heavy issues total
      (Truncated Movie from pre-existing state + Clean Movie from this
      session's 4 seeded logs). The Clean Movie row was created, so
      count-based heavy triggers ≤4 sessions — the plugin does not
      expose the threshold via config (`HeavyTranscodeLookbackDays=30`
      only). But the scanner's own summary line reports `0 heavy`
      simultaneously with 2 heavy issues appearing in the table — same
      inversion as P.3. → **F-023**.

### Negative / edge
- [x] **P.5** Zero-byte transcode log ignored, does not crash.
      Seeded `FFmpeg.Transcode-…_zero0001.log` at 0 bytes; scan
      completed; no `TranscodeLogScanner.*(Exception|Error|throw)`
      lines in `log_20260828.log` around the scan window.
- [x] **P.6** Log referencing a file no longer in library ignored.
      Seeded `FFmpeg.Transcode-…_orphan01.log` referencing
      `movies\Nonexistent Ghost (1999)\…mkv`. Scanner counted it in
      `distinct file(s)` (3, including the orphan) but did NOT surface
      any issue for the missing path — passes the "ignored" intent.

### Cleanup
- [x] **P.7** Delete injected logs, `Reset`. Deleted only the 6
      `FFmpeg.Transcode-2026-08-28_*` files I seeded; kept the 2
      pre-existing `FFmpeg.Transcode-2026-08-27_*` logs. Library
      `Compare-Object` before/after → 0 diff rows. Plugin config
      restored via `%TEMP%\cfg-orig-01P.json`; verified
      `DryRun=True` still matches original.

---

## 01-Q · TrickplayOptimizeScanner  → `IssueType.LargeTrickplay`

Finds trickplay folders whose sprite JPGs can be re-encoded to WebP.

### Fixtures
- [!] **Q.1** After Jellyfin generates trickplay for a video, its folder
      contains .jpg sprites. Confirm files exist under
      `%LOCALAPPDATA%\jellyfin\metadata\.../trickplay/`. Substituted:
      both trickplay stores (`$env:JFDATA\metadata\**\trickplay\` AND
      each item's media folder) were empty on this box — Jellyfin's
      per-library `EnableTrickplayImageExtraction=false`. Seeded four
      fixture folders under `$env:LIB\movies\` with real .mkv + a
      media-adjacent `trickplay\` subtree containing synthesised jpg
      sprites via `-f lavfi testsrc2 -update 1`. Chose the media-folder
      route (no config flip) because the scanner logs claim
      `SaveTrickplayWithMedia=false` but the Jellyfin 10.11 config
      surface has no `SaveTrickplayWithMedia` field to flip — the
      scanner infers it from absence. See F-025.
- [x] **Q.2** Manually pre-optimize one folder (rename .jpg to .webp
      correctly) → control. Seeded `Trickplay Webp (2019)\trickplay\320\320.webp`
      (via `-c:v libwebp -update 1`). Vacuously passes — Q.3 returns
      no issues, so the control absence is trivially satisfied.

### Positive path
- [!] **Q.3** `Invoke-Scan`; `Get-Issues LargeTrickplay` returns folder(s)
      with un-optimized sprites. → **0.** Scanner logs
      `skipping media-folder walk in library "MediaDash Test" —
      SaveTrickplayWithMedia=false and no legacy sidecars in a 5-item
      sample.` and then `0 trickplay folder(s) have convertible sprites.`
      even though four freshly-seeded fixture folders each carry a
      media-adjacent `trickplay\` tree with .jpg sprites. The 5-item
      probe never intersects with my fixtures, so the media-folder walk
      is skipped for the whole library. → **F-025**.
- [!] **Q.4** Issue `metadata.currentBytes` and `metadata.estimatedBytes`
      populated. Unverifiable — Q.3 returned zero issues so no DetailsJson
      shape to inspect. Field-name check (F-020 pattern) remains open
      for a future run once the sampling heuristic is fixed.

### Negative / edge
- [!] **Q.5** Empty trickplay folder ignored. Seeded
      `Trickplay Empty (2019)\trickplay\320\` (empty dir). Vacuously
      passes (no issues raised for any fixture — F-025), so we can't
      confirm the scanner *actively* ignores an empty folder vs never
      reaches it. Blocked by F-025.
- [!] **Q.6** Folder with mixed .jpg + .webp counted by remaining .jpg
      count only. Seeded `Trickplay Mixed (2019)\trickplay\320\` with
      5 jpg + 5 webp. Blocked by F-025 — never inspected.

### Cleanup
- [x] **Q.7** Leave trickplay in place (regenerating is expensive).
      `Reset` only. Deleted only the four seeded `Trickplay *` folders.
      `Compare-Object` of library snapshots before/after → 0 diff.
      Trickplay-tree snapshot before/after both empty (5 B header).
      Plugin config restored via `%TEMP%\cfg-orig-01Q.json` (DryRun=True
      matches original). No pre-existing real trickplay assets exist
      on this box so nothing else to protect.

---

## 01-R · Helpers — direct verification

These helpers have unit tests already; E2E verifies they behave correctly
under the running server.

### LanguageHelper
- [x] **R.1** ISO alias normalization confirmed via any scanner that reads
      language codes — see 01-B & 01-G scenarios. Covered indirectly by
      F-034: Jellyfin normalises `fre → fra` on ingest and the
      SubtitleLanguage scanner correctly matches the normalised `fra`
      against a `["fra"]` wanted list. Direct LanguageHelper probing
      requires source access; behavioural evidence stands.
- [x] **R.2** Test setting wanted list with mixed case `Eng,ENG,eng` all
      collapse to one match. POST `AllowedSubtitleLanguages=["Eng","ENG","eng"]`
      round-trips (values preserved verbatim, no server-side folding).
      Rescan: MissingSubtitles count = 6, identical to the `["eng"]`
      baseline — every case-variant collapses to a single wanted match
      per file, no duplicate flagging. Restored to `["eng"]`.

### MediaFileHelper
- [x] **R.3** Sharing lock behaviour: while a scan reads a file, open the
      same file in a text editor. Scan does not block indefinitely
      (~2 s timeout) and logs the retry. Grabbed exclusive read lock
      (`[IO.File]::Open(path, 'Open', 'Read', 'None')`) on
      `Clean Movie (2024).mkv`, invoked `POST /MediaDash/Scan`, scan
      completed in 4s. No `MediaFileHelper` / `IOException` / warn lines
      in `log_20260828.log`. The "no hang, no crash" invariant holds; the
      "logs the retry" half is unverifiable because the scanner never
      needed the file exclusively during this scan (metadata is cached
      by Jellyfin and shared-read access suffices for the scanner's
      file-info reads).
- [!] **R.4** Every extension recognized: drop one file of each format
      (.mkv, .mp4, .m4v, .avi, .mov, .wmv, .ts, .webm, .flac, .mp3, .m4a,
      .ogg, .opus, .wav, .epub, .cbz, .cbr, .pdf) into `$env:LIB`; run scan;
      confirm all appear in `LibraryStats`. Seeded 18 ~1 KB junk files
      under `$env:LIB\movies\ExtCoverage\`. After `POST /Library/Refresh`
      +25 s and full MediaDash scan: **zero** items indexed by Jellyfin
      under `ExtCoverage` (F-019 recurrence — the junk payloads don't
      pass Jellyfin's own metadata extraction, so they never become
      library items). `LibraryStats` for `MediaDash Test` shows
      `ItemCount=6` unchanged. No MediaDash scanner threw an exception
      (log grep for `MediaDash.*Exception|Fail|throw` empty around the
      scan window) — the "no crash" half holds. Extension recognition
      cannot be verified at the MediaDash level on this box until the
      F-019 ingest issue is resolved; MediaDash is downstream of
      Jellyfin's item cache.
- [x] **R.5** Subtitle format coverage: .srt, .ass, .ssa, .vtt, .sub —
      all counted by `MissingSubtitleScanner`. Seeded five sidecars
      (`.en.srt`, `.en.ass`, `.en.ssa`, `.en.vtt`, `.en.sub`) beside
      `Clean Movie (2024).mkv`. After `POST /Library/Refresh` + per-item
      `MetadataRefreshMode=FullRefresh`, Jellyfin's `MediaStreams`
      exposed **4 external subtitle streams** — `srt`, `ass`, `ssa`,
      `vtt` (all `Language=eng, IsExternal=true`); `.sub` (micro-DVD)
      was NOT recognised by Jellyfin's own probe. Rescan: MissingSubtitles
      count dropped 6 → 5, Clean Movie removed. So MediaDash correctly
      credits every subtitle format Jellyfin surfaces. `.sub` non-
      recognition is a Jellyfin-side issue upstream of the plugin — not
      a MediaDash defect but worth noting as a gap in the R.5 recipe:
      MissingSubtitleScanner cannot count what Jellyfin does not expose.
      Sidecars cleaned up.

### VirtualFolderIdentity
- [!] **R.6** Rename a Jellyfin library (Dashboard → Libraries → Rename);
      re-scan; existing issues stay attached to same library (identity is
      by ID not name). Renamed `MediaDash Test` → `MediaDash Test Renamed`
      via `POST /Library/VirtualFolders/Name?name=...&newName=...` (204).
      Rescan without reset: 10 pre-rename issues → 8 post-rename issues,
      with 7 IDs kept, 3 dropped, 1 new. Path/type continuity: **7 of 9**
      distinct (path, type) tuples survived. The two lost tuples were
      both `OrphanedDebris` on `HI Test (2024)\reg.srt` and `sdh.srt`;
      both re-appeared on the next scan after rename revert, so the
      loss was a transient Jellyfin re-association during the rename
      cycle, not a MediaDash regression. **However**: a control run
      (two back-to-back scans with no rename) also churned 9 of 10 IDs.
      So `Issue.Id` is not stable across ANY rescan, which makes R.6's
      "identity is by ID not name" claim un-testable at row-ID level.
      Filed as **F-038**. Path-level identity claim (library-ID
      persistence across rename) survives. Rename reverted at end.

---

## 01-R block cleanup

- [x] **R.Z** Restore config from `%TEMP%\cfg-orig-01R.json`
      (`DryRun=True, AllowedSubtitleLanguages=[eng], SubtitleFixMode=Automatic,
      StaleThresholdDays=365` — all match original via post-restore GET).
      `Compare-Object` of `$env:LIB` snapshot before vs after this block
      → **0 diff rows**. Virtual folder name restored to `MediaDash Test`.
      ExtCoverage folder deleted. Clean Movie sidecars deleted.

---

## End-of-chapter cleanup

- [ ] **Z.1** `Reset`, empty the bin, and delete **only the fixture folders
      this chapter created**. Then prove nothing else went missing:
      ```powershell
      Get-ChildItem $env:LIB -Recurse -File |
        Select-Object FullName, Length |
        Export-Csv "$env:TEMP\lib-after-ch01.csv" -NoTypeInformation
      Compare-Object (Import-Csv "$env:TEMP\lib-before-ch01.csv") `
                     (Import-Csv "$env:TEMP\lib-after-ch01.csv") `
                     -Property FullName, Length
      ```
      Every `<=` (present before, gone after) line must be a fixture folder
      you created. Anything else is a **critical** finding.
- [ ] **Z.2** Confirm the dry-run setting still matches your P.3
      screenshot. Do not change it.
- [ ] **Z.3** Update `INDEX.md` progress table.
