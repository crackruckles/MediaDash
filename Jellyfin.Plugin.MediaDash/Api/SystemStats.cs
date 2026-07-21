using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Best-effort resource usage snapshot for the Overview tab.
/// System-wide CPU and memory on native Windows and Linux via GetSystemTimes / /proc;
/// null on macOS and inside containers (marked N/A in the UI).
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Win32 API struct field names must match native definitions.")]
[SuppressMessage("Design", "CA1051:Do not declare visible instance fields", Justification = "Win32 P/Invoke struct requires public fields.")]
[SuppressMessage("StyleCop.CSharp.NamingRules", "SA1307:Accessible fields should begin with upper-case letter", Justification = "Win32 MEMORYSTATUSEX field names are lower-camel by native definition.")]
public sealed class SystemStats
{
    private const string NvidiaSmiCommand = "nvidia-smi";

    private static readonly object SampleLock = new();
    private static readonly char[] MeminfoSeparators = [' ', '\t'];

    private static TimeSpan _lastProcCpuTime;
    private static DateTime _lastProcCpuSample;

    private static (long Idle, long Kernel, long User)? _lastWinCpu;
    private static (long Total, long Idle)? _lastLinuxCpu;

    private static double? _cachedGpuPercent;
    private static DateTime _lastGpuSample = DateTime.MinValue;
    private static bool _gpuAvailable = true;

    private static string? _cachedPlatform;
    private static bool _cachedInContainer;

    /// <summary>Gets or sets the Jellyfin process's CPU usage, 0-100. Null when a delta hasn't been sampled yet.</summary>
    public double? CpuPercent { get; set; }

    /// <summary>Gets or sets the whole-system CPU usage, 0-100. Null on macOS, in containers, or when sampling fails.</summary>
    public double? SystemCpuPercent { get; set; }

    /// <summary>Gets or sets the Jellyfin process's working set (RAM currently held by the process).</summary>
    public long RamUsedBytes { get; set; }

    /// <summary>Gets or sets the total RAM available to the process/host (as reported by the GC).</summary>
    public long RamTotalBytes { get; set; }

    /// <summary>Gets or sets whole-system RAM currently in use. Null on macOS, in containers, or when sampling fails.</summary>
    public long? SystemRamUsedBytes { get; set; }

    /// <summary>Gets or sets whole-system RAM total. Null on macOS, in containers, or when sampling fails.</summary>
    public long? SystemRamTotalBytes { get; set; }

    /// <summary>Gets or sets the mean GPU utilization across NVIDIA GPUs (nvidia-smi). Null when nvidia-smi isn't available.</summary>
    public double? GpuPercent { get; set; }

    /// <summary>Gets or sets the number of logical CPU cores on the host.</summary>
    public int CpuCoreCount { get; set; }

    /// <summary>Gets or sets a short platform label ("Windows", "Linux", "macOS", "Container") for the UI.</summary>
    public string Platform { get; set; } = "Unknown";

    /// <summary>Gets or sets a value indicating whether whole-system CPU/RAM stats are available on this host.</summary>
    public bool SystemStatsAvailable { get; set; }

    /// <summary>
    /// Samples the current resource usage. Safe to call frequently; internally caches expensive samples.
    /// </summary>
    /// <returns>The snapshot.</returns>
    public static SystemStats Sample()
    {
        lock (SampleLock)
        {
            DetectPlatform();

            using var proc = Process.GetCurrentProcess();
            proc.Refresh();

            var stats = new SystemStats
            {
                CpuPercent = SampleProcessCpuPercent(proc),
                RamUsedBytes = proc.WorkingSet64,
                RamTotalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                GpuPercent = SampleGpuPercent(),
                CpuCoreCount = Environment.ProcessorCount,
                Platform = _cachedPlatform ?? "Unknown",
                SystemStatsAvailable = !_cachedInContainer && (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            };

            if (stats.SystemStatsAvailable)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    stats.SystemCpuPercent = SampleWindowsCpuPercent();
                    var mem = SampleWindowsMemory();
                    if (mem is not null)
                    {
                        stats.SystemRamUsedBytes = mem.Value.Used;
                        stats.SystemRamTotalBytes = mem.Value.Total;
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    stats.SystemCpuPercent = SampleLinuxCpuPercent();
                    var mem = SampleLinuxMemory();
                    if (mem is not null)
                    {
                        stats.SystemRamUsedBytes = mem.Value.Used;
                        stats.SystemRamTotalBytes = mem.Value.Total;
                    }
                }
            }

            return stats;
        }
    }

    private static void DetectPlatform()
    {
        if (_cachedPlatform is not null)
        {
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            _cachedPlatform = "macOS";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            _cachedInContainer = IsInLinuxContainer();
            _cachedPlatform = _cachedInContainer ? "Container" : "Linux";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _cachedPlatform = "Windows";
        }
        else
        {
            _cachedPlatform = "Unknown";
        }
    }

