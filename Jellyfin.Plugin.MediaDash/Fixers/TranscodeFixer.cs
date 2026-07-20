using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Re-encodes oversized files down to the configured quality ceiling.
/// Riskiest fixer: full temp → verify → swap flow, with free-space check and a larger-output bailout.
/// </summary>
public sealed class TranscodeFixer : IFixer
{
    private static readonly TimeSpan TranscodeTimeout = TimeSpan.FromHours(6);

    private readonly FfprobeService _ffprobe;
    private readonly FfmpegExecutor _ffmpeg;
    private readonly OutputVerifier _verifier;
    private readonly LibraryGuard _guard;
    private readonly RecycleBin _recycleBin;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly ILogger<TranscodeFixer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscodeFixer"/> class.
    /// </summary>
    /// <param name="ffprobe">The probe service.</param>
    /// <param name="ffmpeg">The ffmpeg executor.</param>
    /// <param name="verifier">The output verifier.</param>
    /// <param name="guard">The library path guard.</param>
    /// <param name="recycleBin">The recycle bin.</param>
    /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
    /// <param name="serverConfig">Instance of the <see cref="IServerConfigurationManager"/> interface, used to read the server's hardware acceleration type.</param>
    /// <param name="logger">The logger.</param>
    public TranscodeFixer(
        FfprobeService ffprobe,
        FfmpegExecutor ffmpeg,
        OutputVerifier verifier,
        LibraryGuard guard,
        RecycleBin recycleBin,
        ILibraryMonitor libraryMonitor,
        IServerConfigurationManager serverConfig,
        ILogger<TranscodeFixer> logger)
    {
        _ffprobe = ffprobe;
        _ffmpeg = ffmpeg;
        _verifier = verifier;
        _guard = guard;
        _recycleBin = recycleBin;
        _libraryMonitor = libraryMonitor;
        _serverConfig = serverConfig;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.Quality;

    /// <inheritdoc />
    public async Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (!File.Exists(issue.Path))
        {
            return FixResult.Fail("The file no longer exists; re-scan to refresh the list.");
        }

        if (!_guard.IsInsideLibrary(issue.Path))
        {
            return FixResult.Fail("The file is outside your library folders; MediaDash will not touch it.");
        }

        var probe = await _ffprobe.ProbeAsync(issue.Path, cancellationToken).ConfigureAwait(false);
        var video = probe?.Streams?.FirstOrDefault(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase));
        if (probe is null || probe.Error is not null || video is null)
        {
            return FixResult.Fail("The file could not be analyzed; it may be broken.");
        }

        var originalSize = new FileInfo(issue.Path).Length;
        var targetContainer = string.IsNullOrWhiteSpace(config.TargetContainer) ? "mkv" : config.TargetContainer.TrimStart('.').ToLowerInvariant();
        var targetPath = Path.ChangeExtension(issue.Path, "." + targetContainer);
        var needsDownscale = video.Height is > 0 && video.Height.Value > config.MaxResolutionHeight;
        var disposal = config.GetDisposal(IssueType.Quality);

        var actionText = string.Format(
            CultureInfo.InvariantCulture,
            "re-encoded {0} to {1}p {2} ({3}), {4}",
            Path.GetFileName(issue.Path),
            needsDownscale ? config.MaxResolutionHeight : video.Height,
            config.PreferredCodec.ToUpperInvariant(),
            targetContainer.ToUpperInvariant(),
            disposal == DisposalMethod.RecycleBin ? "original kept in recycle bin" : "original permanently deleted");

        if (config.DryRun)
        {
            return FixResult.DryRun(actionText, issue.SizeSavings);
        }

