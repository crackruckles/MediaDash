using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Probing;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.MediaDash.Fixers.Converters;

/// <summary>
/// Converts incompatible image formats to JPG via ffmpeg.
/// Handles HEIC/HEIF, TIFF, BMP, and camera RAW formats (.raw, .nef, .cr2, .arw, .dng).
/// RAW support depends on the ffmpeg build having libraw — if it doesn't, ConvertAsync returns a friendly message.
/// </summary>
public sealed class ImageConverter : IFormatConverter
{
    private static readonly HashSet<string> Handled = new(StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".heif", ".tif", ".tiff", ".bmp",
        ".raw", ".nef", ".cr2", ".arw", ".dng"
    };

    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".raw", ".nef", ".cr2", ".arw", ".dng"
    };

    private static readonly TimeSpan ConversionTimeout = TimeSpan.FromMinutes(10);

    // ponytail: store IMediaEncoder, not EncoderPath — EncoderPath is resolved lazily by Jellyfin
    // (empty string at DI construction time, valid path after encoder detection completes).
    private readonly IMediaEncoder? _mediaEncoder;
    private readonly string _ffmpegPathOverride; // used by test ctors only
    private readonly ILogger<ImageConverter> _logger;

    /// <summary>Initializes a new instance of the <see cref="ImageConverter"/> class for DI.</summary>
    /// <param name="mediaEncoder">Jellyfin's media encoder, used to resolve the ffmpeg path.</param>
    /// <param name="logger">The logger.</param>
    public ImageConverter(IMediaEncoder mediaEncoder, ILogger<ImageConverter> logger)
    {
        _mediaEncoder = mediaEncoder;
        _ffmpegPathOverride = string.Empty;
        _logger = logger;
    }

    // ponytail: internal ctors let tests use `new ImageConverter()` (property-only) or
    // `new ImageConverter(ffmpegPath)` (live conversion) without implementing IMediaEncoder.
    internal ImageConverter()
        : this(string.Empty)
    {
    }

    internal ImageConverter(string ffmpegPath)
    {
        _ffmpegPathOverride = ffmpegPath;
        _logger = NullLogger<ImageConverter>.Instance;
    }

    /// <inheritdoc />
    public string Key => "image";

    private string FfmpegPath => _mediaEncoder?.EncoderPath ?? _ffmpegPathOverride;

    /// <inheritdoc />
    public MediaKind Kind => MediaKind.Image;

    /// <inheritdoc />
    public IReadOnlySet<CollectionType?> AllowedLibraryTypes { get; } = new HashSet<CollectionType?> { CollectionType.homevideos };

    /// <inheritdoc />
    public ConverterAvailability Availability => ConverterAvailability.Ready;

    /// <inheritdoc />
    public bool CanConvert(string sourceExtension) => Handled.Contains(sourceExtension);

    /// <inheritdoc />
    public Task<ConverterAvailability> EnsureToolchainAsync(CancellationToken cancellationToken)
        => Task.FromResult(ConverterAvailability.Ready);

    /// <inheritdoc />
    public async Task<ConversionResult> ConvertAsync(
        string sourcePath,
        string stagedTargetPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var ffmpegPath = FfmpegPath;
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                return new ConversionResult(false, null, "ffmpeg is not available. Jellyfin's transcoding path may be misconfigured.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(stagedTargetPath)!);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(sourcePath);
            // -vframes 1: HEIC live photos and some RAWs have multiple frames; take the first.
            // -q:v 3: high-quality JPG (~85% equivalent).
            psi.ArgumentList.Add("-vframes");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-q:v");
            psi.ArgumentList.Add("3");
            psi.ArgumentList.Add(stagedTargetPath);

            var media = await MediaProcess.AcquireAsync(cancellationToken).ConfigureAwait(false);
            await using var lifetime = media.ConfigureAwait(false);
            var proc = media.Process;
            proc.StartInfo = psi;
            var stderr = new StringBuilder();
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    stderr.AppendLine(e.Data);
                }
            };

            if (!media.Start())
            {
                return new ConversionResult(false, null, "ffmpeg failed to start.");
            }

            proc.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ConversionTimeout);
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ConversionResult(false, null, "ffmpeg timed out after " + ConversionTimeout.TotalMinutes + " minutes.");
            }

            if (proc.ExitCode != 0)
            {
                // ponytail: RAW-specific friendlier message when ffmpeg lacks libraw.
                var stderrText = stderr.ToString();
                if (RawExtensions.Contains(Path.GetExtension(sourcePath))
                    && (stderrText.Contains("no decoder", StringComparison.OrdinalIgnoreCase)
                        || stderrText.Contains("Decoder", StringComparison.OrdinalIgnoreCase)
                        || stderrText.Contains("Invalid data", StringComparison.OrdinalIgnoreCase)))
                {
                    return new ConversionResult(
                        false,
                        null,
                        "ffmpeg build doesn't support this RAW format. Use a RAW-specific tool like darktable or Adobe DNG Converter to produce a JPG.",
                        stderrText);
                }

                _logger.LogWarning("ffmpeg failed (exit={Exit}) for {Source}: {Stderr}", proc.ExitCode, sourcePath, stderrText);
                return new ConversionResult(false, null, "ffmpeg exited " + proc.ExitCode + ".", stderrText);
            }

            if (!File.Exists(stagedTargetPath) || new FileInfo(stagedTargetPath).Length == 0)
            {
                return new ConversionResult(false, null, "ffmpeg produced no output.");
            }

            return new ConversionResult(true, stagedTargetPath, string.Empty);
        }
        catch (OperationCanceledException)
        {
            SafeDelete(stagedTargetPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Kind} conversion crashed: {Source} -> {Target}", Kind, sourcePath, stagedTargetPath);
            SafeDelete(stagedTargetPath);
            return new ConversionResult(false, null, Kind + " conversion crashed: " + ex.Message, ex.ToString());
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
    }
}
