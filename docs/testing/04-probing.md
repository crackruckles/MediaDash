# 04 · Probing

Everything in `Jellyfin.Plugin.MediaDash/Probing/`: ffprobe wrapper, book
and comic probes, SMART health, file hasher.

Return to [INDEX](INDEX.md).

---

## Session prep

- [x] **P.1** `$env:TOKEN` set.
- [x] **P.2** ffmpeg/ffprobe available (from Jellyfin). Path visible via:
      ```powershell
      $env = curl.exe -s -H "X-Emby-Token: $env:TOKEN" http://localhost:8099/MediaDash/Environment | ConvertFrom-Json
      $env.ffprobePath
      ```
      Confirm inside Jellyfin install dir. (F-012: /Environment omits
      ffprobePath; hardcoded to Jellyfin install dir.)
- [x] **P.3** Fixtures folder ready:
      `C:\dev\mediadash\artifacts\fixtures\probing\`.

---

## 04-A · FfprobeService

Runs `ffprobe -show_streams -show_format` and parses JSON to
`FfprobeData`.

### Positive
- [x] **A.1** Drop `sample-h264-aac.mkv` in library; run a scan; open the
      Jellyfin log and find the ffprobe stdout for that file — pretty JSON
      with `streams[]`, `format`. No stderr errors. (Used
      `Clean Movie (2024).mkv`. Raw ffprobe JSON stored in
      `probe_cache.json` — 4120 bytes, complete streams+format. No log
      lines emitted by MediaDash for the probe itself.)
- [~] **A.2** DB row for that file in `FormatProbeResult` has parsed
      duration, video codec, audio codec, all streams. (`format_probe_cache`
      only has `path,size,mtime_utc,probed_at_utc,ok,reason` columns. The
      raw ffprobe JSON lives in a separate `probe_cache` table.
      See F-075.)
- [~] **A.3** `FfprobeStreamInfo` populated: `codecName`, `codecType`,
      `channels`, `sampleRate`, `bitRate`, `language`, `title`.
      (Present only inside `probe_cache.json` raw blob, not as parsed DTO
      columns. See F-075.)

### Hearing-impaired flag (relies on `FfprobeStreamHearingImpairedTests`)
- [-] **A.4** File with subtitle track `disposition.hearing_impaired=1`
      → `FfprobeStreamInfo.hearingImpaired = true`.
      (HI Test (2024).mkv has correct embedded SDH per direct ffprobe
      but is not indexed as a Jellyfin item (F-019) and never enters
      probe_cache. See F-076.)
- [-] **A.5** Track with title containing `[SDH]` also flagged. (Same
      blocker as A.4. Direct ffprobe confirms `title="English SDH"`.)

### Truncation marker (relies on `FfprobeTruncationMarkerTests`)
- [~] **A.6** Truncated file → ffprobe output has "Invalid data" line;
      `FfprobeError` populated on result; duration not persisted.
      (`Truncated Movie (2021).mkv` is a Jellyfin item but file is missing
      on disk — surfaces as Playability issue "The library entry points
      to a file that no longer exists." rather than an ffprobe error
      row. good-book.epub gives us the ffprobe "Invalid data" message
      instead: stored in `probe_cache.json` as
      `{"error":{"code":-1094995529,"string":"Invalid data found when processing input"}}`.)
- [x] **A.7** No throw at higher layer — scanner receives `null`
      `FfprobeData` for that file. (Scan completed without exceptions
      despite Truncated Movie's missing file and books/comics ffprobe
      errors.)

### Edge cases
- [-] **A.8** File with 0 audio streams parsed; scanner logic handles
      empty audio (see 01-B.4). (No fixture with 0 audio streams
      available in the current library. HI Test has 1 audio stream.)
- [x] **A.9** ffprobe exit code non-zero surfaces in `FfprobeError`.
      (Verified via good-book.epub `probe_cache.json` error blob.)
- [-] **A.10** Timeout: rename a real ffprobe with a script that sleeps
      60 s → probe cancelled after configured timeout, error logged.
      (Skipped per session plan — requires renaming Jellyfin's ffprobe.)
- [-] **A.11** Cancellation propagates: cancel a scan mid-probe; ffprobe
      process killed. (Skipped — scan on this fixture set completes in
      well under a second so mid-probe cancel is not observable.)

### FfprobeFormat / FfprobeData round-trip
- [x] **A.12** `format.bitRate` matches computed rate for a controlled
      file (`filesize * 8 / duration ± 1%`). (Clean Movie: size=2372373,
      duration=20.023 → computed=947832 bps; probe_cache reports
      bit_rate=947859 bps. Delta 27 bps, <0.01%.)

---

## 04-B · BookProbeService  (relies on `BookProbeServiceTests`)

Reads `.epub` / `.pdf` / `.mobi` metadata for the Books library.

### Fixtures
- [x] **B.1** `sample.epub` with valid `content.opf`. (Used existing
      `books\good-book.epub`; ffprobe returns "Invalid data" though —
      not a real epub, just a placeholder. Result is nonetheless in
      `format_probe_cache` with ok=1, reason=null.)
- [-] **B.2** `sample-drm.epub` (encrypted). (No fixture available.)
- [-] **B.3** `sample.pdf` with metadata. (No fixture available. ffmpeg
      cannot generate a PDF; skipped.)
- [x] **B.4** Zero-byte / corrupted epub. (Seeded `books\zero-fixture.epub`
      = 0 bytes. Not indexed as a Jellyfin item — F-019 blocker — so it
      never enters probe_cache. No crash.)

### Positive
- [~] **B.5** Drop fixtures in `$LIB\books\` and run scan. Each yields a
      `BookProbeResult` with title, author(s), pageCount (if available),
      language. (`format_probe_cache` row exists for good-book.epub
      (ok=1, reason=null) but has no title/author/pageCount columns.
      See F-075.)
- [~] **B.6** LibraryStats book counts include these. (LibraryStats
      returns `ItemCount: 0` for the Books library despite probe_cache
      rows. Downstream of F-019. See F-074.)

### DRM
- [-] **B.7** DRM-protected epub returns a result with `drm=true`, no
      throw. (No DRM fixture available.)

### Failure
- [-] **B.8** Zero-byte epub → `BookProbeResult` has `error` populated.
      No scanner crash. (Zero-byte file never entered the pipeline
      because it wasn't indexed as an item — cannot observe the
      BookProbeResult error path from this box. No scanner crash
      observed.)

### Cleanup
- [x] **B.9** Delete fixtures, `Reset`.

---

## 04-C · ComicProbeService  (relies on `ComicProbeServiceTests`)

Reads `.cbz` / `.cbr` archives.

### Fixtures
- [x] **C.1** `sample.cbz` (zip of 10 numbered JPGs). (Used existing
      `comics\good-comic.cbz` — placeholder, not a real zip. ffprobe
      reports "Invalid data".)
- [-] **C.2** `sample.cbr` (rar of same). (rar.exe not available on
      this box.)
- [x] **C.3** Corrupt archive. (Seeded `comics\corrupt.cbz` by
      truncating good-comic.cbz to 50 bytes. Not indexed as item
      (F-019); no scanner crash.)

### Positive
- [~] **C.4** `ComicProbeResult` populated: `pageCount = 10`, first image
      dimensions, byte size, `hasMetadataXml`. (No columns for these
      fields exist anywhere in DB — see F-075. good-comic.cbz has
      `format_probe_cache.ok=1` only.)
- [-] **C.5** Archive with `ComicInfo.xml` extracts title, series, issue.
      (No ComicInfo.xml fixture available.)

### Failure
- [x] **C.6** Corrupt archive → result with `error`, no crash. (No crash
      during scan with corrupt.cbz present. Result unobservable from
      DB — see F-075/F-076.)

### Cleanup
- [x] **C.7** Delete fixtures, `Reset`.

---

## 04-D · SmartHealthProbe / SmartHealthProbeWmi

Reads drive S.M.A.R.T. status. WMI variant is Windows-only.

### Availability
- [~] **D.1** `GET /MediaDash/Environment` includes a `smart` array (one
      per drive containing a library) OR notes admin required.
      (/Environment has NO smart array — SMART data is on
      `/Status.Drives[]` with 5 fields per drive: SmartHealth,
      SmartMessage, SmartModel, SmartTemperatureCelsius,
      SmartWearPercent. See F-073.)
- [~] **D.2** On Windows, `SmartHealthProbeWmi` used automatically. Log
      shows `SmartHealthProbeWmi: ok`. (Log tail has zero MediaDash lines
      matching `SmartHealth|Probe`. `/Status.Drives[0].SmartMessage`
      confirms WMI use: "Windows reports Lexar SSD NM790 2TB (hosting
      C:) is Healthy (WMI/MSFT_PhysicalDisk).")
- [-] **D.3** Non-admin user (drop elevation) → probe returns
      `SmartHealthResult { available: false, reason: "elevation required" }`,
      no crash. (Skipped — requires non-admin session setup outside
      chapter 04 scope.)

### Positive
- [~] **D.4** For a healthy drive: `SmartHealth.status = "OK"`,
      `temperatureC` populated. (Actual: `SmartHealth="healthy"` (lower
      case string, not "OK"), `SmartTemperatureCelsius=51`,
      `SmartWearPercent=0`, `SmartTemperatureMaxCelsius=90`. Doc string
      is wrong — see F-073.)

### Degraded / warning
- [-] **D.5** Set (or simulate) drive with pending sectors. Result shows
      `status = "Warning"` with reason. (Skipped per session plan —
      unreproducible without a bad drive.)
- [-] **D.6** Warning surfaces in Environment tab of config UI (see
      07-config-ui). (Belongs in chapter 07.)

### Cross-platform
- [-] **D.7** On non-Windows dev machine, `SmartHealthProbe` (non-WMI)
      path used — log confirms. (Skipped — Windows box only.)

---

## 04-E · FileHasher  (relies on `FileHasherTests`)

Computes quick fingerprints (head + tail sample) for duplicate detection.

### Positive
- [-] **E.1** Two identical files produce identical hashes. (`file_hashes`
      table is empty after full scan — see F-077. Cannot observe.)
- [-] **E.2** File differing only in the middle produces different hash
      (head+tail sample). (Same blocker — no rows to compare.)
- [-] **E.3** Large file (>10 GB) hashes in constant time (not full
      read). Watch disk I/O — bytes read ≪ file size. (Skipped per
      session plan — safety on 26.9 GB file. And no rows land anyway.)

### Failure
- [-] **E.4** File locked exclusively by another process — hasher retries
      once, then logs warn and returns `null`. (No hasher activity
      observable in log or DB — see F-077.)

### Determinism
- [-] **E.5** Same file hashed 5 times → same result. (Same blocker as
      E.1.)

---

## 04-F · Result types round-trip

- [-] **F.1** `FormatProbeResult` persisted in DB (see 06-A) survives
      restart with all fields intact. (Skipped — do not restart Jellyfin
      mid-session. `format_probe_cache` rows are on disk (WAL flushed);
      round-trip likely OK but not observed.)
- [~] **F.2** `FfprobeError` type serializes into diagnostics. (Fails —
      the ffprobe error is stored in `probe_cache.json` blob only, not
      in the `diagnostics` table. See F-078.)
- [~] **F.3** `BookProbeResult`, `ComicProbeResult` — verify all fields
      present in library stats. (Fails — LibraryStats reports
      ItemCount=0 for Books/Comics libraries; no BookProbeResult /
      ComicProbeResult fields surface via LibraryStats. See F-074 /
      F-075.)

---

## End-of-chapter cleanup

- [x] **Z.1** Delete probing fixtures. (zero-fixture.epub, corrupt.cbz
      removed; `$env:TEMP\04-fixtures\` removed; library snapshot
      before/after both 16 files, no diff.)
- [x] **Z.2** `Reset`. (POST /MediaDash/Reset → 204.)
- [ ] **Z.3** Update INDEX progress.