        // Temp file lives alongside the original during the encode and is aborted if it ever reaches originalSize
        // (see the newSize >= originalSize check below). Worst case we need room for one more copy of the file plus
        // a small margin for muxer overhead; batches free space as they progress because the original is removed each round.
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(issue.Path))!);
        const long safetyMarginBytes = 500L * 1024 * 1024;
        if (drive.AvailableFreeSpace < originalSize + safetyMarginBytes)
        {
            return FixResult.Fail("Not enough free disk space to re-encode this file (needs its own size plus about 500 MB free).");
        }

        var durationSeconds = double.TryParse(probe.Format?.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

        var tempPath = issue.Path + ".mediadash.tmp." + targetContainer;
        try
        {
            var hwEncoder = config.UseHardwareEncoder ? GetHardwareEncoder(config.PreferredCodec) : null;
            string? error;
            if (hwEncoder is not null)
            {
                var hwArgs = BuildArgs(issue.Path, tempPath, probe, video, config, needsDownscale, targetContainer, hwEncoder);
                error = await _ffmpeg.RunAsync(hwArgs, TranscodeTimeout, cancellationToken, progress, durationSeconds).ConfigureAwait(false);
                if (error is not null)
                {
                    _logger.LogWarning("Hardware encoder {Encoder} failed on {Path}; retrying with software. Details: {Error}", hwEncoder, issue.Path, Truncate(error));
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }

                    var swArgs = BuildArgs(issue.Path, tempPath, probe, video, config, needsDownscale, targetContainer, null);
                    error = await _ffmpeg.RunAsync(swArgs, TranscodeTimeout, cancellationToken, progress, durationSeconds).ConfigureAwait(false);
                }
            }
            else
            {
                var args = BuildArgs(issue.Path, tempPath, probe, video, config, needsDownscale, targetContainer, null);
                error = await _ffmpeg.RunAsync(args, TranscodeTimeout, cancellationToken, progress, durationSeconds).ConfigureAwait(false);
            }

            if (error is not null)
            {
                return FixResult.Fail("Re-encoding failed; the original is untouched. Details: " + Truncate(error));
            }

            var verifyError = await _verifier.VerifyAsync(probe, tempPath, cancellationToken).ConfigureAwait(false);
            if (verifyError is not null)
            {
                return FixResult.Fail("The re-encoded file failed verification; the original is untouched. Details: " + verifyError);
            }

            var newSize = new FileInfo(tempPath).Length;
            if (newSize >= originalSize)
            {
                return FixResult.Fail("The re-encoded file would be larger than the original, so the original was kept.");
            }

            string? recyclePath = null;
            if (disposal == DisposalMethod.RecycleBin)
            {
                recyclePath = _recycleBin.MoveToBin(issue.Path);
            }
            else
            {
                File.Delete(issue.Path);
            }

            File.Move(tempPath, targetPath);
            _libraryMonitor.ReportFileSystemChanged(issue.Path);
            _libraryMonitor.ReportFileSystemChanged(targetPath);
            _logger.LogInformation("Transcode fix: {Action}", actionText);
            return new FixResult
            {
                Success = true,
                Message = actionText,
                BytesFreed = originalSize - newSize,
                RecyclePath = recyclePath
            };
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private string? GetHardwareEncoder(string preferredCodec)
    {
        var accel = _serverConfig.GetConfiguration<EncodingOptions>("encoding").HardwareAccelerationType.ToString().ToLowerInvariant();
        var suffix = accel switch
        {
            "amf" => "_amf",
            "nvenc" => "_nvenc",
            "qsv" => "_qsv",
            "videotoolbox" => "_videotoolbox",
            // vaapi needs device/hwupload plumbing; software fallback handles those setups for now.
            _ => null
        };
        if (suffix is null)
        {
            return null;
        }

        var codec = preferredCodec.ToLowerInvariant() switch
        {
            "hevc" or "h265" => "hevc",
            "h264" => "h264",
            "av1" => "av1",
            _ => "hevc"
        };
        return codec + suffix;
    }

    private static List<string> BuildArgs(
        string inputPath,
        string tempPath,
        FfprobeData probe,
        FfprobeStreamInfo video,
        PluginConfiguration config,
        bool needsDownscale,
        string targetContainer,
        string? hardwareEncoder)
    {
        var encoder = hardwareEncoder ?? config.PreferredCodec.ToLowerInvariant() switch
        {
            "hevc" or "h265" => "libx265",
            "h264" => "libx264",
            "av1" => "libsvtav1",
            _ => "libx265"
        };

        var args = new List<string> { "-i", inputPath, "-map", "0:" + video.Index.ToString(CultureInfo.InvariantCulture) };

        var audioStreams = probe.Streams!.Where(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase)).ToList();
        var keptAudio = config.AllowedAudioLanguages.Length > 0
            ? audioStreams.Where(s => LanguageHelper.IsAllowed(s.Language, config.AllowedAudioLanguages)).ToList()
            : audioStreams;
        if (keptAudio.Count == 0)
        {
            keptAudio = audioStreams;
        }

        foreach (var stream in keptAudio)
        {
            args.Add("-map");
            args.Add("0:" + stream.Index.ToString(CultureInfo.InvariantCulture));
        }

        // Subtitles: copied for MKV; MP4 subtitle support is too patchy for a blind copy, so they are left out there.
        if (targetContainer == "mkv")
        {
            var subs = probe.Streams!.Where(s => string.Equals(s.CodecType, "subtitle", StringComparison.OrdinalIgnoreCase)).ToList();
            var keptSubs = config.AllowedSubtitleLanguages.Length > 0
                ? subs.Where(s => LanguageHelper.IsAllowed(s.Language, config.AllowedSubtitleLanguages)).ToList()
                : subs;
            foreach (var stream in keptSubs)
            {
                args.Add("-map");
                args.Add("0:" + stream.Index.ToString(CultureInfo.InvariantCulture));
            }

            args.AddRange(["-c:s", "copy"]);
        }

        args.AddRange(["-c:v", encoder]);
        if (hardwareEncoder is not null)
        {
            // Hardware encoders don't support CRF; target the configured bitrate ceiling scaled to the output resolution,
            // but never spend more bits per pixel than the source did (a downscale reduces the needed bitrate too).
            var height = video.Height ?? config.MaxResolutionHeight;
            var targetHeight = Math.Min(height, config.MaxResolutionHeight);
            var pixels = (double)(video.Width ?? 0) * height;
            var scale = height > 0 ? (double)targetHeight / height : 1;
            var targetPixels = pixels > 0 ? pixels * scale * scale : 1920.0 * 1080.0;
            var targetBits = (long)(config.MaxBitrateMbpsAt1080p * 1_000_000 * (targetPixels / (1920.0 * 1080.0)));
            var sourceBits = long.TryParse(video.BitRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vb) && vb > 0
                ? vb
                : long.TryParse(probe.Format?.BitRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fb) && fb > 0 ? fb : 0;
            if (sourceBits > 0)
            {
                targetBits = Math.Min(targetBits, (long)(sourceBits * scale * scale));
            }

            targetBits = Math.Max(targetBits, 500_000);
            args.AddRange([
                "-b:v", targetBits.ToString(CultureInfo.InvariantCulture),
                "-maxrate", (targetBits * 3 / 2).ToString(CultureInfo.InvariantCulture),
                "-bufsize", (targetBits * 2).ToString(CultureInfo.InvariantCulture)
            ]);
            if (encoder.StartsWith("h264", StringComparison.Ordinal))
            {
                // h264 hardware encoders are 8-bit; normalize input so 10-bit sources don't abort the encoder.
                args.AddRange(["-pix_fmt", "yuv420p"]);
            }
        }
        else if (encoder == "libsvtav1")
        {
            args.AddRange(["-crf", "30", "-preset", "8"]);
        }
        else
        {
            args.AddRange(["-crf", "23", "-preset", "medium"]);
        }

        if (needsDownscale)
        {
            args.AddRange(["-vf", "scale=-2:" + config.MaxResolutionHeight.ToString(CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-c:a", "copy", "-map_chapters", "0", tempPath]);
        return args;
    }

    private static string Truncate(string text) => text.Length > 300 ? text[..300] : text;
}
