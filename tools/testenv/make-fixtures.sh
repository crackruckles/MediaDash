#!/usr/bin/env bash
# MediaDash test-environment fixture generator.
# Builds ONE deterministic media tree covering every scanner-produced IssueType plus
# clean controls, and writes manifest.json describing what each fixture should produce.
# Built once on mdtest-linux, then the same tarball is copied to every test host so all
# three hosts scan byte-identical media.
#
# Usage: make-fixtures.sh <out-dir> [ffmpeg] [sample.cbr]
# Needs: ffmpeg, zip, 7z (p7zip-full), python3
#
# Some fixtures can only exist after Jellyfin has scanned the library (corrupt artwork in
# Jellyfin's metadata folder, orphaned metadata, trickplay, stale DateCreated). Those are
# created by prepare-golden.ps1 and are listed in the manifest with "stage": "post-scan".
# Transcode-log fixtures are seeded fresh on every run by testenv (they age out).
set -euo pipefail

OUT="${1:?usage: make-fixtures.sh <out-dir> [ffmpeg]}"
FF="${2:-ffmpeg}"
CBR="${3:-$(dirname "$0")/../../Jellyfin.Plugin.MediaDash.Tests/fixtures/formats/comic/sample.cbr}"
rm -rf "$OUT"; mkdir -p "$OUT"
M="$OUT/media"; mkdir -p "$M"/{movies,tv,music,books,comics,pictures}
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
MANIFEST="$TMP/manifest.lines"; : > "$MANIFEST"

# expect <relative-path> <comma-separated IssueTypes or "none"> <note> [stage]
expect() { printf '%s\t%s\t%s\t%s\n' "$1" "$2" "${4:-pre-scan}" "$3" >> "$MANIFEST"; }
# fixfails <relative-path> <IssueType> <message substring> <why this failure is correct here>
# A fix outcome that is right for this environment; testenv reports it as "expected", not a problem.
FIXEXP="$TMP/fixexp.lines"; : > "$FIXEXP"
fixfails() { printf '%s\t%s\t%s\t%s\n' "$1" "$2" "$3" "$4" >> "$FIXEXP"; }

SRT="$TMP/eng.srt"; printf '1\n00:00:01,000 --> 00:00:04,000\nTest subtitle\n' > "$SRT"

# vid <out> <w> <h> <seconds> [extra ffmpeg output args...]
# Default: H.264, one eng AAC track, one embedded eng SRT (so it is NOT "missing subtitles").
vid() {
  local out="$1" w="$2" h="$3" d="$4"; shift 4
  mkdir -p "$(dirname "$out")"
  "$FF" -y -v error -f lavfi -i "testsrc2=duration=$d:size=${w}x${h}:rate=24" \
    -f lavfi -i "sine=frequency=440:duration=$d" -i "$SRT" \
    -map 0:v -map 1:a -map 2:s -c:v libx264 -preset veryfast ${RC:--crf 30} -c:a aac -b:a 96k -c:s srt \
    -metadata:s:a:0 language=eng -metadata:s:s:0 language=eng "$@" "$out"
}

# ---------- movies ----------
m="$M/movies"

vid "$m/Clean Movie (2024)/Clean Movie (2024).mkv" 640 360 8
expect "movies/Clean Movie (2024)/Clean Movie (2024).mkv" none "control: healthy, eng audio+subs, grouped"

# Quality: 2160p at a fixed 9 Mbps (> 1080p ceiling). ~22 MB, above the 1 MB test MinScanFileSizeMb.
RC="-b:v 9M -minrate 9M -maxrate 9M -bufsize 4M -x264-params nal-hrd=cbr" vid "$m/Quality Test (2020)/Quality Test (2020).mkv" 3840 2160 20
expect "movies/Quality Test (2020)/Quality Test (2020).mkv" Quality "2160p 9 Mbps over 1080p ceiling"

# Duplicate: the same title as two separate Jellyfin items (separate folders).
vid "$m/Dupe Test (2021)/Dupe Test (2021).mkv" 1920 1080 8
# Same folder name one level down, so Jellyfin gives both items the same name + year (no provider ids).
vid "$m/Duplicates/Dupe Test (2021)/Dupe Test (2021).mkv" 1280 720 8
expect "movies/Duplicates/Dupe Test (2021)/Dupe Test (2021).mkv" Duplicate "720p copy of Dupe Test (2021); the 1080p copy is the keeper"
# Control: merged versions in ONE folder are user-declared alternates and must NOT be flagged.
vid "$m/Versions Test (2021)/Versions Test (2021) - 1080p.mkv" 1920 1080 6
vid "$m/Versions Test (2021)/Versions Test (2021) - 720p.mkv" 1280 720 6
expect "movies/Versions Test (2021)/Versions Test (2021) - 720p.mkv" none "control: Jellyfin merged versions are intentional"

