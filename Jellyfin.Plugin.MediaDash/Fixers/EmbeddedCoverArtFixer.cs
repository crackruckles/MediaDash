using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Extracts a shared folder cover from the first audio file that has embedded artwork, using a
/// libwebp intermediate for smaller output size, then converts back to <c>cover.jpg</c> for maximum
/// client compatibility. When <see cref="PluginConfiguration.EmbeddedCoverStripFromAudio"/> is on,
/// also removes the redundant per-file embedded cover from every audio file in the same folder,
/// recycling the originals in case something needs to be rolled back.
/// </summary>
public sealed class EmbeddedCoverArtFixer : IFixer
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    // Kept in sync with EmbeddedCoverArtScanner.AudioExtensions — the scanner and fixer must agree
    // on which files the pass can touch, otherwise the scanner flags a folder and the fixer refuses.
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".m4b", ".aac", ".opus", ".ogg", ".oga", ".wav", ".wma",
        ".aiff", ".aif", ".aifc", ".ape", ".dsf", ".dff", ".mka", ".wv", ".mpc", ".mp2"
    };

    private readonly FfprobeService _ffprobe;
    private readonly FfmpegExecutor _ffmpeg;
    private readonly LibraryGuard _guard;
    private readonly VerifiedReplacementService _replacement;
    private readonly IPluginConfigurationProvider _configProvider;
    private readonly ILibraryMonitor _libraryMonitor;
    // External audit P1 #4: commit-time protection check for the cover-strip sweep. Nullable so
    // older ctors don't force a protection dep; production DI wires it via the primary ctor.
    private readonly ProtectionService? _protection;
    private readonly ILogger<EmbeddedCoverArtFixer> _logger;

    /// <summary>Initializes a new instance of the <see cref="EmbeddedCoverArtFixer"/> class.</summary>
    /// <param name="ffprobe">The probe service.</param>
    /// <param name="ffmpeg">The ffmpeg executor.</param>
    /// <param name="guard">Library-path guard so we refuse to touch anything outside a library.</param>
    /// <param name="replacement">03-C: the shared verify → promote → dispose → commit protocol.
    /// Takes ownership of the source recycle (or permanent delete) for each stripped audio file.</param>
    /// <param name="configProvider">02-A snapshot provider. Used for the pre-commit policy
    /// recheck so a dry-run flip mid-strip aborts before touching the source.</param>
    /// <param name="libraryMonitor">Jellyfin library monitor — reports post-fix so it re-scans.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="protection">Protection service for commit-time re-checks (external audit P1 #4); DI auto-wires it.</param>
    public EmbeddedCoverArtFixer(
        FfprobeService ffprobe,
        FfmpegExecutor ffmpeg,
        LibraryGuard guard,
        VerifiedReplacementService replacement,
        IPluginConfigurationProvider configProvider,
        ILibraryMonitor libraryMonitor,
        ILogger<EmbeddedCoverArtFixer> logger,
        ProtectionService? protection = null)
    {
        _ffprobe = ffprobe;
        _ffmpeg = ffmpeg;
        _guard = guard;
        _replacement = replacement;
        _configProvider = configProvider;
        _libraryMonitor = libraryMonitor;
        _protection = protection;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.EmbeddedCoverArt;

    /// <inheritdoc />
    public async Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var folder = issue.Path;
        if (!Directory.Exists(folder))
        {
            return FixResult.Fail(FixReason.SourceMissing, "The folder no longer exists; re-scan to refresh the list.");
        }

        if (!_guard.IsInsideLibrary(folder))
        {
            return FixResult.Refused(FixReason.OutsideLibrary, "The folder is outside your library folders; MediaDash will not touch it.");
        }

        var audioFiles = SafeListAudioFiles(folder);
        if (audioFiles.Count == 0)
        {
            return FixResult.NoChange(FixReason.NothingToRemove, "No audio files remain in this folder; re-scan to refresh.");
        }

        var coverFilename = string.IsNullOrWhiteSpace(config.EmbeddedCoverFilename) ? "cover.jpg" : config.EmbeddedCoverFilename;
        // Reject anything that isn't a plain filename: separators, "..", absolute paths, invalid chars.
        // Config XML restores from untrusted sources could otherwise write outside the target folder.
        if (coverFilename.Contains('/', StringComparison.Ordinal)
            || coverFilename.Contains('\\', StringComparison.Ordinal)
            || coverFilename == ".."
            || coverFilename == "."
            || Path.IsPathRooted(coverFilename)
            || coverFilename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return FixResult.Refused(FixReason.UnsupportedFormat, "Refused: cover filename setting '" + coverFilename + "' must be a plain file name (no separators or path traversal).");
        }

        var coverPath = Path.Combine(folder, coverFilename);
        if (File.Exists(coverPath))
        {
            return FixResult.NoChange(FixReason.ExistingCover, "A folder cover already exists at " + coverPath + "; nothing to extract.");
        }

        // Also check the scanner's broader folder-cover name set (cover|folder|album|front + jpg/png/webp)
        // — otherwise an album with folder.jpg gets a second cover.jpg written and Jellyfin picks arbitrarily.
        try
        {
            foreach (var existing in Directory.EnumerateFiles(folder))
            {
                if (Scanners.EmbeddedCoverArtScanner.IsFolderCover(Path.GetFileName(existing)))
                {
                    return FixResult.NoChange(FixReason.ExistingCover, "A folder cover already exists at " + existing + "; nothing to extract.");
                }
            }
        }
        catch (IOException)
        {
            // If we can't read the folder, fall through — extraction will likely fail with a clearer error.
        }

        if (config.DryRun)
        {
            var msg = config.EmbeddedCoverStripFromAudio
                ? "written " + coverFilename + " and stripped the embedded cover from " + audioFiles.Count + " audio file(s)."
                : "written " + coverFilename + " from the first audio file with embedded artwork.";
            return FixResult.DryRun(msg, issue.SizeSavings);
        }

        // Step 1: extract the cover to cover.jpg via a webp intermediate for smaller output.
        var sourceForExtract = await FindAudioWithCoverAsync(audioFiles, cancellationToken).ConfigureAwait(false);
        if (sourceForExtract is null)
        {
            return FixResult.NoChange(FixReason.NothingToRemove, "No audio file in the folder currently has an embedded cover — nothing to extract.");
        }

        var extractError = await ExtractCoverViaWebpAsync(sourceForExtract, coverPath, cancellationToken).ConfigureAwait(false);
        if (extractError is not null)
        {
            return FixResult.Fail(FixReason.ProcessFailed, "Cover extraction failed; the folder is untouched.", TranscodeFixer.Truncate(extractError));
        }

        long freed = 0;
        var stripped = 0;
        var strippedFailures = 0;
        var additionalRecycled = new List<RecycledSidecar>();
        if (config.EmbeddedCoverStripFromAudio)
        {
            for (var i = 0; i < audioFiles.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                progress?.Report(i / (double)audioFiles.Count);

                var target = audioFiles[i];
                StripResult stripResult;
                try
                {
                    stripResult = await StripEmbeddedCoverAsync(target, config.GetDisposal(IssueType.EmbeddedCoverArt), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (stripResult.Success)
                {
                    stripped++;
                    freed += stripResult.BytesFreed;
                    if (!string.IsNullOrEmpty(stripResult.RecyclePath) && !string.IsNullOrEmpty(stripResult.RecycledOriginalPath))
                    {
                        additionalRecycled.Add(new RecycledSidecar
                        {
                            OriginalPath = stripResult.RecycledOriginalPath!,
                            RecyclePath = stripResult.RecyclePath!,
                            Action = "Recycled pre-strip audio original during embedded-cover-art fix."
                        });
                    }
                }
                else if (stripResult.Skipped)
                {
                    // No embedded cover on this file (e.g. bonus track without art) — nothing to do; not an error.
                }
                else
                {
                    strippedFailures++;
                    Diagnostics.Record("EmbeddedCoverArt.Strip", "Could not strip cover from '" + target + "': " + stripResult.Message);
                }
            }
        }

        progress?.Report(1.0);
        _libraryMonitor.ReportFileSystemChangeBeginning(folder);
        _libraryMonitor.ReportFileSystemChangeComplete(folder, refreshPath: true);

        var summary = config.EmbeddedCoverStripFromAudio
            ? "wrote " + coverFilename + " and stripped covers from " + stripped + " audio file(s)"
              + (strippedFailures > 0 ? " (" + strippedFailures + " could not be stripped and were left alone)" : string.Empty)
            : "wrote " + coverFilename;

        return new FixResult
        {
            Success = true,
            Outcome = FixOutcome.Applied,
            Message = summary + ".",
            BytesFreed = freed,
            AdditionalRecycled = additionalRecycled
        };
    }

    // Probe files in order and pick the first one that still carries a video-image stream. Handles
    // the corner case where the scan was correct but a later manual edit dropped the cover from the
    // first track — we walk forward instead of failing.
    private async Task<string?> FindAudioWithCoverAsync(IReadOnlyList<string> audioFiles, CancellationToken cancellationToken)
    {
        foreach (var f in audioFiles)
        {
            FfprobeData? probe = null;
            try
            {
                probe = await _ffprobe.ProbeAsync(f, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "EmbeddedCoverArtFixer: probe failed for {Path}", f);
                continue;
            }

            if (HasEmbeddedCoverStream(probe))
            {
                return f;
            }
        }

        return null;
    }

    // Three-step conversion: raw dump → webp → jpg. Bigger command surface than a naive
    // `ffmpeg -i in -c:v copy cover.jpg` but the roundtrip through webp shrinks the final size
    // meaningfully on covers that come embedded as bloated mjpeg or oversized png.
    private async Task<string?> ExtractCoverViaWebpAsync(string audioSource, string coverPath, CancellationToken cancellationToken)
    {
        var rawPath = TranscodeFixer.SidecarPath(coverPath, "raw", "png");
        var webpPath = TranscodeFixer.SidecarPath(coverPath, "opt", "webp");
        try
        {
            // Step 1: extract the cover image losslessly to PNG so the webp encoder has a clean source
            // regardless of what codec the embed used (mjpeg / png / webp / bmp).
            // R09 (2026-09-20): rawPath tail is `.mediaDash.tmp` — pass -f image2 explicitly.
            var extractArgs = new List<string> { "-i", audioSource, "-an", "-map", "0:v", "-frames:v", "1", "-c:v", "png", "-f", "image2", rawPath };
            var extractError = await _ffmpeg.RunAsync(extractArgs, CommandTimeout, cancellationToken).ConfigureAwait(false);
            if (extractError is not null)
            {
                return "extract to raw PNG failed — " + extractError;
            }

            if (!File.Exists(rawPath))
            {
                return "extract to raw PNG produced no output.";
            }

            // Step 2: transcode PNG → webp using libwebp with a mid-high quality. libwebp's compression
            // ratio at the same visible quality beats jpeg by a wide margin, which is the whole point of
            // this intermediate.
            var webpArgs = new List<string> { "-i", rawPath, "-c:v", "libwebp", "-q:v", "82", "-f", "webp", webpPath };
            var webpError = await _ffmpeg.RunAsync(webpArgs, CommandTimeout, cancellationToken).ConfigureAwait(false);
            if (webpError is not null)
            {
                return "webp compression pass failed — " + webpError;
            }

            // Step 3: webp → jpg for maximum client compatibility. -q:v 3 keeps mjpeg high-quality;
            // the artefact-inducing pass was the webp step, which is intentional for size.
            var jpgArgs = new List<string> { "-i", webpPath, "-c:v", "mjpeg", "-q:v", "3", coverPath };
            var jpgError = await _ffmpeg.RunAsync(jpgArgs, CommandTimeout, cancellationToken).ConfigureAwait(false);
            if (jpgError is not null)
            {
                return "jpg final-pass failed — " + jpgError;
            }

            if (!File.Exists(coverPath))
            {
                return "jpg pass produced no output.";
            }

            return null;
        }
        finally
        {
            SafeDelete(rawPath);
            SafeDelete(webpPath);
        }
    }

    internal static bool StripDurationMatches(FfprobeData? source, FfprobeData? output)
        => source is not null && output is not null
            && source.TryGetDuration(out var originalDuration)
            && output.TryGetDuration(out var outputDuration)
            && Math.Abs(originalDuration - outputDuration) <= 2;

    private async Task<StripResult> StripEmbeddedCoverAsync(string audioPath, DisposalMethod disposal, CancellationToken cancellationToken)
    {
        FfprobeData? probe;
        try
        {
            probe = await _ffprobe.ProbeAsync(audioPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return StripResult.Failed("probe failed: " + ex.Message);
        }

        if (!HasEmbeddedCoverStream(probe))
        {
            return StripResult.SkippedNoCover();
        }

        if (disposal == DisposalMethod.RecycleBin && !_replacement.CanRecycle(audioPath))
        {
            return StripResult.Failed("No safe recycle location is available for the original.");
        }

        var shortfall = FreeSpaceGuard.DescribeShortfall(audioPath, 2);
        if (shortfall is not null)
        {
            return StripResult.Failed(shortfall);
        }

        var originalSize = new FileInfo(audioPath).Length;
        var ext = Path.GetExtension(audioPath).TrimStart('.');
        if (string.IsNullOrEmpty(TranscodeFixer.ContainerToFfmpegFormat(ext)))
        {
            return StripResult.Failed("Unsupported audio container: " + ext);
        }

        var tempPath = TranscodeFixer.SidecarPath(audioPath, "strip", ext);

        // -map 0 -map -0:v drops every video / image stream while keeping audio + chapters +
        // metadata. -c copy avoids re-encoding — pure remux, byte-for-byte on audio data.
        // R09 (2026-09-20): tempPath tail is `.mediaDash.tmp` — pass -f explicitly matching
        // the source audio container so ffmpeg's muxer picks the right one.
        var args = new List<string> { "-i", audioPath, "-map", "0", "-map", "-0:v", "-c", "copy", "-f", TranscodeFixer.ContainerToFfmpegFormat(ext), tempPath };

        // 03-C: capture source identity BEFORE ffmpeg runs so pre-commit recheck fires on stale.
        var audioInfo = new FileInfo(audioPath);
        var expectedSourceLength = audioInfo.Exists ? audioInfo.Length : 0;
        var expectedSourceMtimeTicks = audioInfo.Exists ? audioInfo.LastWriteTimeUtc.Ticks : 0;

        try
        {
            var runError = await _ffmpeg.RunAsync(args, CommandTimeout, cancellationToken).ConfigureAwait(false);
            if (runError is not null)
            {
                return StripResult.Failed("ffmpeg remux failed: " + TranscodeFixer.Truncate(runError));
            }

            if (!File.Exists(tempPath))
            {
                return StripResult.Failed("remux produced no output file.");
            }

            // Verify the rewritten file: it must still parse, still have every audio stream, and
            // must not carry a video image stream any more. Both invariants (cover removed AND
            // audio streams preserved) are enforced INSIDE this callback so a broken ffmpeg /
            // corrupt source combination can't produce a videoless-AND-audioless output that
            // then gets committed over the original.
            var verifyProbe = await _ffprobe.ProbeAsync(tempPath, cancellationToken).ConfigureAwait(false);
            if (verifyProbe is null || verifyProbe.Error is not null || HasEmbeddedCoverStream(verifyProbe))
            {
                return StripResult.Failed("rewritten file failed post-strip verification; original left in place.");
            }

            if (!StripDurationMatches(probe, verifyProbe))
            {
                return StripResult.Failed("rewritten file duration could not be verified; original left in place.");
            }

            var sourceAudioCount = CountAudioStreams(probe);
            var rewrittenAudioCount = CountAudioStreams(verifyProbe);
            if (rewrittenAudioCount < sourceAudioCount)
            {
                return StripResult.Failed(
                    "rewritten file lost audio streams (" + sourceAudioCount + " → " + rewrittenAudioCount
                    + "); original left in place.");
            }

            // 03-C: hand the verified strip to VerifiedReplacementService. Same-path swap;
            // service takes ownership of the recycle/delete of the audio original.
            var request = new ReplacementRequest(
                SourcePath: audioPath,
                OutputPath: tempPath,
                DestinationPath: audioPath,
                ExpectedSourceLength: expectedSourceLength,
                ExpectedSourceMtimeUtcTicks: expectedSourceMtimeTicks,
                RecycleOriginal: disposal == DisposalMethod.RecycleBin,
                PreserveSourceTimestamps: true);

            var replacementResult = await _replacement.ReplaceAsync(
                request,
                verifyOutput: _ => Task.FromResult<string?>(null),
                checkCurrentPolicy: () =>
                {
                    // External audit P1 #4: previously only DryRun was checked. Honour the full
                    // policy stack so cover-strip aborts when EmbeddedCoverArt is toggled off or
                    // the audio folder gets a Never-modify rule mid-sweep.
                    var currentConfig = _configProvider.GetSnapshot();
                    if (currentConfig.DryRun)
                    {
                        return PolicyDecision.Refuse(FixReason.TypeDisabled);
                    }

                    var mode = currentConfig.GetFixMode(IssueType.EmbeddedCoverArt);
                    if (mode == FixMode.Off)
                    {
                        return PolicyDecision.Refuse(FixReason.TypeDisabled);
                    }

                    if (_protection is not null && _protection.IsFixerBlocked(audioPath, IssueType.EmbeddedCoverArt))
                    {
                        return PolicyDecision.Refuse(FixReason.Protected);
                    }

                    return PolicyDecision.Allow;
                },
                cancellationToken).ConfigureAwait(false);

            if (!replacementResult.Applied)
            {
                if (replacementResult.Reason == FixReason.RecoveryRequired && replacementResult.RecoveryPath is not null)
                {
                    Diagnostics.Record(
                        "EmbeddedCoverArt.Swap",
                        replacementResult.DescribeRecovery(audioPath));
                    return StripResult.Failed(replacementResult.DescribeRecovery(audioPath));
                }

                if (replacementResult.Reason == FixReason.SourceChanged)
                {
                    return StripResult.Failed("source changed mid-strip; original preserved.");
                }

                return StripResult.Failed("strip could not be committed: " + (replacementResult.TechnicalDetail ?? replacementResult.Reason.ToString()));
            }

            long freed = 0;
            try
            {
                freed = Math.Max(0, originalSize - new FileInfo(audioPath).Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not measure stripped audio {Path}", audioPath);
            }

            return StripResult.Ok(freed, audioPath, replacementResult.RecyclePath);
        }
        catch (IOException ex)
        {
            return StripResult.Failed("IO error: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StripResult.Failed("access denied: " + ex.Message);
        }
        finally
        {
            SafeDelete(tempPath);
        }
    }

    /// <summary>Counts audio streams in the probe. FU-2 (audit round 4): the cover-strip
    /// verify path uses this to guarantee the rewrite didn't accidentally drop audio.</summary>
    private static int CountAudioStreams(FfprobeData? probe)
    {
        if (probe?.Streams is null)
        {
            return 0;
        }

        var count = 0;
        foreach (var s in probe.Streams)
        {
            if (string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    private static bool HasEmbeddedCoverStream(FfprobeData? probe)
    {
        if (probe?.Streams is null)
        {
            return false;
        }

        foreach (var s in probe.Streams)
        {
            if (!string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var codec = s.CodecName ?? string.Empty;
            if (codec.Equals("mjpeg", StringComparison.OrdinalIgnoreCase)
                || codec.Equals("png", StringComparison.OrdinalIgnoreCase)
                || codec.Equals("jpeg", StringComparison.OrdinalIgnoreCase)
                || codec.Equals("webp", StringComparison.OrdinalIgnoreCase)
                || codec.Equals("bmp", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> SafeListAudioFiles(string folder)
    {
        try
        {
            var list = new List<string>();
            foreach (var f in Directory.EnumerateFiles(folder))
            {
                if (AudioExtensions.Contains(Path.GetExtension(f)))
                {
                    list.Add(f);
                }
            }

            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }
        catch (IOException)
        {
            return new List<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    private static void SafeDelete(string path)
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class StripResult
    {
        public bool Success { get; private init; }

        public bool Skipped { get; private init; }

        public long BytesFreed { get; private init; }

        public string Message { get; private init; } = string.Empty;

        // Non-null when the original was recycled instead of deleted; feeds a per-file history row so
        // the Recycle Bin tab can render a Restore button for each stripped original, not just one.
        public string? RecycledOriginalPath { get; private init; }

        public string? RecyclePath { get; private init; }

        public static StripResult Ok(long freed, string? recycledOriginalPath = null, string? recyclePath = null) => new()
        {
            Success = true,
            BytesFreed = freed,
            RecycledOriginalPath = recycledOriginalPath,
            RecyclePath = recyclePath
        };

        public static StripResult SkippedNoCover() => new() { Skipped = true, Message = "No embedded cover on this file." };

        public static StripResult Failed(string reason) => new() { Message = reason };
    }
}
