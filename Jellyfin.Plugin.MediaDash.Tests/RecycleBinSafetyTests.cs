using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class RecycleBinSafetyTests
{
    [Theory]
    [InlineData("20260822-120102-003-a1b2c3d4")]
    [InlineData("20000101-000000-000-00000000")]
    public void MediaDashBatchDirectoryIsRecognized(string directoryName)
    {
        Assert.True(RecycleBin.IsMediaDashBatchDirectory(directoryName));
    }

    [Theory]
    [InlineData("Movies")]
    [InlineData("20260822-120102-003")]
    [InlineData("20260822-120102-003-not-hex!!")]
    [InlineData("20261322-120102-003-a1b2c3d4")]
    [InlineData("20260822-120102-003-a1b2c3d4-extra")]
    public void UnownedDirectoryIsNotRecognizedAsRecycleBatch(string directoryName)
    {
        Assert.False(RecycleBin.IsMediaDashBatchDirectory(directoryName));
    }
}
