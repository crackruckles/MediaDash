using System;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class UncStorageTests
{
    [Fact]
    public void SharesAppearOnceWithCapacityAndWarningsWithoutMergingDistinctShares()
    {
        var paths = new[] { @"\\nas\a\Films", @"\\NAS\A\Shows", @"\\nas\b", @"C:\Local", "/mnt/media" };
        var probe = new UncVolumeProbe(_ => new(true, 5, 100), TimeSpan.FromSeconds(30));
        StatusQueryService.GetUncLibraryDrives(paths, probe, true, 90);
        Assert.True(SpinWait.SpinUntil(() => StatusQueryService.GetUncLibraryDrives(paths, probe, true, 90).Count == 2, 5000));
        var rows = StatusQueryService.GetUncLibraryDrives(paths, probe, true, 90);
        Assert.Equal(new[] { @"\\nas\a", @"\\nas\b" }, rows.Select(r => r.Root));
        Assert.All(rows, row =>
        {
            Assert.True(row.IsLibraryDrive);
            Assert.True(row.IsNetworkShare);
            Assert.False(row.SpaceReadingStale);
            Assert.Equal(5, row.FreeBytes);
            Assert.Equal(100, row.TotalBytes);
            Assert.Contains("95% full", row.LowSpaceWarning);
            Assert.Equal("unknown", row.SmartHealth);
        });
        Assert.Equal(10, rows.Sum(r => r.FreeBytes));
        Assert.All(StatusQueryService.GetUncLibraryDrives(paths, probe, false, 90), row => Assert.Null(row.LowSpaceWarning));
        Assert.All(StatusQueryService.GetUncLibraryDrives(paths, probe, true, 0), row => Assert.Null(row.LowSpaceWarning));
    }

    [Fact]
    public void UnavailableSharesAreOmittedAndStaleCapacityIsMarked()
    {
        var now = DateTime.UtcNow;
        var probe = new UncVolumeProbe(root => root.EndsWith("offline", StringComparison.Ordinal)
            ? default : new(true, 50, 100), TimeSpan.FromSeconds(30), () => now);
        var paths = new[] { @"\\nas\good", @"\\nas\offline" };
        Assert.Empty(StatusQueryService.GetUncLibraryDrives(paths, probe, true, 90));
        Assert.True(SpinWait.SpinUntil(() => StatusQueryService.GetUncLibraryDrives(paths, probe, true, 90).Count == 1, 5000));
        now = now.AddSeconds(31);
        var stale = Assert.Single(StatusQueryService.GetUncLibraryDrives(paths, probe, true, 90));
        Assert.True(stale.SpaceReadingStale);
        Assert.Equal(50, stale.FreeBytes);
    }

    [Fact]
    public void DiskSpaceHelperMeasuresTheSuppliedDirectoryAndRejectsInvalidPaths()
    {
        Assert.True(FreeSpaceGuard.TryGetDiskSpace(Path.GetTempPath(), out var free, out var total));
        Assert.InRange(free, 0, total);
        Assert.True(total > 0);
        foreach (var path in new[] { "", "\0invalid", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) })
        {
            Assert.False(FreeSpaceGuard.TryGetDiskSpace(path, out free, out total));
            Assert.Equal(0, free);
            Assert.Equal(0, total);
        }
    }
}
