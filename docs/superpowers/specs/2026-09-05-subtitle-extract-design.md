# Subtitle Extract — Design

**Status:** approved, ready for implementation plan
**Target release:** 1.0.8.0 (feature bundle)
**Related:** `SubtitleLanguageScanner`/`Fixer`, `TrackFixer`, `TranscodeFixer`, `FixTask.BuildTranscodeCompanions`, `OutputVerifier`, `RecycleBin`

---

## 1. Problem

Every embedded text subtitle in the user's library is trapped inside its container. That has three costs:

- Non-Jellyfin players (or players with poor MKV support) can't reach subs that are technically already there.
- A re-encode that changes container or drops sub streams (an existing risk with `TranscodeFixer` on unusual codecs) loses them irretrievably.
- Users who want to edit subs (Aegisub, Subtitle Edit, hand-fixing timing) have to extract them manually, one file at a time.

Today the plugin has no way to extract them. `SubtitleLanguageFixer` only *removes* unwanted-language subs; it never saves the removed content.

## 2. Goal

Add a scanner + fixer pair that extracts embedded text subtitles to Jellyfin-conventional sidecar files (`<basename>.<lang>.[flags].<ext>`) and removes the extracted streams from the container. Composes cleanly with existing fixers — when Extract queues alongside AudioLanguage, SubtitleLanguage, or Transcode on the same file, one ffmpeg pass handles everything (combined-pass integration).

## 3. Non-goals (v1)

- Extracting **image-based** subs (PGS on Blu-ray, VobSub on DVD, HDMV_PGS). External image-sub sidecars are poorly supported across players; leaving them in the container is safer than saving something users can't play. `ponytail: revisit if OCR-to-SRT lands as a separate feature.`
- Extracting **closed captions** (EIA-608/EIA-708 embedded in the video stream). Extraction is fiddly and rarely user-requested.
- **Format conversion** (e.g. ASS → SRT). Sidecars keep the source format.
- **Overwriting** existing sidecars — the fixer refuses to touch a stream whose target sidecar already exists.
- A separate `ExtractSubtitleLanguages` whitelist — reuses `AllowedSubtitleLanguages` (with fallback: empty allow-list means extract every text sub).

## 4. Design

### 4.1 New components

- **`Scanners/SubtitleExtractScanner.cs`** (~80 lines, extends `ProbingScannerBase`) — flags any video with at least one qualifying text-subtitle stream (see §4.2). Emits `IssueType.SubtitleExtract`.
- **`Fixers/SubtitleExtractFixer.cs`** (~200 lines, implements `IFixer`) — standalone dispatch path: re-probe, extract qualifying subs to sidecars, remux the container without those streams, verify sidecars + container, swap.
- **`Fixers/SubtitleExtractArgs.cs`** (new, ~50 lines) — shared helper that returns ffmpeg arg fragments (`-map 0:<idx> -c:s copy <sidecar-path>`) for a set of streams. Callable from three sites: the standalone fixer, `TrackFixer.FixCombinedAsync`, and `TranscodeFixer.BuildArgs`.
- **`Data/IssueType.cs`** — add `SubtitleExtract` enum value.
- **`Configuration/PluginConfiguration.cs`** — two new properties: `SubtitleExtractFixMode` (default `Off`), `SubtitleExtractDisposal` (default `RecycleBin`). Both wire into `GetFixMode` / `GetDisposal` switches.
- **`Configuration/configPage.html`** — new "Extract embedded subtitles" card under the existing Subtitles section, with the load/save wiring mirroring adjacent controls.
- **`PluginServiceRegistrator.cs`** — register the new scanner + fixer as singletons.

### 4.2 Qualifying streams

A stream is a candidate for extraction iff **all** of:

- `codec_type == "subtitle"`
- `codec_name ∈ {subrip, srt, ass, ssa, webvtt, mov_text}` (text-based only)
- Language matches `AllowedSubtitleLanguages` (or the allow-list is empty)
- If `SubtitleHearingImpairedMode == true`, hearing-impaired subs are protected — always eligible for extraction regardless of language filter, mirroring `SubtitleLanguageScanner`

Image-based subs (`hdmv_pgs_subtitle`, `dvd_subtitle`, `dvb_subtitle`, `dvb_teletext`, etc.) are skipped at scanner time — they don't cause an issue to be raised on their own.

