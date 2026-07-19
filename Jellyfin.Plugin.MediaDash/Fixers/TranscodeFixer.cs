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
using MediaBrowser.Controller.Library;
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
    /// <param name="logger">The logger.</param>
    public TranscodeFixer(
        FfprobeService ffprobe,
        FfmpegExecutor ffmpeg,
        OutputVerifier verifier,
        LibraryGuard guard,
        RecycleBin recycleBin,
        ILibraryMonitor libraryMonitor,
        ILogger<TranscodeFixer> logger)
    {
        _ffprobe = ffprobe;
        _ffmpeg = ffmpeg;
        _verifier = verifier;
        _guard = guard;
        _recycleBin = recycleBin;
        _libraryMonitor = libraryMonitor;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.Quality;

    /// <inheritdoc />
    public async Task<FixResult> FixAsync(Issue issue, CancellationToken cancellationToken)
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

        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(issue.Path))!);
        if (drive.AvailableFreeSpace < originalSize * 2)
        {
            return FixResult.Fail("Not enough free disk space to safely re-encode (needs about twice the file size).");
        }

        var tempPath = issue.Path + ".mediadash.tmp." + targetContainer;
        try
        {
            var args = BuildArgs(issue.Path, tempPath, probe, video, config, needsDownscale, targetContainer);
            var error = await _ffmpeg.RunAsync(args, TranscodeTimeout, cancellationToken).ConfigureAwait(false);
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

    private static List<string> BuildArgs(
        string inputPath,
        string tempPath,
        FfprobeData probe,
        FfprobeStreamInfo video,
        PluginConfiguration config,
        bool needsDownscale,
        string targetContainer)
    {
        // ponytail: software encoders only; hardware encoder selection from Jellyfin's encoding options can come later
        var encoder = config.PreferredCodec.ToLowerInvariant() switch
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
        if (encoder == "libsvtav1")
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
