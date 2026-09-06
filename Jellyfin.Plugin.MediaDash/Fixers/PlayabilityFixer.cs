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
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Removes files that cannot be played — but only after re-verifying at fix time that the file is still broken.
/// A file that probes and decodes cleanly is never removed, whatever the scan said.
/// </summary>
public sealed class PlayabilityFixer : IFixer
{
    private static readonly TimeSpan RepairRungTimeout = TimeSpan.FromHours(6);

    private readonly FfprobeService _ffprobe;
    private readonly FfmpegExecutor _ffmpeg;
    private readonly OutputVerifier _verifier;
    private readonly LibraryGuard _guard;
    private readonly RecycleBin _recycleBin;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILogger<PlayabilityFixer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayabilityFixer"/> class.
    /// </summary>
    /// <param name="ffprobe">The probe service.</param>
    /// <param name="ffmpeg">The ffmpeg executor (repair ladder).</param>
    /// <param name="verifier">The output verifier (repair ladder).</param>
    /// <param name="guard">The library path guard.</param>
    /// <param name="recycleBin">The recycle bin.</param>
    /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
    /// <param name="logger">The logger.</param>
    public PlayabilityFixer(
        FfprobeService ffprobe,
        FfmpegExecutor ffmpeg,
        OutputVerifier verifier,
        LibraryGuard guard,
        RecycleBin recycleBin,
        ILibraryMonitor libraryMonitor,
        ILogger<PlayabilityFixer> logger)
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
    public bool CanFix(IssueType type) => type == IssueType.Playability;

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

        var stillBroken = await IsStillBrokenAsync(issue, cancellationToken).ConfigureAwait(false);
        if (!stillBroken)
        {
            return FixResult.Fail("The file plays fine now — nothing was removed. Re-scan to clear this issue.");
        }

        // Repair ladder — try to salvage before deleting. Skips out to today's delete path on
        // pre-flight fail, all-rungs-disabled, or all-rungs-fail. Dry-run also skips: there's no
        // way to preview a "the file WOULD have been repaired" outcome accurately.
        // ponytail: double-probe with IsStillBrokenAsync; refactor to share the probe if a
        // real profile shows it matters. Two probes on a stat'd file is cheap next to the fix work.
        if (!config.DryRun)
        {
            var repairProbe = await _ffprobe.ProbeAsync(issue.Path, cancellationToken).ConfigureAwait(false);
            if (repairProbe is not null)
            {
                var repair = await TryRepairAsync(issue, repairProbe, progress, cancellationToken).ConfigureAwait(false);
                if (repair is not null)
                {
                    return repair;
                }
            }
        }

        var size = new FileInfo(issue.Path).Length;
        var disposal = config.GetDisposal(IssueType.Playability);
        var actionText = string.Format(
            CultureInfo.InvariantCulture,
            "removed unplayable file {0} ({1})",
            Path.GetFileName(issue.Path),
            disposal == DisposalMethod.RecycleBin ? "kept in recycle bin" : "permanently deleted");

        if (config.DryRun)
        {
            return FixResult.DryRun(actionText, size);
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

        _libraryMonitor.ReportFileSystemChanged(issue.Path);
        _logger.LogInformation("Playability fix: {Action}", actionText);
        return new FixResult
        {
            Success = true,
            Message = actionText,
            BytesFreed = size,
            RecyclePath = recyclePath
        };
    }

