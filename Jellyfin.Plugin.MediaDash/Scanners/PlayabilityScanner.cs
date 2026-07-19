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
    /// <summary>
    /// Initializes a new instance of the <see cref="PlayabilityScanner"/> class.
    /// </summary>
    /// <param name="ffprobe">The probe service.</param>
    /// <param name="logger">The logger.</param>
    public PlayabilityScanner(FfprobeService ffprobe, ILogger<PlayabilityScanner> logger)
        : base(ffprobe, logger)
    {
    }

    /// <inheritdoc />
    public override IssueType Type => IssueType.Playability;

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
        else if (!probe.Streams.Any(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase)))
        {
            reason = "no-video";
            detail = "The file contains no video stream.";
        }
        else if (!TryGetDuration(probe, out var duration) || duration <= 0)
        {
            reason = "no-duration";
            detail = "The file reports no valid duration, which usually means it is truncated or corrupt.";
        }
        else if (Config.ThoroughPlayabilityCheck)
        {
            var decodeError = await Ffprobe.DecodeCheckAsync(path, duration, cancellationToken).ConfigureAwait(false);
            if (decodeError is not null)
            {
                reason = "decode-error";
                detail = decodeError;
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
}
