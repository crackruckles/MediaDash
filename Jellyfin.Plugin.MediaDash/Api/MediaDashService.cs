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

/// <summary>All data-gathering logic — no hardcoded paths or hardware assumptions.</summary>
public sealed class MediaDashService
{
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MediaDashService> _logger;

    public MediaDashService(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        ILogger<MediaDashService> logger)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    // ── Media directories ──────────────────────────────────────────────────

    /// <summary>
    /// Returns configured media directories, falling back to Jellyfin\'s
    /// own library paths if none are configured.
    /// </summary>
    public IReadOnlyList<string> GetMediaDirectories()
    {
        var configured = Config.MediaDirectories ?? string.Empty;
        var dirs = configured
            .Split(new[] { '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim())
            .Where(d => !string.IsNullOrEmpty(d))
            .ToList();

        if (dirs.Count > 0)
            return dirs;

        // Fall back to Jellyfin library locations
        var jellyfinPaths = new List<string>();
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            foreach (var loc in folder.Locations)
            {
                if (!string.IsNullOrEmpty(loc) && !jellyfinPaths.Contains(loc))
                    jellyfinPaths.Add(loc);
            }
        }
        return jellyfinPaths;
    }

    // ── Disk info ──────────────────────────────────────────────────────────

    public IReadOnlyList<DriveStats> GetDrives()
    {
        var dirs = GetMediaDirectories();
        // Also always include the filesystem root so there's always something to show
        var candidates = dirs.Concat(new[] { "/" }).ToList();
        var seenMount = new HashSet<string>();
        var result    = new List<DriveStats>();

        foreach (var dir in candidates)
        {
            // Walk up until we hit a path that actually exists
            var path = dir;
            while (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
                path = Path.GetDirectoryName(path) ?? string.Empty;
            if (string.IsNullOrEmpty(path)) continue;

            try
            {
                var di    = new DriveInfo(path);
                var mount = di.RootDirectory.FullName;
                if (!seenMount.Add(mount)) continue;   // already reported

                var total = (double)di.TotalSize;
                var free  = (double)di.AvailableFreeSpace;
                var used  = total - free;

                // Label: use the configured library path when available,
                // otherwise fall back to the mount point
                var label = (dir == "/" || dir == mount) ? mount : dir;

                result.Add(new DriveStats(
                    Mount:   mount,
                    Label:   label,
                    TotalGb: Math.Round(total / 1e9, 1),
                    UsedGb:  Math.Round(used  / 1e9, 1),
                    FreeGb:  Math.Round(free  / 1e9, 1),
                    Pct:     Math.Round(used  / total * 100, 1)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not stat {Dir}: {Msg}", dir, ex.Message);
            }
        }
        return result;
    }

    // ── System metrics ─────────────────────────────────────────────────────

    public MetricsInfo GetMetrics()
    {
        var cpuPct  = GetCpuPercent();
        var perCore = GetPerCoreCpu();

        long memTotal = GetMemInfoKb("MemTotal")  * 1024;
        long memAvail = GetMemInfoKb("MemAvailable") * 1024;
        long memUsed  = memTotal - memAvail;
        double memPct = memTotal > 0 ? Math.Round((double)memUsed / memTotal * 100, 1) : 0;

        // GPU — discover dynamically, don\'t assume card0 or AMD
        var (gpuPct, vramUsed, vramTotal) = GetGpuStats();

        // Temperatures — probe all hwmon entries
        var temps = GetTemperatures();

        var (diskR, diskW) = GetDiskIo();

        return new MetricsInfo(
            CpuPct:      cpuPct,
            PerCore:     perCore,
            MemUsedMb:   memUsed  / 1_048_576,
            MemTotalMb:  memTotal / 1_048_576,
            MemPct:      memPct,
            GpuPct:      gpuPct,
            VramUsedMb:  vramUsed  / 1_048_576,
            VramTotalMb: vramTotal / 1_048_576,
            Temps:       temps,
            DiskReadGb:  diskR / 1_073_741_824L,
            DiskWriteGb: diskW / 1_073_741_824L);
    }

    // ── CPU ────────────────────────────────────────────────────────────────

    private static double _lastTotal, _lastIdle;

    private static double GetCpuPercent()
    {
        try
        {
            var line  = File.ReadLines("/proc/stat").First();
            var p     = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            double user   = double.Parse(p[1]), nice  = double.Parse(p[2]),
                   sys    = double.Parse(p[3]), idle  = double.Parse(p[4]),
                   iowait = double.Parse(p[5]);
            double total  = user + nice + sys + idle + iowait;
            double dTotal = total - _lastTotal;
            double dIdle  = idle  - _lastIdle;
            _lastTotal = total; _lastIdle = idle;
            return dTotal == 0 ? 0 : Math.Round((1 - dIdle / dTotal) * 100, 1);
        }
        catch { return 0; }
    }

    private static int[] GetPerCoreCpu()
    {
        try
        {
            return File.ReadLines("/proc/stat")
                .Where(l => l.Length > 3 && l.StartsWith("cpu") && char.IsDigit(l[3]))
                .Select(l => {
                    var p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    double u = double.Parse(p[1]), s = double.Parse(p[3]), id = double.Parse(p[4]);
                    double t = u + s + id;
                    return t > 0 ? (int)Math.Round((u + s) / t * 100) : 0;
                })
                .ToArray();
        }
        catch { return Array.Empty<int>(); }
    }

    private static long GetMemInfoKb(string key)
    {
        try
        {
            var line = File.ReadLines("/proc/meminfo")
                .FirstOrDefault(l => l.StartsWith(key + ":"));
            if (line == null) return 0;
            return long.Parse(line.Split(':', 2)[1].Trim().Split(' ')[0]);
        }
        catch { return 0; }
    }

    // ── GPU — hardware-agnostic discovery ─────────────────────────────────

    private (int Pct, long VramUsed, long VramTotal) GetGpuStats()
    {
        // Try every DRM card, not just card0
        var drmBase = "/sys/class/drm";
        if (Directory.Exists(drmBase))
        {
            foreach (var card in Directory.GetDirectories(drmBase, "card*")
                .OrderBy(d => d))
            {
                var dev = Path.Combine(card, "device");
                var pctPath  = Path.Combine(dev, "gpu_busy_percent");
                var vramU    = Path.Combine(dev, "mem_info_vram_used");
                var vramT    = Path.Combine(dev, "mem_info_vram_total");
                if (File.Exists(pctPath))
                {
                    return (
                        ReadSysFsInt(pctPath),
                        ReadSysFsLong(vramU),
                        ReadSysFsLong(vramT));
                }
            }
        }
        // NVIDIA via nvidia-smi (subprocess — only if available)
        try
        {
            var p = Process.Start(new ProcessStartInfo {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits",
                RedirectStandardOutput = true, UseShellExecute = false });
            if (p != null)
            {
                var line = p.StandardOutput.ReadLine();
                p.WaitForExit(2000);
                if (!string.IsNullOrEmpty(line))
                {
                    var parts = line.Split(',');
                    if (parts.Length >= 3)
                        return (int.Parse(parts[0].Trim()),
                                long.Parse(parts[1].Trim()) * 1_048_576,
                                long.Parse(parts[2].Trim()) * 1_048_576);
                }
            }
        }
        catch { /* nvidia-smi not present */ }
        return (0, 0, 0);
    }

    // ── Temperatures — probe all hwmon entries ─────────────────────────────

    private TemperatureInfo GetTemperatures()
    {
        double? cpu = null, gpu = null, nvme = null;
        var hwmonBase = "/sys/class/hwmon";
        if (!Directory.Exists(hwmonBase)) return new TemperatureInfo(null, null, null);

        foreach (var hwmon in Directory.GetDirectories(hwmonBase))
        {
            var namePath = Path.Combine(hwmon, "name");
            if (!File.Exists(namePath)) continue;
            var name = File.ReadAllText(namePath).Trim().ToLowerInvariant();

            // CPU: k10temp (AMD), coretemp (Intel), cpu_thermal (ARM)
            if (cpu == null && (name.Contains("k10temp") || name.Contains("coretemp") || name.Contains("cpu_thermal")))
                cpu = ReadBestHwmonTemp(hwmon);

            // GPU: amdgpu, nouveau, i915_thermal
            if (gpu == null && (name.Contains("amdgpu") || name.Contains("nouveau") || name.Contains("i915")))
                gpu = ReadBestHwmonTemp(hwmon);

            // NVMe
            if (nvme == null && name.Contains("nvme"))
                nvme = ReadBestHwmonTemp(hwmon);
        }
        return new TemperatureInfo(cpu, gpu, nvme);
    }

    private static double? ReadBestHwmonTemp(string hwmonDir)
    {
        try
        {
            foreach (var f in Directory.GetFiles(hwmonDir, "temp*_input").OrderBy(f => f))
            {
                var raw = File.ReadAllText(f).Trim();
                if (long.TryParse(raw, out var milliC) && milliC > 0)
                    return Math.Round(milliC / 1000.0, 1);
            }
        }
        catch { }
        return null;
    }

    private static int  ReadSysFsInt(string path)  { try { return int.Parse(File.ReadAllText(path).Trim()); } catch { return 0; } }
    private static long ReadSysFsLong(string path) { try { return long.Parse(File.ReadAllText(path).Trim()); } catch { return 0; } }

    // ── Disk I/O ───────────────────────────────────────────────────────────

    private static (long Read, long Write) GetDiskIo()
    {
        try
        {
            long r = 0, w = 0;
            foreach (var line in File.ReadLines("/proc/diskstats"))
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 10) continue;
                if (!Regex.IsMatch(p[2], @"^(sd[a-z]|nvme\d+n\d+|hd[a-z]|vd[a-z]|xvd[a-z])$")) continue;
                r += long.Parse(p[5])  * 512;
                w += long.Parse(p[9])  * 512;
            }
            return (r, w);
        }
        catch { return (0, 0); }
    }

    // ── Jellyfin streams ───────────────────────────────────────────────────

    public StreamInfo[] GetStreams()
    {
        var result = new List<StreamInfo>();
        foreach (var s in _sessionManager.Sessions)
        {
            var np = s.NowPlayingItem;
            if (np == null || s.PlayState?.IsPaused == true) continue;
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

    // ── Encode status ──────────────────────────────────────────────────────

    public EncodeStatusResponse GetEncodeStatus()
    {
        var slots = new List<WorkerStatus>();

        // Try configured status dir first
        var statusDir = Config.EncodeStatusDir;
        if (!string.IsNullOrEmpty(statusDir) && Directory.Exists(statusDir))
        {
            foreach (var f in Directory.GetFiles(statusDir, "slot_*.json").OrderBy(f => f))
            {
                try { slots.Add(ParseWorkerStatus(JsonDocument.Parse(File.ReadAllText(f)).RootElement)); }
                catch { }
            }
        }

        // Fall back to {TempPath}/mediadash_status/slot_*.json
        if (slots.Count == 0)
        {
            var fallback = Path.Combine(Path.GetTempPath(), "mediadash_status");
            if (Directory.Exists(fallback))
            {
                foreach (var f in Directory.GetFiles(fallback, "slot_*.json").OrderBy(f => f))
                {
                    try { slots.Add(ParseWorkerStatus(JsonDocument.Parse(File.ReadAllText(f)).RootElement)); }
                    catch { }
                }
            }
        }

        // Legacy single-file fallback
        if (slots.Count == 0)
        {
            var legacy = Path.Combine(Path.GetTempPath(), "encode_status.json");
            if (File.Exists(legacy))
            {
                try { slots.Add(ParseWorkerStatus(JsonDocument.Parse(File.ReadAllText(legacy)).RootElement)); }
                catch { }
            }
        }

        if (slots.Count == 0)
            return new EncodeStatusResponse(false, null, 0, null, null, 0, 0, 0, 0, 0, 0, null, null, Array.Empty<WorkerStatus>());

        var active  = slots.Where(s => s.Active).ToArray();
        var primary = active.FirstOrDefault() ?? slots[0];
        return new EncodeStatusResponse(
            Active: primary.Active, Name: primary.Name, SourceGb: primary.SourceGb,
            Codec: primary.Codec, StartedAt: primary.StartedAt, DurationS: primary.DurationS,
            Pct: primary.Pct, ElapsedS: primary.ElapsedS, TmpSizeGb: primary.TmpSizeGb,
            EstFinalGb: primary.EstFinalGb, EstSavingGb: primary.EstSavingGb,
            Fps: primary.Fps, Speed: primary.Speed, AllWorkers: slots.ToArray());
    }

    private static WorkerStatus ParseWorkerStatus(JsonElement el) => new(
        el.TryGetBool("active"), el.TryGetStr("name"), el.TryGetDbl("source_gb"),
        el.TryGetStr("codec"), el.TryGetStr("started_at"), (int)el.TryGetDbl("duration_s"),
        el.TryGetDbl("pct"), (int)el.TryGetDbl("elapsed_s"), el.TryGetDbl("tmp_size_gb"),
        el.TryGetDbl("est_final_gb"), el.TryGetDbl("est_saving_gb"),
        el.TryGetStr("fps"), el.TryGetStr("speed"), (int)el.TryGetDbl("worker"));

    // ── Encode remaining ───────────────────────────────────────────────────

    public QueueItem[] GetEncodeRemaining()
    {
        var exts     = Config.VideoExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries)
                             .Select(e => e.Trim().ToLowerInvariant()).ToHashSet();
        var skipCodecs = Config.SkipCodecs.Split(',', StringSplitOptions.RemoveEmptyEntries)
                               .Select(c => c.Trim().ToLowerInvariant()).ToHashSet();

        Dictionary<string, double> state = new();
        var stateFile = Config.ReencodeStateFile;
        if (!string.IsNullOrEmpty(stateFile) && File.Exists(stateFile))
        {
            try
            {
                foreach (var prop in JsonDocument.Parse(File.ReadAllText(stateFile))
                    .RootElement.EnumerateObject())
                    state[prop.Name] = prop.Value.GetDouble();
            }
            catch { }
        }

        var result = new List<QueueItem>();
        foreach (var dir in GetMediaDirectories())
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (file.Contains(".reencode_tmp", StringComparison.Ordinal)) continue;
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!exts.Contains(ext)) continue;
                try
                {
                    var info  = new FileInfo(file);
                    double mt = info.LastWriteTimeUtc.Subtract(DateTime.UnixEpoch).TotalSeconds;
                    if (state.TryGetValue(file, out var done) && Math.Abs(done - mt) < 1.0) continue;
                    result.Add(new QueueItem(info.Name, Math.Round(info.Length / 1e9, 2), file));
                }
                catch { }
            }
        }
        return result.ToArray();
    }

