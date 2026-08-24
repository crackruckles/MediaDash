using System.IO;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class FixTaskDiskFullTests
{
    [Theory]
    [InlineData(unchecked((int)0x80070070))] // Windows ERROR_DISK_FULL (112)
    [InlineData(unchecked((int)0x80070027))] // Windows ERROR_HANDLE_DISK_FULL (39)
    [InlineData(unchecked((int)0x8007001C))] // Linux ENOSPC (28) as .NET encodes it
    public void IsDiskFull_KnownDiskFullCodes_ReturnTrue(int hresult)
    {
        // These are the only HResults that should reach the "drive full" bucket. Anything else must
        // fall through to the generic IOError bucket so we stop mis-labelling sharing violations
        // and network glitches as "disk error" (issue #19).
        var ex = new IOException("simulated") { HResult = hresult };
        Assert.True(FixTask.IsDiskFull(ex));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020))] // ERROR_SHARING_VIOLATION (32)
    [InlineData(unchecked((int)0x80070005))] // ERROR_ACCESS_DENIED (5)
    [InlineData(unchecked((int)0x80070002))] // ERROR_FILE_NOT_FOUND (2)
    [InlineData(unchecked((int)0x80070040))] // ERROR_NETNAME_DELETED (64) — flaky NFS/SMB
    public void IsDiskFull_NonDiskFullCodes_ReturnFalse(int hresult)
    {
        var ex = new IOException("simulated") { HResult = hresult };
        Assert.False(FixTask.IsDiskFull(ex));
    }
}
