#!/usr/bin/env bash
# Generates a tiny synthetic test library exercising every MediaDash scanner.
# Usage: ./make-fixtures.sh <output-dir> [ffmpeg-path]
set -euo pipefail

OUT="${1:?usage: make-fixtures.sh <output-dir> [ffmpeg-path]}"
FFMPEG="${2:-ffmpeg}"

SRC_ARGS=(-f lavfi -i "testsrc2=duration=20:size=WIDTHxHEIGHT:rate=24" -f lavfi -i "sine=frequency=440:duration=20")

gen() { # gen <outfile> <width> <height> <video-args...>
  local out="$1" w="$2" h="$3"; shift 3
  mkdir -p "$(dirname "$out")"
  "$FFMPEG" -y -v error \
    -f lavfi -i "testsrc2=duration=20:size=${w}x${h}:rate=24" \
    -f lavfi -i "sine=frequency=440:duration=20" \
    -map 0:v -map 1:a "$@" "$out"
}

# 1. Quality issue: 4K H.264 at a deliberately high bitrate
gen "$OUT/movies/Big Buck Test (2020)/Big Buck Test (2020) - 2160p.mkv" 3840 2160 \
  -c:v libx264 -b:v 9M -minrate 9M -maxrate 9M -bufsize 2M -c:a aac -metadata:s:a:0 language=eng

# 2. Duplicate: same movie again in 1080p (same folder => same title+year group)
gen "$OUT/movies/Big Buck Test (2020)/Big Buck Test (2020) - 1080p.mkv" 1920 1080 \
  -c:v libx264 -crf 30 -c:a aac -metadata:s:a:0 language=eng

# 3. Playability issue: generate then truncate to 40% of its size
gen "$OUT/movies/Truncated Movie (2021)/Truncated Movie (2021).mkv" 1280 720 \
  -c:v libx264 -crf 30 -c:a aac -metadata:s:a:0 language=eng
f="$OUT/movies/Truncated Movie (2021)/Truncated Movie (2021).mkv"
size=$(wc -c < "$f")
head -c $((size * 2 / 5)) "$f" > "$f.tmp" && mv "$f.tmp" "$f"

# 4. Audio language issue: eng + fra + deu audio tracks
mkdir -p "$OUT/movies/Multi Audio (2022)"
"$FFMPEG" -y -v error \
  -f lavfi -i "testsrc2=duration=20:size=1280x720:rate=24" \
  -f lavfi -i "sine=frequency=440:duration=20" \
  -f lavfi -i "sine=frequency=550:duration=20" \
  -f lavfi -i "sine=frequency=660:duration=20" \
  -map 0:v -map 1:a -map 2:a -map 3:a \
  -c:v libx264 -crf 30 -c:a aac \
  -metadata:s:a:0 language=eng -metadata:s:a:1 language=fra -metadata:s:a:2 language=deu \
  "$OUT/movies/Multi Audio (2022)/Multi Audio (2022).mkv"

# 5. Subtitle language issue: eng + fra + deu subtitle tracks
mkdir -p "$OUT/movies/Sub Heavy (2023)"
srt=$(mktemp --suffix=.srt 2>/dev/null || mktemp -t sub.XXXXXX.srt)
printf '1\n00:00:01,000 --> 00:00:05,000\nTest subtitle\n' > "$srt"
"$FFMPEG" -y -v error \
  -f lavfi -i "testsrc2=duration=20:size=1280x720:rate=24" \
  -f lavfi -i "sine=frequency=440:duration=20" \
  -i "$srt" -i "$srt" -i "$srt" \
  -map 0:v -map 1:a -map 2:s -map 3:s -map 4:s \
  -c:v libx264 -crf 30 -c:a aac -c:s srt \
  -metadata:s:a:0 language=eng \
  -metadata:s:s:0 language=eng -metadata:s:s:1 language=fra -metadata:s:s:2 language=deu \
  "$OUT/movies/Sub Heavy (2023)/Sub Heavy (2023).mkv"
rm -f "$srt"

# 6. Clean file: 720p HEVC-ish small file, single eng audio (no issues expected)
gen "$OUT/movies/Clean Movie (2024)/Clean Movie (2024).mkv" 1280 720 \
  -c:v libx264 -crf 32 -c:a aac -metadata:s:a:0 language=eng

echo "Fixtures written to $OUT"
