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

    // Retry ceiling for slow disks / big files: if the 30-min first pass hits the timeout, run
    // one more attempt with a 5-hour cap before giving up. Real bugs still fail fast (bad codec,
    // permissions, disk full) since those don't trigger the timeout branch.
    private static readonly TimeSpan RemuxRetryTimeout = TimeSpan.FromHours(5);

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
            var ext = Path.GetExtension(issue.Path).TrimStart('.');
            var tempPath = TranscodeFixer.SidecarPath(issue.Path, "tmp", ext);
            var swapPath = TranscodeFixer.SidecarPath(issue.Path, "new", string.Empty);
            var drive = RecycleBin.FindDriveForPath(issue.Path);
            const long safetyMarginBytes = 500L * 1024 * 1024;
            if (drive is not null && drive.AvailableFreeSpace < originalSize + safetyMarginBytes)
            {
                return FixResult.Fail("Not enough free disk space to rebuild this file (needs its own size plus about 500 MB free).");
            }

            var args = new List<string> { "-i", issue.Path, "-map", "0" };
            foreach (var index in removeIndexes)
            {
                args.Add("-map");
                args.Add(string.Format(CultureInfo.InvariantCulture, "-0:{0}", index));
            }

            args.AddRange(["-c", "copy", tempPath]);

            var originalDisposed = false;
            var swapCompleted = false;
            try
            {
                var error = await _ffmpeg.RunAsync(args, RemuxTimeout, cancellationToken).ConfigureAwait(false);
                if (error is not null && FfmpegExecutor.IsTimeoutError(error))
                {
                    _logger.LogInformation("Track remux hit the {InitialTimeout} limit on '{Path}'; retrying with {RetryTimeout}.", RemuxTimeout, issue.Path, RemuxRetryTimeout);
                    Api.Diagnostics.Record(
                        "Track.RemuxRetry",
                        "First remux pass on '" + issue.Path + "' hit the " + RemuxTimeout + " limit — retrying with an extended " + RemuxRetryTimeout + " window for large files on slow machines.");
                    error = await _ffmpeg.RunAsync(args, RemuxRetryTimeout, cancellationToken).ConfigureAwait(false);
                }

                if (error is not null)
                {
                    return FixResult.Fail("Rebuilding the file failed; the original is untouched. Details: " + TranscodeFixer.Truncate(error));
                }

                var verifyError = await _verifier.VerifyAsync(probe, tempPath, cancellationToken).ConfigureAwait(false);
                if (verifyError is not null)
                {
                    return FixResult.Fail("The rebuilt file failed verification; the original is untouched. Details: " + verifyError);
                }

                // Move verified rebuild to a sidecar first, then dispose the original, then rename.
                // Old order deleted the original before the move — a throw in File.Move plus the finally's
                // temp cleanup left the user with nothing.
                if (File.Exists(swapPath))
                {
                    File.Delete(swapPath);
                }

                File.Move(tempPath, swapPath);

                if (disposal == DisposalMethod.RecycleBin)
                {
                    recyclePath = _recycleBin.MoveToBin(issue.Path);
                }
                else if (File.Exists(issue.Path))
                {
                    File.Delete(issue.Path);
                }

                originalDisposed = true;
                File.Move(swapPath, issue.Path);
                swapCompleted = true;
                freed += originalSize - new FileInfo(issue.Path).Length;
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (IOException ex)
                    {
                        _logger.LogWarning(ex, "Could not delete temp remux file {Path}", tempPath);
                    }
                }

                if (!swapCompleted && File.Exists(swapPath))
                {
                    if (originalDisposed)
                    {
                        Api.Diagnostics.Record(
                            "Track.SwapAborted",
                            "Track remux of '" + issue.Path + "' completed but the final rename failed. The rebuilt copy is preserved at '" + swapPath + "' — rename it manually to '" + issue.Path + "'. Do NOT delete this file; it is currently your only copy of the content.");
                    }
                    else
                    {
                        try
                        {
                            File.Delete(swapPath);
                        }
                        catch (IOException ex)
                        {
                            _logger.LogWarning(ex, "Could not delete swap sidecar {Path}", swapPath);
                        }
                    }
                }
            }
        }

        foreach (var externalFile in externalFiles)
        {
            // Defence-in-depth: refuse to touch the video path itself. If a stale DetailsJson (written by
            // an older scanner build) lists the video as an external sidecar, we'd otherwise recycle the
            // freshly-remuxed video and credit its size to freed.
            if (IsSelfReferentialSubtitle(externalFile, issue.Path))
            {
                Api.Diagnostics.Record(
                    "Track.ExternalPathCollision",
                    "Refused to recycle '" + externalFile + "' as an external subtitle — path matches the video being fixed. Re-scan to rebuild the issue with the current scanner.");
                continue;
            }

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

    internal static List<int> ComputeRemovableIndexes(FfprobeData probe, IssueType type, PluginConfiguration config)
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

    /// <summary>
    /// True when an "external subtitle" path from the scanner's DetailsJson is actually the video file
    /// being fixed. Jellyfin can report embedded PGS/Bluray tracks with IsExternal=true and Path pointing
    /// at the container itself; without this guard the remuxed video would be recycled and its size
    /// double-counted into freed bytes. Exposed internal for direct unit testing.
    /// </summary>
    /// <param name="externalPath">Path from the scanner's externalFiles list.</param>
    /// <param name="videoPath">The Issue.Path (the video being fixed).</param>
    /// <returns>True when the two paths refer to the same file.</returns>
    internal static bool IsSelfReferentialSubtitle(string externalPath, string videoPath)
    {
        if (string.IsNullOrEmpty(externalPath) || string.IsNullOrEmpty(videoPath))
        {
            return false;
        }

        // Normalise both sides — legacy scanner rows can store relative paths, trailing separators,
        // or Windows 8.3-form paths that would slip past a raw string comparison and let the
        // just-remuxed video get recycled by the fixer.
        try
        {
            var externalFull = Path.GetFullPath(externalPath);
            var videoFull = Path.GetFullPath(videoPath);
            return string.Equals(externalFull, videoFull, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // Fall back to the strict compare on unusable paths — the guard should refuse deletion
            // rather than incorrectly say "not the video".
            return string.Equals(externalPath, videoPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static List<string> GetExternalFilesForTest(string detailsJson) => GetExternalFiles(detailsJson);

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
        // Field report A9: users read the old "original kept in recycle bin" and thought MediaDash
        // was moving their media there for no reason. The truth is that track removal is a remux —
        // ffmpeg writes a new file next to the source, then the original file is swapped out. The
        // "original in recycle bin" is a safety copy of the pre-edit file, not a delete of the
        // media itself. Spell that out so the note reads as "the file you asked me to edit is fine,
        // your pre-edit copy is recoverable" instead of "your media just went to the bin".
        var keep = disposal == DisposalMethod.RecycleBin
            ? "file rebuilt at the same path; the pre-edit copy is in the recycle bin as a safety net"
            : "file rebuilt at the same path; the pre-edit copy was deleted permanently";
        return string.Format(CultureInfo.InvariantCulture, "{0} from {1} — {2}", what, Path.GetFileName(issue.Path), keep);
    }
}
