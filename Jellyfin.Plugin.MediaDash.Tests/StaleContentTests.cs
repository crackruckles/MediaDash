using System;
using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class StaleContentTests
{
    private static readonly DateTime Cutoff = new(2026, 01, 01, 00, 00, 00, DateTimeKind.Utc);

    [Fact]
    public void FreshlyAdded_NeverPlayed_IsNotStale()
    {
        // Freshly imported items get a grace period — being new is not enough to be stale.
        Assert.False(StaleContentScanner.IsStale(Cutoff.AddDays(1), null, Cutoff));
    }

    [Fact]
    public void OldItem_NeverPlayed_IsStale()
    {
        Assert.True(StaleContentScanner.IsStale(Cutoff.AddDays(-1), null, Cutoff));
    }

    [Fact]
    public void OldItem_PlayedRecently_IsNotStale()
    {
        Assert.False(StaleContentScanner.IsStale(Cutoff.AddDays(-30), Cutoff.AddDays(1), Cutoff));
    }

    [Fact]
    public void OldItem_PlayedLongAgo_IsStale()
    {
        Assert.True(StaleContentScanner.IsStale(Cutoff.AddDays(-30), Cutoff.AddDays(-1), Cutoff));
    }

    [Fact]
    public void OldItem_PlayedOnCutoff_IsStale()
    {
        // Boundary: "played exactly at the cutoff" is not fresher than the cutoff, so it's stale.
        // Cutoff means "played since this point is fresh" — inclusive of moments strictly after.
        Assert.True(StaleContentScanner.IsStale(Cutoff.AddDays(-30), Cutoff, Cutoff));
    }
}
