using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Run explicitly on Windows against an owned writable NAS directory; absence is a failure, never a skip.
[Trait("Category", "ReportedIssueExternal")]
public sealed class ReportedUncAcceptanceTests
{
    [Fact]
    public void Issue91_RealUncCapacityReachesTheOverviewRows()
    {
        Assert.True(OperatingSystem.IsWindows(), "#91 acceptance requires Windows and an actual UNC share.");
        var path = Environment.GetEnvironmentVariable("MEDIADASH_TEST_UNC_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(path), "Set MEDIADASH_TEST_UNC_ROOT to an owned UNC test directory.");
        var root = Assert.Single(UncLibraryRoots.From(new[] { path! }));
        Assert.True(FreeSpaceGuard.TryGetDiskSpace(root, out _, out var total), "The configured share root must be measurable by this account.");
        var probe = new UncVolumeProbe(share => FreeSpaceGuard.TryGetDiskSpace(share, out var free, out var size)
            ? new(true, free, size) : default, TimeSpan.FromSeconds(30));
        var paths = new[] { path!, path!.ToUpperInvariant() };
        Assert.True(SpinWait.SpinUntil(() => StatusQueryService.GetUncLibraryDrives(paths, probe, false, 90).Count == 1, 10000));
        var row = Assert.Single(StatusQueryService.GetUncLibraryDrives(paths, probe, false, 90));
        Assert.Equal(root, row.Root);
        Assert.True(row.IsLibraryDrive);
        Assert.True(row.IsNetworkShare);
        Assert.False(row.SpaceReadingStale);
        Assert.Equal(total, row.TotalBytes);
        Assert.InRange(row.FreeBytes, 0, total);
        Assert.Null(row.LowSpaceWarning);
    }

    [Fact]
    public void Issue89_BothSpaceGuardsMeasureAnExistingUncFileAndPreserveItsBytes()
    {
        Assert.True(OperatingSystem.IsWindows(), "#89 acceptance requires Windows and an actual UNC share.");
        var root = Environment.GetEnvironmentVariable("MEDIADASH_TEST_UNC_ROOT");
        Assert.True(!string.IsNullOrWhiteSpace(root) && root.StartsWith(@"\\", StringComparison.Ordinal),
            "Set MEDIADASH_TEST_UNC_ROOT to an owned writable UNC test directory.");
        Assert.False(root!.StartsWith(@"\\?\", StringComparison.Ordinal) || root.StartsWith(@"\\.\", StringComparison.Ordinal),
            "Use an ordinary \\\\server\\share UNC path; local device paths cannot certify NAS support.");
        Assert.True(Directory.Exists(root), "The owned UNC test directory must already exist.");
        var path = Path.Combine(root!, "mediadash-space-acceptance-" + Guid.NewGuid().ToString("N") + ".bin");
        var bytes = new byte[1024];
        Random.Shared.NextBytes(bytes);
        using (var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) created.Write(bytes);
        try
        {
            Assert.True(FreeSpaceGuard.HasEnoughFreeSpace(path, 2), "A 1 KB file on a test share with at least 2 KB free must pass the space check.");
            Assert.Null(FreeSpaceGuard.DescribeShortfall(path, 2));
            Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(File.ReadAllBytes(path)));
        }
        finally { File.Delete(path); }
    }
}
