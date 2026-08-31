# 02 · Fixers

Every `IFixer` in `Jellyfin.Plugin.MediaDash/Fixers/`, plus supporting
classes (`RecycleBin`, `LibraryGuard`, `FfmpegExecutor`, `OutputVerifier`,
`RenameTemplate`, `PostUpgradeCleanup`).

Fixers mutate the filesystem — always dry-run first, then real, then check
recycle bin.

Return to [INDEX](INDEX.md).

---

## Session prep

- [ ] **P.1** `00-setup.md` §3 done.
- [ ] **P.2** Global dry-run **OFF** for this chapter (each block toggles
      as required, and re-enables at the end).
- [ ] **P.3** All per-fixer disposal methods set to **RecycleBin** (not
      Permanent) unless a specific block requires Permanent.
- [ ] **P.4** `Reset`; libraries empty.
- [ ] **P.5** Confirm bin path resolvable: run any fix once to force bin
      creation, then note `$BIN`.

Helpers:
```powershell
function Invoke-Fix {
  curl.exe -s -X POST -H "X-Emby-Token: $env:TOKEN" http://localhost:8099/MediaDash/Fix | Out-Null
  do {
    Start-Sleep 2
    $s = curl.exe -s -H "X-Emby-Token: $env:TOKEN" http://localhost:8099/MediaDash/Status | ConvertFrom-Json
  } while ($s.fixRunning)
  return $s
}
function Approve-All([string]$type) {
  curl.exe -s -X POST -H "X-Emby-Token: $env:TOKEN" "http://localhost:8099/MediaDash/Issues/ApproveAll?type=$type"
}
function Get-Bin { curl.exe -s -H "X-Emby-Token: $env:TOKEN" http://localhost:8099/MediaDash/RecycleBin/Items | ConvertFrom-Json }
```

---

## 02-A · ArtworkFixer  ← `CorruptArtwork`

Deletes corrupt artwork so Jellyfin re-fetches.

### Prep
- [ ] **A.1** Run 01-A fixtures & scan → 3 issues exist.
- [ ] **A.2** `Approve-All CorruptArtwork`.

### Dry-run
- [ ] **A.3** Enable global dry-run. `Invoke-Fix`.
- [ ] **A.4** Files still on disk (unchanged mtimes).
- [ ] **A.5** History shows 3 entries with `dryRun=true`.
- [ ] **A.6** Recycle bin empty.

### Real
- [ ] **A.7** Disable dry-run. `Invoke-Fix`.
- [ ] **A.8** 3 corrupt artwork files removed from disk.
- [ ] **A.9** Recycle bin has 3 items (disposal = RecycleBin).
- [ ] **A.10** Each bin item has origin manifest with original path.

### Permanent disposal
- [ ] **A.11** Restore fixtures + rescan. Set ArtworkFixer disposal to
      `Permanent`. `Invoke-Fix`.
- [ ] **A.12** Files removed. Recycle bin empty (permanent path).

### Safety invariants
- [ ] **A.13** No file OUTSIDE `$LIB` deleted.
- [ ] **A.14** Healthy control artwork untouched.

### Cleanup
- [ ] **A.15** Empty bin, `Reset`, restore disposal to `RecycleBin`.

---

## 02-B · DuplicateFixer  ← `Duplicate`

Retains the "keep candidate" of each group, moves the rest to bin.

### Prep
- [ ] **B.1** Fixtures from 01-C.
- [ ] **B.2** `Approve-All Duplicate`.

### Dry-run
- [ ] **B.3** `Invoke-Fix` (dry-run on). Nothing deleted; history rows
      show planned moves per group.

### Real
- [ ] **B.4** Disable dry-run. `Invoke-Fix`.
- [ ] **B.5** For each group of 2, keep-candidate survives; other moved
      to bin.
- [ ] **B.6** Bin contains `Inception (2010) [1080p].mkv` (the lower-
      bitrate one) and the second episode copy.
- [ ] **B.7** No duplicate group left un-resolved.

### Negative / edge
- [ ] **B.8** Approve only one issue in a group of 3 — fixer refuses to
      collapse a partial group; log records "insufficient approvals".
- [ ] **B.9** Confidence < threshold group not touched even when
      approved (verify with a synthetic low-confidence pair).

### Restore
- [ ] **B.10** From bin, restore the fixed-out file. It reappears at
      original path. Rescan → duplicate group reopens.

### Cleanup
- [ ] **B.11** Empty bin, delete fixtures, `Reset`.

---

## 02-C · EmbeddedCoverArtFixer  ← `EmbeddedCoverArt`

Extracts a shared `cover.jpg` and optionally strips embedded per-file art.

### Prep
- [ ] **C.1** Fixtures from 01-D.
- [ ] **C.2** Setting `StripEmbeddedAfterExtract` = true. Approve.

