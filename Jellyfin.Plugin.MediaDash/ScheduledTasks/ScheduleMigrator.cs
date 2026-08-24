using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>
/// One-shot startup migration that replaces a legacy DailyTrigger on the fix task with the current
/// opportunistic <see cref="FixTask.FixInterval"/> IntervalTrigger. Jellyfin persists user-modified triggers
/// separately from <see cref="FixTask.GetDefaultTriggers"/>, so bumping the plugin doesn't reseed them —
/// pre-v0.9.1 users would otherwise keep firing fixes once a day at their old configured time.
/// </summary>
/// <remarks>
/// ITaskManager.ScheduledTasks is not populated at the moment host-wide IHostedServices start (plugin
/// scheduled tasks are registered later in Jellyfin's boot). Deferring the check by 30 seconds lets the
/// task list finish assembling; still a no-op when the task already carries an IntervalTrigger.
/// </remarks>
internal sealed class ScheduleMigrator : IHostedService, IDisposable
{
    private static readonly TimeSpan MigrationDelay = TimeSpan.FromSeconds(30);

    private readonly ITaskManager _taskManager;
    private readonly ILogger<ScheduleMigrator> _logger;
    private CancellationTokenSource? _cts;

    public ScheduleMigrator(ITaskManager taskManager, ILogger<ScheduleMigrator> logger)
    {
        _taskManager = taskManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = MigrateAfterDelayAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }

    private async Task MigrateAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(MigrationDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.FixTaskSeeded == true)
            {
                // User is in charge from here on. If they removed the trigger via Dashboard →
                // Scheduled Tasks we respect that; the "Reset scheduled task" button in
                // Settings → Maintenance is the one way to bring it back.
                return;
            }

            var fixTask = _taskManager.ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is FixTask);
            if (fixTask is null)
            {
                _logger.LogDebug("ScheduleMigrator: FixTask not registered {Delay} after startup; skipping.", MigrationDelay);
                return;
            }

            var current = fixTask.Triggers ?? [];
            if (!current.Any(t => t.Type == TaskTriggerInfoType.IntervalTrigger))
            {
                _logger.LogInformation("ScheduleMigrator: seeding fix-task with opportunistic {Interval} IntervalTrigger", FixTask.FixInterval);
                fixTask.Triggers =
                [
                    new TaskTriggerInfo
                    {
                        Type = TaskTriggerInfoType.IntervalTrigger,
                        IntervalTicks = FixTask.FixInterval.Ticks
                    }
                ];
            }

            // Whether we added the trigger or a fresh install already had it from GetDefaultTriggers,
            // mark seeded so subsequent restarts don't re-add a trigger the user has since deleted.
            if (config is not null)
            {
                config.FixTaskSeeded = true;
                Plugin.Instance?.SaveConfiguration();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ScheduleMigrator failed; users on legacy DailyTrigger may need to Save Settings once to migrate.");
            Api.Diagnostics.Record("ScheduleMigrator.Migrate", "Failed to migrate the fix-task trigger to the opportunistic interval: " + ex.Message + ". Open MediaDash → Settings → Save once to force the migration.");
        }
    }
}