# Playability: truncated to 40%, and zero-byte.
f="$m/Truncated Test (2021)/Truncated Test (2021).mkv"; vid "$f" 1280 720 12
s=$(wc -c < "$f"); head -c $((s * 2 / 5)) "$f" > "$f.tmp" && mv "$f.tmp" "$f"
expect "movies/Truncated Test (2021)/Truncated Test (2021).mkv" Playability "truncated to 40%"
mkdir -p "$m/Zero Byte (2020)"; : > "$m/Zero Byte (2020)/Zero Byte (2020).mkv"
expect "movies/Zero Byte (2020)/Zero Byte (2020).mkv" Playability "zero-byte media: invalid probe cannot establish missing subtitles (BUG-32)"
fixfails "movies/Zero Byte (2020)/Zero Byte (2020).mkv" Playability "Repair failed" "nothing to repair; MediaDash hands it back for delete approval"

# AudioLanguage: eng + fra + deu.
mkdir -p "$m/Multi Audio (2022)"
"$FF" -y -v error -f lavfi -i "testsrc2=duration=8:size=640x360:rate=24" \
  -f lavfi -i "sine=frequency=440:duration=8" -f lavfi -i "sine=frequency=550:duration=8" \
  -f lavfi -i "sine=frequency=660:duration=8" -i "$SRT" \
  -map 0:v -map 1:a -map 2:a -map 3:a -map 4:s -c:v libx264 -preset veryfast -crf 30 -c:a aac -b:a 96k -c:s srt \
  -metadata:s:a:0 language=eng -metadata:s:a:1 language=fra -metadata:s:a:2 language=deu \
  -metadata:s:s:0 language=eng "$m/Multi Audio (2022)/Multi Audio (2022).mkv"
expect "movies/Multi Audio (2022)/Multi Audio (2022).mkv" AudioLanguage "fra+deu audio outside keep-list"

# SubtitleLanguage: embedded eng+fra+deu, and an external .fra.srt sidecar.
mkdir -p "$m/Sub Heavy (2023)"
"$FF" -y -v error -f lavfi -i "testsrc2=duration=8:size=640x360:rate=24" \
  -f lavfi -i "sine=frequency=440:duration=8" -i "$SRT" -i "$SRT" -i "$SRT" \
  -map 0:v -map 1:a -map 2:s -map 3:s -map 4:s -c:v libx264 -preset veryfast -crf 30 -c:a aac -b:a 96k -c:s srt \
  -metadata:s:a:0 language=eng -metadata:s:s:0 language=eng -metadata:s:s:1 language=fra \
  -metadata:s:s:2 language=deu "$m/Sub Heavy (2023)/Sub Heavy (2023).mkv"
expect "movies/Sub Heavy (2023)/Sub Heavy (2023).mkv" SubtitleLanguage "embedded fra+deu subs"
vid "$m/Sub Sidecar (2022)/Sub Sidecar (2022).mkv" 640 360 8
cp "$SRT" "$m/Sub Sidecar (2022)/Sub Sidecar (2022).fra.srt"
expect "movies/Sub Sidecar (2022)/Sub Sidecar (2022).mkv" SubtitleLanguage "external .fra.srt sidecar (issue is raised on the video)"

# MissingSubtitles: no subtitle track or sidecar at all.
mkdir -p "$m/No Subs (2023)"
"$FF" -y -v error -f lavfi -i "testsrc2=duration=8:size=640x360:rate=24" -f lavfi -i "sine=frequency=440:duration=8" \
  -map 0:v -map 1:a -c:v libx264 -preset veryfast -crf 30 -c:a aac -b:a 96k -metadata:s:a:0 language=eng \
  "$m/No Subs (2023)/No Subs (2023).mkv"
expect "movies/No Subs (2023)/No Subs (2023).mkv" MissingSubtitles "no eng subtitle; fix needs a Jellyfin subtitle provider"
fixfails "movies/No Subs (2023)/No Subs (2023).mkv" MissingSubtitles "no matches" "test hosts have no subtitle provider configured"

