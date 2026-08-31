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
        return await RunTrackRemuxAsync(issue, probe, removeIndexes, externalFiles, disposal, actionText, originalSize, config, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles the combined case where the SAME file has both an AudioLanguage AND a SubtitleLanguage
    /// issue queued at the same time. Instead of two ffmpeg remuxes back-to-back (each reading the
    /// source in full, each writing an intermediate, each moving the previous copy to the bin), this
    /// path runs ONE remux that drops both categories in one shot. Cuts wall time roughly in half on
    /// TV episodes and by 60–70 % on Blu-ray remuxes.
    /// <para>
    /// Returns a single <see cref="FixResult"/> covering both issues. The caller is responsible for
    /// marking both issues Fixed and recording per-issue history rows (typically one Fix history row
    /// per issue, both pointing at the same bin entry, so both show a Restore button).
    /// </para>
    /// </summary>
    /// <param name="audioIssue">The AudioLanguage issue.</param>
    /// <param name="subtitleIssue">The SubtitleLanguage issue on the same path.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A single result covering both fixes.</returns>
    public async Task<FixResult> FixCombinedAsync(Issue audioIssue, Issue subtitleIssue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (audioIssue.Type != IssueType.AudioLanguage || subtitleIssue.Type != IssueType.SubtitleLanguage)
        {
            return FixResult.Fail("Combined pass requires exactly one AudioLanguage + one SubtitleLanguage issue.");
        }

        if (!string.Equals(audioIssue.Path, subtitleIssue.Path, StringComparison.OrdinalIgnoreCase))
        {
            return FixResult.Fail("Combined pass requires both issues to point at the same file.");
        }

        var config = Plugin.Instance!.Configuration;
        if (!File.Exists(audioIssue.Path))
        {
            return FixResult.Fail("The file no longer exists; re-scan to refresh the list.");
        }

        if (!_guard.IsInsideLibrary(audioIssue.Path))
        {
            return FixResult.Fail("The file is outside your library folders; MediaDash will not touch it.");
        }

        var probe = await _ffprobe.ProbeAsync(audioIssue.Path, cancellationToken).ConfigureAwait(false);
        if (probe?.Streams is null || probe.Error is not null)
        {
            return FixResult.Fail("The file could not be analyzed; it may be broken.");
        }

        // Union of both categories' removeIndexes. Duplicates shouldn't happen (audio and subtitle
        // streams live in disjoint index ranges) but DE-dupe as belt-and-braces.
        var audioRemoves = ComputeRemovableIndexes(probe, IssueType.AudioLanguage, config);
        var subtitleRemoves = ComputeRemovableIndexes(probe, IssueType.SubtitleLanguage, config);
        var removeIndexes = audioRemoves.Concat(subtitleRemoves).Distinct().ToList();
        var externalFiles = GetExternalFiles(subtitleIssue.DetailsJson);

        if (removeIndexes.Count == 0 && externalFiles.Count == 0)
        {
            return FixResult.Fail("Nothing to remove any more — the file may have changed since the scan. Re-scan to refresh.");
        }

        var originalSize = new FileInfo(audioIssue.Path).Length;
        // Both issues' disposals should be the same for a same-file combined pass; if they diverge,
        // prefer the more conservative (RecycleBin over PermanentDelete). The audio disposal drives.
        var audioDisposal = config.GetDisposal(IssueType.AudioLanguage);
        var subtitleDisposal = config.GetDisposal(IssueType.SubtitleLanguage);
        var disposal = audioDisposal == DisposalMethod.RecycleBin || subtitleDisposal == DisposalMethod.RecycleBin
            ? DisposalMethod.RecycleBin
            : audioDisposal;

        var actionText = string.Format(
            CultureInfo.InvariantCulture,
            "Removed {0} unwanted audio track{1} and {2} unwanted subtitle track{3} in one pass{4}{5}.",
            audioRemoves.Count,
            audioRemoves.Count == 1 ? string.Empty : "s",
            subtitleRemoves.Count,
            subtitleRemoves.Count == 1 ? string.Empty : "s",
            externalFiles.Count > 0 ? " + " + externalFiles.Count + " external subtitle sidecar" + (externalFiles.Count == 1 ? string.Empty : "s") : string.Empty,
            disposal == DisposalMethod.RecycleBin ? " (originals recycled)" : " (originals deleted)");
        return await RunTrackRemuxAsync(audioIssue, probe, removeIndexes, externalFiles, disposal, actionText, originalSize, config, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FixResult> RunTrackRemuxAsync(
        Issue issue,
        FfprobeData probe,
        List<int> removeIndexes,
        List<string> externalFiles,
        DisposalMethod disposal,
        string actionText,
        long originalSize,
        PluginConfiguration config,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
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

            // Issue #39: users on multi-language Blu-ray remuxes (Breaking Bad 3xRus / Ger dubs,
            // Transporter .m2ts) hit two related failure modes on -c copy:
            //   - "sample rate not set" / "Could not write header" — the mpegts / .m2ts muxer
            //     refuses to write a header when the input demuxer hasn't populated per-stream
            //     start times.
            //   - Duration mismatch of ~30 s on a 45-min episode — the remux drops leading
            //     negative PTS packets, and the container duration comes out short.
            // -avoid_negative_ts make_zero shifts every packet so t=0 is the first-frame PTS,
            // which lets the muxer write a valid header and stops the duration drift.
            // -fflags +genpts regenerates missing PTS on demux, covering broken source files
            // where individual streams have gaps.
            var args = new List<string> { "-fflags", "+genpts", "-i", issue.Path, "-map", "0" };
            foreach (var index in removeIndexes)
            {
                args.Add("-map");
                args.Add(string.Format(CultureInfo.InvariantCulture, "-0:{0}", index));
            }

            args.AddRange(["-c", "copy", "-avoid_negative_ts", "make_zero", tempPath]);

            var originalDisposed = false;
            var swapCompleted = false;
            // F-202 / issue #31: capture source timestamps BEFORE any ffmpeg / move / recycle
            // touches the file. Jellyfin's Recently Added widget sorts by DateModified — a
            // fixer that bumps it treats every housekeeping rewrite as "newly added" and
            // pollutes the row. Restore the source stamps after the final swap succeeds.
            var srcInfo = new FileInfo(issue.Path);
            var srcCreatedUtc = srcInfo.Exists ? srcInfo.CreationTimeUtc : DateTime.UtcNow;
            var srcModifiedUtc = srcInfo.Exists ? srcInfo.LastWriteTimeUtc : DateTime.UtcNow;
            try
            {
                // First pass suppresses the Ffmpeg.Timeout diagnostic: hitting the 30-min cap on a large
                // Blu-ray remux is expected and the retry usually finishes it. Only the retry's failure
                // (a genuine 5-hour miss) surfaces to the Errors tab. Track.RemuxRetry itself was removed —
                // a happy-path recovery event was noise in Errors, not signal.
                var error = await _ffmpeg.RunAsync(args, RemuxTimeout, cancellationToken, recordDiagnosticOnTimeout: false).ConfigureAwait(false);
                if (error is not null && FfmpegExecutor.IsTimeoutError(error))
                {
                    _logger.LogInformation("Track remux hit the {InitialTimeout} limit on '{Path}'; retrying with {RetryTimeout}.", RemuxTimeout, issue.Path, RemuxRetryTimeout);
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
                // F-202 / issue #31: restore source stamps on the new file. Do this AFTER the
                // Move settles — setting stamps before the move loses them on some filesystems
                // (SetFileInformationByHandle vs rename semantics).
                try
                {
                    File.SetCreationTimeUtc(issue.Path, srcCreatedUtc);
                    File.SetLastWriteTimeUtc(issue.Path, srcModifiedUtc);
                }
                catch (IOException ex)
                {
                    // Non-fatal: users on network shares sometimes can't set file times. Log
                    // the drift so Jellyfin's Recently Added anomaly is traceable, don't fail
                    // the fix — the file is already rebuilt and swapped.
                    _logger.LogInformation("TrackFixer: could not restore source timestamps on '{Path}': {Message}", issue.Path, ex.Message);
                }

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

        var additionalRecycled = new List<RecycledSidecar>();
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
                var sidecarBinPath = _recycleBin.MoveToBin(externalFile);
                // A per-sidecar history row is what makes the Recycle Bin tab's Restore button appear.
                // Without this row the file lives in the bin but shows "no history" — user report class.
                additionalRecycled.Add(new RecycledSidecar
                {
                    OriginalPath = externalFile,
                    RecyclePath = sidecarBinPath,
                    Action = "Recycled external subtitle sidecar during track fix of " + Path.GetFileName(issue.Path) + "."
                });
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
            RecyclePath = recyclePath,
            AdditionalRecycled = additionalRecycled
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
