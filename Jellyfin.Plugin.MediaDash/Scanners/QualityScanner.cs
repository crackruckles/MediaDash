using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
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
        // Audio items (music + audiobooks) — detect-only ceiling, no re-encode.
        var itemKind = item.GetBaseItemKind();
        if (itemKind == BaseItemKind.Audio || itemKind == BaseItemKind.AudioBook)
        {
            // ponytail: kind gates entry; cast still needed for property access. If v12 moved the type, skip cleanly.
            if (item is not Audio audio)
            {
                return Task.FromResult<Issue?>(null);
            }

            var isAudioBook = itemKind == BaseItemKind.AudioBook;
            if (isAudioBook && !Config.QualityScanAudiobooks)
            {
                return Task.FromResult<Issue?>(null);
            }

            var audioStream = probe?.Streams?.FirstOrDefault(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase));
            if (audioStream is not null
                && long.TryParse(audioStream.BitRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var abps)
                && IsAudioOversized(audioStream.CodecName ?? string.Empty, abps))
            {
                long audioFileSize;
                try
                {
                    audioFileSize = new FileInfo(path).Length;
                }
                catch (IOException)
                {
                    return Task.FromResult<Issue?>(null);
                }

                var estimatedSavings = (long)(audioFileSize * 0.30);
                return Task.FromResult<Issue?>(new Issue
                {
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        reason = "audio-oversized",
                        codec = audioStream.CodecName,
                        bitsPerSecond = abps,
                        fixerAvailable = false
                    }),
                    SuggestedFix = "Audio bitrate is higher than the ceiling. MediaDash reports oversized audio but does not re-encode it — reduce manually if desired.",
                    SizeSavings = estimatedSavings
                });
            }

            return Task.FromResult<Issue?>(null);
        }

        var video = probe?.Streams?.FirstOrDefault(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase));
        if (probe is null || video is null || video.Height is not > 0 || video.Width is not > 0)
        {
            return Task.FromResult<Issue?>(null);
        }

        var config = Config;
        if (config.ReencodeFileTypes.Length > 0
            && !config.ReencodeFileTypes.Contains(Path.GetExtension(path).TrimStart('.'), StringComparer.OrdinalIgnoreCase))
        {
            return Task.FromResult<Issue?>(null);
        }

        // Skip small files (samples, trailers, extras) — they aren't the main content and re-encoding them
        // wastes time. Threshold configurable in Advanced settings.
        try
        {
            var sizeMb = new FileInfo(path).Length / (1024L * 1024L);
            if (sizeMb < config.MinScanFileSizeMb)
            {
                return Task.FromResult<Issue?>(null);
            }
        }
        catch (IOException)
        {
            return Task.FromResult<Issue?>(null);
        }

        // HDR passthrough: transcoding HDR without proper color-space plumbing (colorspace, primaries, transfer
        // characteristic flags on the encoder) destroys HDR metadata and produces washed-out SDR. Until we handle
        // it properly, opt out by default and let advanced users flip the switch.
        if (config.SkipHdrContent && IsHdr(video))
        {
            return Task.FromResult<Issue?>(null);
        }

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

    /// <summary>
    /// Detect-only ceiling for audio streams. Lossless codecs are skipped (users of FLAC/ALAC/WAV
    /// keep them deliberately). MP3 above 320 kbps or AAC above 256 kbps is flagged.
    /// </summary>
    /// <param name="codec">The audio codec name (e.g. "mp3", "aac", "flac").</param>
    /// <param name="bitsPerSecond">The audio stream bit rate.</param>
    /// <returns>True when the file should be reported as oversized.</returns>
    public static bool IsAudioOversized(string codec, long bitsPerSecond)
    {
        var c = codec?.ToLowerInvariant() ?? string.Empty;
        if (c is "flac" or "alac" or "wav" or "pcm_s16le" or "pcm_s24le" or "ape" or "wavpack")
        {
            return false;
        }

        if (c == "mp3")
        {
            return bitsPerSecond > 320_000L;
        }

        if (c is "aac" or "m4a" or "libfdk_aac")
        {
            return bitsPerSecond > 256_000L;
        }

        return false;
    }

    private static bool IsHdr(FfprobeStreamInfo video)
    {
        return string.Equals(video.ColorPrimaries, "bt2020", StringComparison.OrdinalIgnoreCase)
            || string.Equals(video.ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase)
            || string.Equals(video.ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase);
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
