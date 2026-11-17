using System;
using System.IO;
using System.Security.Cryptography;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Run explicitly on Windows against an owned writable NAS directory; absence is a failure, never a skip.
[Trait("Category", "ReportedIssueExternal")]
public sealed class ReportedUncAcceptanceTests
{
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
