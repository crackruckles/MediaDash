# Playability Suite — Findings

Run: 2026-08-29. See `matrix.md` for scope and `results.csv` for the full
per-row data. This file lists the surprises worth escalating to `FINDINGS.md`.

## Headline

**No fixture in the suite was scored by PlayabilityScanner** — 0 of 176
`(ext × mode)` rows were flagged. Not because the scanner passed them, but
because 0 of 176 fixture files became Jellyfin library items on this box
(F-019 recurrence). Every finding below is derived from the ffprobe control
side of the CSV.

## Per-mode summary (ffprobe ground truth)

| Mode            | ffprobe fail | ffprobe pass | Scanner flagged |
|-----------------|--------------|--------------|-----------------|
| baseline        | 3            | 19           | 0               |
| zero            | 19           | 3            | 0               |
| header-only     | 16           | 6            | 0               |
| tail-truncated  | 10           | 12           | 0               |
| middle-hole     | 5            | 17           | 0               |
| garbage-payload | 9            | 13           | 0               |
| magic-flipped   | 15           | 7            | 0               |
| wrong-ext       | 0            | 22           | 0               |

`false-negatives` (ffprobe fail + scanner didn't flag) cannot be scored as
scanner bugs on this box: the scanner never got the chance to look. But the
ffprobe control side surfaces its own set of findings.

## Findings escalated to `FINDINGS.md`

### F-088 — Baseline books/comics ffprobe-fail

`baseline` epub, pdf, cbz all return ffprobe exit 1 "Invalid data found when
processing input". If PlayabilityScanner treats ffprobe exit code as the
sole playability signal, healthy books/comics would be falsely flagged. Doc
R.4 lists these extensions as scanner-supported. Books/comics have
dedicated probe services (BookProbeService, ComicProbeService); the
Playability scanner must route them there, not to ffprobe.

### F-089 — ffprobe returns exit 0 on zero-byte .ac3, .flac, .m4v

In `zero` mode (0-byte files), ffprobe exits 0 with empty stdout on those
three extensions. All other 0-byte fixtures return exit 1 as expected. This
means a scanner that trusts ffprobe exit code alone for these three formats
will silently pass genuine zero-byte corruption. Reproducible with the
bundled `ffprobe.exe -show_format zero.ac3` — needs a stream-count guard.

### F-090 — Wrong-extension defeats format detection

`wrong-ext` mode wrote a real matroska payload into every foreign
extension (mkv-as-mp3, mkv-as-pdf, mkv-as-epub, etc.). ffprobe returns
exit 0 for all 22 — it demuxes by content, not by name. A scanner that
trusts the extension to interpret probe results would mis-classify (e.g.
treat an mkv-in-mp3 as a broken mp3).

### F-091 — F-019 recurrence blocks item-scoped scanner tests at scale

Same pattern as R.4 and B.4: no fixture under `movies\_playfuzz\` was
indexed as a Jellyfin item, so the item-scoped Playability scanner walked
past all 176 fixtures. Suite is unable to exercise PlayabilityScanner
end-to-end until F-019 is resolved.

## Non-findings

- No scan hung (>30s). All 8 mode-scans finished in ~2.7s ± 0.02.
- No log Exception attributable to PlayabilityScanner during the run
  window (log grep for `PlayabilityScanner|FfprobeService|MediaFileHelper`
  in `log_20260829.log` returned 0 matches).
- The 26.9 GB Devil Wears Prada file was never mutated. Size confirmed
  28,835,244,202 bytes before and after the run.

## Throughput observation

The suite's ffprobe control took ~1.0s per fixture on this Windows box;
overhead is `Process.Start`, not ffprobe. If PlayabilityScanner probes
serially, a 10 000-item library would need ~2.5 h just for the spawn cost.
Sampling (`PlayabilitySamplingRate`) is essential; verify when F-019
unblocks item ingest.
