using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Best-effort resource usage snapshot for the Overview tab.
/// Values are process-scoped where possible (Jellyfin.exe/jellyfin) — cross-platform means we can't
/// report the whole box without OS-specific plumbing, and this is meant to give the admin a live
/// pulse of the media server, not replace a full monitoring stack.
/// </summary>
public sealed class SystemStats
{
    // ponytail: nvidia-smi shell-out, cached for 3s. If nvidia-smi is missing (non-NVIDIA host, wrong PATH),
    // we give up permanently on this run so we don't spawn processes we know will fail.
    private const string NvidiaSmiCommand = "nvidia-smi";

    private static readonly object SampleLock = new();
    private static TimeSpan _lastCpuTime;
    private static DateTime _lastCpuSample;

    private static double? _cachedGpuPercent;
    private static DateTime _lastGpuSample = DateTime.MinValue;
    private static bool _gpuAvailable = true;

    /// <summary>Gets or sets the Jellyfin process's CPU usage, 0-100. Null when a delta hasn't been sampled yet.</summary>
    public double? CpuPercent { get; set; }

    /// <summary>Gets or sets the Jellyfin process's working set (RAM currently held by the process).</summary>
    public long RamUsedBytes { get; set; }

    /// <summary>Gets or sets the total RAM available to the process/host (as reported by the GC).</summary>
    public long RamTotalBytes { get; set; }

    /// <summary>Gets or sets the mean GPU utilization across NVIDIA GPUs (nvidia-smi). Null when nvidia-smi isn't available.</summary>
    public double? GpuPercent { get; set; }

    /// <summary>Gets or sets the number of logical CPU cores on the host.</summary>
    public int CpuCoreCount { get; set; }

    /// <summary>
    /// Samples the current resource usage. Safe to call frequently; internally caches expensive samples.
    /// </summary>
    /// <returns>The snapshot.</returns>
    public static SystemStats Sample()
    {
        lock (SampleLock)
        {
            using var proc = Process.GetCurrentProcess();
            proc.Refresh();

            return new SystemStats
            {
                CpuPercent = SampleProcessCpuPercent(proc),
                RamUsedBytes = proc.WorkingSet64,
                RamTotalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                GpuPercent = SampleGpuPercent(),
                CpuCoreCount = Environment.ProcessorCount
            };
        }
    }

    private static double? SampleProcessCpuPercent(Process proc)
    {
        var now = DateTime.UtcNow;
        var cpu = proc.TotalProcessorTime;
        if (_lastCpuSample == default)
        {
            _lastCpuTime = cpu;
            _lastCpuSample = now;
            return null;
        }

        var wallMs = (now - _lastCpuSample).TotalMilliseconds;
        var cpuMs = (cpu - _lastCpuTime).TotalMilliseconds;
        _lastCpuTime = cpu;
        _lastCpuSample = now;
        if (wallMs <= 0)
        {
            return null;
        }

        var cores = Math.Max(1, Environment.ProcessorCount);
        var pct = cpuMs / wallMs / cores * 100.0;
        return Math.Clamp(pct, 0, 100);
    }

    private static double? SampleGpuPercent()
    {
        if (!_gpuAvailable)
        {
            return null;
        }

        if ((DateTime.UtcNow - _lastGpuSample).TotalSeconds < 3)
        {
            return _cachedGpuPercent;
        }

        _lastGpuSample = DateTime.UtcNow;
        try
        {
            var psi = new ProcessStartInfo(NvidiaSmiCommand, "--query-gpu=utilization.gpu --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                _gpuAvailable = false;
                return null;
            }

            if (!p.WaitForExit(1500))
            {
                try
                {
                    p.Kill();
                }
                catch (InvalidOperationException)
                {
                }

                return _cachedGpuPercent;
            }

            if (p.ExitCode != 0)
            {
                _gpuAvailable = false;
                return null;
            }

            var lines = p.StandardOutput.ReadToEnd()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToArray();
            if (lines.Length == 0)
            {
                return null;
            }

            var sum = 0.0;
            var count = 0;
            foreach (var line in lines)
            {
                if (double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    sum += v;
                    count++;
                }
            }

            _cachedGpuPercent = count > 0 ? sum / count : (double?)null;
            return _cachedGpuPercent;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // nvidia-smi not on PATH.
            _gpuAvailable = false;
            return null;
        }
        catch (System.IO.FileNotFoundException)
        {
            _gpuAvailable = false;
            return null;
        }
    }
}
