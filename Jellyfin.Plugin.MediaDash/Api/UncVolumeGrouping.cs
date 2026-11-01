using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Several network shares often sit on one volume of the same NAS (for example <c>\\nas\Shows</c> and
/// <c>\\nas\YouTube</c>), and then each of them reports the whole volume's free space. Listing them
/// separately shows the same disk twice and counts its free space twice in the Overview total, so
/// shares that are evidently the same volume are merged into one entry.
/// </summary>
internal static class UncVolumeGrouping
{
    // Two shares are treated as one volume when they are on the same server, report exactly the same
    // total size, and their free space differs by less than this. The readings can be taken up to
    // a cache interval apart, so free space is allowed to drift a little; two distinct volumes
    // matching on both the exact total and free space to within this are not a realistic coincidence.
    private const long FreeSpaceToleranceBytes = 64L * 1024 * 1024;

    /// <summary>Merges readings that are the same volume. Order of first appearance is kept.</summary>
    /// <param name="readings">One reading per share, as <c>(Root, FreeBytes, TotalBytes)</c>.</param>
    /// <returns>One entry per volume: the share names joined with " + ", and the volume's free and total bytes.</returns>
    public static List<(string Label, long FreeBytes, long TotalBytes)> Group(IEnumerable<(string Root, long FreeBytes, long TotalBytes)> readings)
    {
        var groups = new List<(List<string> Roots, long FreeBytes, long TotalBytes)>();

        foreach (var reading in readings)
        {
            var index = groups.FindIndex(g => SameVolume(g.Roots[0], g.FreeBytes, g.TotalBytes, reading.Root, reading.FreeBytes, reading.TotalBytes));
            if (index >= 0)
            {
                groups[index].Roots.Add(reading.Root);
            }
            else
            {
                groups.Add((new List<string> { reading.Root }, reading.FreeBytes, reading.TotalBytes));
            }
        }

        return groups.ConvertAll(g => (string.Join(" + ", g.Roots), g.FreeBytes, g.TotalBytes));
    }

    private static bool SameVolume(string rootA, long freeA, long totalA, string rootB, long freeB, long totalB)
    {
        return totalA == totalB
            && Math.Abs(freeA - freeB) <= FreeSpaceToleranceBytes
            && string.Equals(ServerOf(rootA), ServerOf(rootB), StringComparison.OrdinalIgnoreCase);
    }

    // "\\server\share" -> "server". Anything unexpected yields the whole string, so it only ever
    // matches itself.
    private static string ServerOf(string root)
    {
        var trimmed = root.TrimStart('\\');
        var slash = trimmed.IndexOf('\\', StringComparison.Ordinal);
        return slash < 0 ? trimmed : trimmed.Substring(0, slash);
    }
}
