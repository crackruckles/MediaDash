using System;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class FixTaskWindowTests
{
    [Theory]
    [InlineData("", "", false)]
    [InlineData("22:00", "", false)]
    [InlineData("", "05:00", false)]
    [InlineData("22:00", "05:00", true)]
    [InlineData("00:00", "23:59", true)]
    [InlineData("garbage", "05:00", false)]
    [InlineData("22:00", "25:00", false)] // out-of-range hour
    public void TryParseWindow_HonoursHhmm(string start, string end, bool expected)
    {
        Assert.Equal(expected, FixTask.TryParseWindow(start, end, out _, out _));
    }

    [Fact]
    public void WindowStatus_SameStartAndEnd_AlwaysInside()
    {
        // A user setting the same time on both sides gets 24/7 rather than a never-open window.
        var (inside, _) = FixTask.WindowStatus(new TimeOnly(15, 30), new TimeOnly(2, 0), new TimeOnly(2, 0));
        Assert.True(inside);
    }

    [Theory]
    [InlineData(8, 0, false)]   // before start
    [InlineData(9, 0, true)]    // exactly at start (inclusive)
    [InlineData(12, 0, true)]   // middle
    [InlineData(17, 0, false)]  // exactly at end (exclusive)
    [InlineData(18, 0, false)]  // after end
    public void WindowStatus_SameDay(int hour, int minute, bool expected)
    {
        var (inside, _) = FixTask.WindowStatus(new TimeOnly(hour, minute), new TimeOnly(9, 0), new TimeOnly(17, 0));
        Assert.Equal(expected, inside);
    }

    [Theory]
    [InlineData(21, 59, false)] // just before start
    [InlineData(22, 0, true)]   // at start
    [InlineData(23, 30, true)]  // late evening portion
    [InlineData(0, 0, true)]    // exactly midnight
    [InlineData(3, 0, true)]    // post-midnight portion
    [InlineData(5, 0, false)]   // exactly at end (exclusive)
    [InlineData(12, 0, false)]  // middle of the day — outside
    public void WindowStatus_Overnight(int hour, int minute, bool expected)
    {
        var (inside, _) = FixTask.WindowStatus(new TimeOnly(hour, minute), new TimeOnly(22, 0), new TimeOnly(5, 0));
        Assert.Equal(expected, inside);
    }

    [Fact]
    public void WindowStatus_TimeUntilClose_SameDay()
    {
        // 14:00 inside [09:00-17:00] → 3h until close.
        var (inside, remaining) = FixTask.WindowStatus(new TimeOnly(14, 0), new TimeOnly(9, 0), new TimeOnly(17, 0));
        Assert.True(inside);
        Assert.Equal(TimeSpan.FromHours(3), remaining);
    }

    [Fact]
    public void WindowStatus_TimeUntilClose_OvernightPreMidnight()
    {
        // 23:00 inside [22:00-05:00] → 6h until close (1h until midnight + 5h until end).
        var (inside, remaining) = FixTask.WindowStatus(new TimeOnly(23, 0), new TimeOnly(22, 0), new TimeOnly(5, 0));
        Assert.True(inside);
        Assert.Equal(TimeSpan.FromHours(6), remaining);
    }

    [Fact]
    public void WindowStatus_TimeUntilClose_OvernightPostMidnight()
    {
        // 02:00 inside [22:00-05:00] → 3h until close.
        var (inside, remaining) = FixTask.WindowStatus(new TimeOnly(2, 0), new TimeOnly(22, 0), new TimeOnly(5, 0));
        Assert.True(inside);
        Assert.Equal(TimeSpan.FromHours(3), remaining);
    }
}