### 4.3 Sidecar naming (Jellyfin convention)

`<video-basename>.<lang>[.forced][.sdh][.<title>].<ext>` where:

- `<lang>` — ISO 639-2 from stream metadata; fallback `und`.
- `.forced` — present if stream disposition `forced == 1`.
- `.sdh` — present if `hearing_impaired == 1`.
- `.<title>` — present only if the stream has a `title` tag AND another stream shares the same `<lang>+flags` combination (i.e. needed to disambiguate).
- `<ext>` — `srt` for `subrip`/`srt`/`mov_text`; `ass` for `ass`/`ssa`; `vtt` for `webvtt`.

Example: for `Movie (2024).mkv` with two English subs (one forced, one SDH) and one Spanish:
- `Movie (2024).eng.forced.srt`
- `Movie (2024).eng.sdh.srt`
- `Movie (2024).spa.srt`

### 4.4 Duplicate handling

Sidecar-exists check runs at fix time, per-stream. If the computed target path already exists, the stream is **skipped entirely** — not extracted, not removed from the container. Reported in the fix result message. `ponytail: don't clobber user-authored sidecars; they may contain hand-edited timing.`

### 4.5 Dispatch matrix

| Queued on same file | Handler | ffmpeg passes |
|---|---|---|
| Extract alone | `SubtitleExtractFixer.FixAsync` (standalone) | 1 |
| Extract + AudioLanguage | `TrackFixer.FixCombinedAsync` (2-companion) | 1 |
| Extract + SubtitleLanguage | `TrackFixer.FixCombinedAsync` (2-companion) | 1 |
| Extract + AudioLanguage + SubtitleLanguage | `TrackFixer.FixCombinedAsync` (3-companion) | 1 |
| Extract + Transcode (± any of the above) | `TranscodeFixer.FixAsync` (companion-aware) | 1 |

Zero double-remuxes. `FixTask.BuildTranscodeCompanions` is extended so `SubtitleExtract` is claimable by a Transcode issue on the same path (Transcode wins the tie — its re-encode is authoritative). `FixTask`'s combined-pair block widens to a set: any subset of `{AudioLanguage, SubtitleLanguage, SubtitleExtract}` on the same non-transcode path bundles into one `FixCombinedAsync` call.

### 4.6 Interaction rules (locked)

1. **Language filter before extract, within a single remux.** When SubtitleLanguage and SubtitleExtract both apply to the same file, disallowed-language subs are dropped from the container via `-map -0:s:<disallowed-idx>` first; only the allowed-language survivors become sidecars. So a disallowed sub never becomes a sidecar it wasn't going to be kept as anyway.
2. **Transcode-companion path honors AllowedSubtitleLanguages.** Transcode's existing filter still applies; extraction fires on the subs that would have made it into the transcoded output. Same semantics as rule 1, different code path.
3. **Sidecar-exists check is dispatch-agnostic.** All three code paths (standalone, TrackFixer combined, TranscodeFixer companion) call the same `SubtitleExtractArgs.BuildForCombinedPass` helper, which applies the sidecar-exists check. If a stream is skipped for a pre-existing sidecar, the container-side drop for THAT stream is also skipped — we never orphan an embedded sub whose sidecar we refused to write.

### 4.7 Standalone flow

1. Re-probe at fix time (source of truth; between scan and fix the file may have changed).
2. Compute per-stream sidecar paths via helper. Filter out streams whose sidecar already exists.
3. If no streams remain to extract → `FixResult.Success("nothing to extract; all sidecars already present")`. No remux, original untouched.
4. Compose one ffmpeg command:
   - `-i <src>` `-map 0:v` `-map 0:a` `-c copy`
   - For each kept sub NOT being extracted: `-map 0:<idx>` `-c:s copy` (preserve as-is inside output container)
   - For each stream to extract: `-map 0:<idx>` `-c:s <codec>` `<sidecar-path>` (writes sidecar directly)
   - Output: `SidecarPath(issue.Path, "subxtract.tmp", <ext>)` (reuse `TranscodeFixer.SidecarPath`)
