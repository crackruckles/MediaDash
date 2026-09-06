using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Probing;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Safety invariant #3: an original file is never replaced until the new file passes verification —
/// video/audio streams match the original's shape, and one of (a) container duration within slack,
/// (b) frame counts match, or (c) packet counts match, is true. See VerifyAsync for the three-layer
/// ladder and why we can't just trust duration alone (issue #39).
/// </summary>
public sealed class OutputVerifier
{
    private readonly FfprobeService _ffprobe;
    private readonly ILogger<OutputVerifier>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutputVerifier"/> class.
    /// </summary>
    /// <param name="ffprobe">The probe service.</param>
    /// <param name="logger">Optional logger — null in unit tests, DI-injected in production.</param>
    public OutputVerifier(FfprobeService ffprobe, ILogger<OutputVerifier>? logger = null)
    {
        _ffprobe = ffprobe;
        _logger = logger;
    }

    /// <summary>
    /// Verifies a produced file against its original before any swap happens.
    /// </summary>
    /// <param name="originalProbe">Probe data of the original file.</param>
    /// <param name="outputPath">The newly produced file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Null when the output is good; otherwise the reason it failed verification.</returns>
    public Task<string?> VerifyAsync(FfprobeData originalProbe, string outputPath, CancellationToken cancellationToken)
        => VerifyAsync(originalProbe, originalPath: null, outputPath, cancellationToken);

    /// <summary>
    /// Verifies a produced file against its original, passing the original's path so the packet-count
    /// rescue (Layer 3) can walk both files when the duration and frame-count checks are inconclusive.
    /// Callers that have the original path available should prefer this overload.
    /// </summary>
    /// <param name="originalProbe">Probe data of the original file.</param>
    /// <param name="originalPath">Full path of the original file, or null to skip the packet-count rescue.</param>
    /// <param name="outputPath">The newly produced file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Null when the output is good; otherwise the reason it failed verification.</returns>
    public async Task<string?> VerifyAsync(FfprobeData originalProbe, string? originalPath, string outputPath, CancellationToken cancellationToken)
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

        // Duration comparison ladder. Each layer's job is to accept the file when the higher layer
        // was too pessimistic on a container with untrustworthy metadata.
        //
        // Layer 1: container duration ± max(2 s, dur × 2 %). Cheap; passes for well-behaved sources.
        //   Fails when the source container declared a bogus Format.Duration (issue #39: Blu-ray AV1
        //   remuxes where the top-level duration matched a removed audio track, not the video).
        //
        // Layer 2: video-stream nb_frames. Populated free from the container header on MP4 and many
        //   MKVs. For a -c copy remux the video stream is byte-for-byte identical, so nb_frames MUST
        //   be equal. Instant when present.
        //
        // Layer 3: video-packet count via -count_packets. Slow (seconds to minutes on a Blu-ray),
        //   but definitive when Layers 1 and 2 both give up. Only fires on the failure-rescue path.
        //
        // A file that fails all three layers is genuinely different from its source — real truncation,
        // real re-encode drift beyond what the slack allows, or a corrupt remux.
        var originalDuration = GetVideoDurationSeconds(originalProbe);
        var newDuration = GetVideoDurationSeconds(probe);
        var slack = Math.Max(2.0, originalDuration * 0.02);
        var durationDelta = Math.Abs(originalDuration - newDuration);
        var durationCheckPasses = originalDuration <= 0 || durationDelta <= slack;

        if (durationCheckPasses)
        {
            return null;
        }

        // Layer 2 — free nb_frames match rescues the case where container duration lied on either side.
        var originalFrames = GetVideoNbFrames(originalProbe);
        var newFrames = GetVideoNbFrames(probe);
        if (originalFrames is long ofr && newFrames is long nfr && ofr > 0 && nfr > 0)
        {
            // Muxer edge cases can produce off-by-one differences (e.g. a trailing partial frame
            // dropped on remux). Anything within 2 frames is faithful; anything larger is real drift.
            if (Math.Abs(ofr - nfr) <= 2)
            {
                _logger?.LogInformation(
                    "OutputVerifier: container duration disagreed ({OrigDur:F1}s → {NewDur:F1}s, delta {Delta:F1}s > slack {Slack:F1}s) but video frame counts match ({Frames}). Accepting.",
                    originalDuration,
                    newDuration,
                    durationDelta,
                    slack,
                    ofr);
                return null;
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "Duration mismatch: original {0:F1}s, new file {1:F1}s (allowed slack {2:F1}s). Frame counts also disagreed: original {3}, new {4}.",
                originalDuration,
                newDuration,
                slack,
                ofr,
                nfr);
        }

        // Layer 3 — walk both files for a definitive packet count. Slow, only when we'd otherwise reject.
        _logger?.LogInformation(
            "OutputVerifier: duration disagreed ({OrigDur:F1}s → {NewDur:F1}s, delta {Delta:F1}s > slack {Slack:F1}s) and nb_frames unavailable. Falling back to packet-count walk. This can take a minute or two on large files.",
            originalDuration,
            newDuration,
            durationDelta,
            slack);

        // Layer 3 is only possible when the caller passed the original path. The parameter is
        // optional because tests and older callers use the two-arg overload; the new-path overload
        // (recommended for TrackFixer + TranscodeFixer) enables the rescue.
        if (string.IsNullOrEmpty(originalPath))
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Duration mismatch: original {0:F1}s, new file {1:F1}s (allowed slack {2:F1}s). Frame counts unavailable and could not verify by packet count.",
                originalDuration,
                newDuration,
                slack);
        }

        var origPackets = await _ffprobe.CountVideoPacketsAsync(originalPath, cancellationToken).ConfigureAwait(false);
        var newPackets = await _ffprobe.CountVideoPacketsAsync(outputPath, cancellationToken).ConfigureAwait(false);
        if (origPackets is long op && newPackets is long np && op > 0 && np > 0)
        {
            // Same 2-packet tolerance as the frame-count check — occasionally the mux boundary
            // produces a trailing packet difference on otherwise-identical streams.
            if (Math.Abs(op - np) <= 2)
            {
                _logger?.LogInformation(
                    "OutputVerifier: packet-count rescue succeeded. Original {OrigPackets}, new {NewPackets} (delta ≤ 2). Container duration on the source was unreliable.",
                    op,
                    np);
                return null;
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "Duration mismatch: original {0:F1}s, new file {1:F1}s (allowed slack {2:F1}s). Packet counts also disagreed: original {3}, new {4}.",
                originalDuration,
                newDuration,
                slack,
                op,
                np);
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "Duration mismatch: original {0:F1}s, new file {1:F1}s (allowed slack {2:F1}s). Could not verify by frame or packet count.",
            originalDuration,
            newDuration,
            slack);
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

    // Extracts video-stream nb_frames when the container declared it. Returns null when absent
    // or unparseable — the caller falls through to the packet-count rescue in that case.
    internal static long? GetVideoNbFrames(FfprobeData probe)
    {
        var video = probe.Streams?.FirstOrDefault(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase));
        if (video is null || string.IsNullOrEmpty(video.NbFrames))
        {
            return null;
        }

        return long.TryParse(video.NbFrames, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : null;
    }
}
