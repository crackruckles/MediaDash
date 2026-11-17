using System;
using System.Reflection;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Model.Tasks;
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

    [Fact]
    public void IsLibraryScanRunning_NullManager_False()
    {
        Assert.False(IdleCheck.IsLibraryScanRunning(null));
    }

    [Fact]
    public void IsLibraryScanRunning_LibraryScanIdle_False()
    {
        var manager = FakeTaskManager("RefreshLibrary", TaskState.Idle);
        Assert.False(IdleCheck.IsLibraryScanRunning(manager));
    }

    [Fact]
    public void IsLibraryScanRunning_LibraryScanRunning_True()
    {
        var manager = FakeTaskManager("RefreshLibrary", TaskState.Running);
        Assert.True(IdleCheck.IsLibraryScanRunning(manager));
    }

    [Fact]
    public void IsLibraryScanRunning_OtherTaskRunning_False()
    {
        // Some other scheduled task running (e.g. our own MediaDashScan or trickplay) must not
        // trip the gate — only Jellyfin's "RefreshLibrary" counts.
        var manager = FakeTaskManager("MediaDashScan", TaskState.Running);
        Assert.False(IdleCheck.IsLibraryScanRunning(manager));
    }

    private static ITaskManager FakeTaskManager(string key, TaskState state)
    {
        var task = DispatchProxy.Create<IScheduledTask, ProxyHandler>();
        ((ProxyHandler)(object)task).Handler = method => method.Name == "get_Key" ? key : null;

        var worker = DispatchProxy.Create<IScheduledTaskWorker, ProxyHandler>();
        ((ProxyHandler)(object)worker).Handler = method => method.Name switch
        {
            "get_ScheduledTask" => task,
            "get_State" => state,
            _ => null
        };

        var manager = DispatchProxy.Create<ITaskManager, ProxyHandler>();
        ((ProxyHandler)(object)manager).Handler = method =>
            method.Name == "get_ScheduledTasks" ? new[] { worker } : null;
        return manager;
    }

    public class ProxyHandler : DispatchProxy
    {
        public Func<MethodInfo, object?> Handler { get; set; } = _ => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!);
    }
}
