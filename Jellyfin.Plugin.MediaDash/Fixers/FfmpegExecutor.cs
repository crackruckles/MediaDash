using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// <returns>The last portion of stderr on failure, or null on success.</returns>
    public async Task<string?> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var encoderPath = _mediaEncoder.EncoderPath;
        if (string.IsNullOrEmpty(encoderPath))
        {
            return "The server has no ffmpeg configured.";
        }

        using var process = new Process();
        process.StartInfo.FileName = encoderPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.ArgumentList.Add("-y");
        process.StartInfo.ArgumentList.Add("-v");
        process.StartInfo.ArgumentList.Add("error");
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
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var tail = stderr.Length > 2000 ? stderr[^2000..] : stderr;
                return string.IsNullOrWhiteSpace(tail) ? $"ffmpeg exited with code {process.ExitCode}" : tail;
            }

            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return $"ffmpeg exceeded the {timeout} time limit and was stopped";
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run ffmpeg at {Path}", encoderPath);
            return ex.Message;
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
            _logger.LogDebug(ex, "Could not kill ffmpeg process");
        }
    }
}
