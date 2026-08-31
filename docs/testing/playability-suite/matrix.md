# Playability Suite — Test Matrix

Generated 2026-08-29 for the targeted PlayabilityScanner fuzz suite.

## Extension enumeration

Jellyfin doc-supported (from `01-scanners.md` R.4):
```
.mkv .mp4 .m4v .avi .mov .wmv .ts .webm .flac .mp3 .m4a .ogg .opus .wav .epub .cbz .cbr .pdf
```
Count = 18.

ffmpeg (10.11.11 bundled build) `-formats` reports **361** demuxer entries.
That list contains hundreds of niche/legacy raw-container formats (3dostr, 4xm,
aax, ace, ads, aix, alp, amrnb, ape, apm, ...) that are technically
"demuxable" but never appear in a Jellyfin library. Full intersection with
Jellyfin's own naming rules is out of scope for one pass; we take a
pragmatic subset: every extension in the doc list that ffmpeg can generate
end-to-end, plus a handful of mainstream extensions ffmpeg supports but
Jellyfin's own R.4 list omits (flv, mpg, 3gp, aac, ac3).

Combined test set = **22** extensions.

## Categories & baseline generation

| Category         | Ext   | Muxer used      | Codecs (v/a)         | Doc R.4? |
|------------------|-------|-----------------|----------------------|----------|
| video-container  | mkv   | matroska        | h264 / aac           | yes      |
| video-container  | mp4   | mp4             | h264 / aac           | yes      |
| video-container  | m4v   | mp4             | h264 / aac           | yes      |
| video-container  | mov   | mov             | h264 / aac           | yes      |
| video-container  | avi   | avi             | h264 / aac           | yes      |
| video-container  | wmv   | asf             | h264 / aac           | yes      |
| video-container  | ts    | mpegts          | h264 / aac           | yes      |
| video-container  | webm  | webm            | vp8 / vorbis         | yes      |
| video-container  | flv   | flv             | h264 / aac           | no       |
| video-container  | mpg   | mpeg            | mpeg2video / mp2     | no       |
| video-container  | 3gp   | 3gp             | h264 / aac           | no       |
| audio-container  | mp3   | mp3             | libmp3lame           | yes      |
| audio-container  | flac  | flac            | flac                 | yes      |
| audio-container  | m4a   | ipod            | aac                  | yes      |
| audio-container  | ogg   | ogg             | libvorbis            | yes      |
| audio-container  | opus  | opus            | libopus              | yes      |
| audio-container  | wav   | wav             | pcm_s16le            | yes      |
| audio-container  | aac   | adts            | aac                  | no       |
| audio-container  | ac3   | ac3             | ac3                  | no       |
| book             | epub  | hand-crafted    | zip w/ mimetype+opf  | yes      |
| book             | pdf   | hand-crafted    | minimal PDF 1.4      | yes      |
| comic            | cbz   | hand-crafted    | zip of 2 JPGs        | yes      |

Skipped from doc R.4:
- **cbr** — needs external `rar.exe`, not present. `cbz` covers the comic
  probe path.

## Break-mode table

Each break-mode operates on a copy of the baseline fixture.

| # | Mode              | Mutation                                                              |
|---|-------------------|-----------------------------------------------------------------------|
| 1 | baseline          | untouched                                                             |
| 2 | zero              | truncate to 0 bytes                                                   |
| 3 | header-only       | keep first 512 bytes, discard remainder                               |
| 4 | tail-truncated    | keep first 40% of bytes, discard remainder                            |
| 5 | middle-hole       | zero bytes at offset 1024..2048                                       |
| 6 | garbage-payload   | keep first 8 KB, replace rest with random bytes                       |
| 7 | magic-flipped     | XOR-flip first 4 bytes (breaks container magic)                       |
| 8 | wrong-ext         | copy mkv baseline payload, save under each category's extension       |
| 9 | oversize-header   | skipped (requires per-container metadata injection; noted per-row)    |

Mode 9 is degenerate for containers where the "reported duration" lives in
a chunk the scanner would have to seek to — implementing per-container
mutation for 22 extensions exceeds the value it produces. Recorded as `skip`
in results.csv.

## Fixture layout

Workspace: `C:\dev\mediadash-fixtures\movies\_playfuzz\`

```
_playfuzz/
  baseline/                v-<ext>.<ext>, a-<ext>.<ext>, b-<ext>.<ext>, c-<ext>.<ext>
  zero/                    same names, 0 bytes
  header-only/             same names, first 512 B
  tail-truncated/          same names, first 40 %
  middle-hole/             same names, hole at 1024..2048
  garbage-payload/         same names, 8 KB header + random tail
  magic-flipped/           same names, magic XORed
  wrong-ext/               same names, payload is v-mkv baseline
```

Seeded under `movies\_playfuzz\` rather than beside `Clean Movie (2024)`
because R.4 already showed junk files never become items on this box
(F-019). The item-scoped scanner walks past them regardless of where they
sit; results.csv captures both `jellyfin_indexed` (nearly always false) and
the ffprobe ground truth so the ffprobe comparison stands even when the
scanner is starved of items.

## Cleanup contract

`_playfuzz\` is the only writable directory. At end-of-run:
1. `Remove-Item $env:LIB\movies\_playfuzz -Recurse -Force`
2. `Compare-Object` library snapshot before vs. after — expect 0 diff.
3. Restore plugin config from `%TEMP%\cfg-orig-playfuzz.json`.
