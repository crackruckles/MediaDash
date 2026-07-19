using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Flags files that exceed the configured quality ceiling (resolution or bitrate) and estimates the space a re-encode would save.
/// </summary>
public sealed class QualityScanner : ProbingScannerBase
{
    private const double FullHdPixels = 1920.0 * 1080.0;

    /// <summary>
    /// Initializes a new instance of the <see cref="QualityScanner"/> class.
    /// </summary>
    /// <param name="ffprobe">The probe service.</param>
    /// <param name="logger">The logger.</param>
    public QualityScanner(FfprobeService ffprobe, ILogger<QualityScanner> logger)
        : base(ffprobe, logger)
    {
    }

    /// <inheritdoc />
    public override IssueType Type => IssueType.Quality;

    /// <inheritdoc />
    protected override Task<Issue?> EvaluateAsync(BaseItem item, string path, FfprobeData? probe, CancellationToken cancellationToken)
    {
        var video = probe?.Streams?.FirstOrDefault(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase));
        if (probe is null || video is null || video.Height is not > 0 || video.Width is not > 0)
        {
            return Task.FromResult<Issue?>(null);
        }

        var config = Config;
        var tolerance = 1 + (config.QualityTolerancePercent / 100.0);
        var height = video.Height.Value;
        var width = video.Width.Value;

        var videoBitrate = ParseBitrate(video.BitRate) ?? ParseBitrate(probe.Format?.BitRate) ?? 0;
        var pixels = (double)width * height;
        var cappedPixels = Math.Min(pixels, FullHdPixels * config.MaxResolutionHeight * config.MaxResolutionHeight / (1080.0 * 1080.0));
        var allowedBits = config.MaxBitrateMbpsAt1080p * 1_000_000 * (cappedPixels / FullHdPixels);

        var tooTall = height > config.MaxResolutionHeight * tolerance;
        var tooFat = videoBitrate > allowedBits * tolerance;
        if (!tooTall && !tooFat)
        {
            return Task.FromResult<Issue?>(null);
        }

        long fileSize;
        try
        {
            fileSize = new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return Task.FromResult<Issue?>(null);
        }

        var savings = EstimateSavings(probe, fileSize, videoBitrate, allowedBits, cappedPixels / pixels);
        var issue = new Issue
        {
            DetailsJson = JsonSerializer.Serialize(new
            {
                width,
                height,
                codec = video.CodecName,
                videoBitrate,
                allowedBitrate = (long)allowedBits,
                maxHeight = config.MaxResolutionHeight,
                targetCodec = config.PreferredCodec
            }),
            SuggestedFix = string.Format(
                CultureInfo.InvariantCulture,
                "Re-encode to {0}p {1} to save space without a visible quality loss.",
                Math.Min(height, config.MaxResolutionHeight),
                config.PreferredCodec.ToUpperInvariant()),
            SizeSavings = savings
        };
        return Task.FromResult<Issue?>(issue);
    }

    private static long? ParseBitrate(string? raw)
    {
        return raw is not null && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : null;
    }

    private static long EstimateSavings(FfprobeData probe, long fileSize, long videoBitrate, double allowedBits, double pixelRatio)
    {
        if (!double.TryParse(probe.Format?.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) || duration <= 0)
        {
            return 0;
        }

        // ponytail: linear bitrate model, ignores codec efficiency gains; good enough for a savings estimate shown in the UI.
        // A downscale reduces the needed bitrate roughly by the pixel ratio, so the new bitrate is
        // whichever is lower: the ceiling, or the current bitrate scaled to the target resolution.
        var currentVideoBytes = videoBitrate > 0 ? videoBitrate / 8.0 * duration : fileSize * 0.85;
        var newBits = Math.Min(allowedBits, (videoBitrate > 0 ? videoBitrate : allowedBits) * pixelRatio);
        var newVideoBytes = newBits / 8.0 * duration;
        var savings = (long)(currentVideoBytes - newVideoBytes);
        return Math.Max(0, Math.Min(savings, fileSize));
    }
}
