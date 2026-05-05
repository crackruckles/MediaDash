using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MediaDash.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// All data-gathering logic for MediaDash.
/// Injected into the API controller via Jellyfin's DI container.
/// </summary>
public sealed class MediaDashService
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<MediaDashService> _logger;

    public MediaDashService(
        ISessionManager sessionManager,
        ILogger<MediaDashService> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance!.Configuration;

    // ── Disk ──────────────────────────────────────────────────────────────

    public DiskInfo GetDisk()
    {
        var root = Config.MediaRootPath;
        if (!Directory.Exists(root))
            root = "/";

        var info = new DriveInfo(root);
        var total = (double)info.TotalSize;
        var free  = (double)info.AvailableFreeSpace;
        var used  = total - free;
        return new DiskInfo(
            Math.Round(total / 1e9, 1),
            Math.Round(used  / 1e9, 1),
            Math.Round(free  / 1e9, 1),
            Math.Round(used  / total * 100, 1));
    }

    // ── Metrics ───────────────────────────────────────────────────────────

    public MetricsInfo GetMetrics()
    {
        var cpuPct  = GetCpuPercent();
        var perCore = GetPerCoreCpu();
        var mem     = GC.GetGCMemoryInfo();

        // Total physical memory via /proc/meminfo on Linux
        long memTotalBytes = GetTotalMemoryBytes();
        long memUsedBytes  = memTotalBytes - GetAvailableMemoryBytes();

        var gpuPct   = ReadSysFsInt("/sys/class/drm/card0/device/gpu_busy_percent");
        var vramUsed = ReadSysFsLong("/sys/class/drm/card0/device/mem_info_vram_used");
        var vramTotal= ReadSysFsLong("/sys/class/drm/card0/device/mem_info_vram_total");

        var temps = new TemperatureInfo(
            ReadHwmonTemp("k10temp", "Tctl"),
            ReadHwmonTemp("amdgpu",  null),
            ReadHwmonTemp("nvme",    null));

        var (diskR, diskW) = GetDiskIo();

        return new MetricsInfo(
            CpuPct:     cpuPct,
            PerCore:    perCore,
            MemUsedMb:  memUsedBytes  / 1_048_576,
            MemTotalMb: memTotalBytes / 1_048_576,
            MemPct:     memTotalBytes > 0
                            ? Math.Round((double)memUsedBytes / memTotalBytes * 100, 1)
                            : 0,
            GpuPct:     gpuPct,
            VramUsedMb: vramUsed  / 1_048_576,
            VramTotalMb:vramTotal / 1_048_576,
            Temps:      temps,
            DiskReadGb: diskR / 1_073_741_824L,
            DiskWriteGb:diskW / 1_073_741_824L);
    }

    private static double _lastCpuIdle, _lastCpuTotal;

    private static double GetCpuPercent()
    {
        try
        {
            var line = File.ReadLines("/proc/stat").First();
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            double user = double.Parse(parts[1]), nice = double.Parse(parts[2]),
                   sys  = double.Parse(parts[3]), idle = double.Parse(parts[4]),
                   iowait = double.Parse(parts[5]);
            double total = user + nice + sys + idle + iowait;
            double diffTotal = total - _lastCpuTotal;
            double diffIdle  = idle  - _lastCpuIdle;
            _lastCpuTotal = total; _lastCpuIdle = idle;
            if (diffTotal == 0) return 0;
            return Math.Round((1 - diffIdle / diffTotal) * 100, 1);
        }
        catch { return 0; }
    }

    private static int[] GetPerCoreCpu()
    {
        try
        {
            var lines = File.ReadLines("/proc/stat")
                            .Where(l => l.StartsWith("cpu") && l.Length > 3 && char.IsDigit(l[3]))
                            .ToArray();
            return lines.Select(l =>
            {
                var p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                double user = double.Parse(p[1]), sys = double.Parse(p[3]),
                       idle = double.Parse(p[4]);
                double total = user + sys + idle;
                return total > 0 ? (int)Math.Round((user + sys) / total * 100) : 0;
            }).ToArray();
        }
        catch { return Array.Empty<int>(); }
    }

    private static long GetTotalMemoryBytes()
    {
        try
        {
            var line = File.ReadLines("/proc/meminfo")
                           .First(l => l.StartsWith("MemTotal:"));
            return long.Parse(line.Split(':', 2)[1].Trim().Split(' ')[0]) * 1024;
        }
        catch { return Environment.WorkingSet; }
    }

    private static long GetAvailableMemoryBytes()
    {
        try
        {
            var line = File.ReadLines("/proc/meminfo")
                           .First(l => l.StartsWith("MemAvailable:"));
            return long.Parse(line.Split(':', 2)[1].Trim().Split(' ')[0]) * 1024;
        }
        catch { return 0; }
    }

    private static int ReadSysFsInt(string path)
    {
        try { return int.Parse(File.ReadAllText(path).Trim()); }
        catch { return 0; }
    }

    private static long ReadSysFsLong(string path)
    {
        try { return long.Parse(File.ReadAllText(path).Trim()); }
        catch { return 0; }
    }

    private static double? ReadHwmonTemp(string driver, string? labelContains)
    {
        try
        {
            var hwmonBase = "/sys/class/hwmon";
            if (!Directory.Exists(hwmonBase)) return null;

            foreach (var hwmon in Directory.GetDirectories(hwmonBase))
            {
                var namePath = Path.Combine(hwmon, "name");
                if (!File.Exists(namePath)) continue;
                var name = File.ReadAllText(namePath).Trim();
                if (!name.Equals(driver, StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var tempInput in Directory.GetFiles(hwmon, "temp*_input"))
                {
                    if (labelContains != null)
                    {
                        var labelPath = tempInput.Replace("_input", "_label");
                        if (!File.Exists(labelPath)) continue;
                        var label = File.ReadAllText(labelPath).Trim();
                        if (!label.Contains(labelContains, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }
                    var raw = long.Parse(File.ReadAllText(tempInput).Trim());
                    return Math.Round(raw / 1000.0, 1);
                }
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private static (long Read, long Write) GetDiskIo()
    {
        try
        {
            long totalRead = 0, totalWrite = 0;
            foreach (var line in File.ReadLines("/proc/diskstats"))
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 10) continue;
                // Only physical disks (sda, nvme0n1, etc.) — skip partitions
                var dev = p[2];
                if (!Regex.IsMatch(dev, @"^(sd[a-z]|nvme\d+n\d+|hd[a-z]|vd[a-z])$"))
                    continue;
                totalRead  += long.Parse(p[5])  * 512;
                totalWrite += long.Parse(p[9])  * 512;
            }
            return (totalRead, totalWrite);
        }
        catch { return (0, 0); }
    }

    // ── Active Jellyfin streams (uses injected ISessionManager) ──────────

    public StreamInfo[] GetStreams()
    {
        var sessions = _sessionManager.Sessions;
        var result = new List<StreamInfo>();
        foreach (var s in sessions)
        {
            var np = s.NowPlayingItem;
            if (np == null) continue;
            if (s.PlayState?.IsPaused == true) continue;

            long ticks = s.PlayState?.PositionTicks ?? 0;
            long total = Math.Max(np.RunTimeTicks ?? 1, 1);
            result.Add(new StreamInfo(
                User:        s.UserName ?? "?",
                Client:      s.Client   ?? "",
                Device:      s.DeviceName ?? "",
                Title:       np.Name ?? "",
                Type:        np.Type.ToString(),
                Series:      np.SeriesName ?? "",
                ProgressPct: Math.Round((double)ticks / total * 100, 1)));
        }
        return result.ToArray();
    }

    // ── Encoder status ────────────────────────────────────────────────────

    public EncodeStatusResponse GetEncodeStatus()
    {
        var statusDir = Config.EncodeStatusDir;
        var slots = new List<WorkerStatus>();

        if (Directory.Exists(statusDir))
        {
            foreach (var f in Directory.GetFiles(statusDir, "slot_*.json")
                                       .OrderBy(f => f))
            {
                try
                {
                    var doc = JsonDocument.Parse(File.ReadAllText(f));
                    slots.Add(ParseWorkerStatus(doc.RootElement));
                }
                catch { /* corrupt slot file — skip */ }
            }
        }

        // Fallback: single-file legacy format
        if (slots.Count == 0)
        {
            var legacy = Path.Combine(
                Path.GetTempPath(), "encode_status.json");
            if (File.Exists(legacy))
            {
                try
                {
                    var doc = JsonDocument.Parse(File.ReadAllText(legacy));
                    slots.Add(ParseWorkerStatus(doc.RootElement));
                }
                catch { /* ignore */ }
            }
        }

        if (slots.Count == 0)
            return new EncodeStatusResponse(false, null, 0, null, null, 0,
                0, 0, 0, 0, 0, null, null, Array.Empty<WorkerStatus>());

        var active  = slots.Where(s => s.Active).ToArray();
        var primary = active.FirstOrDefault() ?? slots[0];

        return new EncodeStatusResponse(
            Active:      primary.Active,
            Name:        primary.Name,
            SourceGb:    primary.SourceGb,
            Codec:       primary.Codec,
            StartedAt:   primary.StartedAt,
            DurationS:   primary.DurationS,
            Pct:         primary.Pct,
            ElapsedS:    primary.ElapsedS,
            TmpSizeGb:   primary.TmpSizeGb,
            EstFinalGb:  primary.EstFinalGb,
            EstSavingGb: primary.EstSavingGb,
            Fps:         primary.Fps,
            Speed:       primary.Speed,
            AllWorkers:  slots.ToArray());
    }

    private static WorkerStatus ParseWorkerStatus(JsonElement el)
    {
        return new WorkerStatus(
            Active:      el.TryGetBool("active"),
            Name:        el.TryGetStr("name"),
            SourceGb:    el.TryGetDbl("source_gb"),
            Codec:       el.TryGetStr("codec"),
            StartedAt:   el.TryGetStr("started_at"),
            DurationS:   (int)el.TryGetDbl("duration_s"),
            Pct:         el.TryGetDbl("pct"),
            ElapsedS:    (int)el.TryGetDbl("elapsed_s"),
            TmpSizeGb:   el.TryGetDbl("tmp_size_gb"),
            EstFinalGb:  el.TryGetDbl("est_final_gb"),
            EstSavingGb: el.TryGetDbl("est_saving_gb"),
            Fps:         el.TryGetStr("fps"),
            Speed:       el.TryGetStr("speed"),
            Worker:      (int)el.TryGetDbl("worker"));
    }

    // ── Encode remaining queue count ──────────────────────────────────────

    public QueueItem[] GetEncodeRemaining()
    {
        var stateFile  = Config.ReencodeStateFile;
        var mediaRoot  = Config.MediaRootPath;
        var exts       = Config.VideoExtensions.Split(',',
                             StringSplitOptions.RemoveEmptyEntries)
                             .Select(e => e.Trim().ToLowerInvariant())
                             .ToHashSet();

        Dictionary<string, double> state = new();
        if (File.Exists(stateFile))
        {
            try
            {
                var doc = JsonDocument.Parse(File.ReadAllText(stateFile));
                foreach (var prop in doc.RootElement.EnumerateObject())
                    state[prop.Name] = prop.Value.GetDouble();
            }
            catch { /* corrupt state — treat as empty */ }
        }

        var results = new List<QueueItem>();
        if (!Directory.Exists(mediaRoot)) return results.ToArray();

        foreach (var file in Directory.EnumerateFiles(
                     mediaRoot, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (!exts.Contains(ext)) continue;
            if (file.Contains(".reencode_tmp", StringComparison.Ordinal)) continue;

            try
            {
                var info  = new FileInfo(file);
                double mtime = info.LastWriteTimeUtc
                                   .Subtract(DateTime.UnixEpoch).TotalSeconds;
                if (state.TryGetValue(file, out var done) &&
                    Math.Abs(done - mtime) < 1.0)
                    continue;

                results.Add(new QueueItem(
                    info.Name,
                    Math.Round(info.Length / 1e9, 2),
                    file));
            }
            catch { /* file disappeared — skip */ }
        }

        return results.ToArray();
    }

    // ── Encoding / stripping active (process detection) ───────────────────

    public bool IsProcessActive(string nameFragment)
    {
        if (string.IsNullOrWhiteSpace(nameFragment)) return false;
        try
        {
            foreach (var dir in Directory.GetDirectories("/proc")
                                         .Where(d => int.TryParse(
                                             Path.GetFileName(d), out _)))
            {
                var cmdline = Path.Combine(dir, "cmdline");
                if (!File.Exists(cmdline)) continue;
                var text = File.ReadAllText(cmdline)
                               .Replace('\0', ' ');
                if (text.Contains(nameFragment,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { /* /proc race — ignore */ }
        return false;
    }

    // ── Status overview ───────────────────────────────────────────────────

    public StatusInfo GetStatus()
    {
        var cfg = Config;
        var paused  = File.Exists(cfg.PauseFlagPath);
        var h       = DateTime.Now.Hour;
        var ps      = cfg.QuietHoursStart;
        var pe      = cfg.QuietHoursEnd;
        var inQuiet = ps < pe ? (h >= ps && h < pe) : (h >= ps || h < pe);

        var encoding  = IsProcessActive(cfg.ReencodeProcessName);
        var stripping = IsProcessActive(cfg.StripProcessName);
        var streams   = GetStreams();

        return new StatusInfo(
            Paused:          paused,
            InQuietHours:    inQuiet,
            EncodingActive:  encoding,
            StrippingActive: stripping,
            JellyfinStreams: streams,
            StreamCount:     streams.Length,
            Schedule:        new ScheduleInfo(ps, pe),
            Time:            DateTime.Now.ToString("HH:mm"));
    }

    // ── Strip log parser ──────────────────────────────────────────────────

    public StripEntry[] ParseStripLog()
    {
        var path = Config.StripLogPath;
        if (!File.Exists(path)) return Array.Empty<StripEntry>();

        var results = new Dictionary<string, (int audio, int subs, double savedMb, string status)>();
        string? current = null;

        foreach (var line in File.ReadLines(path))
        {
            var m = Regex.Match(line, @"Inspecting: (.+)$");
            if (m.Success)
            {
                current = m.Groups[1].Value.Trim();
                results[current] = (0, 0, 0, "clean");
                continue;
            }
            if (current == null) continue;

            var (a, s, mb, st) = results[current];
            if (line.Contains("Audio track") && line.Contains("-> DROP"))
                results[current] = (a + 1, s, mb, "processed");
            else if (line.Contains("Sub   track") && line.Contains("-> DROP"))
                results[current] = (a, s + 1, mb, "processed");

            var m2 = Regex.Match(line, @"Done\. [\d.]+MB -> [\d.]+MB \(saved ([\d.]+)MB\)");
            if (m2.Success)
                results[current] = (a, s, double.Parse(m2.Groups[1].Value), st);
        }

        return results.Select(kv => new StripEntry(
            kv.Key, kv.Value.audio, kv.Value.subs,
            kv.Value.savedMb, kv.Value.status)).ToArray();
    }

    // ── Reencode log parser ───────────────────────────────────────────────

    public ReencodeEntry[] ParseReencodeLog()
    {
        var path = Config.ReencodeLogPath;
        if (!File.Exists(path)) return Array.Empty<ReencodeEntry>();

        var results = new Dictionary<string, ReencodeEntry>();
        string? current = null;

        foreach (var line in File.ReadLines(path))
        {
            var m = Regex.Match(line,
                @"Encoding: (.+?) \(([\d.]+)GB (\w+)\)");
            if (m.Success)
            {
                current = m.Groups[1].Value.Trim();
                results[current] = new ReencodeEntry(
                    current, double.Parse(m.Groups[2].Value),
                    m.Groups[3].Value, 0, 0, 0, "encoding", false);
                continue;
            }
            if (current == null) continue;

            var m2 = Regex.Match(line,
                @"Done in (\d+)min: ([\d.]+)GB->([\d.]+)GB");
            if (m2.Success)
            {
                var before = double.Parse(m2.Groups[2].Value);
                var after  = double.Parse(m2.Groups[3].Value);
                var entry  = results[current];
                results[current] = entry with
                {
                    ElapsedMin = int.Parse(m2.Groups[1].Value),
                    BeforeGb   = before,
                    AfterGb    = after,
                    SavedGb    = Math.Round(before - after, 1),
                    Status     = "done",
                };
            }
            else if (line.Contains("Output too small") &&
                     results.TryGetValue(current, out var e) &&
                     e.Status == "encoding")
            {
                results[current] = e with { Status = "failed" };
            }
            else if (line.Contains("stream active") &&
                     results.TryGetValue(current, out var e2))
            {
                results[current] = e2 with { StreamPaused = true };
            }
        }

        return results.Values.ToArray();
    }

    // ── Dupes report ──────────────────────────────────────────────────────

    public DupesReport GetDupesReport()
    {
        var path = Config.DupesReportPath;
        if (!File.Exists(path))
            return new DupesReport(0, 0, "0B", Array.Empty<DupeGroup>(), null);
        try
        {
            return JsonSerializer.Deserialize<DupesReport>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new DupesReport(0, 0, "0B", Array.Empty<DupeGroup>(), null);
        }
        catch { return new DupesReport(0, 0, "0B", Array.Empty<DupeGroup>(), null); }
    }

    // ── Actions ───────────────────────────────────────────────────────────

    public void Pause()
        => File.WriteAllText(Config.PauseFlagPath, "");

    public void Resume()
    {
        if (File.Exists(Config.PauseFlagPath))
            File.Delete(Config.PauseFlagPath);
        File.WriteAllText(Config.ForceFlagPath, "");
    }

    public bool StartService(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) return false;
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName  = "systemctl",
                Arguments = $"start {serviceName}.service",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            });
            p?.WaitForExit(5000);
            return true;
        }
        catch { return false; }
    }

    public bool StartDupesScan()
    {
        var script = Config.DupesScanScript;
        if (string.IsNullOrWhiteSpace(script) || !File.Exists(script))
            return false;
        Process.Start(new ProcessStartInfo
        {
            FileName  = "python3",
            Arguments = script,
            UseShellExecute = false,
        });
        return true;
    }

    public DeleteResponse DeleteDupes(string? imdbFilter)
    {
        var report = GetDupesReport();
        if (report.Groups.Length == 0)
            return new DeleteResponse(true, 0, 0, Array.Empty<string>());

        int deleted = 0; long freed = 0;
        var errors = new List<string>();

        foreach (var g in report.Groups)
        {
            if (imdbFilter != null && g.Imdb != imdbFilter) continue;
            foreach (var f in g.Duplicates)
            {
                try
                {
                    if (!File.Exists(f.Path))
                    { errors.Add($"{f.Path}: not found"); continue; }
                    var size = new FileInfo(f.Path).Length;
                    File.Delete(f.Path);
                    deleted++;
                    freed += size;
                }
                catch (Exception ex)
                { errors.Add($"{f.Path}: {ex.Message}"); }
            }
        }

        StartDupesScan();          // regenerate report after deletion
        return new DeleteResponse(true, deleted,
            Math.Round(freed / 1e9, 2), errors.ToArray());
    }

    public ScheduleSaveResponse SaveSchedule(int pauseStart, int pauseEnd)
    {
        Plugin.Instance!.Configuration.QuietHoursStart = pauseStart;
        Plugin.Instance!.Configuration.QuietHoursEnd   = pauseEnd;
        Plugin.Instance!.SaveConfiguration();
        return new ScheduleSaveResponse(true, pauseStart, pauseEnd);
    }
}

// ── JsonElement extension helpers ─────────────────────────────────────────────

internal static class JsonElementExtensions
{
    public static bool   TryGetBool(this JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
    public static string? TryGetStr(this JsonElement el, string key)
        => el.TryGetProperty(key, out var v) ? v.GetString() : null;
    public static double TryGetDbl(this JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.TryGetDouble(out var d) ? d : 0;
}
