using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MediaDash.Api;

internal static class DiskIo
{
    private static readonly object WindowsLock = new();
    private static IDisposable? _windowsRead;
    private static IDisposable? _windowsWrite;

    internal static string? Source => OperatingSystem.IsWindows() ? "PhysicalDisk (_Total)" : OperatingSystem.IsLinux() ? "whole disks" : null;

    internal static (long Read, long Write)? ReadCounters()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return ParseLinuxCounters(File.ReadLines("/proc/diskstats"));
            }

            return OperatingSystem.IsWindows() ? ReadWindowsCounters() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            return null;
        }
    }

    internal static Dictionary<string, (long Read, long Write)?> ReadDevices()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                return ParseLinuxDevices(File.ReadLines("/proc/diskstats"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new(StringComparer.Ordinal);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                return ReadWindowsDevices();
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            {
                return new(StringComparer.Ordinal);
            }
        }

        return new(StringComparer.Ordinal);
    }

    internal static (long Read, long Write)? ParseLinuxCounters(IEnumerable<string> lines)
    {
        var devices = ParseLinuxDevices(lines);
        return devices.Count == 0 ? null : (devices.Values.Sum(value => value!.Value.Read), devices.Values.Sum(value => value!.Value.Write));
    }

    // Physical whole disks only. Device-mapper (LVM, LUKS, Proxmox thin volumes) and md RAID devices sit on
    // top of those disks, so listing them both floods the System tab with one tile per volume and
    // double-counts the same bytes in the total. Inside an LXC the host's full /proc/diskstats is visible.
    internal static Dictionary<string, (long Read, long Write)?> ParseLinuxDevices(IEnumerable<string> lines)
    {
        var devices = new Dictionary<string, (long Read, long Write)?>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 9
                && Regex.IsMatch(fields[2], @"^(?:[shv]d[a-z]+|xvd[a-z]+|nvme[0-9]+n[0-9]+|mmcblk[0-9]+)$", RegexOptions.CultureInvariant)
                && long.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out var read)
                && long.TryParse(fields[9], NumberStyles.None, CultureInfo.InvariantCulture, out var write)
                && read <= long.MaxValue / 512 && write <= long.MaxValue / 512)
            {
                devices[fields[2]] = (read * 512, write * 512);
            }
        }

        return devices;
    }

    internal static double? BytesPerSecond(long? before, long? after, double seconds)
        => before.HasValue && after >= before && seconds > 0 ? (after - before) / seconds : null;

    [SupportedOSPlatform("windows")]
    private static Dictionary<string, (long Read, long Write)?> ReadWindowsDevices()
    {
        var category = new PerformanceCounterCategory("PhysicalDisk").ReadCategory();
        var reads = category["Disk Read Bytes/sec"];
        var writes = category["Disk Write Bytes/sec"];
        var devices = new Dictionary<string, (long Read, long Write)?>(StringComparer.Ordinal);
        if (reads is null || writes is null)
        {
            return devices;
        }

        foreach (InstanceData read in reads.Values)
        {
            if (!string.Equals(read.InstanceName, "_Total", StringComparison.OrdinalIgnoreCase)
                && writes[read.InstanceName] is { } write)
            {
                devices.Add(read.InstanceName, (read.RawValue, write.RawValue));
            }
        }

        return devices;
    }

    [SupportedOSPlatform("windows")]
    private static (long Read, long Write) ReadWindowsCounters()
    {
        lock (WindowsLock)
        {
            _windowsRead ??= new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", readOnly: true);
            _windowsWrite ??= new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", readOnly: true);
            return (((PerformanceCounter)_windowsRead).RawValue, ((PerformanceCounter)_windowsWrite).RawValue);
        }
    }
}
