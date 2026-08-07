using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Runs the server's bundled ffmpeg for remux and transcode operations.
/// </summary>
public sealed class FfmpegExecutor
{
    private const string OutTimeKey = "out_time_us=";

    private static readonly string[] ProgressKeys =
    [
        "frame=", "fps=", "bitrate=", "total_size=", "out_time_ms=", "out_time=",
        "dup_frames=", "drop_frames=", "speed=", "progress="
    ];

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<FfmpegExecutor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegExecutor"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
    /// <param name="logger">The logger.</param>
    public FfmpegExecutor(IMediaEncoder mediaEncoder, ILogger<FfmpegExecutor> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Runs ffmpeg with the given arguments.
    /// </summary>
    /// <param name="args">The ffmpeg arguments.</param>
    /// <param name="timeout">Maximum run time before the process is killed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="progress">Optional 0..1 progress reporter, driven by ffmpeg's own <c>-progress pipe:2</c> output when <paramref name="totalDurationSeconds"/> is set.</param>
    /// <param name="totalDurationSeconds">The expected total duration of the output; used to convert ffmpeg's out_time_us into a fraction. Set to 0 to skip progress plumbing.</param>
    /// <returns>The last portion of stderr on failure, or null on success.</returns>
    public async Task<string?> RunAsync(
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IProgress<double>? progress = null,
        double totalDurationSeconds = 0)
    {
        var encoderPath = _mediaEncoder.EncoderPath;
        if (string.IsNullOrEmpty(encoderPath))
        {
            return "The server has no ffmpeg configured.";
        }

        // Orphan-sweep before spawning: a hot-reloaded plugin or a Jellyfin crash mid-encode can leave a
        // stale ffmpeg still writing to our sidecar path. Two ffmpegs open on the same output produces
        // interleaved garbage. Anything that clearly belongs to us (cmdline contains a mediadash.tmp/new
        // marker) gets killed and its output cleaned up so the fresh encode starts on a clean file.
        SweepStaleMediaDashFfmpegs();

        var reportProgress = progress is not null && totalDurationSeconds > 0;

        using var process = new Process();
        process.StartInfo.FileName = encoderPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.ArgumentList.Add("-y");
        process.StartInfo.ArgumentList.Add("-v");
        process.StartInfo.ArgumentList.Add("error");
        if (reportProgress)
        {
            // ffmpeg writes key=value blocks to fd 2 alongside any error messages;
            // out_time_us gives us elapsed encoded microseconds without polling temp file sizes.
            process.StartInfo.ArgumentList.Add("-progress");
            process.StartInfo.ArgumentList.Add("pipe:2");
            process.StartInfo.ArgumentList.Add("-nostats");
        }

        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        _logger.LogInformation("Running ffmpeg {Args}", string.Join(' ', args));
        try
        {
            process.Start();
            var stderrTail = new StringBuilder();
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false)) is not null)
            {
                if (reportProgress && line.StartsWith(OutTimeKey, StringComparison.Ordinal))
                {
                    if (long.TryParse(line.AsSpan(OutTimeKey.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                    {
                        var fraction = us / (totalDurationSeconds * 1_000_000.0);
                        progress!.Report(Math.Clamp(fraction, 0, 1));
                    }

                    continue;
                }

                // Skip the rest of the -progress noise; keep only real error/warning lines for diagnostics.
                if (reportProgress && IsProgressKeyValueLine(line))
                {
                    continue;
                }

                stderrTail.AppendLine(line);
                if (stderrTail.Length > 4000)
                {
                    stderrTail.Remove(0, stderrTail.Length - 2000);
                }
            }

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var tail = stderrTail.ToString();
                var msg = string.IsNullOrWhiteSpace(tail) ? $"ffmpeg exited with code {process.ExitCode}" : tail;
                Api.Diagnostics.Record(
                    "Ffmpeg.Error",
                    "ffmpeg failed" + FindInputHint(args) + ": " + TrimForDiagnostic(msg) + ". The original file was left untouched.");
                return msg;
            }

            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            var msg = TimeoutError(timeout);
            Api.Diagnostics.Record(
                "Ffmpeg.Timeout",
                "ffmpeg exceeded the " + timeout + " limit" + FindInputHint(args) + " and was stopped. Larger files or slow disks may need a longer window — the track fixer already auto-retries once with a 5-hour cap.");
            return msg;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run ffmpeg at {Path}", encoderPath);
            Api.Diagnostics.Record("Ffmpeg.RunFailed", "Could not execute ffmpeg at '" + encoderPath + "': " + ex.Message + ". Every re-encode fix is blocked until this is fixed.");
            return ex.Message;
        }
    }

