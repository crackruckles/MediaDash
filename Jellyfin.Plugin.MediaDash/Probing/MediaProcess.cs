using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;

namespace Jellyfin.Plugin.MediaDash.Probing;

internal sealed class MediaProcess : IAsyncDisposable
{
    /// <summary>Most media processes MediaDash runs at once when Low system impact mode is off.</summary>
    internal const int MaxConcurrent = 2;

    private static readonly SemaphoreSlim Slot = new(MaxConcurrent, MaxConcurrent);

    // Low system impact mode keeps the old one-at-a-time behaviour: every process started in that
    // mode also holds this exclusive slot, so at most one low-impact process runs at a time.
    private static readonly SemaphoreSlim LowImpactSlot = new(1, 1);
    private readonly CancellationToken _cancellationToken;
    private readonly SemaphoreSlim? _exclusive;
    private bool _started;
    private IDisposable? _diagnosticOperation;

    private MediaProcess(SemaphoreSlim? exclusive, CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        _exclusive = exclusive;
    }

    internal Process Process { get; } = new();

    // Unknown configuration (tests, early startup) counts as low impact: one process at a time.
    internal static bool AllowsConcurrency => !(Plugin.Instance?.Configuration?.LowSystemImpactMode ?? true);

    internal static async Task<MediaProcess> AcquireAsync(CancellationToken cancellationToken)
    {
        using var operation = RunDiagnostics.Track("waiting for media process slot");
        SemaphoreSlim? exclusive = null;
        if (!AllowsConcurrency)
        {
            await LowImpactSlot.WaitAsync(cancellationToken).ConfigureAwait(false);
            exclusive = LowImpactSlot;
        }

        try
        {
            await Slot.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            exclusive?.Release();
            throw;
        }

        return new MediaProcess(exclusive, cancellationToken);
    }

    internal bool Start()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var executable = Path.GetFileNameWithoutExtension(Process.StartInfo.FileName);
        _diagnosticOperation = RunDiagnostics.Track(executable.ToLowerInvariant() switch
        {
            "ffprobe" => "FFprobe running",
            "ffmpeg" => "FFmpeg running",
            _ => "media process running",
        });
        _started = StartProcess(Process);
        return _started;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_started || Process.HasExited)
        {
            Release();
            return;
        }

        try
        {
            Process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The child may have exited between HasExited and Kill.
        }
        catch (Win32Exception error)
        {
            Api.Diagnostics.Record("MediaProcess.Stop", "Could not stop a MediaDash media process: " + error.Message);
        }

        // Keep ownership and the slot until the child exits, even if cancellation
        // returns to the caller before the OS finishes stopping the process.
        try
        {
            await ReleaseOnExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Api.Diagnostics.Record("MediaProcess.Stop", "A MediaDash media process is still stopping. The next media job will wait for it to exit.");
        }
    }

    private async Task ReleaseOnExitAsync()
    {
        try
        {
            await Process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Api.Diagnostics.Record("MediaProcess.Stop", "Could not confirm a MediaDash media process exited: " + error.Message);
            return;
        }

        Release();
    }

    private void Release()
    {
        _diagnosticOperation?.Dispose();
        Process.Dispose();
        Slot.Release();
        _exclusive?.Release();
    }

    private static bool StartProcess(Process process)
    {
        string? ionice = null;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && (Plugin.Instance?.Configuration?.LowSystemImpactMode ?? false))
        {
            ionice = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator)
                .Select(directory => Path.Combine(directory, "ionice"))
                .FirstOrDefault(File.Exists);
        }

        if (ionice is null)
        {
            return process.Start();
        }

        var executable = process.StartInfo.FileName;
        var arguments = process.StartInfo.ArgumentList.ToArray();
        WrapWithIonice(process.StartInfo, ionice);
        try
        {
            return process.Start();
        }
        catch (Win32Exception ex) when (ionice is not null && ex.NativeErrorCode is 2 or 13)
        {
            // The optional launcher disappeared or is not executable; no media process started.
            process.StartInfo.FileName = executable;
            process.StartInfo.ArgumentList.Clear();
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            return process.Start();
        }
    }

    internal static void WrapWithIonice(ProcessStartInfo startInfo, string? ionice)
    {
        if (ionice is null)
        {
            return;
        }

        // Set priority before exec so every ffmpeg I/O worker inherits it. -t tolerates kernel refusal.
        startInfo.ArgumentList.Insert(0, startInfo.FileName);
        startInfo.ArgumentList.Insert(0, "--");
        startInfo.ArgumentList.Insert(0, "3");
        startInfo.ArgumentList.Insert(0, "-c");
        startInfo.ArgumentList.Insert(0, "-t");
        startInfo.FileName = ionice;
    }
}
