using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Shared free-disk-space precondition used before any encode/remux/repair mutation. Backs
/// CLAUDE.md safety invariant 5 (transcode fixers need at least 2× source size available before
/// beginning). Fails closed when the output directory can't be probed so a failed query can't
/// be interpreted as "plenty of space." Overflow-safe against pathological source sizes.
/// </summary>
internal static class FreeSpaceGuard
{
    /// <summary>
    /// Returns true when the volume holding <paramref name="path"/> has at least
    /// <paramref name="multiplier"/> × source size available. Callers use multiplier=2 for the
    /// standard transcode/remux gate; the repair ladder passes 3 when a re-encode rung is enabled.
    /// </summary>
    /// <param name="path">Full path to the source file. Must exist.</param>
    /// <param name="multiplier">Space headroom multiplier (e.g. 2 for `2 × source`).</param>
    /// <returns>True when free space is at least <c>multiplier × sourceSize</c>. False if the drive
    /// can't be identified, the file is missing, or the volume is short.</returns>
    public static bool HasEnoughFreeSpace(string path, int multiplier) => DescribeShortfall(path, multiplier) is null;

    /// <summary>
    /// Returns a human-readable shortfall ("needs ~12.4 GB free on D:\, only 1.8 GB available")
    /// when the volume holding <paramref name="path"/> is short on space, or null when the gate
    /// passes. Callers append this to their fixer-specific prefix so the user sees concrete
    /// numbers in the error row instead of just "needs at least twice its own size".
    /// </summary>
    /// <param name="path">Full path to the source file.</param>
    /// <param name="multiplier">Space headroom multiplier (e.g. 2 for `2 × source`).</param>
    /// <returns>Shortfall description, or null when there's enough space (or multiplier &lt;= 0).</returns>
    public static string? DescribeShortfall(string path, int multiplier) => DescribeShortfall(path, multiplier, availableBytes: null);

    internal static string? DescribeShortfall(string path, int multiplier, long? availableBytes)
    {
        if (multiplier <= 0)
        {
            return null;
        }

        try
        {
            var fi = new FileInfo(path);
            var isFolder = Directory.Exists(path);
            if (!fi.Exists && !isFolder)
            {
                return "the file to fix couldn't be measured";
            }

            // Outputs are siblings of the source: probe that directory, not the container's / or C:\.
            var directory = fi.DirectoryName!;
            var size = isFolder
                ? Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }).Sum(f => new FileInfo(f).Length)
                : fi.Length;
            var available = availableBytes ?? GetAvailableFreeSpace(directory);
            if (available / multiplier >= size)
            {
                return null;
            }

            // Compare by division above and saturate the display value so multiplication can't wrap.
            var needed = size <= long.MaxValue / multiplier ? size * multiplier : long.MaxValue;
            return string.Format(
                CultureInfo.InvariantCulture,
                "Not enough free disk space: needs ~{0} free at {1} (file is {2} × {3}), only {4} available",
                Human(needed),
                directory,
                Human(size),
                multiplier,
                Human(available));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or OverflowException)
        {
            return "couldn't determine free space for this path: " + ex.Message;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out long available, out long total, out long free);

    /// <summary>Measures an existing directory, preserving UNC, mount and caller-quota semantics.</summary>
    /// <param name="directory">Directory on the filesystem to measure; never reduced to its path root.</param>
    /// <param name="availableBytes">Space available to the current account.</param>
    /// <param name="totalBytes">Total capacity visible to the current account.</param>
    /// <returns>False when the directory cannot be measured.</returns>
    internal static bool TryGetDiskSpace(string directory, out long availableBytes, out long totalBytes)
    {
        availableBytes = 0;
        totalBytes = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            {
                return false;
            }

            var space = GetDiskSpace(directory);
            availableBytes = space.Available;
            totalBytes = space.Total;
            return true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static long GetAvailableFreeSpace(string directory) => GetDiskSpace(directory).Available;

    private static (long Available, long Total) GetDiskSpace(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            // Unix DriveInfo queries the supplied path, including bind mounts and symlink targets.
            var drive = new DriveInfo(directory);
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }

        // DriveInfo rejects UNC paths and reduces local paths to a drive letter, losing mount points.
        if (!GetDiskFreeSpaceEx(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, out var available, out var total, out _))
        {
            throw new IOException("Free-space query failed at '" + directory + "': " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        return (available, total);
    }

    private static string Human(long bytes)
    {
        const double KB = 1024d, MB = KB * 1024d, GB = MB * 1024d, TB = GB * 1024d;
        double b = bytes;
        if (b >= TB)
        {
            return (b / TB).ToString("0.##", CultureInfo.InvariantCulture) + " TB";
        }

        if (b >= GB)
        {
            return (b / GB).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
        }

        if (b >= MB)
        {
            return (b / MB).ToString("0.##", CultureInfo.InvariantCulture) + " MB";
        }

        if (b >= KB)
        {
            return (b / KB).ToString("0.##", CultureInfo.InvariantCulture) + " KB";
        }

        return bytes.ToString(CultureInfo.InvariantCulture) + " B";
    }
}
