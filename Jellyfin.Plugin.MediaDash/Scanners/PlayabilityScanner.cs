using System;
using System.Globalization;
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
/// Flags files that are broken or unlikely to play: probe failures, missing video streams,
/// zero durations, and (optionally) decode errors at the start or end of the file.
/// </summary>
public sealed class PlayabilityScanner : ProbingScannerBase
{
    private readonly Probing.BookProbeService _bookProbe;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayabilityScanner"/> class.
    /// </summary>
    /// <param name="ffprobe">The probe service.</param>
    /// <param name="bookProbe">The book probe service.</param>
    /// <param name="logger">The logger.</param>
    public PlayabilityScanner(FfprobeService ffprobe, Probing.BookProbeService bookProbe, ILogger<PlayabilityScanner> logger)
        : base(ffprobe, logger)
    {
        _bookProbe = bookProbe;
    }

    /// <inheritdoc />
    public override IssueType Type => IssueType.Playability;

    /// <summary>
    /// True when the file is short enough that ffmpeg's regional start/middle/end sampling collapses;
    /// a short (e.g. spoken-word or one-track) audio file is best sampled end-to-end.
    /// </summary>
    /// <param name="durationSeconds">Duration in seconds as reported by the probe.</param>
    /// <returns>True when the whole file should be decoded rather than sampled.</returns>
    public static bool ShouldSampleWholeFile(double durationSeconds)
    {
        return durationSeconds > 0 && durationSeconds < 60;
    }

    /// <inheritdoc />
    protected override async Task<Issue?> EvaluateAsync(BaseItem item, string path, FfprobeData? probe, CancellationToken cancellationToken)
    {
        string? reason = null;
        string? detail = null;

        if (!System.IO.File.Exists(path))
        {
            return new Issue
            {
                DetailsJson = JsonSerializer.Serialize(new { reason = "missing", detail = "The library entry points to a file that no longer exists." }),
                SuggestedFix = "The file is gone but Jellyfin still lists it. Restore the file, or run a library scan in Jellyfin to remove the dead entry.",
                SizeSavings = 0
            };
        }

        if (item is MediaBrowser.Controller.Entities.Book)
        {
            var bp = await _bookProbe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
            if (bp.Ok)
            {
                return null;
            }

            long bookSize = 0;
            try
            {
                bookSize = new System.IO.FileInfo(path).Length;
            }
            catch (System.IO.IOException)
            {
            }

            return new Issue
            {
                DetailsJson = JsonSerializer.Serialize(new { reason = "book-corrupt", detail = bp.Reason }),
                SuggestedFix = "This book file can't be read. Approve to remove it — it goes to the recycle bin first unless you chose permanent delete.",
                SizeSavings = bookSize
            };
        }

        if (probe is null)
        {
            // ffprobe unavailable — infrastructure problem, not a file problem.
            return null;
        }

        if (probe.Error is not null || probe.Streams is null || probe.Streams.Count == 0)
        {
            reason = "unreadable";
            detail = probe.Error?.Message ?? "The file could not be read as a media file.";
        }
        else if (item is not MediaBrowser.Controller.Entities.Audio.Audio
            && !probe.Streams.Any(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase)))
        {
            reason = "no-video";
            detail = "The file contains no video stream.";
        }
        else if (item is MediaBrowser.Controller.Entities.Audio.Audio
            && !probe.Streams.Any(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase)))
        {
            reason = "no-audio";
            detail = "The file contains no audio stream.";
        }
        else if (!TryGetDuration(probe, out var duration) || duration <= 0)
        {
            reason = "no-duration";
            detail = "The file reports no valid duration, which usually means it is truncated or corrupt.";
        }
        else if (Config.ThoroughPlayabilityCheck)
        {
            // Bitrate-vs-size sanity check first (cheap, no ffmpeg). If the container claims duration D
            // and bitrate B, expected file size ≈ B*D/8. When the actual file is meaningfully smaller,
            // the file was truncated even though its header still advertises the full duration. Only
            // fires when both bit_rate and duration are known and positive. Tolerance 40% accommodates
            // both VBR variance and containers where the reported bitrate is the video stream only
            // (which is common) — we deliberately want false negatives over false positives here.
            var bitrate = TryParseLong(probe.Format?.BitRate);
            long actualSize = 0;
            try
            {
                actualSize = new System.IO.FileInfo(path).Length;
            }
            catch (System.IO.IOException)
            {
            }

            if (bitrate is > 0 && duration > 0 && actualSize > 0)
            {
                var expectedBytes = bitrate.Value / 8.0 * duration;
                if (actualSize < expectedBytes * 0.6)
                {
                    reason = "size-truncated";
                    detail = string.Format(
                        CultureInfo.InvariantCulture,
                        "File is {0} bytes but the container's bitrate × duration expects ~{1:F0} bytes — the file appears to hold much less content than it advertises.",
                        actualSize,
                        expectedBytes);
                }
            }

            if (reason is null)
            {
                var decodeError = ShouldSampleWholeFile(duration)
                    ? await Ffprobe.DecodeCheckAsync(path, durationSeconds: 0, cancellationToken).ConfigureAwait(false)
                    : await Ffprobe.DecodeCheckAsync(path, duration, cancellationToken).ConfigureAwait(false);
                if (decodeError is not null)
                {
                    reason = "decode-error";
                    detail = decodeError;
                }
            }
        }

        if (reason is null)
        {
            return null;
        }

        long size = 0;
        try
        {
            size = new System.IO.FileInfo(path).Length;
        }
        catch (System.IO.IOException)
        {
        }

        return new Issue
        {
            DetailsJson = JsonSerializer.Serialize(new { reason, detail }),
            SuggestedFix = "This file can't be played. Approve to remove it — it goes to the recycle bin first unless you chose permanent delete.",
            SizeSavings = size
        };
    }

    private static bool TryGetDuration(FfprobeData probe, out double duration)
    {
        duration = 0;
        var raw = probe.Format?.Duration ?? probe.Streams?.FirstOrDefault(s => s.Duration is not null)?.Duration;
        return raw is not null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
    }

    private static long? TryParseLong(string? raw)
    {
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
