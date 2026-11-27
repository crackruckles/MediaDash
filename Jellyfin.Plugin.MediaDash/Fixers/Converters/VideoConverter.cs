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
/// Remuxes RMVB into MKV via ffmpeg stream-copy.
/// RealVideo codecs (rv30/rv40) survive in MKV; stream-copy is attempted first.
/// If the build's codec support is truly incompatible, ffmpeg exits non-zero and the fixer's
/// error path handles it — no retry-with-transcode loop for one obscure format.
/// </summary>
public sealed class VideoConverter : IFormatConverter
{
    private static readonly HashSet<string> Handled = new(StringComparer.OrdinalIgnoreCase)
    {
        ".rmvb"
    };

    private static readonly TimeSpan ConversionTimeout = TimeSpan.FromMinutes(10);

    // ponytail: store IMediaEncoder, not EncoderPath — EncoderPath is resolved lazily by Jellyfin
    // (empty string at DI construction time, valid path after encoder detection completes).
    private readonly IMediaEncoder? _mediaEncoder;
    private readonly string _ffmpegPathOverride; // used by test ctors only
    private readonly ILogger<VideoConverter> _logger;

    /// <summary>Initializes a new instance of the <see cref="VideoConverter"/> class for DI.</summary>
    /// <param name="mediaEncoder">Jellyfin's media encoder, used to resolve the ffmpeg path.</param>
    /// <param name="logger">The logger.</param>
    public VideoConverter(IMediaEncoder mediaEncoder, ILogger<VideoConverter> logger)
    {
        _mediaEncoder = mediaEncoder;
        _ffmpegPathOverride = string.Empty;
        _logger = logger;
    }

    // ponytail: internal ctors for tests — mirrors AudiobookConverter/ImageConverter pattern.
    internal VideoConverter()
        : this(string.Empty)
    {
    }

    internal VideoConverter(string ffmpegPath)
    {
        _ffmpegPathOverride = ffmpegPath;
        _logger = NullLogger<VideoConverter>.Instance;
    }

    /// <inheritdoc />
    public string Key => "video";

    private string FfmpegPath => _mediaEncoder?.EncoderPath ?? _ffmpegPathOverride;

    /// <inheritdoc />
    public MediaKind Kind => MediaKind.Video;

    /// <inheritdoc />
    public IReadOnlySet<CollectionType?> AllowedLibraryTypes { get; } = new HashSet<CollectionType?>
    {
        CollectionType.movies,
        CollectionType.tvshows
    };

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
            // External review #2: ffmpeg's default stream selection on an input picks ONE best
            // video + ONE best audio stream and silently drops any extras. RMVB files commonly
            // ship multiple audio tracks (dub + original); without -map 0 they vanish on remux.
            // -map 0 maps every stream from input 0, then -c:s copy preserves subtitle tracks
            // alongside the video and audio copies that follow. Attached pictures (cover art)
            // are preserved too — they're low-cost and some players show them.
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0");
            // Stream-copy: RMVB → MKV container remux. RealVideo rv30/rv40 are valid in MKV.
            // If the codec is genuinely incompatible the fixer's error path takes over.
            psi.ArgumentList.Add("-c:v");
            psi.ArgumentList.Add("copy");
            psi.ArgumentList.Add("-c:a");
            psi.ArgumentList.Add("copy");
            psi.ArgumentList.Add("-c:s");
            psi.ArgumentList.Add("copy");
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
