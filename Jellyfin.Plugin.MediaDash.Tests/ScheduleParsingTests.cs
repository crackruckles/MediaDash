using System;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class ScheduleParsingTests
{
    [Theory]
    [InlineData("03:00", 3, 0)]
    [InlineData("00:00", 0, 0)]
    [InlineData("23:59", 23, 59)]
    [InlineData("9:30", 9, 30)]
    public void ValidHhMmParsesToTicksFromMidnight(string input, int hours, int minutes)
    {
        Assert.Equal(new TimeSpan(hours, minutes, 0).Ticks, FixTask.ParseScheduleTicks(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bogus")]
    [InlineData("25:00")]
    public void InvalidValuesFallBackToThreeAm(string? input)
    {
        Assert.Equal(TimeSpan.FromHours(3).Ticks, FixTask.ParseScheduleTicks(input));
    }
}
