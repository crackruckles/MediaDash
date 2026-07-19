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

        var originalDuration = ParseDuration(originalProbe);
        var newDuration = ParseDuration(probe);
        if (originalDuration > 0 && Math.Abs(originalDuration - newDuration) > 2.0)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Duration mismatch: original {0:F1}s, new file {1:F1}s.",
                originalDuration,
                newDuration);
        }

        return null;
    }

    private static double ParseDuration(FfprobeData probe)
    {
        return double.TryParse(probe.Format?.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }
}
