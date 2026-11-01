using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Picks the UNC share roots (<c>\\server\share</c>) out of a list of library folder paths.
/// <c>DriveInfo.GetDrives()</c> only ever lists drive letters, so libraries on a NAS addressed by UNC
/// path never match a drive and used to be missing from the Overview storage card entirely.
/// </summary>
internal static class UncLibraryRoots
{
    private static readonly char[] PathSeparators = { '\\', '/' };

    /// <summary>Returns the distinct UNC share roots among <paramref name="libraryPaths"/>.</summary>
    /// <param name="libraryPaths">Configured library folder paths (any mix of local and UNC).</param>
    /// <returns>Share roots such as <c>\\sunnynas\Shows</c>, de-duplicated case-insensitively, in first-seen order.</returns>
    public static IReadOnlyList<string> From(IEnumerable<string> libraryPaths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>();

        foreach (var path in libraryPaths)
        {
            var root = ShareRootOf(path);
            if (root is not null && seen.Add(root))
            {
                roots.Add(root);
            }
        }

        return roots;
    }

    private static string? ShareRootOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // Extended-length form of a UNC path: \\?\UNC\server\share\... -> \\server\share\...
        const string extendedUncPrefix = @"\\?\UNC\";
        if (path.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = string.Concat(@"\\", path.AsSpan(extendedUncPrefix.Length));
        }

        // Only plain \\server\share paths. Device paths (\\?\C:\, \\.\) and anything else are not shares.
        if (!path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return null;
        }

        // Split on separators rather than trusting Path.GetPathRoot, which is Windows-only in behaviour
        // (it returns "/" for these on Linux) and would make this untestable off-Windows.
        var parts = path.Substring(2).Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        return @"\\" + parts[0] + @"\" + parts[1];
    }
}