    // ── Status overview ────────────────────────────────────────────────────

    public StatusInfo GetStatus()
    {
        var cfg      = Config;
        var paused   = !string.IsNullOrEmpty(cfg.PauseFlagPath) && File.Exists(cfg.PauseFlagPath);
        var h        = DateTime.Now.Hour;
        var ps       = cfg.QuietHoursStart;
        var pe       = cfg.QuietHoursEnd;
        var inQuiet  = ps < pe ? (h >= ps && h < pe) : (h >= ps || h < pe);
        var encoding = !string.IsNullOrEmpty(cfg.ReencodeProcessName) && IsProcessActive(cfg.ReencodeProcessName);
        var strip    = !string.IsNullOrEmpty(cfg.StripProcessName)    && IsProcessActive(cfg.StripProcessName);
        var streams  = GetStreams();
        return new StatusInfo(
            Paused: paused, InQuietHours: inQuiet,
            EncodingActive: encoding, StrippingActive: strip,
            JellyfinStreams: streams, StreamCount: streams.Length,
            Schedule: new ScheduleInfo(ps, pe), Time: DateTime.Now.ToString("HH:mm"));
    }

    public bool IsProcessActive(string nameFragment)
    {
        if (string.IsNullOrWhiteSpace(nameFragment)) return false;
        try
        {
            foreach (var dir in Directory.GetDirectories("/proc")
                .Where(d => int.TryParse(Path.GetFileName(d), out _)))
            {
                var cmdline = Path.Combine(dir, "cmdline");
                if (!File.Exists(cmdline)) continue;
                if (File.ReadAllText(cmdline).Replace('\0', ' ')
                    .Contains(nameFragment, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }

    // ── Log parsers ────────────────────────────────────────────────────────

    public StripEntry[] ParseStripLog()
    {
        var path = Config.StripLogPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Array.Empty<StripEntry>();

        var results = new Dictionary<string, (int audio, int subs, double savedMb, string status)>();
        string? current = null;
        foreach (var line in File.ReadLines(path))
        {
            var m = Regex.Match(line, @"Inspecting: (.+)$");
            if (m.Success) { current = m.Groups[1].Value.Trim(); results[current] = (0, 0, 0, "clean"); continue; }
            if (current == null) continue;
            var (a, s, mb, st) = results[current];
            if (line.Contains("Audio track") && line.Contains("-> DROP")) results[current] = (a + 1, s, mb, "processed");
            else if (line.Contains("Sub   track") && line.Contains("-> DROP")) results[current] = (a, s + 1, mb, "processed");
            var m2 = Regex.Match(line, @"Done\. [\d.]+MB -> [\d.]+MB \(saved ([\d.]+)MB\)");
            if (m2.Success) { var (a2, s2, _, st2) = results[current]; results[current] = (a2, s2, double.Parse(m2.Groups[1].Value), st2); }
        }
        return results.Select(kv => new StripEntry(kv.Key, kv.Value.audio, kv.Value.subs, kv.Value.savedMb, kv.Value.status)).ToArray();
    }

    public ReencodeEntry[] ParseReencodeLog()
    {
        var path = Config.ReencodeLogPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Array.Empty<ReencodeEntry>();

        var results = new Dictionary<string, ReencodeEntry>();
        string? current = null;
        foreach (var line in File.ReadLines(path))
        {
            var m = Regex.Match(line, @"\[W\d+\] Encoding: (.+?) \(([\d.]+)GB (\w+)\)");
            if (!m.Success) m = Regex.Match(line, @"Encoding: (.+?) \(([\d.]+)GB (\w+)\)");
            if (m.Success)
            {
                current = m.Groups[1].Value.Trim();
                results[current] = new ReencodeEntry(current, double.Parse(m.Groups[2].Value), m.Groups[3].Value, 0, 0, 0, "encoding", false);
                continue;
            }
            if (current == null) continue;
            var m2 = Regex.Match(line, @"Done in (\d+)min: ([\d.]+)GB->([\d.]+)GB");
            if (m2.Success)
            {
                var before = double.Parse(m2.Groups[2].Value); var after = double.Parse(m2.Groups[3].Value);
                results[current] = results[current] with { ElapsedMin = int.Parse(m2.Groups[1].Value), BeforeGb = before, AfterGb = after, SavedGb = Math.Round(before - after, 1), Status = "done" };
            }
            else if (line.Contains("Output too small") && results.TryGetValue(current, out var e) && e.Status == "encoding")
                results[current] = e with { Status = "failed" };
        }
        return results.Values.ToArray();
    }

    public DupesReport GetDupesReport()
    {
        var path = Config.DupesReportPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return new DupesReport(0, 0, "0B", Array.Empty<DupeGroup>(), null);
        try { return JsonSerializer.Deserialize<DupesReport>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new DupesReport(0, 0, "0B", Array.Empty<DupeGroup>(), null); }
        catch { return new DupesReport(0, 0, "0B", Array.Empty<DupeGroup>(), null); }
    }

    // ── Actions ────────────────────────────────────────────────────────────

    public void Pause()
    {
        var path = Config.PauseFlagPath;
        if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, "");
    }

    public void Resume()
    {
        var pausePath = Config.PauseFlagPath;
        if (!string.IsNullOrEmpty(pausePath) && File.Exists(pausePath)) File.Delete(pausePath);
        var forcePath = Config.ForceFlagPath;
        if (!string.IsNullOrEmpty(forcePath)) File.WriteAllText(forcePath, "");
    }

    public bool StartService(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) return false;
        try
        {
            var p = Process.Start(new ProcessStartInfo {
                FileName = "systemctl", Arguments = $"start {serviceName}.service",
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p?.WaitForExit(5000);
            return true;
        }
        catch { return false; }
    }

    public bool StartDupesScan()
    {
        var script = Config.DupesScanScript;
        if (string.IsNullOrWhiteSpace(script) || !File.Exists(script)) return false;
        Process.Start(new ProcessStartInfo { FileName = "python3", Arguments = script, UseShellExecute = false });
        return true;
    }

    public DeleteResponse DeleteDupes(string? imdbFilter)
    {
        var report = GetDupesReport();
        if (report.Groups.Length == 0) return new DeleteResponse(true, 0, 0, Array.Empty<string>());
        int deleted = 0; long freed = 0; var errors = new List<string>();
        foreach (var g in report.Groups)
        {
            if (imdbFilter != null && g.Imdb != imdbFilter) continue;
            foreach (var f in g.Duplicates)
            {
                try { if (!File.Exists(f.Path)) { errors.Add($"{f.Path}: not found"); continue; }
                    var size = new FileInfo(f.Path).Length; File.Delete(f.Path); deleted++; freed += size; }
                catch (Exception ex) { errors.Add($"{f.Path}: {ex.Message}"); }
            }
        }
        StartDupesScan();
        return new DeleteResponse(true, deleted, Math.Round(freed / 1e9, 2), errors.ToArray());
    }

    public ScheduleSaveResponse SaveSchedule(int pauseStart, int pauseEnd)
    {
        Plugin.Instance!.Configuration.QuietHoursStart = pauseStart;
        Plugin.Instance!.Configuration.QuietHoursEnd   = pauseEnd;
        Plugin.Instance!.SaveConfiguration();
        return new ScheduleSaveResponse(true, pauseStart, pauseEnd);
    }

    // ── Language settings ──────────────────────────────────────────────────

    public LanguageSettings GetLanguageSettings() => new(
        Config.KeepAudioLanguages,
        Config.KeepSubtitleLanguages,
        Config.AlwaysKeepFirstAudio,
        Config.KeepCommentaryTracks,
        Config.EnableTrackStripping);

    public void SaveLanguageSettings(LanguageSettings s)
    {
        var cfg = Plugin.Instance!.Configuration;
        cfg.KeepAudioLanguages     = s.KeepAudio;
        cfg.KeepSubtitleLanguages  = s.KeepSubs;
        cfg.AlwaysKeepFirstAudio   = s.AlwaysKeepFirst;
        cfg.KeepCommentaryTracks   = s.KeepCommentary;
        cfg.EnableTrackStripping   = s.Enabled;
        Plugin.Instance!.SaveConfiguration();
    }

    // ── Jellyfin library info (for Settings UI) ────────────────────────────

    public LibraryInfo[] GetLibraries()
    {
        var result = new List<LibraryInfo>();
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            foreach (var loc in folder.Locations)
            {
                if (!string.IsNullOrEmpty(loc))
                    result.Add(new LibraryInfo(folder.Name, folder.CollectionType?.ToString() ?? "unknown", loc));
            }
        }
        return result.ToArray();
    }
}

// ── JsonElement helpers ────────────────────────────────────────────────────

internal static class JEx
{
    public static bool    TryGetBool(this JsonElement el, string key) => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
    public static string? TryGetStr(this JsonElement el, string key)  => el.TryGetProperty(key, out var v) ? v.GetString() : null;
    public static double  TryGetDbl(this JsonElement el, string key)  => el.TryGetProperty(key, out var v) && v.TryGetDouble(out var d) ? d : 0;
}
