using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Probing;

/// <summary>
/// Runs ffprobe against media files, caching results by path, size and modification time
/// so unchanged files are not probed again on re-scan.
/// </summary>
public sealed class FfprobeService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly IMediaEncoder _mediaEncoder;
    private readonly MediaDashDb _db;
    private readonly ILogger<FfprobeService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FfprobeService"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface, used to locate the server's bundled ffprobe.</param>
    /// <param name="db">The plugin database for probe caching.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{FfprobeService}"/> interface.</param>
    public FfprobeService(IMediaEncoder mediaEncoder, MediaDashDb db, ILogger<FfprobeService> logger)
    {
        _mediaEncoder = mediaEncoder;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Probes a media file, returning parsed ffprobe output.
    /// A result with <see cref="FfprobeData.Error"/> set (or no streams) means the file itself is unreadable —
    /// that is a playability finding, not an infrastructure failure.
    /// </summary>
    /// <param name="path">Full path of the file to probe.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The parsed probe data, or null when the file is missing or ffprobe could not be executed.</returns>
    public async Task<FfprobeData?> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            _logger.LogDebug("Skipping probe, file no longer exists: {Path}", path);
            return null;
        }

        var size = fileInfo.Length;
        var mtimeTicks = fileInfo.LastWriteTimeUtc.Ticks;

        var cached = _db.GetCachedProbe(path, size, mtimeTicks);
        if (cached is not null)
        {
            return Deserialize(cached, path);
        }

        var json = await RunFfprobeAsync(path, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        var data = Deserialize(json, path);
        if (data is not null)
        {
            _db.StoreProbe(path, size, mtimeTicks, json);
        }

        return data;
    }

    /// <summary>
    /// Test-plays the start, middle and end of a file (30 seconds each) with ffmpeg to catch corruption ffprobe misses.
    /// Results are cached by path, size and modification time so unchanged files are not decoded again.
    /// </summary>
    /// <param name="path">Full path of the file to check.</param>
    /// <param name="durationSeconds">The file's duration in seconds, used to locate the middle segment; 0 skips the middle check.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The decode error output, or null when the file decodes cleanly.</returns>
    public async Task<string?> DecodeCheckAsync(string path, double durationSeconds, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            return null;
        }

        var cached = _db.GetCachedDecode(path, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks);
        if (cached is not null)
        {
            return cached.Length == 0 ? null : cached;
        }

        // Start: decode the first 30s (or the whole file if it's shorter). If ffmpeg's decoded time
        // falls significantly short of what we asked for, treat that as truncation regardless of any
        // stderr markers — ffmpeg sometimes hits EOF cleanly without emitting one.
        var startExpected = durationSeconds > 0 ? Math.Min(30.0, durationSeconds) : 30.0;
        var error = await RunFfmpegDecodeAsync(["-i", path, "-t", "30", "-f", "null", "-"], startExpected, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(error) && durationSeconds > 90)
        {
            var middle = ((long)(durationSeconds / 2)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            error = await RunFfmpegDecodeAsync(["-ss", middle, "-i", path, "-t", "30", "-f", "null", "-"], 30.0, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(error))
        {
            var endExpected = durationSeconds > 0 ? Math.Min(30.0, durationSeconds) : 30.0;
            error = await RunFfmpegDecodeAsync(["-sseof", "-30", "-i", path, "-f", "null", "-"], endExpected, cancellationToken).ConfigureAwait(false);
        }

        var result = string.IsNullOrWhiteSpace(error) ? null : error;
        _db.StoreDecode(path, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks, result ?? string.Empty);
        return result;
    }

    private async Task<string?> RunFfmpegDecodeAsync(string[] args, double expectedSeconds, CancellationToken cancellationToken)
    {
        var encoderPath = _mediaEncoder.EncoderPath;
        if (string.IsNullOrEmpty(encoderPath))
        {
            return null;
        }

        using var process = new Process();
        process.StartInfo.FileName = encoderPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardError = true;
        // -xerror makes ffmpeg exit non-zero on real decode errors; anything on stderr without a non-zero exit
        // is a non-fatal warning (SEI noise, HEVC parser chatter, benign packet issues) and doesn't mean the file is broken.
        process.StartInfo.ArgumentList.Add("-xerror");
        process.StartInfo.ArgumentList.Add("-v");
        process.StartInfo.ArgumentList.Add("error");
        // -stats enables the periodic "frame= ... time=HH:MM:SS.ms" progress line on stderr. Zero cost at -v error;
        // we need it so we can compare what ffmpeg actually decoded against what we asked for.
        process.StartInfo.ArgumentList.Add("-stats");
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            process.Start();
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0 || HasTruncationMarker(stderr))
            {
                return string.IsNullOrWhiteSpace(stderr) ? "ffmpeg exited with an error" : stderr;
            }

            // ffmpeg sometimes hits EOF cleanly (exit 0, no truncation marker) but still stopped short of the
            // requested segment. When the last time= we saw is meaningfully less than we asked for, the file
            // had less playable content than the container advertised — that's a truncation the shallow check
            // would miss. 10% tolerance covers seek imprecision at segment boundaries.
            if (expectedSeconds > 1.0)
            {
                var decoded = ParseLastTimeSeconds(stderr);
                if (decoded is double d && d < expectedSeconds * 0.9)
                {
                    return string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Decoded only {0:F2}s of {1:F0}s requested — the container claims more content than the file actually holds.",
                        d,
                        expectedSeconds);
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return "decode check timed out";
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    // ffmpeg quirk: "File ended prematurely" during decode is emitted to stderr but treated as clean EOF —
    // exit code stays 0, even with -xerror. That silently hides truncated files ("sort of plays") from
    // the shallow decode check. Same story for "Truncating packet". If we see either marker in stderr,
    // treat it as an error regardless of exit code.
    internal static bool HasTruncationMarker(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr))
        {
            return false;
        }

        return stderr.Contains("File ended prematurely", StringComparison.Ordinal)
            || stderr.Contains("Truncating packet", StringComparison.Ordinal);
    }

    // ffmpeg's -stats output prints progress lines like:
    //   frame=  190 fps=0.0 q=-0.0 Lsize=N/A time=00:00:07.84 bitrate=N/A speed=205x elapsed=0:00:00.03
    // The last such line's time= is what actually got decoded. Compare to the requested segment to catch
    // clean-EOF truncation the marker-scan misses (some containers stop early without printing a marker).
    internal static double? ParseLastTimeSeconds(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr))
        {
            return null;
        }

        // Rightmost match; -stats emits multiple lines during a decode, only the last one matters.
        var matches = System.Text.RegularExpressions.Regex.Matches(
            stderr,
            @"\btime=(\d+):(\d{2}):(\d{2}(?:\.\d+)?)");
        if (matches.Count == 0)
        {
            return null;
        }

        var m = matches[matches.Count - 1];
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        if (!int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Integer, invariant, out var h)
            || !int.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Integer, invariant, out var mm)
            || !double.TryParse(m.Groups[3].Value, System.Globalization.NumberStyles.Float, invariant, out var ss))
        {
            return null;
        }

        return (h * 3600.0) + (mm * 60.0) + ss;
    }

    private FfprobeData? Deserialize(string json, string path)
    {
        try
        {
            return JsonSerializer.Deserialize<FfprobeData>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not parse ffprobe output for {Path}", path);
            return null;
        }
    }

    private async Task<string?> RunFfprobeAsync(string path, CancellationToken cancellationToken)
    {
        var probePath = _mediaEncoder.ProbePath;
        if (string.IsNullOrEmpty(probePath))
        {
            _logger.LogError("The server has no ffprobe configured; cannot analyze media files");
            return null;
        }

        using var process = new Process();
        process.StartInfo.FileName = probePath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        foreach (var arg in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-show_error", path })
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
            return stdout;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("ffprobe timed out after {Timeout} on {Path}", ProbeTimeout, path);
            TryKill(process);
            return null;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run ffprobe at {ProbePath}", probePath);
            return null;
        }
    }

    private void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not kill timed-out ffprobe process");
        }
    }
}