# Exercise Jellyfin's external stream indexing without any embedded subtitle fallback.
for title in "English Sidecar Only" "French Sidecar Only"; do
  d="$m/$title (2023)"; stem="$d/$title (2023)"
  mkdir -p "$d"
  "$FF" -y -v error -i "$m/No Subs (2023)/No Subs (2023).mkv" -map 0:v -map 0:a \
    -c copy -movflags +faststart "$stem.mp4"
done
cp "$SRT" "$m/English Sidecar Only (2023)/English Sidecar Only (2023).en.srt"
expect "movies/English Sidecar Only (2023)/English Sidecar Only (2023).mp4" none "control: Jellyfin indexes the only English subtitle from an .en.srt sidecar"
cp "$SRT" "$m/French Sidecar Only (2023)/French Sidecar Only (2023).fra.srt"
expect "movies/French Sidecar Only (2023)/French Sidecar Only (2023).mp4" MissingSubtitles,SubtitleLanguage "French sidecar is removable and does not satisfy the English subtitle requirement"
fixfails "movies/French Sidecar Only (2023)/French Sidecar Only (2023).mp4" MissingSubtitles "no matches" "test hosts have no subtitle provider configured"

vid "$m/Only French Audio (2023)/Only French Audio (2023).mkv" 640 360 8 -metadata:s:a:0 language=fra
expect "movies/Only French Audio (2023)/Only French Audio (2023).mkv" none "control: preserve the only audio track even though it is outside the keep list"

# Healthy modern and legacy containers, with sidecars to isolate playback from subtitle detection.
for ext in webm mkv avi ts; do
  case "$ext" in
    webm) title="VP9 Opus"; codecs=(-c:v libvpx-vp9 -deadline realtime -c:a libopus) ;;
    mkv) title="HEVC Main 10"; codecs=(-c:v libx265 -preset ultrafast -x265-params pools=1:log-level=error -pix_fmt yuv420p10le -c:a aac) ;;
    avi) title="Legacy AVI"; codecs=(-c:v mpeg4 -c:a libmp3lame) ;;
    ts) title="MPEG2 Transport"; codecs=(-c:v mpeg2video -c:a mp2) ;;
  esac
  d="$m/Format $title (2024)"; stem="$d/Format $title (2024)"
  mkdir -p "$d"
  "$FF" -y -v error -i "$m/No Subs (2023)/No Subs (2023).mkv" -map 0:v -map 0:a "${codecs[@]}" "$stem.$ext"
  cp "$SRT" "$stem.eng.srt"
  expect "movies/Format $title (2024)/Format $title (2024).$ext" none "control: decodable $title with an English sidecar"
done

d="$m/Format Truncated MP4 (2024)"; mkdir -p "$d"
f="$m/English Sidecar Only (2023)/English Sidecar Only (2023).mp4"
s=$(wc -c < "$f"); head -c $((s * 2 / 5)) "$f" > "$d/Format Truncated MP4 (2024).mp4"
cp "$SRT" "$d/Format Truncated MP4 (2024).eng.srt"
expect "movies/Format Truncated MP4 (2024)/Format Truncated MP4 (2024).mp4" Playability "fast-start header remains readable but most packets are missing"

d="$m/Format Junk MP4 (2024)"; mkdir -p "$d"
printf 'This file has no MP4 container or streams.\n' > "$d/Format Junk MP4 (2024).mp4"
expect "movies/Format Junk MP4 (2024)/Format Junk MP4 (2024).mp4" Playability "invalid container: failed probe cannot establish missing subtitles (BUG-32)"
fixfails "movies/Format Junk MP4 (2024)/Format Junk MP4 (2024).mp4" Playability "isn't a valid media container" "there are no media packets to recover"

# Valid media under an unknown extension should be ignored even in an indexed movie folder.
cp "$f" "$m/Format VP9 Opus (2024)/Format Unknown Extension.mdtestunknown"
expect "movies/Format VP9 Opus (2024)/Format Unknown Extension.mdtestunknown" none "control: unsupported extension next to indexed media"

# Misplaced: a TV episode filed under Movies.
vid "$m/Misfiled Show (2020)/Misfiled Show S01E01.mkv" 640 360 8
expect "movies/Misfiled Show (2020)/Misfiled Show S01E01.mkv" Misplaced "KNOWN GAP: default JellyfinMetadata sort source types items by library, so an episode inside Movies is a Movie"

