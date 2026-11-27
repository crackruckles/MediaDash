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
/// Converts obscure music formats to FLAC or MP3 via ffmpeg.
/// Trackers (.669/.it/.mod/.s3m/.xm) and legacy lossless (.ape/.wv/.tak/.tta) → FLAC.
/// WMA → MP3 (libmp3lame -q:a 2).
/// </summary>
public sealed class MusicConverter : IFormatConverter
{
    private static readonly HashSet<string> Handled = new(StringComparer.OrdinalIgnoreCase)
    {
        // Tracker formats
        ".669", ".it", ".mod", ".s3m", ".xm",
        // Legacy lossless
        ".ape", ".wv", ".tak", ".tta",
        // WMA
        ".wma"
    };

    private static readonly TimeSpan ConversionTimeout = TimeSpan.FromMinutes(10);

    // ponytail: store IMediaEncoder, not EncoderPath — EncoderPath is resolved lazily by Jellyfin
    // (empty string at DI construction time, valid path after encoder detection completes).
    private readonly IMediaEncoder? _mediaEncoder;
    private readonly string _ffmpegPathOverride; // used by test ctors only
    private readonly ILogger<MusicConverter> _logger;

    /// <summary>Initializes a new instance of the <see cref="MusicConverter"/> class for DI.</summary>
    /// <param name="mediaEncoder">Jellyfin's media encoder, used to resolve the ffmpeg path.</param>
    /// <param name="logger">The logger.</param>
    public MusicConverter(IMediaEncoder mediaEncoder, ILogger<MusicConverter> logger)
    {
        _mediaEncoder = mediaEncoder;
        _ffmpegPathOverride = string.Empty;
        _logger = logger;
    }

    // ponytail: internal ctors for tests — mirrors AudiobookConverter/ImageConverter pattern.
    internal MusicConverter()
        : this(string.Empty)
    {
    }

    internal MusicConverter(string ffmpegPath)
    {
        _ffmpegPathOverride = ffmpegPath;
        _logger = NullLogger<MusicConverter>.Instance;
    }

    /// <inheritdoc />
    public string Key => "music";

    private string FfmpegPath => _mediaEncoder?.EncoderPath ?? _ffmpegPathOverride;

    /// <inheritdoc />
    public MediaKind Kind => MediaKind.Music;

    /// <inheritdoc />
    public IReadOnlySet<CollectionType?> AllowedLibraryTypes { get; } = new HashSet<CollectionType?> { CollectionType.music };

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
            foreach (var arg in ArgsForSource(Path.GetExtension(sourcePath)))
            {
                psi.ArgumentList.Add(arg);
            }

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
                _logger.LogWarning("ffmpeg failed (exit={Exit}) for {Source}: {Stderr}", proc.ExitCode, sourcePath, stderr);
                return new ConversionResult(false, null, "ffmpeg exited " + proc.ExitCode + ".", stderr.ToString());
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

    // WMA gets lossy MP3; everything else (trackers + legacy lossless) gets FLAC.
    private static IEnumerable<string> ArgsForSource(string sourceExt)
    {
        switch (sourceExt.ToLowerInvariant())
        {
            case ".wma":
                yield return "-c:a";
                yield return "libmp3lame";
                yield return "-q:a";
                yield return "2";
                break;
            default:
                yield return "-c:a";
                yield return "flac";
                break;
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
