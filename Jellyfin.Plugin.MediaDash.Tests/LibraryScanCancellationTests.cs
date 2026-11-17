using System;
using System.Reflection;
using System.Threading;
using Jellyfin.Data.Events;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Model.Tasks;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class LibraryScanCancellationTests
{
    [Fact]
    public void AutomaticRunIsCancelledWhenLibraryScanStarts()
    {
        var manager = CreateManager(out var handler);
        using var scope = CreateScope(manager, CancellationToken.None);
        var token = Token(scope);
        Assert.False(token.IsCancellationRequested);
        handler.Executing!(manager, new GenericEventArgs<IScheduledTaskWorker>(Worker("RefreshLibrary", TaskState.Running)));
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void ExistingScanCancelsAutomaticRun()
    {
        var manager = CreateManager(out var handler);
        handler.Workers = new[] { Worker("RefreshLibrary", TaskState.Cancelling) };
        using var scope = CreateScope(manager, CancellationToken.None);
        Assert.True(Token(scope).IsCancellationRequested);
    }

    [Fact]
    public void OtherTasksDoNotCancelAndDisposalUnsubscribes()
    {
        var manager = CreateManager(out var handler);
        var scope = CreateScope(manager, CancellationToken.None);
        handler.Executing!(manager, new GenericEventArgs<IScheduledTaskWorker>(Worker("MediaDashFix", TaskState.Running)));
        Assert.False(Token(scope).IsCancellationRequested);
        scope.Dispose();
        Assert.Null(handler.Executing);
    }

    [Fact]
    public void ManualRunBypassesLibraryScanButStillHonoursStop()
    {
        using var stop = new CancellationTokenSource();
        using var scope = CreateScope(null, stop.Token);
        var token = Token(scope);
        Assert.False(token.IsCancellationRequested);
        stop.Cancel();
        Assert.True(token.IsCancellationRequested);
    }

    private static LibraryScanCancellation CreateScope(ITaskManager? manager, CancellationToken token) => new(manager, token);

    private static CancellationToken Token(LibraryScanCancellation scope) => scope.Token;

    private static ITaskManager CreateManager(out ManagerProxy handler)
    {
        var manager = DispatchProxy.Create<ITaskManager, ManagerProxy>();
        handler = (ManagerProxy)(object)manager;
        return manager;
    }

    private static IScheduledTaskWorker Worker(string key, TaskState state)
    {
        var task = DispatchProxy.Create<IScheduledTask, IdleCheckTests.ProxyHandler>();
        ((IdleCheckTests.ProxyHandler)(object)task).Handler = method => method.Name == "get_Key" ? key : null;
        var worker = DispatchProxy.Create<IScheduledTaskWorker, IdleCheckTests.ProxyHandler>();
        ((IdleCheckTests.ProxyHandler)(object)worker).Handler = method => method.Name switch
        {
            "get_ScheduledTask" => task,
            "get_State" => state,
            _ => null
        };
        return worker;
    }

    public class ManagerProxy : DispatchProxy
    {
        public IScheduledTaskWorker[] Workers { get; set; } = Array.Empty<IScheduledTaskWorker>();
        public EventHandler<GenericEventArgs<IScheduledTaskWorker>>? Executing { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_ScheduledTasks": return Workers;
                case "add_TaskExecuting": Executing += (EventHandler<GenericEventArgs<IScheduledTaskWorker>>)args![0]!; break;
                case "remove_TaskExecuting": Executing -= (EventHandler<GenericEventArgs<IScheduledTaskWorker>>)args![0]!; break;
            }

            return null;
        }
    }
}
