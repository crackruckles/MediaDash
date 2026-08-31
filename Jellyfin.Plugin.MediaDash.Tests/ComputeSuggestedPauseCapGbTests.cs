using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class ComputeSuggestedPauseCapGbTests
{
    private const long Gib = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(500L * Gib, 3L * Gib, 497)]
    [InlineData(1024L * Gib, 3L * Gib, 1021)]
    [InlineData(10L * Gib, 3L * Gib, 7)]
    public void SubtractsFloorFromTotal(long totalBytes, long floorBytes, int expected)
    {
        Assert.Equal(expected, MediaDashController.ComputeSuggestedPauseCapGb(totalBytes, floorBytes));
    }

    [Fact]
    public void ClampsToOneGbFloor_OnTinyVolumes()
    {
        // A 2 GB volume with a 3 GB floor would mathematically give -1; the setting only accepts
        // >=1 or the save silently disables the cap by writing 0. Clamp so the user still gets
        // a working cap, even if the volume is smaller than the safety floor.
        Assert.Equal(1, MediaDashController.ComputeSuggestedPauseCapGb(2L * Gib, 3L * Gib));
        Assert.Equal(1, MediaDashController.ComputeSuggestedPauseCapGb(0, 3L * Gib));
    }

    [Fact]
    public void FloorConstant_MatchesTheUserFacingMessage()
    {
        // The FixTask floor and the DiskInfo endpoint's default suggestion must agree — otherwise
        // the "Jellyfin needs 3 GB" message we show doesn't line up with the number the pause
        // check actually enforces. Locks both together in a single assertion.
        Assert.Equal(3L * Gib, FixTask.BinVolumeMinFreeBytes);
    }
}