    private static bool IsInLinuxContainer()
    {
        try
        {
            if (File.Exists("/.dockerenv"))
            {
                return true;
            }

            if (File.Exists("/proc/1/cgroup"))
            {
                var cg = File.ReadAllText("/proc/1/cgroup");
                if (cg.Contains("docker", StringComparison.OrdinalIgnoreCase)
                    || cg.Contains("kubepods", StringComparison.OrdinalIgnoreCase)
                    || cg.Contains("containerd", StringComparison.OrdinalIgnoreCase)
                    || cg.Contains("lxc", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static double? SampleProcessCpuPercent(Process proc)
    {
        var now = DateTime.UtcNow;
        var cpu = proc.TotalProcessorTime;
        if (_lastProcCpuSample == default)
        {
            _lastProcCpuTime = cpu;
            _lastProcCpuSample = now;
            return null;
        }

        var wallMs = (now - _lastProcCpuSample).TotalMilliseconds;
        var cpuMs = (cpu - _lastProcCpuTime).TotalMilliseconds;
        _lastProcCpuTime = cpu;
        _lastProcCpuSample = now;
        if (wallMs <= 0)
        {
            return null;
        }

        var cores = Math.Max(1, Environment.ProcessorCount);
        var pct = cpuMs / wallMs / cores * 100.0;
        return Math.Clamp(pct, 0, 100);
    }

    [SupportedOSPlatform("windows")]
    private static double? SampleWindowsCpuPercent()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
        {
            return null;
        }

        var idle = FileTimeToLong(idleFt);
        var kernel = FileTimeToLong(kernelFt);
        var user = FileTimeToLong(userFt);

        if (_lastWinCpu is null)
        {
            _lastWinCpu = (idle, kernel, user);
            return null;
        }

        var prev = _lastWinCpu.Value;
        _lastWinCpu = (idle, kernel, user);

        // Kernel time on Windows already includes idle time, so total = kernel + user.
        var totalDelta = (kernel - prev.Kernel) + (user - prev.User);
        var idleDelta = idle - prev.Idle;
        if (totalDelta <= 0)
        {
            return null;
        }

        var busy = totalDelta - idleDelta;
        return Math.Clamp(busy * 100.0 / totalDelta, 0, 100);
    }

    [SupportedOSPlatform("windows")]
    private static (long Used, long Total)? SampleWindowsMemory()
    {
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref ms))
        {
            return null;
        }

        var total = (long)ms.ullTotalPhys;
        var avail = (long)ms.ullAvailPhys;
        return (total - avail, total);
    }

    [SupportedOSPlatform("linux")]
    private static double? SampleLinuxCpuPercent()
    {
        try
        {
            var line = File.ReadLines("/proc/stat").FirstOrDefault();
            if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal))
            {
                return null;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            long total = 0, idle = 0;
            for (var i = 1; i < parts.Length; i++)
            {
                if (!long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                {
                    continue;
                }

                total += v;
                // Fields (kernel docs): user nice system idle iowait irq softirq steal guest guest_nice.
                // Idle-like time is field 4 (idle) + field 5 (iowait); we treat iowait as idle for %busy.
                if (i is 4 or 5)
                {
                    idle += v;
                }
            }

            if (_lastLinuxCpu is null)
            {
                _lastLinuxCpu = (total, idle);
                return null;
            }

            var prev = _lastLinuxCpu.Value;
            _lastLinuxCpu = (total, idle);

            var totalDelta = total - prev.Total;
            var idleDelta = idle - prev.Idle;
            if (totalDelta <= 0)
            {
                return null;
            }

            return Math.Clamp((totalDelta - idleDelta) * 100.0 / totalDelta, 0, 100);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("linux")]
    private static (long Used, long Total)? SampleLinuxMemory()
    {
        try
        {
            long total = 0, available = 0, free = 0, buffers = 0, cached = 0;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    total = ParseMeminfoLine(line);
                }
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                {
                    available = ParseMeminfoLine(line);
                }
                else if (line.StartsWith("MemFree:", StringComparison.Ordinal))
                {
                    free = ParseMeminfoLine(line);
                }
                else if (line.StartsWith("Buffers:", StringComparison.Ordinal))
                {
                    buffers = ParseMeminfoLine(line);
                }
                else if (line.StartsWith("Cached:", StringComparison.Ordinal))
                {
                    cached = ParseMeminfoLine(line);
                }
            }

            if (total <= 0)
            {
                return null;
            }

            // Prefer MemAvailable (modern kernels' honest "free RAM to apps"); fall back to free+buffers+cached.
            var availableBytes = available > 0 ? available : (free + buffers + cached);
            return (total - availableBytes, total);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long ParseMeminfoLine(string line)
    {
        // Format: "MemTotal:       16384000 kB"
        var parts = line.Split(MeminfoSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
        {
            return 0;
        }

        return kb * 1024L;
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
            _gpuAvailable = false;
            return null;
        }
        catch (FileNotFoundException)
        {
            _gpuAvailable = false;
            return null;
        }
    }

    private static long FileTimeToLong(long ft) => ft;

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
