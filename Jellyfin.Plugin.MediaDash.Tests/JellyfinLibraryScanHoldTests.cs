using System;
using System.Reflection;
using Jellyfin.Data.Events;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Production 2026-11-22: a Jellyfin library scan running beside a MediaDash fix run rewrote NFOs for
// already-moved episodes at their old paths. Library scans now wait for MediaDash.
public sealed class JellyfinLibraryScanHoldTests
{
    private static (ITaskManager Manager, LibraryScanCancellationTests.ManagerProxy Proxy, IScheduledTaskWorker Worker, Action<TaskState> SetState) Fixture()
    {
        var manager = DispatchProxy.Create<ITaskManager, LibraryScanCancellationTests.ManagerProxy>();
        var proxy = (LibraryScanCancellationTests.ManagerProxy)(object)manager;
        var task = DispatchProxy.Create<IScheduledTask, IdleCheckTests.ProxyHandler>();
        ((IdleCheckTests.ProxyHandler)(object)task).Handler = method => method.Name == "get_Key" ? IdleCheck.JellyfinLibraryScanKey : null;
        var state = TaskState.Idle;
        var worker = DispatchProxy.Create<IScheduledTaskWorker, IdleCheckTests.ProxyHandler>();
        ((IdleCheckTests.ProxyHandler)(object)worker).Handler = method => method.Name switch
        {
            "get_ScheduledTask" => task,
            "get_State" => state,
            _ => null
        };
        proxy.Workers = [worker];
        return (manager, proxy, worker, value => state = value);
    }

    [Fact]
    public void ALibraryScanStartingDuringARunIsPausedAndRunOnceAfterwards()
    {
        var (manager, proxy, worker, _) = Fixture();
        using (JellyfinLibraryScanHold.Acquire(manager, NullLogger.Instance, cancelRunning: false))
        {
            proxy.Executing!.Invoke(manager, new GenericEventArgs<IScheduledTaskWorker>(worker));
            proxy.Executing!.Invoke(manager, new GenericEventArgs<IScheduledTaskWorker>(worker));
            Assert.Equal(2, proxy.Cancelled.Count);
            Assert.Empty(proxy.Executed);
        }

        Assert.Equal([worker], proxy.Executed);
        Assert.Null(proxy.Executing);
    }

    [Fact]
    public void OverlappingRunsShareOneHoldAndOneDeferredScan()
    {
        var (manager, proxy, worker, _) = Fixture();
        var scan = JellyfinLibraryScanHold.Acquire(manager, NullLogger.Instance, cancelRunning: false);
        var fix = JellyfinLibraryScanHold.Acquire(manager, NullLogger.Instance, cancelRunning: false);
        proxy.Executing!.Invoke(manager, new GenericEventArgs<IScheduledTaskWorker>(worker));
        scan.Dispose();
        Assert.True(JellyfinLibraryScanHold.IsHeld(manager));
        Assert.Empty(proxy.Executed);
        fix.Dispose();
        fix.Dispose();
        Assert.Single(proxy.Executed);
        Assert.False(JellyfinLibraryScanHold.IsHeld(manager));
    }

    [Fact]
    public void AnExplicitRunPausesALibraryScanAlreadyRunning()
    {
        var (manager, proxy, worker, setState) = Fixture();
        setState(TaskState.Running);
        var hold = JellyfinLibraryScanHold.Acquire(manager, NullLogger.Instance, cancelRunning: true);
        Assert.Equal([worker], proxy.Cancelled);
        setState(TaskState.Idle);
        hold.Dispose();
        Assert.Equal([worker], proxy.Executed);
    }

    [Fact]
    public void AScheduledRunNeverInterruptsALibraryScanAlreadyRunningAndOtherTasksAreUntouched()
    {
        var (manager, proxy, worker, setState) = Fixture();
        setState(TaskState.Running);
        var other = DispatchProxy.Create<IScheduledTaskWorker, IdleCheckTests.ProxyHandler>();
        var otherTask = DispatchProxy.Create<IScheduledTask, IdleCheckTests.ProxyHandler>();
        ((IdleCheckTests.ProxyHandler)(object)otherTask).Handler = method => method.Name == "get_Key" ? "RefreshChannels" : null;
        ((IdleCheckTests.ProxyHandler)(object)other).Handler = method => method.Name == "get_ScheduledTask" ? otherTask : null;
        using (JellyfinLibraryScanHold.Acquire(manager, NullLogger.Instance, cancelRunning: false))
        {
            proxy.Executing!.Invoke(manager, new GenericEventArgs<IScheduledTaskWorker>(other));
            Assert.Empty(proxy.Cancelled);
        }

        Assert.Empty(proxy.Executed);
        Assert.Null(proxy.Executing);
    }
}