# Ungrouped: loose movie at the library root.
vid "$m/Loose Movie (2019).mkv" 640 360 8
expect "movies/Loose Movie (2019).mkv" Ungrouped "loose file at library root"

# MalwareRisk: harmless dummies only (MZ header + padding, no code).
d="$m/Suspicious Test (2020)"; vid "$d/Suspicious Test (2020).mkv" 640 360 8
printf 'MZ\x90\x00' > "$d/setup.exe"; head -c 4096 /dev/zero >> "$d/setup.exe"
printf '@echo off\r\nrem mdtest dummy\r\n' > "$d/codec-install.bat"
printf '# mdtest dummy\n' > "$d/install.ps1"
printf '#!/bin/sh\n# mdtest dummy\n' > "$d/run.sh"
for x in setup.exe codec-install.bat install.ps1 run.sh; do
  expect "movies/Suspicious Test (2020)/$x" MalwareRisk "harmless dummy executable/script"
done

# CorruptNfo: zero-byte, malformed XML, unrecognised root.
for t in "Nfo Zero" "Nfo Malformed" "Nfo Wrong Root"; do vid "$m/$t (2020)/$t (2020).mkv" 640 360 6; done
: > "$m/Nfo Zero (2020)/Nfo Zero (2020).nfo"
printf '<?xml version="1.0"?>\n<movie><title>Broken</title>\n' > "$m/Nfo Malformed (2020)/Nfo Malformed (2020).nfo"
printf '<?xml version="1.0"?>\n<notamovie><title>x</title></notamovie>\n' > "$m/Nfo Wrong Root (2020)/Nfo Wrong Root (2020).nfo"
expect "movies/Nfo Zero (2020)/Nfo Zero (2020).nfo" CorruptNfo "zero-byte nfo"
expect "movies/Nfo Malformed (2020)/Nfo Malformed (2020).nfo" CorruptNfo "unterminated XML"
expect "movies/Nfo Wrong Root (2020)/Nfo Wrong Root (2020).nfo" CorruptNfo "unrecognised root element"

# SubtitleFonts: .ass sidecar with an embedded font no style references.
d="$m/Fonts Test (2020)"; vid "$d/Fonts Test (2020).mkv" 640 360 8
{
  printf '[Script Info]\nScriptType: v4.00+\n\n[V4+ Styles]\n'
  printf 'Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n'
  printf 'Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1\n\n'
  printf '[Fonts]\nfontname: UnusedFont_0.ttf\n'
  head -c 90000 /dev/urandom | base64 -w 80 | tr '+/' '!-'; printf '\n'   # > 50 KB reclaim floor
  printf '[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n'
  printf 'Dialogue: 0,0:00:01.00,0:00:04.00,Default,,0,0,0,,Test subtitle\n'
} > "$d/Fonts Test (2020).eng.ass"
expect "movies/Fonts Test (2020)/Fonts Test (2020).eng.ass" SubtitleFonts "embedded UnusedFont_0.ttf not referenced by any style"

# OrphanedDebris (pre-scan part): empty folder, sidecar with no video.
mkdir -p "$m/Empty Leftover Folder"
expect "movies/Empty Leftover Folder" OrphanedDebris "empty folder"
mkdir -p "$m/Orphan Sub (2020)"; cp "$SRT" "$m/Orphan Sub (2020)/Orphan Sub (2020).eng.srt"
expect "movies/Orphan Sub (2020)/Orphan Sub (2020).eng.srt" OrphanedDebris "sidecar whose video is gone"
expect "movies/Orphan Sub (2020)" OrphanedDebris "folder holding only an orphan sidecar is also empty of media"

