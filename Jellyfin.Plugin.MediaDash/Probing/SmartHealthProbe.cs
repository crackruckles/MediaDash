using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Api;

namespace Jellyfin.Plugin.MediaDash.Probing;

/// <summary>
/// Runs <c>smartctl -j -H</c> against the drive backing each library / recycle-bin root and
/// caches the answer so the Overview poll never blocks. Never throws — a missing smartctl,
/// a permission problem, or an exotic device is reported as <see cref="SmartHealth.Unknown"/>
/// with a hint the user can act on.
/// </summary>
public static class SmartHealthProbe
{
    // ponytail: TTL long because SMART state changes slowly; a 10-min lag beats hammering the disk on every 3-s poll.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> InFlight = new(StringComparer.OrdinalIgnoreCase);
    // Per-device dedup for the smartctl error path — a drive that timed out, threw an IOException,
    // or came back with a non-zero exit is going to keep doing that on every 10-min refresh.
    // Suppress the second and Nth Errors-tab emission so the user sees the class once, not once per
    // refresh × N drives × M error kinds. Reset on Jellyfin restart.
    private static readonly ConcurrentDictionary<string, byte> SmartctlKnownFailing =
        new(StringComparer.OrdinalIgnoreCase);

    private static int _smartctlMissing; // 0 = unknown / present, 1 = confirmed missing on PATH

    /// <summary>
    /// Returns the last-known SMART health for the drive that hosts <paramref name="driveRoot"/>.
    /// If nothing is cached yet, kicks off a background probe and returns
    /// <see cref="SmartHealth.Unknown"/> so the caller (the Status endpoint) never blocks.
    /// </summary>
    /// <param name="driveRoot">The drive root, e.g. <c>C:\</c> or <c>/mnt/media</c>.</param>
    /// <returns>The cached or just-scheduled health result.</returns>
    public static SmartHealthResult Get(string driveRoot)
    {
        if (string.IsNullOrWhiteSpace(driveRoot))
        {
            return new SmartHealthResult(SmartHealth.Unknown, "No drive root supplied.");
        }

        var hasHit = Cache.TryGetValue(driveRoot, out var hit);
        if (hasHit && DateTime.UtcNow - hit.At < CacheTtl)
        {
            return hit.Result;
        }

        // First call for this root (or the entry is stale) — kick off a refresh and return whatever we
        // have so the poll never blocks. Stale-but-present beats making the Overview wait 3 s.
        ScheduleProbe(driveRoot);
        return hasHit ? hit.Result : new SmartHealthResult(SmartHealth.Unknown, "Checking SMART status…");
    }

