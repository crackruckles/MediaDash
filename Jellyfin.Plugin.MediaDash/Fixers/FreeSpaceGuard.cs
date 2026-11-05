using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Shared free-disk-space precondition used before any encode/remux/repair mutation. Backs
/// CLAUDE.md safety invariant 5 (transcode fixers need at least 2× source size available before
/// beginning). Fails closed when the target drive can't be resolved so a missing DriveInfo can't
/// be interpreted as "plenty of space." Overflow-safe against pathological source sizes.
/// </summary>
internal static class FreeSpaceGuard
{
    /// <summary>
    /// Returns true when the volume holding <paramref name="path"/> has at least
    /// <paramref name="multiplier"/> × source size available. Callers use multiplier=2 for the
    /// standard transcode/remux gate; the repair ladder passes 3 when a re-encode rung is enabled.
    /// </summary>
    /// <param name="path">Full path to the source file. Must exist and be resolvable to a drive.</param>
    /// <param name="multiplier">Space headroom multiplier (e.g. 2 for `2 × source`).</param>
    /// <returns>True when free space is at least <c>multiplier × sourceSize</c>. False if the drive
    /// can't be identified, the file is missing, or the volume is short.</returns>
    public static bool HasEnoughFreeSpace(string path, int multiplier)
    {
        if (multiplier <= 0)
        {
            return true;
        }

        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists)
            {
                return false;
            }

            if (!TryGetAvailableFreeSpace(fi.FullName, out var available))
            {
                return false;
            }

            // Overflow-safe: compare via division instead of `fi.Length * multiplier`, which would
            // wrap for sources larger than long.MaxValue / multiplier (~4.6 EB at multiplier=2 —
            // unrealistic today, but "unrealistic" is exactly how overflow bugs get shipped).
            return available / multiplier >= fi.Length;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Measures the bytes available to the current user on the volume holding
    /// <paramref name="fullPath"/>. On Windows this uses <c>GetDiskFreeSpaceExW</c>, which (unlike
    /// <see cref="DriveInfo"/>) accepts UNC/NAS paths such as <c>\\nas\share\Shows\…</c> and honours
    /// per-user quotas. Everywhere else, and as a fallback on Windows, it uses
    /// <see cref="DriveInfo"/> on the path root. Returns false (fail closed) when the volume can't
    /// be measured.
    /// </summary>
    /// <param name="fullPath">Absolute path to a file or directory on the volume to measure.</param>
    /// <param name="availableBytes">Bytes available to the caller, or 0 when this returns false.</param>
    /// <returns>True when the free space could be measured.</returns>
    internal static bool TryGetAvailableFreeSpace(string fullPath, out long availableBytes)
    {
        availableBytes = 0;

        if (string.IsNullOrEmpty(fullPath))
        {
            return false;
        }

        if (OperatingSystem.IsWindows() && TryGetAvailableFreeSpaceWindows(fullPath, out availableBytes))
        {
            return true;
        }

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        try
        {
            availableBytes = new DriveInfo(root).AvailableFreeSpace;
            return true;
        }
        catch (ArgumentException)
        {
            // DriveInfo only understands drive letters on Windows; a UNC root lands here.
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryGetAvailableFreeSpaceWindows(string fullPath, out long availableBytes)
    {
        availableBytes = 0;

        // Try the containing directory first: it resolves mounted-folder volumes correctly. Fall
        // back to the path root, which is short enough to dodge MAX_PATH for very deep trees.
        // GetDiskFreeSpaceExW requires a trailing separator on a bare UNC share root.
        var candidates = new[] { Path.GetDirectoryName(fullPath), Path.GetPathRoot(fullPath) };
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }

            var dir = Path.EndsInDirectorySeparator(candidate) ? candidate : candidate + Path.DirectorySeparatorChar;
            if (GetDiskFreeSpaceExW(dir, out var freeToCaller, out _, out _))
            {
                // The API reports unsigned 64-bit; clamp so the signed comparison in the caller
                // can never go negative.
                availableBytes = freeToCaller > long.MaxValue ? long.MaxValue : (long)freeToCaller;
                return true;
            }
        }

        return false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static extern bool GetDiskFreeSpaceExW(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);
}