# Post-scan targets (created by prepare-golden.ps1 after Jellyfin's first scan).
vid "$m/Artwork Target (2020)/Artwork Target (2020).mkv" 640 360 8
expect "metadata:Artwork Target (2020)" CorruptArtwork "prepare-golden zeroes this item's poster in Jellyfin's metadata folder" post-scan
expect "metadata:0dd0dd0d0dd0dd0d0dd0dd0d0dd0dd0d" OrphanedDebris "prepare-golden adds a metadata folder for an item id Jellyfin does not know" post-scan
vid "$m/Trickplay Target (2020)/Trickplay Target (2020).mkv" 640 360 30
expect "trickplay:Trickplay Target (2020)" LargeTrickplay "KNOWN GAP: Jellyfin 10.11 and 12 shard trickplay as data/trickplay/<xx>/<id>; scanner expects data/trickplay/<id>" post-scan
vid "$m/Stale Test (2015)/Stale Test (2015).mkv" 640 360 8
expect "movies/Stale Test (2015)/Stale Test (2015).mkv" Stale "prepare-golden backdates DateCreated via the API" post-scan
# Both are under the Quality ceiling. Heavy exercises a smaller replacement; Failed exercises refusal.
RC="-b:v 7M -minrate 7M -maxrate 7M -bufsize 8M -x264-params nal-hrd=cbr" vid "$m/Heavy Transcode (2021)/Heavy Transcode (2021).mkv" 1920 1080 10
expect "movies/Heavy Transcode (2021)/Heavy Transcode (2021).mkv" HeavyTranscode "testenv seeds a fresh transcode log each run" run-time
RC="-b:v 4M -minrate 4M -maxrate 4M -bufsize 8M -x264-params nal-hrd=cbr" vid "$m/Failed Transcode (2021)/Failed Transcode (2021).mkv" 1920 1080 10
expect "movies/Failed Transcode (2021)/Failed Transcode (2021).mkv" FailedTranscode "testenv seeds a failed transcode log each run" run-time
fixfails "movies/Failed Transcode (2021)/Failed Transcode (2021).mkv" FailedTranscode "larger than the original" "the smaller source must be preserved"

# Path-handling controls.
vid "$m/日本語 Test (2023)/日本語 Test (2023).mkv" 640 360 6
expect "movies/日本語 Test (2023)/日本語 Test (2023).mkv" none "control: CJK path"
vid "$m/Emoji 🎬 Test (2023)/Emoji 🎬 Test (2023).mkv" 640 360 6
expect "movies/Emoji 🎬 Test (2023)/Emoji 🎬 Test (2023).mkv" none "control: emoji path"
vid "$m/Brackets & Co [2022] \$money/Brackets & Co [2022] \$money.mkv" 640 360 6
expect "movies/Brackets & Co [2022] \$money/Brackets & Co [2022] \$money.mkv" none "control: shell-hostile characters"

# ---------- tv ----------
t="$M/tv"
for e in 01 02; do vid "$t/Test Show (2020)/Season 01/Test Show S01E$e.mkv" 640 360 6; done
expect "tv/Test Show (2020)/Season 01/Test Show S01E01.mkv" none "control: healthy episode"
vid "$t/Movie In Tv (2018)/Movie In Tv (2018).mkv" 640 360 8
expect "tv/Movie In Tv (2018)/Movie In Tv (2018).mkv" Misplaced "KNOWN GAP: default JellyfinMetadata sort source types items by library, so a movie inside TV is an Episode"

# ---------- music ----------
COVER="$TMP/cover.jpg"; "$FF" -y -v error -f lavfi -i "testsrc2=size=600x600:duration=1" -frames:v 1 "$COVER"
# track <out> <title> <n> <album> [bitrate]
track() {
  mkdir -p "$(dirname "$1")"
  "$FF" -y -v error -f lavfi -i "sine=frequency=$((300 + $3 * 50)):duration=12" -i "$COVER" \
    -map 0:a -map 1:v -c:a libmp3lame -b:a "${5:-192k}" -c:v mjpeg -disposition:v attached_pic -id3v2_version 3 \
    -metadata title="$2" -metadata artist="Test Artist" -metadata album="$4" -metadata track="$3" "$1"
}
for n in 1 2 3; do track "$M/music/Test Artist/Cover Album/0$n - Song $n.mp3" "Song $n" $n "Cover Album"; done
expect "music/Test Artist/Cover Album" EmbeddedCoverArt "every track embeds the same cover, no cover.jpg"
# Duplicate song: identical artist/album/title/duration in a second folder (has folder cover, so no cover issue).
track "$M/music/Test Artist/Dupe Album/01 - Dupe Song.mp3" "Dupe Song" 1 "Dupe Album"
track "$M/music/Test Artist/Dupe Album (Copy)/01 - Dupe Song.mp3" "Dupe Song" 1 "Dupe Album" 128k
cp "$COVER" "$M/music/Test Artist/Dupe Album/cover.jpg"; cp "$COVER" "$M/music/Test Artist/Dupe Album (Copy)/cover.jpg"
expect "music/Test Artist/Dupe Album (Copy)/01 - Dupe Song.mp3" Duplicate "same artist/album/title/duration, lower bitrate"
# Audio quality ceiling: AAC above 256 kbps (detect-only).
mkdir -p "$M/music/Test Artist/Loud Album"
"$FF" -y -v error -f lavfi -i "anoisesrc=duration=12:color=white:amplitude=0.5:sample_rate=48000" -ac 6 -c:a aac -b:a 512k \
  -metadata title="Loud" -metadata artist="Test Artist" -metadata album="Loud Album" "$M/music/Test Artist/Loud Album/01 - Loud.m4a"