    /// <summary>Sentinel test callers use to detect a wall-clock timeout vs any other ffmpeg failure.</summary>
    /// <param name="error">The error string returned by <see cref="RunAsync"/>.</param>
    /// <returns>True if the failure was due to the RunAsync timeout expiring.</returns>
    public static bool IsTimeoutError(string error)
    {
        return error is not null && error.Contains("time limit and was stopped", StringComparison.Ordinal);
    }

    private static string TimeoutError(TimeSpan timeout) => $"ffmpeg exceeded the {timeout} time limit and was stopped";

    private static string FindInputHint(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], "-i", StringComparison.Ordinal))
            {
                return " on '" + args[i + 1] + "'";
            }
        }

        return string.Empty;
    }

    private static string TrimForDiagnostic(string message)
    {
        // Diagnostic panel prefers a single readable line — collapse whitespace + cap length.
        var collapsed = System.Text.RegularExpressions.Regex.Replace(message, @"\s+", " ").Trim();
        return collapsed.Length > 400 ? collapsed[..400] + "…" : collapsed;
    }

    private static bool IsProgressKeyValueLine(string line)
    {
        // -progress emits key=value noise (fps, bitrate, speed, progress, per-stream stats).
        // Filter is narrow so real ffmpeg errors still bubble up in the stderr tail.
        foreach (var key in ProgressKeys)
        {
            if (line.StartsWith(key, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return line.StartsWith("stream_", StringComparison.Ordinal);
    }

    private void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not kill ffmpeg process");
        }
    }

    private void SweepStaleMediaDashFfmpegs()
    {
        // /proc/<pid>/cmdline is the cheap cross-distro way to read a process's argv without shelling out.
        // Windows would need System.Management (WMI) or P/Invoke to CreateToolhelp32Snapshot — not worth
        // pulling in for the primary failure mode we care about (Linux container hot-reload).
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        try
        {
            foreach (var p in Process.GetProcessesByName("ffmpeg"))
            {
                using (p)
                {
                    string? cmd;
                    try
                    {
                        var cmdlinePath = "/proc/" + p.Id.ToString(CultureInfo.InvariantCulture) + "/cmdline";
                        if (!File.Exists(cmdlinePath))
                        {
                            continue;
                        }

                        cmd = File.ReadAllText(cmdlinePath);
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }

                    if (!cmd.Contains("mediadash.tmp", StringComparison.Ordinal)
                        && !cmd.Contains("mediadash.new", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // /proc/pid/cmdline uses NUL as the argv separator. The output path is the last argument.
                    var args = cmd.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    var outputPath = args.Length > 0 ? args[^1] : null;

                    _logger.LogWarning(
                        "Killing stale MediaDash ffmpeg pid={Pid}, output={Output}. Likely an orphan from a hot-reload or a Jellyfin restart mid-encode.",
                        p.Id,
                        outputPath ?? "(unknown)");
                    try
                    {
                        p.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not kill stale ffmpeg pid={Pid}", p.Id);
                    }

                    if (outputPath is not null
                        && (outputPath.Contains("mediadash.tmp", StringComparison.Ordinal)
                            || outputPath.Contains("mediadash.new", StringComparison.Ordinal))
                        && File.Exists(outputPath))
                    {
                        try
                        {
                            File.Delete(outputPath);
                        }
                        catch (IOException ex)
                        {
                            _logger.LogWarning(ex, "Could not delete stale sidecar {Path}", outputPath);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MediaDash ffmpeg sweep failed");
        }
    }
}