5. Verify every written sidecar exists, is > 0 bytes, and probes successfully (`ffprobe -show_streams` returns ≥ 1 stream).
6. Verify the temp container with `OutputVerifier.VerifyAsync`.
7. Any verify failure → delete the temp container + every sidecar written this pass, return `FixResult.Fail(reason)`. Original untouched, no partial state.
8. Swap: original container → `RecycleBin` (per `SubtitleExtractDisposal`), temp container → original path.
9. `ILibraryMonitor.ReportFileSystemChanged` on the video path AND each written sidecar (Jellyfin picks up new external subs via a metadata refresh).

### 4.8 Combined-pass flow (TrackFixer companion case)

`TrackFixer.FixCombinedAsync` signature widens to accept an optional third companion `Issue? extractCompanion = null`.

- Compose one ffmpeg command that:
  - Applies AudioLanguage filter (`-map -0:a:<disallowed-idx>`) if that companion is present.
  - Applies SubtitleLanguage filter if present.
  - Emits sidecar extraction args (via helper) if `extractCompanion` is present.
- Verify container + all sidecars.
- On success, mark all companion issues as Fixed; write history rows for each.
- On failure, revert all changes (delete temp container, delete sidecars); mark all companions Failed.

### 4.9 Transcode-companion flow

`TranscodeFixer.FixAsync` extended:

- Accepts a nullable `Issue? extractCompanion` alongside the existing transcode-companion set.
- After deciding which subs survive `AllowedSubtitleLanguages`, if `extractCompanion` is present, computes the sidecar targets for those survivors and passes them to `BuildArgs` as `subsToExtract`.
- `BuildArgs` emits `-map 0:<idx>` `-c:s copy <sidecar-path>` for each stream in `subsToExtract`, and OMITS mapping those streams into the output container.
- Verification: container per existing flow, plus each written sidecar per §4.7 step 5.

### 4.10 Settings card

Under the existing Subtitles section:

**Title:** Extract embedded subtitles
**Blurb:** "Save embedded text subtitles as separate `.srt` / `.ass` files next to the video, then remove them from the container. Only text subtitles in your allowed languages are extracted; picture-based subtitles (Blu-ray PGS, DVD VobSub) stay in the container."

- **Fix mode** dropdown: Off / Detect only / Auto-fix (default: **Off**)
- **Disposal** dropdown: Recycle bin / Permanent delete (default: **Recycle bin**)

## 5. Files touched

New (3):
1. `Jellyfin.Plugin.MediaDash/Scanners/SubtitleExtractScanner.cs`
2. `Jellyfin.Plugin.MediaDash/Fixers/SubtitleExtractFixer.cs`
3. `Jellyfin.Plugin.MediaDash/Fixers/SubtitleExtractArgs.cs`

Modified (5):
4. `Jellyfin.Plugin.MediaDash/Data/IssueType.cs` — add enum value.
5. `Jellyfin.Plugin.MediaDash/Configuration/PluginConfiguration.cs` — two new properties + `GetFixMode`/`GetDisposal` cases.
6. `Jellyfin.Plugin.MediaDash/Configuration/configPage.html` — settings card + wiring.
7. `Jellyfin.Plugin.MediaDash/PluginServiceRegistrator.cs` — register new scanner + fixer.
8. `Jellyfin.Plugin.MediaDash/ScheduledTasks/FixTask.cs` — extend `BuildTranscodeCompanions` eligibility set, widen combined-pair detection block, update the dispatch switch to route through the widened `FixCombinedAsync` and companion-aware `TranscodeFixer.FixAsync`.
9. `Jellyfin.Plugin.MediaDash/Fixers/TrackFixer.cs` — widen `FixCombinedAsync` to accept an optional Extract companion; call helper to append extraction args.
10. `Jellyfin.Plugin.MediaDash/Fixers/TranscodeFixer.cs` — thread `subsToExtract` param through `FixAsync` → `BuildArgs`.

New tests:
11. `Jellyfin.Plugin.MediaDash.Tests/SubtitleExtractScannerTests.cs` — scanner unit tests.
12. `Jellyfin.Plugin.MediaDash.Tests/SubtitleExtractFixerTests.cs` — standalone-fixer unit tests.
13. `Jellyfin.Plugin.MediaDash.Tests/SubtitleExtractCombinedPassTests.cs` — dispatch matrix tests (2-companion, 3-companion, transcode-companion, sidecar-exists skip).
14. `Jellyfin.Plugin.MediaDash.Tests/FixTaskSubtitleExtractCompanionTests.cs` — `BuildTranscodeCompanions` + combined-pair detection with Extract in the mix.