cp "$COVER" "$M/music/Test Artist/Loud Album/cover.jpg"
expect "music/Test Artist/Loud Album/01 - Loud.m4a" Quality "5.1 AAC ~275 kbps over the flat 256 kbps ceiling (detect-only)"

d="$M/music/Test Artist/Format FLAC"; mkdir -p "$d"
"$FF" -y -v error -f lavfi -i "sine=frequency=580:duration=8" -i "$COVER" \
  -map 0:a -map 1:v -c:a flac -c:v copy -disposition:v attached_pic \
  -metadata title="Format FLAC" -metadata artist="Test Artist" -metadata album="Format FLAC" "$d/Format FLAC.flac"
cp "$COVER" "$d/cover.jpg"
expect "music/Test Artist/Format FLAC/Format FLAC.flac" none "control: FLAC with embedded JPEG artwork and an existing folder cover"

# ---------- books ----------
# epub <out> <title> <isbn>  -- minimal valid EPUB 2
epub() {
  local out="$1" title="$2" isbn="$3" w="$TMP/epub.$$"
  rm -rf "$w"; mkdir -p "$w/META-INF" "$w/OEBPS"
  printf 'application/epub+zip' > "$w/mimetype"
  printf '<?xml version="1.0"?>\n<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>\n' > "$w/META-INF/container.xml"
  cat > "$w/OEBPS/content.opf" <<OPF
<?xml version="1.0" encoding="UTF-8"?>
<package xmlns="http://www.idpf.org/2007/opf" unique-identifier="BookId" version="2.0">
<metadata xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:opf="http://www.idpf.org/2007/opf">
<dc:title>$title</dc:title><dc:creator>Test Author</dc:creator><dc:language>en</dc:language>
<dc:identifier id="BookId" opf:scheme="ISBN">$isbn</dc:identifier></metadata>
<manifest><item id="c1" href="c1.xhtml" media-type="application/xhtml+xml"/><item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/></manifest>
<spine toc="ncx"><itemref idref="c1"/></spine></package>
OPF
  printf '<?xml version="1.0"?>\n<ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><head/><docTitle><text>%s</text></docTitle><navMap><navPoint id="p1" playOrder="1"><navLabel><text>One</text></navLabel><content src="c1.xhtml"/></navPoint></navMap></ncx>\n' "$title" > "$w/OEBPS/toc.ncx"
  printf '<?xml version="1.0"?>\n<html xmlns="http://www.w3.org/1999/xhtml"><head><title>%s</title></head><body><p>Test book.</p></body></html>\n' "$title" > "$w/OEBPS/c1.xhtml"
  mkdir -p "$(dirname "$out")"; rm -f "$out"
  (cd "$w" && zip -q -X0 "$out" mimetype && zip -q -Xr9 "$out" META-INF OEBPS)
}
b="$M/books"
epub "$b/Test Author/Valid Book/Valid Book.epub" "Valid Book" 9780000000017
expect "books/Test Author/Valid Book/Valid Book.epub" none "control: valid EPUB"
epub "$b/Test Author/Dupe Book/Dupe Book.epub" "Dupe Book" 9780000000024
epub "$b/Test Author/Dupe Book Copy/Dupe Book Copy.epub" "Dupe Book" 9780000000024
expect "books/Test Author/Dupe Book Copy/Dupe Book Copy.epub" Duplicate "KNOWN GAP: Jellyfin stores the provider id as ISBN; DuplicateScanner looks up Isbn"
epub "$b/Test Author/Broken Book/Broken Book.epub" "Broken Book" 9780000000031
f="$b/Test Author/Broken Book/Broken Book.epub"; s=$(wc -c < "$f"); head -c $((s / 2)) "$f" > "$f.tmp" && mv "$f.tmp" "$f"
expect "books/Test Author/Broken Book/Broken Book.epub" Playability "EPUB zip truncated to 50%"
mkdir -p "$b/Test Author/Plain Text"; printf 'A plain text book.\n' > "$b/Test Author/Plain Text/Plain Text.txt"
expect "books/Test Author/Plain Text/Plain Text.txt" FormatCompatibility "plain text needs EPUB conversion"
fixfails "books/Test Author/Plain Text/Plain Text.txt" FormatCompatibility "converter toggle is disabled" "ebook conversion needs Calibre, which the test hosts do not have"
# Audiobooks live under Books: MediaDash routes AudioBook items to the Books target, so this is a
# correctly placed control (a separate Audiobooks library would get every item flagged Misplaced).
mkdir -p "$b/Test Author/Lost Audiobook"
"$FF" -y -v error -f lavfi -i "sine=frequency=300:duration=12" -c:a aac -b:a 64k -f mp4 \
  -metadata title="Lost Audiobook" -metadata artist="Test Author" -metadata genre="Audiobook" \
  "$b/Test Author/Lost Audiobook/Lost Audiobook.m4b"
