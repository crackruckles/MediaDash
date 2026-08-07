using System;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class IdleCheckTests
{
    private static readonly DateTime Now = new(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NowPlayingItemPresent_IsBusy()
    {
        Assert.True(IdleCheck.IsSessionActive(hasNowPlaying: true, lastPlaybackCheckIn: default, Now));
    }

    [Fact]
    public void RecentPlaybackCheckInWithinWindow_IsBusy()
    {
        // Someone was watching 5 minutes ago — within the 15-minute grace window.
        Assert.True(IdleCheck.IsSessionActive(hasNowPlaying: false, lastPlaybackCheckIn: Now.AddMinutes(-5), Now));
    }

    [Fact]
    public void StalePlaybackCheckInOutsideWindow_IsIdle()
    {
        Assert.False(IdleCheck.IsSessionActive(hasNowPlaying: false, lastPlaybackCheckIn: Now.AddMinutes(-20), Now));
    }

    [Fact]
    public void AdminBrowsingDashboardOnly_IsIdle()
    {
        // Regression guard for the 2026-08-07 bug report: an admin session sitting on the Jellyfin
        // dashboard bumps LastActivityDate every few seconds via the poll, but LastPlaybackCheckIn
        // only moves during actual playback. IsSessionActive must ignore the browsing activity.
        var stalePlaybackTime = Now.AddHours(-3);
        Assert.False(IdleCheck.IsSessionActive(hasNowPlaying: false, lastPlaybackCheckIn: stalePlaybackTime, Now));
    }

    [Fact]
    public void NeverPlayedSession_IsIdle()
    {
        // Client that has never played anything: LastPlaybackCheckIn is default(DateTime).
        Assert.False(IdleCheck.IsSessionActive(hasNowPlaying: false, lastPlaybackCheckIn: default, Now));
    }
}
