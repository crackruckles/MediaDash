using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
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
/// Removes unwanted audio and subtitle tracks by lossless remux (<c>-c copy</c>).
/// Track lists are recomputed from a fresh probe at fix time — never trusted from stale scan data —
/// and the remux never drops the last audio track (safety invariant #2).
/// </summary>
public sealed class TrackFixer : IFixer
{
    private static readonly TimeSpan RemuxTimeout = TimeSpan.FromMinutes(30);

    private readonly FfprobeService _ffprobe;
    private readonly FfmpegExecutor _ffmpeg;
    private readonly OutputVerifier _verifier;
    private readonly LibraryGuard _guard;
    private readonly RecycleBin _recycleBin;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILogger<TrackFixer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrackFixer"/> class.
    /// </summary>
    /// <param name="ffprobe">The probe service.</param>
    /// <param name="ffmpeg">The ffmpeg executor.</param>
    /// <param name="verifier">The output verifier.</param>
    /// <param name="guard">The library path guard.</param>
    /// <param name="recycleBin">The recycle bin.</param>
    /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
    /// <param name="logger">The logger.</param>
    public TrackFixer(
        FfprobeService ffprobe,
        FfmpegExecutor ffmpeg,
        OutputVerifier verifier,
        LibraryGuard guard,
        RecycleBin recycleBin,
        ILibraryMonitor libraryMonitor,
        ILogger<TrackFixer> logger)
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
    public bool CanFix(IssueType type) => type is IssueType.AudioLanguage or IssueType.SubtitleLanguage;

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
        if (probe?.Streams is null || probe.Error is not null)
        {
            return FixResult.Fail("The file could not be analyzed; it may be broken.");
        }

        var removeIndexes = ComputeRemovableIndexes(probe, issue.Type, config);
        var externalFiles = issue.Type == IssueType.SubtitleLanguage ? GetExternalFiles(issue.DetailsJson) : [];

        if (removeIndexes.Count == 0 && externalFiles.Count == 0)
        {
            return FixResult.Fail("Nothing to remove any more — the file may have changed since the scan. Re-scan to refresh.");
        }

        var originalSize = new FileInfo(issue.Path).Length;
        var disposal = config.GetDisposal(issue.Type);
        var actionText = BuildActionText(issue, removeIndexes.Count, externalFiles.Count, disposal);

        if (config.DryRun)
        {
            return FixResult.DryRun(actionText, issue.SizeSavings);
        }

        string? recyclePath = null;
        long freed = 0;

        if (removeIndexes.Count > 0)
        {
            var tempPath = issue.Path + ".mediadash.tmp" + Path.GetExtension(issue.Path);
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(issue.Path))!);
            if (drive.AvailableFreeSpace < originalSize * 2)
            {
                return FixResult.Fail("Not enough free disk space to safely rebuild the file (needs about twice the file size).");
            }

            var args = new List<string> { "-i", issue.Path, "-map", "0" };
            foreach (var index in removeIndexes)
            {
                args.Add("-map");
                args.Add(string.Format(CultureInfo.InvariantCulture, "-0:{0}", index));
            }

            args.AddRange(["-c", "copy", tempPath]);

            try
            {
                var error = await _ffmpeg.RunAsync(args, RemuxTimeout, cancellationToken).ConfigureAwait(false);
                if (error is not null)
                {
                    return FixResult.Fail("Rebuilding the file failed; the original is untouched. Details: " + Truncate(error));
                }

                var verifyError = await _verifier.VerifyAsync(probe, tempPath, cancellationToken).ConfigureAwait(false);
                if (verifyError is not null)
                {
                    return FixResult.Fail("The rebuilt file failed verification; the original is untouched. Details: " + verifyError);
                }

                if (disposal == DisposalMethod.RecycleBin)
                {
                    recyclePath = _recycleBin.MoveToBin(issue.Path);
                }
                else
                {
                    File.Delete(issue.Path);
                }

                File.Move(tempPath, issue.Path);
                freed += originalSize - new FileInfo(issue.Path).Length;
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        foreach (var externalFile in externalFiles)
        {
            if (!File.Exists(externalFile) || !_guard.IsInsideLibrary(externalFile))
            {
                continue;
            }

            freed += new FileInfo(externalFile).Length;
            if (disposal == DisposalMethod.RecycleBin)
            {
                _recycleBin.MoveToBin(externalFile);
            }
            else
            {
                File.Delete(externalFile);
            }

            _libraryMonitor.ReportFileSystemChanged(externalFile);
        }

        _libraryMonitor.ReportFileSystemChanged(issue.Path);
        _logger.LogInformation("Track fix: {Action}", actionText);
        return new FixResult
        {
            Success = true,
            Message = actionText,
            BytesFreed = Math.Max(0, freed),
            RecyclePath = recyclePath
        };
    }

    private static List<int> ComputeRemovableIndexes(FfprobeData probe, IssueType type, PluginConfiguration config)
    {
        if (type == IssueType.AudioLanguage)
        {
            var audio = probe.Streams!.Where(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase)).ToList();
            var keep = audio.Where(t => LanguageHelper.IsAllowed(t.Language, config.AllowedAudioLanguages)).ToList();
            if (audio.Count <= 1 || keep.Count == 0)
            {
                // Safety invariant: never remove the last audio track or all allowed tracks.
                return [];
            }

            return audio.Where(t => !LanguageHelper.IsAllowed(t.Language, config.AllowedAudioLanguages)).Select(t => t.Index).ToList();
        }

        return probe.Streams!
            .Where(s => string.Equals(s.CodecType, "subtitle", StringComparison.OrdinalIgnoreCase)
                && !LanguageHelper.IsAllowed(s.Language, config.AllowedSubtitleLanguages))
            .Select(s => s.Index)
            .ToList();
    }

    private static List<string> GetExternalFiles(string detailsJson)
    {
        try
        {
            using var details = JsonDocument.Parse(detailsJson);
            if (details.RootElement.TryGetProperty("externalFiles", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                return files.EnumerateArray().Select(f => f.GetString()).Where(f => !string.IsNullOrEmpty(f)).Select(f => f!).ToList();
            }
        }
        catch (JsonException)
        {
        }

        return [];
    }

    private static string BuildActionText(Issue issue, int embeddedCount, int externalCount, DisposalMethod disposal)
    {
        var what = issue.Type == IssueType.AudioLanguage
            ? string.Format(CultureInfo.InvariantCulture, "removed {0} audio track(s)", embeddedCount)
            : string.Format(CultureInfo.InvariantCulture, "removed {0} subtitle track(s) and {1} subtitle file(s)", embeddedCount, externalCount);
        var keep = disposal == DisposalMethod.RecycleBin ? "original kept in recycle bin" : "original permanently deleted";
        return string.Format(CultureInfo.InvariantCulture, "{0} from {1} ({2})", what, Path.GetFileName(issue.Path), keep);
    }

    private static string Truncate(string text) => text.Length > 300 ? text[..300] : text;
}