    private static void ScheduleProbe(string driveRoot)
    {
        if (!InFlight.TryAdd(driveRoot, 0))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var result = ProbeBlocking(driveRoot);
                Cache[driveRoot] = new CacheEntry(result, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                // Fire-and-forget: no caller observes the exception. Surface via Diagnostics so a
                // repeated crash (unexpected exception, not a normal Unknown result) is visible
                // instead of the Overview silently sitting on "Checking SMART status…" forever.
                Diagnostics.Record("SmartHealth.ProbeCrashed", "SMART probe crashed for " + driveRoot + ": " + ex.Message + ". Overview will retry after the next TTL.");
            }
            finally
            {
                InFlight.TryRemove(driveRoot, out _);
            }
        });
    }

    private static SmartHealthResult ProbeBlocking(string driveRoot)
    {
        // Preferred path on Windows: query WMI natively (MSFT_PhysicalDisk on Win8+, else the legacy
        // MSStorageDriver_FailurePredictStatus). No external binary required. Falls through to smartctl
        // if WMI can't correlate the drive (Storage Spaces virtual disks, some USB bridges).
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var wmi = SmartHealthProbeWmi.Query(driveRoot);
            if (wmi is { } value && value.Status != SmartHealth.Unknown)
            {
                return value;
            }
        }

        // Fast path: if we already discovered smartctl isn't reachable anywhere, don't shell out again.
        if (Interlocked.CompareExchange(ref _smartctlMissing, 0, 0) == 1)
        {
            return new SmartHealthResult(SmartHealth.Unknown, MissingHint());
        }

        // Locate smartctl: bundled next to the plugin DLL first (users can drop the smartmontools binary
        // there for detail parity), then well-known install paths, then bare "smartctl" on PATH.
        var smartctl = ResolveSmartctl();
        if (smartctl is null)
        {
            Interlocked.Exchange(ref _smartctlMissing, 1);
            return new SmartHealthResult(SmartHealth.Unknown, MissingHint());
        }

        var device = ResolveDeviceForRoot(driveRoot);
        if (device is null)
        {
            return new SmartHealthResult(SmartHealth.Unknown, "Could not resolve a block device for " + driveRoot + ". SMART not checked.");
        }

        try
        {
            // ArgumentList (not string concat) — a device path with whitespace or shell metacharacters
            // would otherwise tokenize into extra smartctl args (argument injection).
            var psi = new ProcessStartInfo(smartctl)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-j");
            psi.ArgumentList.Add("-H");
            psi.ArgumentList.Add("-A");
            psi.ArgumentList.Add(device);
            using var p = Process.Start(psi);
            if (p is null)
            {
                Interlocked.Exchange(ref _smartctlMissing, 1);
                return new SmartHealthResult(SmartHealth.Unknown, MissingHint());
            }

            // Drain BOTH stdout and stderr concurrently before WaitForExit. Enterprise SSDs produce
            // >64KB SMART attribute logs; a synchronous ReadToEnd after WaitForExit deadlocks the
            // child on its own pipe buffer. Stderr must also be drained even though we ignore it —
            // a smartctl warning line otherwise blocks the same way.
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
            {
                try
                {
                    p.Kill();
                }
                catch (InvalidOperationException)
                {
                }

                if (SmartctlKnownFailing.TryAdd(device + ":timeout", 0))
                {
                    Diagnostics.Record("SmartHealth", "smartctl timed out on " + device + " — SMART health will show as Unknown until the next probe. Subsequent timeouts on this device are suppressed for the rest of the session.");
                }

                return new SmartHealthResult(SmartHealth.Unknown, "smartctl took longer than 3 s.");
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();
            return ParseSmartctlJson(stdout, device);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // ExitCode 2 = "file not found" on Windows; on Linux this is thrown when smartctl isn't on PATH.
            Interlocked.Exchange(ref _smartctlMissing, 1);
            return new SmartHealthResult(SmartHealth.Unknown, MissingHint());
        }
        catch (IOException ex)
        {
            if (SmartctlKnownFailing.TryAdd(device + ":io", 0))
            {
                Diagnostics.Record("SmartHealth", "smartctl failed for " + device + ": " + ex.Message + " Subsequent IO errors on this device are suppressed for the rest of the session.");
            }

            return new SmartHealthResult(SmartHealth.Unknown, "smartctl error: " + ex.Message);
        }
    }

    // Resolution order:
    //   1. bundled: <plugin folder>/smartctl.exe (Windows) or smartctl (Linux/macOS) — drop-in
    //   2. Windows install paths: Program Files\smartmontools\bin\smartctl.exe
    //   3. bare "smartctl" (relies on PATH)
    // The bundled/known-install paths let Windows users get full attribute detail without editing PATH,
    // and let admins pin a specific smartmontools version by dropping the binary into the plugin folder.
    private static string? ResolveSmartctl()
    {
        var pluginDir = Path.GetDirectoryName(typeof(SmartHealthProbe).Assembly.Location);
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var exeName = isWindows ? "smartctl.exe" : "smartctl";

        if (pluginDir is not null)
        {
            var bundled = Path.Combine(pluginDir, exeName);
            if (File.Exists(bundled))
            {
                return bundled;
            }
        }

        if (isWindows)
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var installed = Path.Combine(programFiles, "smartmontools", "bin", "smartctl.exe");
            if (File.Exists(installed))
            {
                return installed;
            }
        }
        else
        {
            // Try well-known absolute paths before falling back to PATH resolution — on hostile
            // multi-tenant hosts, a user with control over PATH could otherwise plant a fake smartctl
            // in a higher-priority PATH entry. Absolute paths are unspoofable without root.
            foreach (var abs in new[] { "/usr/sbin/smartctl", "/usr/bin/smartctl", "/usr/local/sbin/smartctl", "/usr/local/bin/smartctl" })
            {
                if (File.Exists(abs))
                {
                    return abs;
                }
            }
        }

        // Bare command name — Process.Start will resolve via PATH; a Win32Exception below signals missing.
        return "smartctl";
    }

    // smartctl -j -A returns overall health, ATA attribute table, and (for NVMe) an nvme_smart_health_information_log.
    // We pull the coarse verdict from smart_status.passed and pluck a few well-known attributes for the stats view.
    private static SmartHealthResult ParseSmartctlJson(string json, string device)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SmartHealthResult(SmartHealth.Unknown, "smartctl produced no output for " + device + ".");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // smartctl always writes JSON, even when it couldn't actually check the device
            // (permission denied, unsupported device, no SMART support). Its own exit_status field
            // is the tell — non-zero means smartctl failed to complete, so any absence of
            // smart_status is NOT "self-assessment failed" — it's Unknown with a specific reason.
            // On Linux this is the most common case: Jellyfin runs unprivileged, smartctl needs
            // CAP_SYS_RAWIO or sudo, and every drive comes back Unknown without a useful hint.
            if (root.TryGetProperty("smartctl", out var meta)
                && meta.TryGetProperty("exit_status", out var exit)
                && exit.ValueKind == JsonValueKind.Number
                && exit.GetInt32() != 0)
            {
                var firstMessage = string.Empty;
                if (meta.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in msgs.EnumerateArray())
                    {
                        if (m.TryGetProperty("string", out var ms) && ms.ValueKind == JsonValueKind.String)
                        {
                            firstMessage = ms.GetString() ?? string.Empty;
                            break;
                        }
                    }
                }

                var isPermissionDenied = firstMessage.Contains("Permission denied", StringComparison.OrdinalIgnoreCase);
                var hint = firstMessage;
                if (isPermissionDenied)
                {
                    hint = "smartctl needs CAP_SYS_RAWIO to read SMART on " + device + ". On Linux with the jellyfin-server package: `sudo setcap 'cap_sys_rawio+ep' $(which smartctl)`. Docker: run the container with `--cap-add=SYS_RAWIO --device=" + device + "` (the --device flag is required — a mount alone does not expose the block device inside the container).";
                }
                else if (string.IsNullOrEmpty(firstMessage))
                {
                    hint = "smartctl exited " + exit.GetInt32() + " on " + device + " without a diagnostic message.";
                }

                // Permission-denied is an expected outcome for unprivileged Jellyfin; the Overview
                // Storage-health card already surfaces the setcap/--cap-add hint via the returned
                // SmartHealthResult. Recording it to the persistent Errors tab as well is duplicate
                // noise for a host-level config issue the plugin can't itself fix.
                // Every other non-zero exit is worth surfacing once per device per session — after that
                // the row exists on the Errors tab and re-emitting it every 10 minutes just increments
                // a counter the user already saw.
                if (!isPermissionDenied && SmartctlKnownFailing.TryAdd(device + ":exit" + exit.GetInt32(), 0))
                {
                    Diagnostics.Record("SmartHealth", "smartctl exit_status=" + exit.GetInt32() + " on " + device + ": " + (firstMessage.Length > 0 ? firstMessage : "(no message)") + " Subsequent identical failures on this device are suppressed for the rest of the session.");
                }

                return new SmartHealthResult(SmartHealth.Unknown, hint);
            }

            bool overallPassed = root.TryGetProperty("smart_status", out var status)
                && status.TryGetProperty("passed", out var passed)
                && passed.ValueKind == JsonValueKind.True;

            string? failingAttr = null;
            if (root.TryGetProperty("ata_smart_attributes", out var attrs)
                && attrs.TryGetProperty("table", out var table)
                && table.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in table.EnumerateArray())
                {
                    if (entry.TryGetProperty("when_failed", out var wf)
                        && wf.ValueKind == JsonValueKind.String
                        && !string.Equals(wf.GetString(), "never", StringComparison.OrdinalIgnoreCase))
                    {
                        failingAttr = entry.TryGetProperty("name", out var n) ? n.GetString() : "attribute";
                        break;
                    }
                }
            }

            SmartHealthResult result;
            if (!overallPassed)
            {
                result = new SmartHealthResult(SmartHealth.Failing, "smartctl reports SMART self-assessment FAILED on " + device + ".");
            }
            else if (failingAttr is not null)
            {
                result = new SmartHealthResult(SmartHealth.Warning, "SMART attribute '" + failingAttr + "' is past threshold on " + device + ".");
            }
            else
            {
                result = new SmartHealthResult(SmartHealth.Healthy, "SMART self-assessment PASSED on " + device + ".");
            }

            PopulateSmartctlStats(root, result);
            return result;
        }
        catch (JsonException ex)
        {
            return new SmartHealthResult(SmartHealth.Unknown, "Could not parse smartctl output: " + ex.Message);
        }
    }

    // Extract the six user-facing stats from the smartctl JSON blob. NVMe payloads live under
    // nvme_smart_health_information_log; ATA/SATA under ata_smart_attributes.table + power_on_time + temperature.
    private static void PopulateSmartctlStats(JsonElement root, SmartHealthResult result)
    {
        if (root.TryGetProperty("model_name", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
        {
            result.ModelName = modelEl.GetString();
        }

        if (root.TryGetProperty("temperature", out var tempEl))
        {
            if (tempEl.TryGetProperty("current", out var cur) && cur.TryGetInt32(out var c))
            {
                result.TemperatureCelsius = c;
            }

            if (tempEl.TryGetProperty("lifetime_max", out var max) && max.TryGetInt32(out var m))
            {
                result.TemperatureMaxCelsius = m;
            }
        }

        if (root.TryGetProperty("power_on_time", out var potEl)
            && potEl.TryGetProperty("hours", out var hoursEl)
            && hoursEl.TryGetInt64(out var hours))
        {
            result.PowerOnHours = hours;
        }

        // NVMe drives report a distinct block. Prefer its fields when present — they're already normalised.
        if (root.TryGetProperty("nvme_smart_health_information_log", out var nvme))
        {
            if (result.PowerOnHours is null
                && nvme.TryGetProperty("power_on_hours", out var nvHours)
                && nvHours.TryGetInt64(out var poh))
            {
                result.PowerOnHours = poh;
            }

            if (nvme.TryGetProperty("percentage_used", out var pctUsed) && pctUsed.TryGetInt32(out var pu))
            {
                result.WearPercent = pu;
            }

            if (nvme.TryGetProperty("media_errors", out var me) && me.TryGetInt64(out var meVal))
            {
                result.ReadErrorsUncorrected = meVal;
            }
        }

        // ATA/SATA drives: walk the attribute table and pluck by well-known IDs.
        // 9 = Power_On_Hours, 173/177/231/233 = wear/life-remaining variants, 187/198 = uncorrected errors, 194 = temperature.
        if (root.TryGetProperty("ata_smart_attributes", out var ataAttrs)
            && ataAttrs.TryGetProperty("table", out var table)
            && table.ValueKind == JsonValueKind.Array)
        {
            foreach (var attr in table.EnumerateArray())
            {
                if (!attr.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id))
                {
                    continue;
                }

                if (!attr.TryGetProperty("raw", out var rawEl)
                    || !rawEl.TryGetProperty("value", out var rawVal))
                {
                    continue;
                }

                switch (id)
                {
                    case 9 when result.PowerOnHours is null && rawVal.TryGetInt64(out var poh):
                        result.PowerOnHours = poh;
                        break;
                    case 194 when result.TemperatureCelsius is null && rawVal.TryGetInt32(out var tmp):
                        result.TemperatureCelsius = tmp;
                        break;
                    case 187 or 198 when rawVal.TryGetInt64(out var errs):
                        // Merge into a single uncorrected-errors bucket regardless of read vs. write split.
                        result.ReadErrorsUncorrected = (result.ReadErrorsUncorrected ?? 0) + errs;
                        break;
                    case 231 when result.WearPercent is null && attr.TryGetProperty("value", out var normalised) && normalised.TryGetInt32(out var lifeLeft):
                        // Attribute 231 "SSD_Life_Left" reports normalised remaining (100 = new). Convert to wear-used.
                        result.WearPercent = 100 - lifeLeft;
                        break;
                    case 173 or 177 or 233 when result.WearPercent is null && rawVal.TryGetInt32(out var wearRaw):
                        result.WearPercent = wearRaw;
                        break;
                }
            }
        }
    }

    // Windows: smartctl accepts drive letters directly ("smartctl -H C:"). Linux/macOS: resolve mount → block device.
    private static string? ResolveDeviceForRoot(string driveRoot)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var trimmed = driveRoot.TrimEnd('\\', '/');
            return trimmed.Length >= 2 && trimmed[1] == ':' ? trimmed[..2] : trimmed;
        }

        var raw = ResolveDeviceViaDf(driveRoot);
        if (raw is null)
        {
            return null;
        }

        // df returns the PARTITION or virtual device (/dev/sda1, /dev/nvme0n1p1, /dev/mapper/vg0-x,
        // /dev/dm-0, /dev/md0). smartctl mostly wants the underlying disk. Walk up with lsblk to the
        // physical parent — /dev/sda1 → /dev/sda, /dev/nvme0n1p1 → /dev/nvme0n1, /dev/dm-0 → the
        // first PV disk. Where lsblk can't resolve (multi-PV LVM, MD, ZFS pool) we return null and
        // the caller surfaces "SMART not supported on virtual device" with a hint.
        // GitHub #6 + #12-SMART: users on other plugins see SMART because those plugins do this
        // walk-up. Passing /dev/sdb1 to smartctl on some drivers returns "This device is a
        // partition of the disk /dev/sdb" with a non-zero exit → we used to report Unknown forever.
        return ResolveParentDisk(raw) ?? raw;
    }

    private static string? ResolveDeviceViaDf(string driveRoot)
    {
        try
        {
            // Use ArgumentList so spaces / special chars in the mount path don't tokenize into two
            // args (which produces silent Unknown SMART state for libraries at "/mnt/media backup").
            var psi = new ProcessStartInfo("df")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--output=source");
            psi.ArgumentList.Add(driveRoot);
            using var p = Process.Start(psi);
            if (p is null || !p.WaitForExit(1500))
            {
                return null;
            }

            foreach (var line in p.StandardOutput.ReadToEnd().Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.Equals("Filesystem", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (trimmed.StartsWith("/dev/", StringComparison.Ordinal))
                {
                    // Trim to the first whitespace-delimited token — some filesystem drivers produce
                    // trailing text on the source column ("/dev/sda1 extra") which would otherwise
                    // flow into the smartctl arg list.
                    var device = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                    device = device.Split('\t', StringSplitOptions.RemoveEmptyEntries)[0];
                    return device;
                }
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // df not available on this host — no auto-mapping possible.
        }
        catch (IOException)
        {
        }

        return null;
    }

    /// <summary>
    /// Walks a partition or device-mapper node up to its parent physical disk via <c>lsblk</c>.
    /// Internal for tests. Returns <c>null</c> when lsblk isn't available, when the input is already
    /// a top-level disk (PKNAME empty), or when the resolution chain hits a virtual pool device
    /// that smartctl can't check (multi-device MD/dm/ZFS).
    /// </summary>
    /// <param name="devicePath">Absolute device path like <c>/dev/sda1</c>.</param>
    /// <returns>The parent disk path (e.g. <c>/dev/sda</c>), or null when unresolvable.</returns>
    internal static string? ResolveParentDisk(string devicePath)
    {
        if (string.IsNullOrEmpty(devicePath) || !devicePath.StartsWith("/dev/", StringComparison.Ordinal))
        {
            return null;
        }

        // Walk up at most three levels so we don't loop forever on a pathological config.
        // Typical chain: partition → disk (1 hop). LVM on a partition: dm-0 → sda1 → sda (2 hops).
        var current = devicePath;
        for (var i = 0; i < 3; i++)
        {
            var pkname = InvokeLsblkPkname(current);
            if (string.IsNullOrEmpty(pkname))
            {
                // Empty PKNAME means this device IS the top-level disk. Return it as-is.
                return i == 0 ? null : current;
            }

            current = "/dev/" + pkname;
        }

        return current;
    }

    private static string? InvokeLsblkPkname(string devicePath)
    {
        try
        {
            var psi = new ProcessStartInfo("lsblk")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-no");
            psi.ArgumentList.Add("PKNAME");
            psi.ArgumentList.Add(devicePath);
            using var p = Process.Start(psi);
            if (p is null || !p.WaitForExit(1500))
            {
                return null;
            }

            if (p.ExitCode != 0)
            {
                return null;
            }

            // lsblk prints one line per level — for a partition on a plain disk it's a single
            // "sda\n". For a partition that has children (rare — bind-mounts, holders) it may
            // print multiple. First non-blank line is the immediate parent.
            foreach (var raw in p.StandardOutput.ReadToEnd().Split('\n'))
            {
                var trimmed = raw.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // lsblk not installed — fall through, caller uses df result verbatim.
        }
        catch (IOException)
        {
        }

        return null;
    }

    private static string MissingHint()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "WMI could not correlate this drive and no smartctl was found. Install smartmontools (https://smartmontools.org) or drop smartctl.exe next to the plugin DLL for SMART detail."
            : "smartctl not installed. Run `apt install smartmontools` (Debian/Ubuntu) or your distro's equivalent to enable SMART checks.";

    private readonly record struct CacheEntry(SmartHealthResult Result, DateTime At);
}
