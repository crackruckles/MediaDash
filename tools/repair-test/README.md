# repair-test — Playability repair ladder test suite

Ten fixtures covering each rung of `PlayabilityFixer`'s repair ladder plus a healthy
negative control. Two validate scripts turn "does it work?" into a machine-checkable
pass/fail.

## Fixtures

| Fixture | Target rung | Broken in | Expected post-repair name |
|---|---|---|---|
| `rung1-bad-index.mkv` | 1 (quick remux) | MKV cues section chopped | same |
| `rung1-truncated-mp4.mp4` | 1 (quick remux) | MP4 tail truncated (moov present via faststart) | same |
| `rung1-noaudio-header.mkv` | 1 (quick remux) | Small tail loss, forces `+genpts+igndts` rewrite | same |
| `rung2-broken-sub.mkv` | 2 (drop broken streams) | Junk-bytes SRT track alongside good video/audio | same |
| `rung2-broken-second-audio.mkv` | 2 (drop broken streams) | Two audio tracks, tail packets XORed | same |
| `rung3-hevc-in-avi.avi` | 3 (container coerce to MKV) | HEVC muxed into AVI | `rung3-hevc-in-avi.mkv` |
| `rung3-flv-with-junk.flv` | 3 (container coerce to MKV) | H264-in-FLV, tail scrambled | `rung3-flv-with-junk.mkv` |
| `rung4-bitstream-damage.mkv` | 4 (re-encode) | 5% mid-file XOR (survives remux) | same |
| `rung4-heavy-noise.mkv` | 4 (re-encode) | 20% mid-file XOR (rung 3 verify fails) | same |
| `neg-healthy.mkv` | 0 (must NOT be flagged) | nothing | same, untouched |

## Workflow

```powershell
# 1. Generate the fixtures.
.\regenerate.ps1

# 2. Pre-flight: confirm each fixture is broken (or healthy) as expected.
.\validate-fixtures.ps1        # exit 0 = all fixtures match manifest

# 3. Point a Jellyfin library at .\fixtures\ and run MediaDash Scan + Fix.

# 4. Post-flight: confirm the repair produced the right output per rung.
.\validate-outputs.ps1         # exit 0 = every fixture repaired correctly
```

Regenerate a single fixture in isolation:

```powershell
.\regenerate.ps1 -Only rung3-hevc-in-avi.avi
```

Override ffmpeg location:

```powershell
.\regenerate.ps1        -Ffmpeg 'C:\tools\ffmpeg\bin\ffmpeg.exe'
.\validate-fixtures.ps1 -Ffmpeg 'C:\tools\ffmpeg\bin\ffmpeg.exe'
.\validate-outputs.ps1  -Ffmpeg 'C:\tools\ffmpeg\bin\ffmpeg.exe'
```

Override recycle-bin root (default is `%LOCALAPPDATA%\jellyfin-v10\data\mediadash\recycle`):

```powershell
.\validate-outputs.ps1 -RecycleBinRoot 'D:\my\bin\path'
```

## Manifest

`fixture-manifest.json` is written by `regenerate.ps1`. The validate scripts read it — if
you add a fixture manually, add its entry there too so validation picks it up.
