using System;
using System.Threading;
using Jellyfin.Data.Events;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

internal sealed class LibraryScanCancellation : IDisposable
{
    private readonly ITaskManager? _taskManager;
    private readonly CancellationTokenSource _cancellation;
    private readonly object _sync = new();
    private bool _disposed;

    public LibraryScanCancellation(ITaskManager? taskManager, CancellationToken cancellationToken)
    {
        _taskManager = taskManager;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_taskManager is not null)
        {
            _taskManager.TaskExecuting += OnTaskExecuting;
            // Subscribe first so a scan starting during setup cannot slip between the checks.
            if (IdleCheck.IsLibraryScanRunning(_taskManager))
            {
                _cancellation.Cancel();
            }
        }
    }

    public CancellationToken Token => _cancellation.Token;

    public void Dispose()
    {
        if (_taskManager is not null)
        {
            _taskManager.TaskExecuting -= OnTaskExecuting;
        }

        lock (_sync)
        {
            _disposed = true;
            _cancellation.Dispose();
        }
    }

    private void OnTaskExecuting(object? sender, GenericEventArgs<IScheduledTaskWorker> e)
    {
        if (!string.Equals(e.Argument.ScheduledTask.Key, IdleCheck.JellyfinLibraryScanKey, StringComparison.Ordinal))
        {
            return;
        }

        lock (_sync)
        {
            if (!_disposed)
            {
                _cancellation.Cancel();
            }
        }
    }
}
