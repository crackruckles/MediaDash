using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

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
    private static readonly Regex LuidRegex = new(@"luid_(0x[0-9a-fA-F]+)_(0x[0-9a-fA-F]+)", RegexOptions.Compiled);

    // PDH "Utilization Percentage" is a rate-style counter — the first NextValue() call on a freshly-constructed
    // PerformanceCounter always returns 0 because there's no previous sample to compute a delta against. Cache
    // the counters between polls so the second sample (3s later) can compute a real value.
    // Typed as IDisposable (not Dictionary<string, PerformanceCounter>) so the PerformanceCounter TYPE isn't
    // resolved at class-load time — on Linux the Windows-specific assembly wouldn't load and the plugin would
    // fail to initialise. Cast inside Windows-guarded code paths.
    private static readonly Dictionary<string, IDisposable> WinGpuCounters = new(StringComparer.OrdinalIgnoreCase);

    private static TimeSpan _lastProcCpuTime;
    private static DateTime _lastProcCpuSample;

    private static (long Idle, long Kernel, long User)? _lastWinCpu;
    private static (long Total, long Idle)? _lastLinuxCpu;

    private static List<GpuInfo>? _cachedNvidiaGpus;
    private static DateTime _lastGpuSample = DateTime.MinValue;
    private static bool _gpuAvailable = true;

    private static List<GpuInfo>? _cachedSysfsGpus;
    private static DateTime _lastSysfsGpuSample = DateTime.MinValue;
    private static bool _sysfsGpuAvailable = true;

    private static List<GpuInfo>? _cachedWinGpus;
    private static DateTime _lastWinGpuSample = DateTime.MinValue;
    private static bool _winGpuAvailable = true;

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

    /// <summary>Gets or sets the mean GPU utilization across detected GPUs. Kept for backward compat; UI should prefer <see cref="Gpus"/>.</summary>
    public double? GpuPercent { get; set; }

    /// <summary>Gets or sets a short label identifying where the aggregate GPU number came from. Null when no GPU data is available.</summary>
    public string? GpuSource { get; set; }

    /// <summary>Gets or sets per-GPU utilization details. Empty when no GPU counter is available on this host.</summary>
    public IReadOnlyList<GpuInfo> Gpus { get; set; } = [];

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
        try
        {
            return SampleCore();
        }
        catch (Exception ex)
        {
            // Anything the fine-grained catches below miss (a runtime issue in the Windows-only PerformanceCounter
            // path on Linux, a permission error in /proc, an assembly-load failure) surfaces here so the Errors
            // tab has something to look at instead of the API 500ing without explanation.
            Diagnostics.Record("SystemStats", ex.GetType().Name + ": " + ex.Message);
            return new SystemStats
            {
                CpuCoreCount = Environment.ProcessorCount,
                Platform = _cachedPlatform ?? "Unknown"
            };
        }
    }

    private static SystemStats SampleCore()
    {
        lock (SampleLock)
        {
            DetectPlatform();

            using var proc = Process.GetCurrentProcess();
            proc.Refresh();

            var gpus = SampleGpus();
            var stats = new SystemStats
            {
                CpuPercent = SampleProcessCpuPercent(proc),
                RamUsedBytes = proc.WorkingSet64,
                RamTotalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                Gpus = gpus,
                GpuPercent = gpus.Count == 0 ? null : gpus.Max(g => g.Percent),
                GpuSource = gpus.Count == 0 ? null : gpus[0].Source,
                CpuCoreCount = Environment.ProcessorCount,
                Platform = _cachedPlatform ?? "Unknown"
            };

            // Try to sample system-wide regardless of container status. cgroup v2 hosts and many container
            // runtimes still expose /proc and /sys correctly; the previous strict gate was silently hiding
            // working numbers. macOS is left null — no cross-platform path we've plumbed yet.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
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

            // Derived from what we actually got — so a container host that DOES expose /proc still shows data.
            stats.SystemStatsAvailable = stats.SystemCpuPercent is not null || stats.SystemRamTotalBytes is not null;

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
                // LXC is intentionally NOT flagged here — Proxmox and similar LXC hosts pass through /proc,
                // so we can still sample system-wide there. Only the container runtimes that consistently
                // sandbox /proc are flagged (docker, kubernetes, containerd).
                if (cg.Contains("docker", StringComparison.OrdinalIgnoreCase)
                    || cg.Contains("kubepods", StringComparison.OrdinalIgnoreCase)
                    || cg.Contains("containerd", StringComparison.OrdinalIgnoreCase))
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
        catch (IOException ex)
        {
            Diagnostics.Record("SystemStats.Linux", ex.Message);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            Diagnostics.Record("SystemStats.Linux", ex.Message);
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
        catch (IOException ex)
        {
            Diagnostics.Record("SystemStats.Linux", ex.Message);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            Diagnostics.Record("SystemStats.Linux", ex.Message);
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

    private static List<GpuInfo> SampleGpus()
    {
        // On Windows we prefer the PDH source because it enumerates every physical GPU (integrated + dedicated)
        // in one pass. nvidia-smi only sees NVIDIA cards, which would hide an iGPU that shares the box. On Linux
        // sysfs covers AMD/Intel, nvidia-smi covers NVIDIA — combine them so users with mixed setups see everything.
        var results = new List<GpuInfo>();
        var nvidia = SampleNvidiaGpus() ?? new List<GpuInfo>();
        results.AddRange(nvidia);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var wins = SampleWindowsGpus();
            if (wins is not null)
            {
                // Windows PDH already sees the NVIDIA cards; only add PDH data for physical devices not covered by nvidia-smi.
                foreach (var w in wins)
                {
                    if (!nvidia.Any(n => string.Equals(n.Name, w.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        results.Add(w);
                    }
                }
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var sysfs = SampleLinuxSysfsGpus();
            if (sysfs is not null)
            {
                foreach (var s in sysfs)
                {
                    if (!nvidia.Any(n => string.Equals(n.Name, s.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        results.Add(s);
                    }
                }
            }
        }

        // Re-index in enumeration order so the UI has a stable, dense set of GPU indices.
        for (var i = 0; i < results.Count; i++)
        {
            results[i].Index = i;
        }

        return results;
    }

    [SupportedOSPlatform("windows")]
    private static List<GpuInfo>? SampleWindowsGpus()
    {
        if (!_winGpuAvailable)
        {
            return null;
        }

        if ((DateTime.UtcNow - _lastWinGpuSample).TotalSeconds < 3)
        {
            return _cachedWinGpus;
        }

        _lastWinGpuSample = DateTime.UtcNow;
        try
        {
            if (!PerformanceCounterCategory.Exists("GPU Engine"))
            {
                _winGpuAvailable = false;
                return null;
            }

            var category = new PerformanceCounterCategory("GPU Engine");
            var instances = category.GetInstanceNames();
            if (instances.Length == 0)
            {
                _winGpuAvailable = false;
                return null;
            }

            // Reconcile cached counters with the current instance list. Stale PIDs (processes that stopped
            // using the GPU) get disposed; new ones (like ffmpeg starting a transcode) get created and primed
            // with a throwaway NextValue() call so the *next* poll returns a real delta.
            var currentSet = new HashSet<string>(instances, StringComparer.OrdinalIgnoreCase);
            foreach (var stale in WinGpuCounters.Keys.Where(k => !currentSet.Contains(k)).ToList())
            {
                WinGpuCounters[stale].Dispose();
                WinGpuCounters.Remove(stale);
            }

            foreach (var newInst in currentSet)
            {
                if (WinGpuCounters.ContainsKey(newInst))
                {
                    continue;
                }

                try
                {
                    var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", newInst, readOnly: true);
                    pc.NextValue(); // Prime — first call is always 0 on rate counters. Discarded intentionally.
                    WinGpuCounters[newInst] = pc;
                }
                catch (InvalidOperationException)
                {
                    // Instance disappeared between GetInstanceNames() and construction. Skip and try next poll.
                }
            }

            static double ReadCounter(IDisposable counter) => ((PerformanceCounter)counter).NextValue();

            // Instance names look like:
            //   pid_1234_luid_0x00000000_0x0000ABCD_phys_0_eng_3_engtype_3D
            // The luid identifies a physical GPU. Within a card there are multiple engines (3D / VideoDecode /
            // VideoEncode / Copy) potentially running in parallel across many processes. Task Manager's headline
            // GPU% is the MAX across engines per adapter — a busy 3D engine + idle copy engine shows as
            // whatever the 3D engine is doing, not sum. Match that so our numbers line up with Windows.
            var perLuid = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var perEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (instance, pc) in WinGpuCounters)
            {
                var luid = ExtractLuid(instance);
                if (luid is null)
                {
                    continue;
                }

                double val;
                try
                {
                    val = ReadCounter(pc);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                // Sum concurrent processes on the same engine, then take max across engines per LUID.
                var engineKey = luid + "|" + ExtractEngineType(instance);
                perEngine[engineKey] = (perEngine.TryGetValue(engineKey, out var running) ? running : 0) + val;
            }

            foreach (var (engineKey, engineTotal) in perEngine)
            {
                var luid = engineKey.Split('|')[0];
                var capped = Math.Min(100, engineTotal);
                if (!perLuid.TryGetValue(luid, out var existing) || capped > existing)
                {
                    perLuid[luid] = capped;
                }
            }

            if (perLuid.Count == 0)
            {
                _winGpuAvailable = false;
                return null;
            }

            var gpus = new List<GpuInfo>();
            var index = 0;
            foreach (var (luid, pct) in perLuid.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                gpus.Add(new GpuInfo
                {
                    Index = index++,
                    Name = $"GPU {luid}",
                    Percent = Math.Clamp(pct, 0, 100),
                    Source = "Windows PDH"
                });
            }

            _cachedWinGpus = gpus;
            return gpus;
        }
        catch (UnauthorizedAccessException ex)
        {
            Diagnostics.Record("SystemStats.WindowsGPU", ex.Message);
            _winGpuAvailable = false;
            return null;
        }
        catch (InvalidOperationException ex)
        {
            Diagnostics.Record("SystemStats.WindowsGPU", ex.Message);
            _winGpuAvailable = false;
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Diagnostics.Record("SystemStats.WindowsGPU", ex.Message);
            _winGpuAvailable = false;
            return null;
        }
    }

    private static string? ExtractLuid(string instanceName)
    {
        var m = LuidRegex.Match(instanceName);
        return m.Success ? $"{m.Groups[1].Value}:{m.Groups[2].Value}" : null;
    }

    private static string ExtractEngineType(string instanceName)
    {
        // Instance format: pid_X_luid_Y_Z_phys_N_eng_E_engtype_TYPE. Return TYPE (e.g., "3D", "VideoDecode").
        var idx = instanceName.LastIndexOf("engtype_", StringComparison.Ordinal);
        return idx < 0 ? "unknown" : instanceName[(idx + "engtype_".Length)..];
    }

    private static List<GpuInfo>? SampleNvidiaGpus()
    {
        if (!_gpuAvailable)
        {
            return null;
        }

        if ((DateTime.UtcNow - _lastGpuSample).TotalSeconds < 3)
        {
            return _cachedNvidiaGpus;
        }

        _lastGpuSample = DateTime.UtcNow;
        try
        {
            var psi = new ProcessStartInfo(NvidiaSmiCommand, "--query-gpu=index,name,utilization.gpu --format=csv,noheader,nounits")
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

                return _cachedNvidiaGpus;
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

            var gpus = new List<GpuInfo>();
            foreach (var line in lines)
            {
                var parts = line.Split(',');
                if (parts.Length < 3)
                {
                    continue;
                }

                if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx)
                    || !double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var util))
                {
                    continue;
                }

                gpus.Add(new GpuInfo
                {
                    Index = idx,
                    Name = parts[1].Trim(),
                    Percent = util,
                    Source = "NVIDIA"
                });
            }

            _cachedNvidiaGpus = gpus;
            return gpus;
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

    [SupportedOSPlatform("linux")]
    private static List<GpuInfo>? SampleLinuxSysfsGpus()
    {
        if (!_sysfsGpuAvailable)
        {
            return null;
        }

        if ((DateTime.UtcNow - _lastSysfsGpuSample).TotalSeconds < 3)
        {
            return _cachedSysfsGpus;
        }

        _lastSysfsGpuSample = DateTime.UtcNow;
        try
        {
            if (!Directory.Exists("/sys/class/drm/"))
            {
                _sysfsGpuAvailable = false;
                return null;
            }

            var gpus = new List<GpuInfo>();
            foreach (var card in Directory.EnumerateDirectories("/sys/class/drm/", "card*").OrderBy(c => c, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(card);
                // Only card entries with a device/gpu_busy_percent report — connector nodes (card0-VGA-1 etc.)
                // and card entries whose driver doesn't expose the counter (some Intel iGPUs on old kernels) are skipped.
                var busyFile = Path.Combine(card, "device/gpu_busy_percent");
                if (!File.Exists(busyFile))
                {
                    continue;
                }

                var raw = File.ReadAllText(busyFile).Trim();
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                {
                    continue;
                }

                gpus.Add(new GpuInfo
                {
                    Index = gpus.Count,
                    Name = ReadSysfsVendorLabel(card) ?? name,
                    Percent = v,
                    Source = "Linux sysfs"
                });
            }

            if (gpus.Count == 0)
            {
                _sysfsGpuAvailable = false;
                return null;
            }

            _cachedSysfsGpus = gpus;
            return gpus;
        }
        catch (IOException)
        {
            return _cachedSysfsGpus;
        }
        catch (UnauthorizedAccessException)
        {
            _sysfsGpuAvailable = false;
            return null;
        }
    }

    [SupportedOSPlatform("linux")]
    private static string? ReadSysfsVendorLabel(string cardDir)
    {
        // vendor is a PCI vendor ID in the form 0x1002 (AMD) / 0x10de (NVIDIA) / 0x8086 (Intel).
        // Best-effort naming so a user with an iGPU + dGPU can tell them apart without exact model names.
        try
        {
            var vendorFile = Path.Combine(cardDir, "device/vendor");
            if (!File.Exists(vendorFile))
            {
                return null;
            }

            var vendor = File.ReadAllText(vendorFile).Trim();
            return vendor.ToLowerInvariant() switch
            {
                "0x1002" => $"AMD ({Path.GetFileName(cardDir)})",
                "0x10de" => $"NVIDIA ({Path.GetFileName(cardDir)})",
                "0x8086" => $"Intel ({Path.GetFileName(cardDir)})",
                _ => Path.GetFileName(cardDir)
            };
        }
        catch (IOException)
        {
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
