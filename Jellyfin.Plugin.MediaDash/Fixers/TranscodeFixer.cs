using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
    private readonly ILibraryManager _libraryManager;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly MediaDashDb _db;
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
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface, used to resolve the item behind an issue for post-encode renaming.</param>
    /// <param name="serverConfig">Instance of the <see cref="IServerConfigurationManager"/> interface, used to read the server's hardware acceleration type.</param>
    /// <param name="db">The plugin database, used to re-point sibling issues after a container change or canonical rename.</param>
    /// <param name="logger">The logger.</param>
    public TranscodeFixer(
        FfprobeService ffprobe,
        FfmpegExecutor ffmpeg,
        OutputVerifier verifier,
        LibraryGuard guard,
        RecycleBin recycleBin,
        ILibraryMonitor libraryMonitor,
        ILibraryManager libraryManager,
        IServerConfigurationManager serverConfig,
        MediaDashDb db,
        ILogger<TranscodeFixer> logger)
    {
        _ffprobe = ffprobe;
        _ffmpeg = ffmpeg;
        _verifier = verifier;
        _guard = guard;
        _recycleBin = recycleBin;
        _libraryMonitor = libraryMonitor;
        _libraryManager = libraryManager;
        _serverConfig = serverConfig;
        _db = db;
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

        var tempPath = SidecarPath(issue.Path, "tmp", targetContainer);
        // The swap sidecar sits next to the eventual output so it lands on the same volume, which
        // makes the final rename atomic. Distinct from tempPath so the finally cleanup below never
        // fights the swap-preservation branch.
        var swapPath = SidecarPath(targetPath, "new", string.Empty);
        var originalDisposed = false;
        var swapCompleted = false;
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

            // Move the verified encode to a sidecar next to the target BEFORE touching the original.
            // Old order (delete original -> move temp -> ...) lost the file if the move ever threw,
            // because the finally cleanup then deleted the temp too. Under this order any pre-dispose
            // failure leaves the original intact; any post-dispose failure preserves the encoded copy.
            if (File.Exists(swapPath))
            {
                File.Delete(swapPath);
            }

            File.Move(tempPath, swapPath);

            string? recyclePath = null;
            if (disposal == DisposalMethod.RecycleBin)
            {
                recyclePath = _recycleBin.MoveToBin(issue.Path);
            }
            else if (File.Exists(issue.Path))
            {
                File.Delete(issue.Path);
            }

            originalDisposed = true;
            File.Move(swapPath, targetPath);
            swapCompleted = true;
            var finalPath = targetPath;
            if (config.RenameAfterTranscode)
            {
                // Rename is best-effort: any failure (missing metadata, collision, permission) keeps the
                // re-encoded file under its original basename, which is a safe fallback.
                var renamed = TryRenameToCanonical(targetPath, issue.ItemId, video.Height ?? 0, targetContainer);
                if (renamed is not null)
                {
                    finalPath = renamed;
                    actionText += " (renamed to " + Path.GetFileName(renamed) + ")";
                }
            }

            _libraryMonitor.ReportFileSystemChanged(issue.Path);
            _libraryMonitor.ReportFileSystemChanged(finalPath);
            // Re-point any other queued issues on this file (audio-language cleanup, sorter, grouper, …)
            // at the new location so they don't fail with "no longer exists" on the next fix run.
            _db.RelocateIssuePaths(issue.Path, finalPath);
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
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Could not delete temp transcode file {Path}", tempPath);
                }
            }

            if (!swapCompleted && File.Exists(swapPath))
            {
                if (originalDisposed)
                {
                    // Only reachable if the final rename failed AFTER the original was disposed.
                    // The sidecar is now the only copy of the content — do not delete it.
                    Api.Diagnostics.Record(
                        "Transcode.SwapAborted",
                        "Re-encode of '" + issue.Path + "' completed but the final rename to '" + targetPath + "' failed. The re-encoded copy is preserved at '" + swapPath + "' — rename it manually. Do NOT delete this file; it is currently your only copy of the content.");
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
            "vp9" => "vp9",
            _ => "hevc"
        };
        // VP9 hardware encoding is Intel-QSV-only in the wild: NVENC/AMF/VideoToolbox all lack a VP9
        // encoder path. Fall through to software rather than emitting a non-existent ffmpeg encoder.
        if (codec == "vp9" && suffix != "_qsv")
        {
            return null;
        }

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
            "vp9" => "libvpx-vp9",
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
        if (hardwareEncoder is not null && config.PreferredGpuIndex is int gpuIndex && gpuIndex >= 0)
        {
            // Encoder-family-specific device selection flag. NVENC and AMF take the index as an encoder option;
            // QSV and VideoToolbox don't expose a per-encoder index this way, but a Jellyfin server-wide
            // hardware acceleration device setting covers those. We only send the flag when we know it applies.
            var gpuStr = gpuIndex.ToString(CultureInfo.InvariantCulture);
            if (hardwareEncoder.EndsWith("_nvenc", StringComparison.Ordinal))
            {
                args.AddRange(["-gpu", gpuStr]);
            }
            else if (hardwareEncoder.EndsWith("_amf", StringComparison.Ordinal))
            {
                args.AddRange(["-gpu", gpuStr]);
            }
        }

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
        else
        {
            var (crf, preset, av1Crf, av1Preset, vp9Crf, vp9Cpu) = config.SoftwareEncodePreset switch
            {
                EncodePreset.Faster => ("25", "fast", "32", "6", "35", "4"),
                EncodePreset.Best => ("20", "slow", "28", "10", "28", "0"),
                _ => ("23", "medium", "30", "8", "31", "2")
            };
            if (encoder == "libsvtav1")
            {
                args.AddRange(["-crf", av1Crf, "-preset", av1Preset]);
            }
            else if (encoder == "libvpx-vp9")
            {
                // VP9 uses a two-arg constant-quality mode: -b:v 0 tells the encoder to honour -crf.
                // -cpu-used trades speed for compression like x264/x265 presets.
                args.AddRange(["-crf", vp9Crf, "-b:v", "0", "-cpu-used", vp9Cpu, "-row-mt", "1"]);
            }
            else
            {
                args.AddRange(["-crf", crf, "-preset", preset]);
            }
        }

        if (needsDownscale)
        {
            args.AddRange(["-vf", "scale=-2:" + config.MaxResolutionHeight.ToString(CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-c:a", "copy", "-map_chapters", "0", tempPath]);
        return args;
    }

    // ffmpeg spends most of its output on stream/encoder banners; the actual failure line
    // is always at the end (e.g. "No space left on device", "Invalid argument", codec-specific errors).
    // Show the tail so the user can see the real reason instead of x265's greeting card.
    internal static string Truncate(string text)
    {
        const int Max = 600;
        if (string.IsNullOrEmpty(text) || text.Length <= Max)
        {
            return text;
        }

        return "… " + text[^Max..];
    }

    // Linux's NAME_MAX is 255 bytes. Appending our ".mediadash.tmp.<ext>" suffix to a file whose
    // own basename is already close to that limit (common with Cyrillic/CJK filenames — each
    // char is 2-3 UTF-8 bytes) blows past it and ffmpeg fails with "Error opening output".
    // Fall back to a short, stable hash-based sidecar name in the same directory.
    // Exposed internal for direct testing without spinning up ffmpeg.
    internal static string SidecarPath(string sibling, string marker, string extension)
    {
        var dir = Path.GetDirectoryName(sibling) ?? string.Empty;
        var name = Path.GetFileName(sibling);
        var extSuffix = string.IsNullOrEmpty(extension) ? string.Empty : "." + extension;
        var candidate = name + ".mediadash." + marker + extSuffix;
        if (Encoding.UTF8.GetByteCount(candidate) <= 240)
        {
            return Path.Combine(dir, candidate);
        }

        // 16 hex chars = 8 bytes of SHA-256; collision-proof for a per-directory sidecar.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)), 0, 8);
        return Path.Combine(dir, "mediadash." + marker + "." + hash + extSuffix);
    }

    private string? TryRenameToCanonical(string currentPath, Guid itemId, int height, string extension)
    {
        var item = itemId == Guid.Empty ? null : _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return null;
        }

        var canonical = RenameTemplate.Build(item, height, extension);
        if (canonical is null)
        {
            return null;
        }

        var dir = Path.GetDirectoryName(currentPath);
        if (string.IsNullOrEmpty(dir))
        {
            return null;
        }

        var targetPath = Path.Combine(dir, canonical);
        if (string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(currentPath), StringComparison.OrdinalIgnoreCase))
        {
            // Already canonically named — nothing to do.
            return currentPath;
        }

        if (File.Exists(targetPath))
        {
            _logger.LogInformation("Skip canonical rename: {Target} already exists", targetPath);
            return null;
        }

        try
        {
            File.Move(currentPath, targetPath);
            return targetPath;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Canonical rename failed for {Path}", currentPath);
            Api.Diagnostics.Record("Transcode.RenameFailed", "Re-encode of '" + currentPath + "' succeeded, but the canonical rename step could not run: " + ex.Message + ". The re-encoded file is at its old name — you can rename it by hand or ignore it. Turn off 'Rename after re-encode' in Settings > Quality to stop this attempt.");
            return null;
        }
    }
}
