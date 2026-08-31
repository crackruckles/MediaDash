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
    private readonly RecycleBin _recycleBin;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILogger<EmbeddedCoverArtFixer> _logger;

    /// <summary>Initializes a new instance of the <see cref="EmbeddedCoverArtFixer"/> class.</summary>
    /// <param name="ffprobe">The probe service.</param>
    /// <param name="ffmpeg">The ffmpeg executor.</param>
    /// <param name="guard">Library-path guard so we refuse to touch anything outside a library.</param>
    /// <param name="recycleBin">Recycle bin used to back up originals before stripping.</param>
    /// <param name="libraryMonitor">Jellyfin library monitor — reports post-fix so it re-scans.</param>
    /// <param name="logger">The logger.</param>
    public EmbeddedCoverArtFixer(
        FfprobeService ffprobe,
        FfmpegExecutor ffmpeg,
        LibraryGuard guard,
        RecycleBin recycleBin,
        ILibraryMonitor libraryMonitor,
        ILogger<EmbeddedCoverArtFixer> logger)
    {
        _ffprobe = ffprobe;
        _ffmpeg = ffmpeg;
        _guard = guard;
        _recycleBin = recycleBin;
        _libraryMonitor = libraryMonitor;
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
            return FixResult.Fail("The folder no longer exists; re-scan to refresh the list.");
        }

        if (!_guard.IsInsideLibrary(folder))
        {
            return FixResult.Fail("The folder is outside your library folders; MediaDash will not touch it.");
        }

        var audioFiles = SafeListAudioFiles(folder);
        if (audioFiles.Count == 0)
        {
            return FixResult.Fail("No audio files remain in this folder; re-scan to refresh.");
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
            return FixResult.Fail("Refused: cover filename setting '" + coverFilename + "' must be a plain file name (no separators or path traversal).");
        }

        var coverPath = Path.Combine(folder, coverFilename);
        if (File.Exists(coverPath))
        {
            return FixResult.Fail("A folder cover already exists at " + coverPath + "; nothing to extract.");
        }

        // Also check the scanner's broader folder-cover name set (cover|folder|album|front + jpg/png/webp)
        // — otherwise an album with folder.jpg gets a second cover.jpg written and Jellyfin picks arbitrarily.
        try
        {
            foreach (var existing in Directory.EnumerateFiles(folder))
            {
                if (Scanners.EmbeddedCoverArtScanner.IsFolderCover(Path.GetFileName(existing)))
                {
                    return FixResult.Fail("A folder cover already exists at " + existing + "; nothing to extract.");
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
            return FixResult.Fail("No audio file in the folder currently has an embedded cover — nothing to extract.");
        }

        var extractError = await ExtractCoverViaWebpAsync(sourceForExtract, coverPath, cancellationToken).ConfigureAwait(false);
        if (extractError is not null)
        {
            return FixResult.Fail("Cover extraction failed; the folder is untouched. Details: " + TranscodeFixer.Truncate(extractError));
        }

        long freed = 0;
        var stripped = 0;
        var strippedFailures = 0;
        var additionalRecycled = new List<RecycledSidecar>();
        if (config.EmbeddedCoverStripFromAudio)
        {
            for (var i = 0; i < audioFiles.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(i / (double)audioFiles.Count);

                var target = audioFiles[i];
                var stripResult = await StripEmbeddedCoverAsync(target, config.GetDisposal(IssueType.EmbeddedCoverArt), cancellationToken).ConfigureAwait(false);
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
            var extractArgs = new List<string> { "-i", audioSource, "-an", "-map", "0:v", "-frames:v", "1", "-c:v", "png", rawPath };
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
            var webpArgs = new List<string> { "-i", rawPath, "-c:v", "libwebp", "-q:v", "82", webpPath };
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

        var originalSize = new FileInfo(audioPath).Length;
        var ext = Path.GetExtension(audioPath).TrimStart('.');
        var tempPath = TranscodeFixer.SidecarPath(audioPath, "strip", ext);
        var swapPath = TranscodeFixer.SidecarPath(audioPath, "swap", string.Empty);

        // -map 0 -map -0:v drops every video / image stream while keeping audio + chapters +
        // metadata. -c copy avoids re-encoding — pure remux, byte-for-byte on audio data.
        var args = new List<string> { "-i", audioPath, "-map", "0", "-map", "-0:v", "-c", "copy", tempPath };

        var originalDisposed = false;
        var swapDone = false;
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
            // must not carry a video image stream any more.
            var verifyProbe = await _ffprobe.ProbeAsync(tempPath, cancellationToken).ConfigureAwait(false);
            if (verifyProbe is null || verifyProbe.Error is not null || HasEmbeddedCoverStream(verifyProbe))
            {
                return StripResult.Failed("rewritten file failed post-strip verification; original left in place.");
            }

            if (File.Exists(swapPath))
            {
                File.Delete(swapPath);
            }

            File.Move(tempPath, swapPath);

            string? recyclePath = null;
            if (disposal == DisposalMethod.RecycleBin)
            {
                recyclePath = _recycleBin.MoveToBin(audioPath);
            }
            else if (File.Exists(audioPath))
            {
                File.Delete(audioPath);
            }

            originalDisposed = true;
            File.Move(swapPath, audioPath);
            swapDone = true;

            var newSize = new FileInfo(audioPath).Length;
            var freed = Math.Max(0, originalSize - newSize);
            return StripResult.Ok(freed, audioPath, recyclePath);
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
            if (!swapDone && File.Exists(swapPath))
            {
                if (originalDisposed)
                {
                    Diagnostics.Record(
                        "EmbeddedCoverArt.Swap",
                        "Strip finished but the final rename failed for '" + audioPath + "'. The stripped file is at '" + swapPath + "'; move it into place manually if you want to keep the strip.");
                }
                else
                {
                    SafeDelete(swapPath);
                }
            }
        }
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
