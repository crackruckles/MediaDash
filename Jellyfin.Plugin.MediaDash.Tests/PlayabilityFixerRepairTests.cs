using System;
using System.IO;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Pre-flight guard for the playability repair ladder. If the guard mis-fires we either
/// starve the ladder (never repair) or run repairs that fill the volume mid-encode. Both
/// end users badly, so the check has its own test even though the surrounding rung logic
/// is exercised by the tools/repair-test end-to-end harness.
/// </summary>
public class PlayabilityFixerRepairTests
{
    [Fact]
    public void HasFreeSpace_TrueForTinyFile()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, new byte[] { 0x01 });
            Assert.True(PlayabilityFixer.HasFreeSpace(tmp, multiplier: 3));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void HasFreeSpace_FalseForMissingFile()
    {
        var gone = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"));
        Assert.False(PlayabilityFixer.HasFreeSpace(gone, multiplier: 2));
    }

    [Fact]
    public void HasFreeSpace_FalseWhenMultiplierWouldOverflowFreeSpace()
    {
        // Ask for 1000× the free space on the temp drive by pretending a huge file is there.
        // Rather than actually writing gigs, we assert the arithmetic itself refuses the impossible
        // by pointing at a real file and demanding a multiplier that no volume can satisfy.
        var tmp = Path.GetTempFileName();
        try
        {
            var oneMb = new byte[1024 * 1024];
            File.WriteAllBytes(tmp, oneMb);
            var drive = new DriveInfo(Path.GetPathRoot(tmp) ?? "/");
            var impossibleMultiplier = (int)Math.Min(int.MaxValue, drive.AvailableFreeSpace / oneMb.Length + 1);
            Assert.False(PlayabilityFixer.HasFreeSpace(tmp, impossibleMultiplier));
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