expect "books/Test Author/Lost Audiobook/Lost Audiobook.m4b" none "control: audiobook correctly under Books target"

# ---------- comics ----------
c="$M/comics"; mkdir -p "$c/Test Series" "$TMP/pages"
for p in 1 2 3; do "$FF" -y -v error -f lavfi -i "testsrc2=size=800x1200:duration=1" -frames:v 1 "$TMP/pages/page$p.jpg"; done
(cd "$TMP/pages" && zip -q "$c/Test Series/Test Series 001.cbz" page*.jpg)
expect "comics/Test Series/Test Series 001.cbz" none "control: valid CBZ"
(cd "$TMP/pages" && 7z a -bd -bso0 "$c/Test Series/Test Series 002.cb7" page*.jpg)
expect "comics/Test Series/Test Series 002.cb7" FormatCompatibility "CB7 should be repacked as CBZ"
head -c 50000 /dev/urandom > "$c/Test Series/Test Series 003.cbz"
expect "comics/Test Series/Test Series 003.cbz" Playability "CBZ that is random bytes"
mkdir -p "$c/Format Comic Pair"
cp "$c/Test Series/Test Series 001.cbz" "$c/Format Comic Pair/Format Comic Pair 001.cbz"
cp "$CBR" "$c/Format Comic Pair/Format Comic Pair 002.cbr"
expect "comics/Format Comic Pair/Format Comic Pair 001.cbz" none "control: ZIP comic archive"
expect "comics/Format Comic Pair/Format Comic Pair 002.cbr" FormatCompatibility "valid RAR comic archive, eligible for repacking to CBZ"

# ---------- pictures ----------
mkdir -p "$M/pictures/Test Album"
"$FF" -y -v error -f lavfi -i "testsrc2=size=1200x800:duration=1" -frames:v 1 "$M/pictures/Test Album/scan.tif"
expect "pictures/Test Album/scan.tif" FormatCompatibility "TIFF should convert to JPG"
"$FF" -y -v error -f lavfi -i "testsrc2=size=1200x800:duration=1" -frames:v 1 "$M/pictures/Test Album/photo.jpg"
expect "pictures/Test Album/photo.jpg" none "control: JPG"

# ---------- manifest ----------
python3 - "$MANIFEST" "$OUT/manifest.json" "$FIXEXP" <<'PY'
import json, sys
fixexp = []
for line in open(sys.argv[3], encoding="utf-8"):
    path, typ, match, why = line.rstrip("\n").split("\t")
    fixexp.append({"path": path, "type": typ, "messageContains": match, "why": why})
rows = []
for line in open(sys.argv[1], encoding="utf-8"):
    path, types, stage, note = line.rstrip("\n").split("\t")
    rows.append({"path": path, "expect": [] if types == "none" else types.split(","), "stage": stage, "note": note})
json.dump({"version": 1, "libraries": ["movies", "tv", "music", "books", "comics", "pictures"],
           "fixtures": rows, "fixExpectations": fixexp}, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False, indent=2)
covered = sorted({t for r in rows for t in r["expect"]})
print(f"{len(rows)} manifest entries; issue types covered: {', '.join(covered)} ({len(covered)})")
PY
du -sh "$M" | cut -f1 | sed 's/^/media size: /'
