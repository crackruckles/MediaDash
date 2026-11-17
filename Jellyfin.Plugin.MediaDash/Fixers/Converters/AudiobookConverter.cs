using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Probing;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers.Converters;

/// <summary>
/// Handles audiobook format issues.
/// DRM refusal for AAX/AA; folder-merge for multi-file M4A/MP3 audiobooks.
/// </summary>
public sealed class AudiobookConverter : IFormatConverter
{
    private static readonly HashSet<string> DrmExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aax", ".aa"
    };

    // Sentinel used by CanConvert so callers can check folder-merge support uniformly.
    internal const string FolderMergeKey = "audiobook-folder-merge";

    private static readonly TimeSpan MergeTimeout = TimeSpan.FromMinutes(30);

    private readonly IMediaEncoder? _mediaEncoder;
    private readonly FfprobeService? _ffprobe;
    private readonly ILogger<AudiobookConverter>? _logger;

    /// <summary>Initializes a new instance of the <see cref="AudiobookConverter"/> class for DI.</summary>
    /// <param name="mediaEncoder">Jellyfin's media encoder, used to resolve the ffmpeg path.</param>
    /// <param name="ffprobe">Probe service for per-file duration.</param>
    /// <param name="logger">The logger.</param>
    public AudiobookConverter(IMediaEncoder mediaEncoder, FfprobeService ffprobe, ILogger<AudiobookConverter> logger)
    {
        _mediaEncoder = mediaEncoder;
        _ffprobe = ffprobe;
        _logger = logger;
    }

    // ponytail: parameterless ctor kept so the 4 existing unit tests that call `new AudiobookConverter()`
    // compile without adding stubs. Tests that exercise MergeFolderAsync supply their own instance.
    internal AudiobookConverter()
    {
    }

    /// <inheritdoc />
    public string Key => "audiobook";

    /// <inheritdoc />
    public MediaKind Kind => MediaKind.Audiobook;

    /// <inheritdoc />
    public IReadOnlySet<CollectionType?> AllowedLibraryTypes { get; } = new HashSet<CollectionType?> { CollectionType.books };

    /// <inheritdoc />
    public ConverterAvailability Availability => ConverterAvailability.Ready;

    /// <inheritdoc />
    public bool CanConvert(string sourceExtension)
        => DrmExtensions.Contains(sourceExtension)
            || string.Equals(sourceExtension, FolderMergeKey, StringComparison.OrdinalIgnoreCase);

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
        // Folder path — attempt merge.
        if (Directory.Exists(sourcePath))
        {
            return await MergeFolderAsync(sourcePath, stagedTargetPath, progress, cancellationToken).ConfigureAwait(false);
        }

        // File path — DRM refusal path (verbatim from Phase 3-1).
        if (DrmExtensions.Contains(Path.GetExtension(sourcePath)))
        {
            return new ConversionResult(
                false,
                null,
                "This file is DRM-protected (Audible AAX/AA). MediaDash cannot legally convert DRM. Use Audible's own app or a licensed conversion tool, then re-import as M4B.");
        }

        return new ConversionResult(false, null, "AudiobookConverter cannot handle " + Path.GetExtension(sourcePath));
    }

    private async Task<ConversionResult> MergeFolderAsync(
        string sourceFolderPath,
        string stagedTargetPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        // 1. Re-classify the folder — keeps the converter self-contained and defends against
        //    DetailsJson drift (files added/removed since the scan).
        var files = ClassifyAndSortFolder(sourceFolderPath);
        if (files is null || files.Count < 2)
        {
            return new ConversionResult(false, null, "Folder contents changed since scan or no longer matches the multi-file audiobook pattern.");
        }

        // 2. Probe each file for its duration in seconds — chapter offsets accumulate these.
        if (_ffprobe is null)
        {
            return new ConversionResult(false, null, "FfprobeService is not available (test path — use the DI constructor).");
        }

        var durations = new List<double>(files.Count);
        foreach (var file in files)
        {
            var probe = await _ffprobe.ProbeAsync(file, cancellationToken).ConfigureAwait(false);
            if (probe?.Format?.Duration is not string dStr
                || !double.TryParse(dStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                || d <= 0)
            {
                return new ConversionResult(false, null, "Could not probe duration of " + Path.GetFileName(file));
            }

            durations.Add(d);
        }

        // Detect codec: M4A inputs are already AAC — use stream copy. MP3 inputs need encode.
        var ext = Path.GetExtension(files[0]).ToLowerInvariant();
        var audioArgs = ext == ".m4a"
            ? new[] { "-c:a", "copy" }
            : new[] { "-c:a", "aac", "-b:a", "96k" };

        // 3. Write the ffmpeg concat manifest to a temp .txt file.
        var concatTxt = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
        try
        {
            var sb = new StringBuilder();
            foreach (var file in files)
            {
                // ffmpeg concat demuxer uses single-quoted paths; escape internal single quotes.
                sb.Append("file '").Append(file.Replace("'", @"'\''", StringComparison.Ordinal)).Append("'\n");
            }

            await File.WriteAllTextAsync(concatTxt, sb.ToString(), cancellationToken).ConfigureAwait(false);

            // 4. Build the chapter metadata file (ffmetadata format).
            var metadataTxt = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
            try
            {
                var meta = new StringBuilder();
                meta.AppendLine(";FFMETADATA1");
                double cumulativeMs = 0;
                for (int i = 0; i < files.Count; i++)
                {
                    var startMs = (long)Math.Round(cumulativeMs);
                    cumulativeMs += durations[i] * 1000;
                    var endMs = (long)Math.Round(cumulativeMs);
                    meta.AppendLine("[CHAPTER]");
                    meta.AppendLine("TIMEBASE=1/1000");
                    meta.AppendLine("START=" + startMs.ToString(CultureInfo.InvariantCulture));
                    meta.AppendLine("END=" + endMs.ToString(CultureInfo.InvariantCulture));
                    meta.AppendLine("title=" + Path.GetFileNameWithoutExtension(files[i]));
                }

                await File.WriteAllTextAsync(metadataTxt, meta.ToString(), cancellationToken).ConfigureAwait(false);

                // 5. Spawn ffmpeg: concat + merge metadata + encode.
                var ffmpegPath = _mediaEncoder?.EncoderPath;
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
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-nostdin");
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("concat");
                psi.ArgumentList.Add("-safe");
                psi.ArgumentList.Add("0");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(concatTxt);
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(metadataTxt);
                psi.ArgumentList.Add("-map_metadata");
                psi.ArgumentList.Add("1");
                foreach (var a in audioArgs)
                {
                    psi.ArgumentList.Add(a);
                }

                psi.ArgumentList.Add("-vn");
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
                timeoutCts.CancelAfter(MergeTimeout);
                try
                {
                    await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new ConversionResult(false, null, "ffmpeg merge timed out after 30 minutes.");
                }

                if (proc.ExitCode != 0)
                {
                    _logger?.LogWarning("ffmpeg audiobook-merge failed (exit={Exit}): {Stderr}", proc.ExitCode, stderr);
                    return new ConversionResult(false, null, "ffmpeg exited " + proc.ExitCode + ".", stderr.ToString());
                }

                if (!File.Exists(stagedTargetPath) || new FileInfo(stagedTargetPath).Length == 0)
                {
                    return new ConversionResult(false, null, "ffmpeg produced no output.");
                }

                return new ConversionResult(true, stagedTargetPath, string.Empty);
            }
            finally
            {
                try
                {
                    File.Delete(metadataTxt);
                }
                catch (IOException)
                {
                }
            }
        }
        finally
        {
            try
            {
                File.Delete(concatTxt);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Applies the same classification rules as the scanner and returns the sorted file list,
    /// or null if the folder no longer matches the multi-file audiobook shape.
    /// </summary>
    /// <param name="dir">The folder path to classify.</param>
    /// <returns>Sorted list of file paths, or null if the folder does not match.</returns>
    internal static List<string>? ClassifyAndSortFolder(string dir)
    {
        var mp3s = Directory.GetFiles(dir, "*.mp3", SearchOption.TopDirectoryOnly);
        var m4as = Directory.GetFiles(dir, "*.m4a", SearchOption.TopDirectoryOnly);

        string[] files;
        if (mp3s.Length >= 2 && m4as.Length == 0)
        {
            files = mp3s;
        }
        else if (m4as.Length >= 2 && mp3s.Length == 0)
        {
            files = m4as;
        }
        else
        {
            return null;
        }

        var numbered = new List<(int Num, string Path)>(files.Length);
        foreach (var f in files)
        {
            if (FormatCompatibilityScanner.TryReadTrackNumber(Path.GetFileNameWithoutExtension(f), out var n))
            {
                numbered.Add((n, f));
            }
            else
            {
                return null;
            }
        }

        numbered.Sort((a, b) => a.Num.CompareTo(b.Num));
        for (int i = 1; i < numbered.Count; i++)
        {
            if (numbered[i].Num == numbered[i - 1].Num)
            {
                return null;
            }
        }

        return numbered.Select(x => x.Path).ToList();
    }
}