## 6. Tests

### 6.1 Unit — scanner

- Videos with allowed-language text subs → flagged.
- Videos with disallowed-language text subs only → not flagged (SubtitleLanguageFixer's job).
- Videos with only image-based subs → not flagged.
- Videos with no subs → not flagged.
- Empty `AllowedSubtitleLanguages` → any text sub triggers.
- SDH sub with `SubtitleHearingImpairedMode=true` and non-allowed language → flagged (protection carries over).

### 6.2 Unit — standalone fixer

- All qualifying streams extracted → sidecars created with correct Jellyfin naming; original recycled; container has only non-extracted streams.
- One qualifying stream, sidecar already exists → skipped; original untouched; result message notes the skip.
- No qualifying streams at fix time (re-probe shows subs already gone) → `Success("nothing to extract")`; no side effects.
- ffmpeg fails during extract → temp container deleted, any partial sidecars deleted, original untouched.
- Sidecar verify fails (0 bytes, unparseable) → same cleanup as ffmpeg failure.
- Container verify fails after successful sidecars → sidecars deleted too, all-or-nothing.

### 6.3 Unit — combined-pass dispatch

- `BuildTranscodeCompanions` claims SubtitleExtract when a Transcode issue is on the same path.
- Combined-pair detection includes Extract in the set-of-track-companions when no Transcode is queued for the path.
- Extract + AudioLanguage + SubtitleLanguage on one file → all three dispatched to `FixCombinedAsync` in a single call; all three marked Fixed on success.
- Extract-only on a file (no other issues) → standalone `SubtitleExtractFixer.FixAsync` fires.

### 6.4 Unit — args helper

- `SubtitleExtractArgs.BuildForCombinedPass` emits correct `-map` + `-c:s copy` fragments for a stream set.
- Sidecar-exists filter drops the expected streams from the returned set.

### 6.5 Real-world (per `feedback_mediadash_real_world_tests`)

`tools/subtitle-extract-test/` with a script generating four fixture MKVs:

- `fixture-single-text-sub.mkv` — one English subrip stream.
- `fixture-multi-lang.mkv` — English + Spanish + French subrip streams; user allow-list configured for English + Spanish only.
- `fixture-mixed-text-and-pgs.mkv` — English subrip + English PGS. Only subrip extracts; PGS stays.
- `fixture-preexisting-sidecar.mkv` — same as single, but a sidecar with the target name is already on disk.

Deploy the plugin to localhost:8099, run scan + fix through the plugin path for each fixture, confirm:
- Correct sidecar files exist with correct names.
- Container has only expected residual streams.
- Jellyfin picks up the new external subs (visible in the item's Subtitles menu after refresh).
- Web player can select and display each extracted sub.

## 7. Version & release

1.0.8.0 bundle. Per `project_mediadash_roadmap`, this joins the media organiser, audio-conversion scanner/fixer, Library-tab redesign, and playability repair ladder in the same release.

**Bundle ordering note:** this feature establishes the multi-companion combined-pass pattern (SubtitleExtract slotting into `TrackFixer.FixCombinedAsync` alongside AudioLanguage/SubtitleLanguage). The planned audio-conversion scanner (see `project_mediadash_audio_scanner`) will use the same pattern for `AudioConversion` companions. Whichever ships first pays the pattern-establishment tax:

- If subtitle-extract lands first: `FixCombinedAsync`'s companion set widens to accept a third companion type. Audio-scanner later widens to a fourth. Small delta.
- If audio-scanner lands first: same shape. Neither ordering is materially cheaper.

Recommendation: land subtitle-extract before audio-scanner within the 1.0.8.0 window — the subtitle path is smaller and validates the pattern before audio-scanner adds encoder-config plumbing on top.

Release cut via `tools/release.ps1 -Version 1.0.8.0 -Changelog "..."` per CLAUDE.md; do not hand-edit `manifest.json` checksums.

## 8. Follow-ups (noted, out of scope)

- OCR image subs → text sidecars (separate spec; requires Tesseract or subtile-edit's VobSub OCR).
- Extract closed captions (EIA-608/708) from the video stream.
- Format conversion (ASS → SRT for players that can't handle ASS).
- Per-library extract settings (mirror any per-library pattern the audio-scanner establishes first).
