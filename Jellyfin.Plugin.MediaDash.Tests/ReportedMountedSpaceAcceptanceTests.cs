using System;
using System.IO;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Run explicitly in an owned Linux mount namespace; missing prerequisites fail, never skip.
[Trait("Category", "ReportedIssueExternal")]
public sealed class ReportedMountedSpaceAcceptanceTests
{
    [Theory]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    public void Issue89_UsesMediaMountInsteadOfContainerRoot(bool enoughSpace, int multiplier)
    {
        Assert.True(OperatingSystem.IsLinux(), "Mount acceptance requires Linux and owned test mounts.");
        var directory = Environment.GetEnvironmentVariable(enoughSpace
            ? "MEDIADASH_TEST_ROOMY_MOUNT" : "MEDIADASH_TEST_SMALL_MOUNT");
        Assert.True(!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory),
            "Set MEDIADASH_TEST_ROOMY_MOUNT and MEDIADASH_TEST_SMALL_MOUNT to owned Linux mount directories.");
        var rootFree = new DriveInfo("/").AvailableFreeSpace;
        var mountFree = new DriveInfo(directory!).AvailableFreeSpace;
        // Sparse length crosses the relevant free-space boundary without consuming that space.
        var length = (enoughSpace ? rootFree : mountFree) / multiplier + 1024;
        Assert.Equal(enoughSpace, mountFree / multiplier >= length);
        Assert.Equal(!enoughSpace, rootFree / multiplier >= length);
        var path = Path.Combine(directory!, "mediadash-space-" + Guid.NewGuid().ToString("N") + ".bin");
        using (var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            created.WriteByte(0x89);
            created.SetLength(length);
        }

        try
        {
            Assert.Equal(enoughSpace, FreeSpaceGuard.HasEnoughFreeSpace(path, multiplier));
            var shortfall = FreeSpaceGuard.DescribeShortfall(path, multiplier);
            if (enoughSpace)
            {
                Assert.Null(shortfall);
            }
            else
            {
                Assert.NotNull(shortfall);
                Assert.Contains("Not enough free disk space", shortfall);
                Assert.Contains(directory!, shortfall);
            }

            Assert.Equal(length, new FileInfo(path).Length);
            using var source = File.OpenRead(path);
            Assert.Equal(0x89, source.ReadByte());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