### Real
- [ ] **C.3** `Invoke-Fix`.
- [ ] **C.4** `cover.jpg` written to the flagged album folder.
- [ ] **C.5** Each mp3 in the folder now has no APIC frame (verify with
      `ffprobe -show_streams`).
- [ ] **C.6** Total folder size decreased.
- [ ] **C.7** Original mp3s have entries in bin as pre-strip backups.

### Setting off
- [ ] **C.8** `StripEmbeddedAfterExtract` = false. Restore fixtures. Fix.
      cover.jpg extracted, mp3s unchanged.

### Failure modes
- [ ] **C.9** Folder becomes read-only → fix skipped, history row shows
      `error=WriteAccessDenied`, no partial state.

### Cleanup
- [ ] **C.10** Empty bin, restore setting, `Reset`.

---

## 02-D · MediaGrouperFixer  ← `Ungrouped`

Moves loose files into per-title parent folders.

### Prep
- [ ] **D.1** Fixtures from 01-E. Approve.

### Dry-run
- [ ] **D.2** Fix (dry-run on). Nothing moved. Planned target folders
      shown in history detail.

### Real
- [ ] **D.3** Fix. `Loose (2019).mkv` now at
      `$LIB\movies\Loose (2019)\Loose (2019).mkv`.
- [ ] **D.4** `Show S01E01.mkv` moved to
      `$LIB\shows\Show\Season 01\Show S01E01.mkv` (per config).
- [ ] **D.5** Related sidecars (subs, .nfo) moved together.

### Collision handling
- [ ] **D.6** Target folder already contains a file with same name → fixer
      appends `.2` suffix, both survive, history flags "renamed on collision".

### Cleanup
- [ ] **D.7** Delete fixtures, `Reset`.

---

## 02-E · MediaSorterFixer  ← `Misplaced`

Moves cross-library-misplaced items into the correct library.

### Prep
- [ ] **E.1** Fixtures from 01-F. Approve.

