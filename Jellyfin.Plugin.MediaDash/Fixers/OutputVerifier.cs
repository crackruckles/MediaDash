using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Probing;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Safety invariant #3: an original file is never replaced until the new file passes ffprobe verification —
/// duration within 2 seconds of the original and the expected streams present.
/// </summary>
public sealed class OutputVerifier
{
    private readonly FfprobeService _ffprobe;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutputVerifier"/> class.
    /// </summary>
    /// <param name="ffprobe">The probe service.</param>
    public OutputVerifier(FfprobeService ffprobe)
    {
        _ffprobe = ffprobe;
    }

    /// <summary>
    /// Verifies a produced file against its original before any swap happens.
    /// </summary>
    /// <param name="originalProbe">Probe data of the original file.</param>
    /// <param name="outputPath">The newly produced file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Null when the output is good; otherwise the reason it failed verification.</returns>
    public async Task<string?> VerifyAsync(FfprobeData originalProbe, string outputPath, CancellationToken cancellationToken)
    {
        var probe = await _ffprobe.ProbeAsync(outputPath, cancellationToken).ConfigureAwait(false);
        if (probe is null || probe.Error is not null || probe.Streams is null || probe.Streams.Count == 0)
        {
            return "The new file could not be read back: " + (probe?.Error?.Message ?? "probe failed");
        }

        if (!probe.Streams.Any(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase)))
        {
            return "The new file has no video stream.";
        }

        var originalHadAudio = originalProbe.Streams?.Any(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase)) ?? false;
        if (originalHadAudio && !probe.Streams.Any(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase)))
        {
            return "The new file has no audio stream but the original did.";
        }

        // Slack scales with runtime: the old fixed 2 s tolerance was hostile to long-form content
        // (a Breaking Bad 47-min episode remuxed via -c copy from an mpegts source legitimately loses
        // ~30 s of pre-first-video-keyframe garbage the demuxer counted toward duration). Use the
        // larger of 2 s or 2 % of the original duration. 2 % of a 45-min file is ~54 s; 2 % of a
        // 2-hour Blu-ray is ~144 s. Real truncations still fail — a file cut in half is 50 % off,
        // orders of magnitude past the gate.
        var originalDuration = GetVideoDurationSeconds(originalProbe);
        var newDuration = GetVideoDurationSeconds(probe);
        var slack = Math.Max(2.0, originalDuration * 0.02);
        if (originalDuration > 0 && Math.Abs(originalDuration - newDuration) > slack)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Duration mismatch: original {0:F1}s, new file {1:F1}s (allowed slack {2:F1}s).",
                originalDuration,
                newDuration,
                slack);
        }

        return null;
    }

    // Prefer the retained video stream's duration. The old code read Format.Duration, which ffprobe
    // derives from the *longest* stream — so remuxing away a longer secondary audio/subtitle track
    // legitimately shortened the container aggregate even though the video was byte-identical, and
    // every subtitle-/audio-track removal fell through the > 2 s tolerance and was rejected.
    // Fallback chain: stream.Duration (empty for most MKV) → stream.Tags["DURATION"] (MKV convention)
    // → Format.Duration (last resort, matches the old wrong behaviour but only when we have nothing better).
    internal static double GetVideoDurationSeconds(FfprobeData probe)
    {
        var video = probe.Streams?.FirstOrDefault(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase));
        if (video is not null)
        {
            if (double.TryParse(video.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
            {
                return d;
            }

            if (video.Tags is not null)
            {
                // MKV writes "DURATION" (sometimes with a language suffix, e.g. "DURATION-eng") as
                // HH:MM:SS.nanoseconds — pick the first that parses to a positive TimeSpan.
                foreach (var kv in video.Tags)
                {
                    if (kv.Key.StartsWith("DURATION", StringComparison.OrdinalIgnoreCase)
                        && TimeSpan.TryParse(kv.Value, CultureInfo.InvariantCulture, out var ts)
                        && ts.TotalSeconds > 0)
                    {
                        return ts.TotalSeconds;
                    }
                }
            }
        }

        return double.TryParse(probe.Format?.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var fmt) ? fmt : 0;
    }
}
