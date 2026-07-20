using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>
/// Scheduled task that drains the fix queue: automatic-mode issues are queued first,
/// then every queued issue is handed to its fixer. Respects dry-run and pause-during-playback.
/// </summary>
public sealed class FixTask : IScheduledTask
{
    private static readonly IssueType[] FixableTypes =
    [
        IssueType.Duplicate,
        IssueType.Quality,
        IssueType.SubtitleLanguage,
        IssueType.AudioLanguage,
        IssueType.Playability
    ];

    private readonly MediaDashDb _db;
    private readonly IEnumerable<IFixer> _fixers;
    private readonly RecycleBin _recycleBin;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<FixTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FixTask"/> class.
    /// </summary>
    /// <param name="db">The plugin database.</param>
    /// <param name="fixers">All registered fixers.</param>
    /// <param name="recycleBin">The recycle bin.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="logger">The logger.</param>
    public FixTask(MediaDashDb db, IEnumerable<IFixer> fixers, RecycleBin recycleBin, ISessionManager sessionManager, ILogger<FixTask> logger)
    {
        _db = db;
        _fixers = fixers;
        _recycleBin = recycleBin;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the next run skips the server-idle check.
    /// Set by the dashboard's "Run fixes now" button — the person clicking it is themselves an active session.
    /// </summary>
    internal static bool BypassIdleCheckOnce { get; set; }

    /// <inheritdoc />
    public string Name => "Apply approved fixes";

    /// <inheritdoc />
    public string Key => "MediaDashFix";

    /// <inheritdoc />
    public string Description => "Applies approved and automatic fixes: removes duplicates, re-encodes oversized files, strips unwanted tracks.";

    /// <inheritdoc />
    public string Category => "MediaDash";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var bypassIdleCheck = BypassIdleCheckOnce;
        BypassIdleCheckOnce = false;

        if (config.PauseDuringPlayback && !bypassIdleCheck && IdleCheck.IsServerBusy(_sessionManager))
        {
            _logger.LogInformation("Skipping fix run: someone is watching or was recently active. Queued issues stay queued.");
            progress.Report(100);
            return;
        }

        foreach (var type in FixableTypes)
        {
            if (config.GetFixMode(type) == FixMode.Automatic)
            {
                var queued = _db.QueueDetectedIssues(type);
                if (queued > 0)
                {
                    _logger.LogInformation("Auto-queued {Count} {Type} issues", queued, type);
                }
            }
        }

        // Smallest files first so early re-encodes free disk space for the bigger ones behind them.
        // Missing files sort to the front (size 0) so they fail fast rather than block the queue.
        var queue = _db.GetIssues(status: IssueStatus.Queued)
            .Where(i => config.GetFixMode(i.Type) is FixMode.ManualApprove or FixMode.Automatic)
            .OrderBy(GetFileSizeOrZero)
            .ToList();

        _logger.LogInformation("MediaDash fix run: {Count} queued issues (dry-run: {DryRun})", queue.Count, config.DryRun);

        for (var i = 0; i < queue.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (config.PauseDuringPlayback && !bypassIdleCheck && IdleCheck.IsServerBusy(_sessionManager))
            {
                _logger.LogInformation("Pausing fix run: someone started using the server. Remaining issues stay queued.");
                break;
            }

            var issue = queue[i];
            var fixer = _fixers.FirstOrDefault(f => f.CanFix(issue.Type));
            if (fixer is null)
            {
                continue;
            }

            var itemIndex = i;
            var slot = 100.0 / queue.Count;
            progress.Report(itemIndex * slot);
            Plugin.CurrentActivity = issue.Path;
            // Synchronous IProgress: Progress<T> queues callbacks and can reorder reports, leading to a jittery bar.
            var itemProgress = new SynchronousProgress(fraction => progress.Report((itemIndex + Math.Clamp(fraction, 0, 1)) * slot));

            try
            {
                var result = await fixer.FixAsync(issue, itemProgress, cancellationToken).ConfigureAwait(false);
                _db.AddHistory(new HistoryEntry
                {
                    IssueId = issue.Id,
                    Type = issue.Type,
                    Path = issue.Path,
                    Action = result.Message,
                    BytesFreed = result.Success && !result.WasDryRun ? result.BytesFreed : 0,
                    RecyclePath = result.RecyclePath,
                    FixedAtUtc = DateTime.UtcNow,
                    WasDryRun = result.WasDryRun
                });

                if (result.Success && !result.WasDryRun)
                {
                    _db.UpdateIssueStatus(issue.Id, IssueStatus.Fixed);
                }
                else if (!result.Success)
                {
                    _logger.LogWarning("Fix failed for {Path}: {Message}", issue.Path, result.Message);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error fixing {Path}", issue.Path);
            }

            progress.Report((i + 1) * 100.0 / queue.Count);
        }

        _recycleBin.Purge(config.RecycleBinRetentionDays);
        Plugin.CurrentActivity = null;
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            }
        ];
    }

    private static long GetFileSizeOrZero(Data.Issue issue)
    {
        try
        {
            return System.IO.File.Exists(issue.Path) ? new System.IO.FileInfo(issue.Path).Length : 0;
        }
        catch (System.IO.IOException)
        {
            return 0;
        }
    }

    private sealed class SynchronousProgress : IProgress<double>
    {
        private readonly Action<double> _handler;

        public SynchronousProgress(Action<double> handler)
        {
            _handler = handler;
        }

        public void Report(double value) => _handler(value);
    }
}