    /// <summary>
    /// Attempts to repair a broken file. Returns a Success FixResult if any rung produced a
    /// verified replacement; null when the caller should fall through to today's delete path.
    /// Each rung is gated by its own config toggle; a rung that produces no verified output
    /// hands off to the next.
    /// </summary>
    private async Task<FixResult?> TryRepairAsync(
        Issue issue,
        FfprobeData originalProbe,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;

        // All four disabled = feature turned off. Skip pre-flight and just fall through.
        if (!config.RepairAttemptRemux && !config.RepairAttemptDropStreams
            && !config.RepairAttemptContainerCoerce && !config.RepairAttemptReencode)
        {
            return null;
        }

        // Pre-flight: enough free space on target volume for repair.
        // 3× source when re-encode is on (temp + safety), 2× otherwise (temp swap only).
        if (!HasFreeSpace(issue.Path, config.RepairAttemptReencode ? 3 : 2))
        {
            _logger.LogInformation("Playability repair skipped for {Path}: insufficient free disk.", issue.Path);
            return null;
        }

        string? outPath;
        if (config.RepairAttemptRemux && (outPath = await TryRung1Async(issue, originalProbe, cancellationToken).ConfigureAwait(false)) is not null)
        {
            return await SwapRepairedAsync(issue, outPath, "quick remux", extensionChanged: false, cancellationToken).ConfigureAwait(false);
        }

        if (config.RepairAttemptDropStreams && (outPath = await TryRung2Async(issue, originalProbe, cancellationToken).ConfigureAwait(false)) is not null)
        {
            return await SwapRepairedAsync(issue, outPath, "dropped broken streams", extensionChanged: false, cancellationToken).ConfigureAwait(false);
        }

        if (config.RepairAttemptContainerCoerce && (outPath = await TryRung3Async(issue, originalProbe, cancellationToken).ConfigureAwait(false)) is not null)
        {
            return await SwapRepairedAsync(issue, outPath, "container changed to .mkv (Jellyfin watch history reset)", extensionChanged: true, cancellationToken).ConfigureAwait(false);
        }

        if (config.RepairAttemptReencode && (outPath = await TryRung4Async(issue, originalProbe, progress, cancellationToken).ConfigureAwait(false)) is not null)
        {
            return await SwapRepairedAsync(issue, outPath, "video re-encoded", extensionChanged: true, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    // Rung 1: error-tolerant remux, same container. Recovers bad index, wrong duration,
    // missing moov atom, EOF truncation. Cheapest rung — always attempted first when enabled.
    // ponytail: single remux pass; add multi-attempt with -analyzeduration hints only if
    // real fixtures show recoverable files that this misses.
    private async Task<string?> TryRung1Async(Issue issue, FfprobeData originalProbe, CancellationToken cancellationToken)
    {
        var ext = Path.GetExtension(issue.Path).TrimStart('.');
        if (string.IsNullOrEmpty(ext))
        {
            return null;
        }

        var tempPath = TranscodeFixer.SidecarPath(issue.Path, "repair.tmp1", ext);
        var args = new List<string>
        {
            "-err_detect", "ignore_err",
            "-fflags", "+genpts+igndts",
            "-i", issue.Path,
            "-map", "0",
            "-c", "copy",
            "-avoid_negative_ts", "make_zero",
            tempPath
        };

        var error = await _ffmpeg.RunAsync(args, RepairRungTimeout, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            _logger.LogDebug("Rung 1 remux failed for {Path}: {Error}", issue.Path, TranscodeFixer.Truncate(error));
            TryDelete(tempPath);
            return null;
        }

        var verifyError = await _verifier.VerifyAsync(originalProbe, issue.Path, tempPath, cancellationToken).ConfigureAwait(false);
        if (verifyError is not null)
        {
            _logger.LogDebug("Rung 1 verify failed for {Path}: {Error}", issue.Path, verifyError);
            TryDelete(tempPath);
            return null;
        }

        return tempPath;
    }

    // Rung 2: per-stream decode check via ffprobe, drop the streams that error.
    // Safety hard-stop: refuse if the drop would leave 0 audio or 0 video streams.
    private async Task<string?> TryRung2Async(Issue issue, FfprobeData originalProbe, CancellationToken cancellationToken)
    {
        if (originalProbe.Streams is null || originalProbe.Streams.Count == 0)
        {
            return null;
        }

        var brokenIndexes = new List<int>();
        foreach (var stream in originalProbe.Streams)
        {
            var decodeError = await _ffprobe.DecodeStreamAsync(issue.Path, stream.Index, cancellationToken).ConfigureAwait(false);
            if (decodeError is not null)
            {
                brokenIndexes.Add(stream.Index);
            }
        }

        if (brokenIndexes.Count == 0)
        {
            return null;
        }

        // Safety: never leave zero video or zero audio streams. Refuse the whole rung; caller falls to rung 3.
        var survivingVideo = originalProbe.Streams.Any(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase) && !brokenIndexes.Contains(s.Index));
        var survivingAudio = originalProbe.Streams.Any(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase) && !brokenIndexes.Contains(s.Index));
        if (!survivingVideo || !survivingAudio)
        {
            _logger.LogDebug("Rung 2 refused for {Path}: dropping would leave 0 video or 0 audio streams.", issue.Path);
            return null;
        }

        var ext = Path.GetExtension(issue.Path).TrimStart('.');
        if (string.IsNullOrEmpty(ext))
        {
            return null;
        }

        var tempPath = TranscodeFixer.SidecarPath(issue.Path, "repair.tmp2", ext);
        var args = new List<string>
        {
            "-err_detect", "ignore_err",
            "-i", issue.Path,
            "-map", "0",
        };
        foreach (var idx in brokenIndexes)
        {
            args.Add("-map");
            args.Add("-0:" + idx.ToString(CultureInfo.InvariantCulture));
        }

        args.AddRange(["-c", "copy", tempPath]);

        var error = await _ffmpeg.RunAsync(args, RepairRungTimeout, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            _logger.LogDebug("Rung 2 remux failed for {Path}: {Error}", issue.Path, TranscodeFixer.Truncate(error));
            TryDelete(tempPath);
            return null;
        }

        var verifyError = await _verifier.VerifyAsync(originalProbe, issue.Path, tempPath, cancellationToken).ConfigureAwait(false);
        if (verifyError is not null)
        {
            _logger.LogDebug("Rung 2 verify failed for {Path}: {Error}", issue.Path, verifyError);
            TryDelete(tempPath);
            return null;
        }

        return tempPath;
    }

    // Rung 3: repack into MKV (universal container). Changes the file extension —
    // Jellyfin re-indexes the file and watch history for the title resets.
    // SwapRepairedAsync uses extensionChanged: true so the output lands at
    // Path.ChangeExtension(original, ".mkv") and both paths are reported to the library monitor.
    private async Task<string?> TryRung3Async(Issue issue, FfprobeData originalProbe, CancellationToken cancellationToken)
    {
        // Same-container coerce is meaningless — that's rung 1's job.
        if (string.Equals(Path.GetExtension(issue.Path), ".mkv", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var tempPath = TranscodeFixer.SidecarPath(issue.Path, "repair.tmp3", "mkv");
        var args = new List<string>
        {
            "-err_detect", "ignore_err",
            "-fflags", "+genpts+igndts",
            "-i", issue.Path,
            "-map", "0",
            "-c", "copy",
            "-avoid_negative_ts", "make_zero",
            tempPath
        };

        var error = await _ffmpeg.RunAsync(args, RepairRungTimeout, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            _logger.LogDebug("Rung 3 mkv-coerce failed for {Path}: {Error}", issue.Path, TranscodeFixer.Truncate(error));
            TryDelete(tempPath);
            return null;
        }

        var verifyError = await _verifier.VerifyAsync(originalProbe, issue.Path, tempPath, cancellationToken).ConfigureAwait(false);
        if (verifyError is not null)
        {
            _logger.LogDebug("Rung 3 verify failed for {Path}: {Error}", issue.Path, verifyError);
            TryDelete(tempPath);
            return null;
        }

        return tempPath;
    }

    // Rung 4: last-resort full re-encode into MKV. Slow (hours per file for 4K sources) and
    // only runs on the background scheduled scan. Uses conservative h264 + AAC targets so the
    // output plays on the widest range of clients; user isn't asked which codec because if
    // we're here the source is broken enough that survival trumps optimality.
    // ponytail: h264/aac hardcoded; add a config knob only if a user files a preference (i-bl-05).
    private async Task<string?> TryRung4Async(Issue issue, FfprobeData originalProbe, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var tempPath = TranscodeFixer.SidecarPath(issue.Path, "repair.tmp4", "mkv");
        var args = new List<string>
        {
            "-err_detect", "ignore_err",
            "-fflags", "+genpts+igndts",
            "-i", issue.Path,
            "-map", "0:v:0?",
            "-map", "0:a?",
            "-map", "0:s?",
            "-c:v", "libx264",
            "-preset", "medium",
            "-crf", "20",
            "-c:a", "aac",
            "-b:a", "192k",
            "-c:s", "copy",
            "-map_chapters", "0",
            tempPath
        };

        double duration = 0;
        if (double.TryParse(originalProbe.Format?.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
        {
            duration = d;
        }

        var error = await _ffmpeg.RunAsync(args, RepairRungTimeout, cancellationToken, progress, duration).ConfigureAwait(false);
        if (error is not null)
        {
            _logger.LogInformation("Rung 4 re-encode failed for {Path}: {Error}", issue.Path, TranscodeFixer.Truncate(error));
            TryDelete(tempPath);
            return null;
        }

        var verifyError = await _verifier.VerifyAsync(originalProbe, issue.Path, tempPath, cancellationToken).ConfigureAwait(false);
        if (verifyError is not null)
        {
            _logger.LogInformation("Rung 4 verify failed for {Path}: {Error}", issue.Path, verifyError);
            TryDelete(tempPath);
            return null;
        }

        return tempPath;
    }

    // Exposed internal for direct unit-testing without spinning up a full fixer.
    internal static bool HasFreeSpace(string path, int multiplier)
    {
        try
        {
            var fi = new FileInfo(path);
            var drive = new DriveInfo(Path.GetPathRoot(fi.FullName) ?? "/");
            return drive.AvailableFreeSpace >= fi.Length * multiplier;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task<FixResult> SwapRepairedAsync(
        Issue issue,
        string repairedTempPath,
        string rungLabel,
        bool extensionChanged,
        CancellationToken cancellationToken)
    {
        var finalPath = extensionChanged
            ? Path.ChangeExtension(issue.Path, ".mkv")
            : issue.Path;

        var recyclePath = _recycleBin.MoveToBin(issue.Path);
        File.Move(repairedTempPath, finalPath, overwrite: false);
        _libraryMonitor.ReportFileSystemChanged(issue.Path);
        if (extensionChanged)
        {
            _libraryMonitor.ReportFileSystemChanged(finalPath);
        }

        var message = $"repaired {Path.GetFileName(issue.Path)} ({rungLabel})";
        _logger.LogInformation("Playability repair: {Message}", message);
        await Task.CompletedTask.ConfigureAwait(false);
        return new FixResult
        {
            Success = true,
            Message = message,
            BytesFreed = 0,
            RecyclePath = recyclePath
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; leftover sidecars get swept next FfmpegExecutor.RunAsync call.
        }
    }

    private async Task<bool> IsStillBrokenAsync(Issue issue, CancellationToken cancellationToken)
    {
        // Re-verify only the *specific* condition the scanner flagged. Older logic tested "no video
        // stream" for every issue, which recycled healthy audio-kind files flagged with reason "no-audio".
        var reason = TryGetReason(issue.DetailsJson);

        var probe = await _ffprobe.ProbeAsync(issue.Path, cancellationToken).ConfigureAwait(false);
        if (probe is null || probe.Error is not null || probe.Streams is null || probe.Streams.Count == 0)
        {
            return true;
        }

        switch (reason)
        {
            case "no-audio":
                return !probe.Streams.Any(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase));

            case "no-video":
                return !probe.Streams.Any(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase));

            case "no-duration":
                return !TryGetDuration(probe, out var d) || d <= 0;

            case "size-truncated":
                // A truncated file usually still reports a positive header duration — that's what
                // made it truncated in the first place. Re-run the scanner's bitrate × duration vs
                // file-size heuristic; only if the file now matches its advertised size (or its
                // bitrate/duration are unknown) does the reason no longer hold.
                if (!TryGetDuration(probe, out var truncDuration) || truncDuration <= 0)
                {
                    return true;
                }

                if (!long.TryParse(probe.Format?.BitRate, System.Globalization.NumberStyles.Integer, CultureInfo.InvariantCulture, out var bitrate) || bitrate <= 0)
                {
                    // Without bitrate we can't recompute — trust the original detection.
                    return true;
                }

                try
                {
                    var actualSize = new FileInfo(issue.Path).Length;
                    var expectedBytes = bitrate / 8.0 * truncDuration;
                    return actualSize < expectedBytes * 0.6;
                }
                catch (IOException)
                {
                    return true;
                }

            case "decode-error":
                if (!TryGetDuration(probe, out var duration) || duration <= 0)
                {
                    return true;
                }

                var decodeError = await _ffprobe.DecodeCheckAsync(issue.Path, duration, cancellationToken).ConfigureAwait(false);
                return decodeError is not null;

            case "unreadable":
            case "book-or-comic-corrupt":
                // Reached only if the probe now returns streams — the file is readable, no longer broken.
                return false;

            default:
                // Legacy issues without a persisted reason: fall back to the original strict check
                // (any missing video stream or missing duration counts as broken).
                if (!probe.Streams.Any(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                return !TryGetDuration(probe, out var legacyDuration) || legacyDuration <= 0;
        }
    }

    private static string? TryGetReason(string detailsJson)
    {
        try
        {
            using var details = JsonDocument.Parse(detailsJson);
            return details.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetDuration(FfprobeData probe, out double duration)
    {
        duration = 0;
        var raw = probe.Format?.Duration ?? probe.Streams?.FirstOrDefault(s => s.Duration is not null)?.Duration;
        return raw is not null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
    }
}
