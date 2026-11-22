using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Events;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>
/// Keeps Jellyfin's own library scan from running while MediaDash scans or fixes. A library scan
/// that starts during a run is cancelled and run once after the last MediaDash run ends, so it
/// indexes the finished result instead of a library that is half moved.
/// </summary>
/// <remarks>
/// Production 2026-11-22: a library scan ran alongside a fix run that was moving episodes out of
/// shared folders. Jellyfin, still holding the old paths, wrote NFOs for already-moved episodes back
/// into those folders, and every remaining episode there was refused as changed after approval.
/// Holds are counted per task manager, so a scan and a fix run overlapping share one hold and one
/// deferred library scan.
/// </remarks>
internal sealed class JellyfinLibraryScanHold : IDisposable
{
    private static readonly object Gate = new();
    private static readonly Dictionary<ITaskManager, State> States = new(ReferenceEqualityComparer.Instance);
    private static readonly TimeSpan RecheckDelay = TimeSpan.FromSeconds(2);

    private readonly ITaskManager? _taskManager;
    private bool _released;

    private JellyfinLibraryScanHold(ITaskManager? taskManager) => _taskManager = taskManager;

    /// <summary>Holds Jellyfin library scans until the returned hold is disposed.</summary>
    /// <param name="taskManager">Jellyfin's task manager; null holds nothing.</param>
    /// <param name="logger">Run logger.</param>
    /// <param name="cancelRunning">Also pause a library scan that is already running (an explicit run).</param>
    /// <returns>The hold.</returns>
    internal static JellyfinLibraryScanHold Acquire(ITaskManager? taskManager, ILogger logger, bool cancelRunning)
    {
        var hold = new JellyfinLibraryScanHold(taskManager);
        if (taskManager is null)
        {
            return hold;
        }

        State state;
        lock (Gate)
        {
            if (!States.TryGetValue(taskManager, out state!))
            {
                state = new State(taskManager);
                States.Add(taskManager, state);
                taskManager.TaskExecuting += state.OnTaskExecuting;
            }

            state.Holders++;
            state.Logger = logger;
        }

        if (cancelRunning && state.CancelRunningScan("a MediaDash run started"))
        {
            lock (Gate)
            {
                state.Deferred = true;
            }
        }

        return hold;
    }

    /// <summary>True while a MediaDash run holds library scans for this task manager.</summary>
    /// <param name="taskManager">Jellyfin's task manager.</param>
    /// <returns>True when held.</returns>
    internal static bool IsHeld(ITaskManager taskManager)
    {
        lock (Gate)
        {
            return States.TryGetValue(taskManager, out var state) && state.Holders > 0;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        if (_taskManager is null)
        {
            return;
        }

        State? finished = null;
        lock (Gate)
        {
            if (States.TryGetValue(_taskManager, out var state) && --state.Holders <= 0)
            {
                States.Remove(_taskManager);
                _taskManager.TaskExecuting -= state.OnTaskExecuting;
                finished = state;
            }
        }

        if (finished is { Deferred: true })
        {
            finished.StartLibraryScan();
        }
    }

    private sealed class State(ITaskManager manager)
    {
        public int Holders { get; set; }

        public bool Deferred { get; set; }

        public ILogger? Logger { get; set; }

        public void OnTaskExecuting(object? sender, GenericEventArgs<IScheduledTaskWorker> e)
        {
            if (!string.Equals(e.Argument.ScheduledTask.Key, IdleCheck.JellyfinLibraryScanKey, StringComparison.Ordinal))
            {
                return;
            }

            lock (Gate)
            {
                if (Holders <= 0)
                {
                    return;
                }

                Deferred = true;
            }

            try
            {
                manager.Cancel(e.Argument);
                Logger?.LogInformation("Paused a Jellyfin library scan that started during a MediaDash run; it runs once MediaDash finishes.");
                Api.Diagnostics.Record("LibraryScanHold", "A Jellyfin library scan that started during a MediaDash run was paused. It runs when MediaDash finishes.");
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "Could not pause Jellyfin's library scan.");
            }

            // The event can fire before the scan's own cancellation source exists; check again shortly.
            _ = Task.Run(async () =>
            {
                await Task.Delay(RecheckDelay).ConfigureAwait(false);
                if (IsHeld(manager))
                {
                    CancelRunningScan("a MediaDash run is in progress");
                }
            });
        }

        public bool CancelRunningScan(string reason)
        {
            var worker = LibraryScan();
            if (worker is null || worker.State == TaskState.Idle)
            {
                return false;
            }

            try
            {
                manager.Cancel(worker);
                Logger?.LogInformation("Paused Jellyfin's library scan because {Reason}; it runs once MediaDash finishes.", reason);
                Api.Diagnostics.Record("LibraryScanHold", "Jellyfin's library scan was paused because " + reason + ". It runs when MediaDash finishes.");
                return true;
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "Could not pause Jellyfin's library scan.");
                return false;
            }
        }

        public void StartLibraryScan()
        {
            var worker = LibraryScan();
            if (worker is null || worker.State != TaskState.Idle)
            {
                return;
            }

            try
            {
                _ = manager.Execute(worker, new TaskOptions());
                Logger?.LogInformation("MediaDash finished; running the Jellyfin library scan it paused.");
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "Could not start the paused Jellyfin library scan.");
                Api.Diagnostics.Record("LibraryScanHold", "The paused Jellyfin library scan could not be restarted: " + ex.Message + ". Run 'Scan Media Library' from the dashboard.");
            }
        }

        private IScheduledTaskWorker? LibraryScan()
        {
            try
            {
                return manager.ScheduledTasks.FirstOrDefault(worker =>
                    string.Equals(worker.ScheduledTask.Key, IdleCheck.JellyfinLibraryScanKey, StringComparison.Ordinal));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