### Real
- [ ] **E.2** Fix. `episode-like.mkv` moved from `$LIB\movies\` to
      `$LIB\shows\...`; `movie-like.mkv` moved from `$LIB\shows\` to
      `$LIB\movies\...`.

### Filename-based rename via `RenameTemplate`
- [ ] **E.3** Setting `NormalizeFilenames = true` renames using template.
      Move a file `The Movie 2020.mkv` → becomes `The Movie (2020).mkv`.
- [ ] **E.4** Template output for `RenameTemplateTests` cases matches doc.

### Cross-device move (relies on `FileBrowserCrossDeviceMoveTests`)
- [ ] **E.5** If test library spans two drives, move copies + verifies +
      deletes source (does not use rename).

### Cleanup
- [ ] **E.6** Delete fixtures, restore setting, `Reset`.

---

## 02-F · MissingSubtitleFixer  ← `MissingSubtitles`

Downloads / fetches missing subs from configured providers.

### Prep
- [ ] **F.1** OpenSubtitles/etc providers configured OR set fixer to
      "external command" mode with a stub script.
- [ ] **F.2** Fixtures from 01-G. Approve.

### Real
- [ ] **F.3** Fix. `.eng.srt` sidecars appear for both missing-subs files.
- [ ] **F.4** History row shows provider used + language.

### Quota (relies on `FixTaskSubtitleQuotaTests`)
- [ ] **F.5** Setting `MaxSubtitleDownloadsPerRun = 1` → only one file
      fixed per run; other stays as issue.

### Failure
- [ ] **F.6** Kill network mid-run → fixer surfaces provider error, does
      not create empty .srt files.

### Cleanup
- [ ] **F.7** Delete fixtures, restore quota, `Reset`.

---

## 02-G · NfoFixer  ← `CorruptNfo`

Deletes broken NFO files to trigger re-fetch.

### Prep
- [ ] **G.1** Fixtures from 01-H. Approve.

### Real
- [ ] **G.2** Fix. 3 broken NFOs removed. Healthy one intact.
- [ ] **G.3** Bin contains the 3 removed NFOs.
- [ ] **G.4** Trigger Jellyfin library scan; new NFOs get re-generated by
      the provider.

### Cleanup
- [ ] **G.5** Empty bin, `Reset`.

---

## 02-H · OrphanCleanupFixer  ← `OrphanedDebris`

Deletes orphaned sidecars, trickplay dirs, metadata dirs.

### Prep
- [ ] **H.1** Fixtures from 01-I. Approve.

### Real
- [ ] **H.2** Fix. Orphaned sidecar + orphaned trickplay folder gone.
      Real (control) folder intact.
- [ ] **H.3** Bin contains the removed items.

### Safety
- [ ] **H.4** Fixer refuses to touch anything outside `$LIB` — inject a
      fake "orphan" path pointing to `$env:TEMP\...` (via debug SQL) and
      confirm fixer logs
      `LibraryGuard: refused path outside library` and does not delete.

### Cleanup
- [ ] **H.5** Empty bin, `Reset`.

---

## 02-I · PlayabilityFixer  ← `Playability`

Removes unplayable files (moves to bin).

### Prep
- [ ] **I.1** Fixtures from 01-J. Approve.

### Real
- [ ] **I.2** Fix. 2 broken videos in bin; healthy control intact.

### With re-fetch (if enabled)
- [ ] **I.3** Setting `AttemptRedownload = true` + a valid download
      provider stub → after removal, a fresh copy is fetched. History has
      re-download entry.

### Cleanup
- [ ] **I.4** Empty bin, `Reset`.

---

## 02-J · SubtitleFontFixer  ← `SubtitleFonts`

Rewrites .ass to remove unused font blocks.

### Prep
- [ ] **J.1** Fixtures from 01-M. Approve.

### Real
- [ ] **J.2** Fix. ASS file rewritten. File size dropped by ~KB.
- [ ] **J.3** `ffprobe` still reports the sub track. Rendering test in
      Jellyfin Web Player: subtitles still display correctly.
- [ ] **J.4** Original ASS file in bin as `.orig` variant.

### Force-font override case
- [ ] **J.5** Fix applied to file with override — all embedded fonts
      stripped, override styling still applies at play time.

### Cleanup
- [ ] **J.6** Empty bin, `Reset`.

---

## 02-K · SuspiciousFileFixer  ← `MalwareRisk`

Moves suspicious files to bin.

### Prep
- [ ] **K.1** Fixtures from 01-O. Approve.

### Real
- [ ] **K.2** Fix. `hello.exe`, `install.bat` in bin. `readme.txt` untouched.

### Permanent-only override
- [ ] **K.3** Setting `SuspiciousDisposal = Permanent` → files deleted
      permanently, not binned (defensible security default).

### Cleanup
- [ ] **K.4** Empty bin, restore setting, `Reset`.

---

## 02-L · TrackFixer  ← `AudioLanguage` / `SubtitleLanguage`

Strips unwanted audio/subtitle tracks by remux (no re-encode).

### Prep
- [ ] **L.1** Fixtures from 01-B / 01-N. Approve.

### Real
- [ ] **L.2** Fix. File re-muxed. ffprobe reports only wanted-language
      tracks.
- [ ] **L.3** Duration unchanged (±2 s). Video stream count = 1.

### Last-track guard (safety invariant #2)
- [ ] **L.4** Attempt to strip the last audio track (file has only one
      audio track, in unwanted lang):
      fixer refuses, history logs `LastTrackGuard: refused`. File
      untouched.
- [ ] **L.5** Same guard for video: file with only one video stream never
      loses it.

### Sub sidecar sync (relies on `TrackFixerSubtitleGuardTests`)
- [ ] **L.6** Fixer does not delete external `.srt` even if it kills a
      redundant embedded sub.

### Cleanup
- [ ] **L.7** Empty bin, `Reset`.

---

## 02-M · TranscodeFixer  ← `HeavyTranscode` / `FailedTranscode`

Full re-encode to a compatible codec.

### Prep
- [ ] **M.1** Set encode preset to `HevcMedium`.
- [ ] **M.2** Fixtures from 01-P. Approve.

### Real
- [ ] **M.3** Fix. Output file has expected codec (verify with ffprobe).
- [ ] **M.4** Output duration within ±2 s of source (invariant #3).
- [ ] **M.5** Original in bin.

### Disk space guard (invariant #5)
- [ ] **M.6** Simulate < 2× free space (fill drive with dummy file). Fix
      run refuses to start transcode; history row logs
      `InsufficientSpace`.

### Failure recovery (relies on `TranscodeFixerSidecarTests`)
- [ ] **M.7** Kill ffmpeg mid-run. On next run, sidecar `.mediadash-tmp`
      file removed; source not damaged.

### Encoder options
- [ ] **M.8** Iterate presets: `H264Fast`, `HevcMedium`, `AV1Slow`. Each
      produces a playable output.

### Cleanup
- [ ] **M.9** Empty bin, restore preset, `Reset`.

---

## 02-N · TrickplayOptimizeFixer  ← `LargeTrickplay`

Re-encodes trickplay JPGs to WebP (kept with .jpg extension so client
still fetches them).

### Prep
- [ ] **N.1** Fixtures from 01-Q. Approve.

### Real
- [ ] **N.2** Fix. Trickplay folder shrinks (compare `Get-ChildItem
      -Recurse | Measure -Sum Length` before/after).
- [ ] **N.3** File extensions still `.jpg`. First 4 bytes are WebP
      signature (`RIFF....WEBP`).
- [ ] **N.4** Jellyfin Web Player scrubber still displays trickplay
      thumbs.

### Cleanup
- [ ] **N.5** `Reset` (leave trickplay in place).

---

## 02-O · PostUpgradeCleanup

One-shot cleanup task, offered as a banner in the config UI.

### Trigger via API
- [ ] **O.1** Status endpoint:
      ```
      curl.exe -s -H "X-Emby-Token: $env:TOKEN" http://localhost:8099/MediaDash/PostUpgradeCleanup/Status
      ```
      Returns `{ offered, dismissed, alreadyRun, ... }`.
- [ ] **O.2** Run:
      ```
      curl.exe -X POST -H "X-Emby-Token: $env:TOKEN" http://localhost:8099/MediaDash/PostUpgradeCleanup/Run
      ```
      Returns byte count freed.
- [ ] **O.3** Once run, `alreadyRun=true`, further Run calls no-op.
- [ ] **O.4** `dismissOnly=true` marks dismissed without running:
      ```
      curl.exe -X POST -H "X-Emby-Token: $env:TOKEN" "http://localhost:8099/MediaDash/PostUpgradeCleanup/Run?dismissOnly=true"
      ```

### Safety
- [ ] **O.5** Only sweeps Jellyfin's trickplay dir; no user files touched.
      Verify by seeding a stray `.jpg` in `$LIB` and confirming it
      survives the run.

---

## 02-P · Fixer support classes

### RecycleBin  (relies on `RecycleBin*Tests`)
- [ ] **P.1** Move to bin: any fixer run produces bin item with sidecar
      manifest (`.mediadash-manifest.json`) alongside file.
- [ ] **P.2** Restore endpoint:
      ```
      curl.exe -X POST -H "X-Emby-Token: $env:TOKEN" -d '{"itemIds":["<id>"]}' `
        -H "Content-Type: application/json" `
        http://localhost:8099/MediaDash/RecycleBin/Items/Restore
      ```
      File returns to original location. Sidecar removed.
- [ ] **P.3** Restore into a colliding path — server picks
      `-restored-<n>` suffix (relies on `RestoreResolveNonCollidingPathTests`).
- [ ] **P.4** Consolidate: put bins in 2 libraries, then
      `POST /MediaDash/RecycleBin/Consolidate` merges them (relies on
      `RecycleBinConsolidateBetweenTests`).
- [ ] **P.5** Adopt an "orphaned" bin folder (created by hand, no plugin
      state):
      ```
      curl.exe -X POST -H "X-Emby-Token: $env:TOKEN" -d '{"paths":["<path>"]}' `
        -H "Content-Type: application/json" `
        http://localhost:8099/MediaDash/RecycleBin/AdoptBatch
      ```
      (relies on `RecycleBinAutoAdoptBehaviorTests`).
- [ ] **P.6** Empty bin honours "empty older than N days" setting.
- [ ] **P.7** `Get-Bin | Where-Object { $_.originalPath -notlike "$LIB*" }`
      → empty. (Invariant #1.)

### LibraryGuard
- [ ] **P.8** Every fixer's operations pass through `LibraryGuard`. Try to
      call a fixer against a fake path outside library; observe refusal in
      log and no filesystem change (test via a debug script that calls
      internal APIs).

### FfmpegExecutor
- [ ] **P.9** Command line uses Jellyfin's bundled ffmpeg path (log line
      "ffmpeg ..." → path is inside Jellyfin install dir).
- [ ] **P.10** Cancel a running fix → ffmpeg process killed within 5 s.
- [ ] **P.11** ffmpeg failure surfaced with exit code + last N stderr
      lines in the History entry.

### OutputVerifier
- [ ] **P.12** Verifier rejects an output with duration off by > 2 s;
      original preserved (invariant #3, relies on `OutputVerifierDurationTests`).
- [ ] **P.13** Verifier rejects an output missing an expected stream (e.g.
      transcode dropped audio).

### RenameTemplate
- [ ] **P.14** Each template variable tested: `{title}`, `{year}`,
      `{sxxeyy}`, `{quality}`. Bad template shows error at settings save.

### PostUpgradeCleanupResult
- [ ] **P.15** Result JSON round-trips through the API without missing
      fields (compare returned shape against `PostUpgradeCleanupResult.cs`).

---

## End-of-chapter cleanup

- [ ] **Z.1** Empty bin, `Reset`, wipe `$LIB`.
- [ ] **Z.2** Re-enable global dry-run.
- [ ] **Z.3** Reset every fixer disposal method to its default.
- [ ] **Z.4** Update INDEX progress.
