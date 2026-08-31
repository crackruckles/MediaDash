using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
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
    private readonly Probing.ComicProbeService _comicProbe;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayabilityScanner"/> class.
    /// </summary>
    /// <param name="ffprobe">The probe service.</param>
    /// <param name="bookProbe">The book probe service.</param>
    /// <param name="comicProbe">The comic probe service.</param>
    /// <param name="logger">The logger.</param>
    public PlayabilityScanner(FfprobeService ffprobe, Probing.BookProbeService bookProbe, Probing.ComicProbeService comicProbe, ILogger<PlayabilityScanner> logger)
        : base(ffprobe, logger)
    {
        _bookProbe = bookProbe;
        _comicProbe = comicProbe;
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

        // .strm files are text pointers to remote/network streams; ffprobe on the local text file
        // would always flag them as unreadable. Skip playability checks for them.
        if (string.Equals(System.IO.Path.GetExtension(path), ".strm", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (item.GetBaseItemKind() == BaseItemKind.Book)
        {
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            bool ok;
            string? probeReason;
            if (ext is ".cbz" or ".cbr" or ".cb7")
            {
                var cp = await _comicProbe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
                ok = cp.Ok;
                probeReason = cp.Reason;
            }
            else
            {
                var bp = await _bookProbe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
                ok = bp.Ok;
                probeReason = bp.Reason;
            }

            if (ok)
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
                DetailsJson = JsonSerializer.Serialize(new { reason = "book-or-comic-corrupt", detail = probeReason }),
                SuggestedFix = "This file can't be read. Approve to remove it — it goes to the recycle bin first unless you chose permanent delete.",
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
        else if (IsContainerExtensionMismatch(path, probe, out var mismatchDetail))
        {
            // F-213: ffmpeg happily demuxes any container regardless of the file extension.
            // Users rename files (or downloads arrive with the wrong extension) and end up
            // with `.mp3` files that are actually MKVs — Jellyfin then routes them to the
            // audio pipeline where they may or may not play. Flag the mismatch so the user
            // can rename to the correct extension.
            reason = "container-extension-mismatch";
            detail = mismatchDetail;
        }
        else if (!IsAudioKind(item.GetBaseItemKind())
            && !probe.Streams.Any(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase)))
        {
            reason = "no-video";
            detail = "The file contains no video stream.";
        }
        else if (IsAudioKind(item.GetBaseItemKind())
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

    private static bool IsAudioKind(BaseItemKind kind)
        => kind == BaseItemKind.Audio || kind == BaseItemKind.AudioBook;

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

    /// <summary>
    /// F-213: cross-checks ffprobe's reported container against the file extension. ffmpeg
    /// silently demuxes any container regardless of extension, so a renamed / mis-arrived
    /// `.mp3` that's actually an MKV gets routed to Jellyfin's audio pipeline and behaves
    /// oddly. Flag mismatches so the user can rename to something honest.
    /// </summary>
    private static bool IsContainerExtensionMismatch(string path, FfprobeData probe, out string detail)
    {
        detail = string.Empty;
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant().TrimStart('.');
        var formatName = probe.Format?.FormatName?.ToLowerInvariant();
        if (string.IsNullOrEmpty(ext) || string.IsNullOrEmpty(formatName))
        {
            return false;
        }

        // ffprobe reports format_name as a comma-separated list of matching demuxers
        // (e.g. "matroska,webm", "mp4,mov,m4a,3gp,3g2,mj2", "mp3"). Any overlap between
        // the extension and the reported list means we're fine.
        var demuxers = formatName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var extensionAliases = ext switch
        {
            "mkv" or "webm" => new[] { "matroska", "webm" },
            "mp4" or "m4v" or "m4a" or "mov" or "3gp" or "3g2" => new[] { "mp4", "mov", "m4a", "3gp", "3g2", "mj2" },
            "avi" => new[] { "avi" },
            "wmv" or "asf" => new[] { "asf" },
            "flv" => new[] { "flv" },
            "ts" or "mts" or "m2ts" => new[] { "mpegts" },
            "mpg" or "mpeg" or "vob" => new[] { "mpeg", "mpegvideo", "mpegps" },
            "ogv" or "ogg" or "ogm" or "opus" => new[] { "ogg" },
            "mp3" => new[] { "mp3" },
            "aac" => new[] { "aac" },
            "flac" => new[] { "flac" },
            "wav" => new[] { "wav" },
            "ac3" => new[] { "ac3" },
            "dts" => new[] { "dts" },
            "wma" => new[] { "asf" },
            "aif" or "aiff" => new[] { "aiff" },
            _ => Array.Empty<string>()
        };

        if (extensionAliases.Length == 0)
        {
            // Unknown extension — don't flag. Let the item pass; other detectors will catch
            // truly broken files.
            return false;
        }

        var overlap = extensionAliases.Any(alias => demuxers.Contains(alias));
        if (overlap)
        {
            return false;
        }

        detail = string.Format(
            CultureInfo.InvariantCulture,
            "The file's extension is '.{0}' but its container is '{1}'. Playback may fail on strict clients. Rename to a matching extension (or re-encode into an actual .{0} container) to make the extension honest.",
            ext,
            formatName);
        return true;
    }
}
