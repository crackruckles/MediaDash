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
        IssueType.Playability,
        IssueType.Misplaced
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

        // An issue reaches Queued status either because the auto-queue step above put it there (Automatic mode)
        // or because the user explicitly approved it in the UI. Manual approval is a stronger signal than the
        // type's default mode, so DetectOnly does NOT filter it back out — only Off does (the type is disabled
        // entirely). Previous versions silently dropped DetectOnly-queued items and left users staring at
        // "Run fixes now" doing nothing.
        // Smallest files first so early re-encodes free disk space for the bigger ones behind them.
        // Missing files sort to the front (size 0) so they fail fast rather than block the queue.
        var allQueued = _db.GetIssues(status: IssueStatus.Queued).ToList();
        var offCount = allQueued.Count(i => config.GetFixMode(i.Type) == FixMode.Off);
        var queue = allQueued
            .Where(i => config.GetFixMode(i.Type) != FixMode.Off)
            .OrderBy(GetFileSizeOrZero)
            .ToList();

        _logger.LogInformation("MediaDash fix run: {Count} queued issues (dry-run: {DryRun})", queue.Count, config.DryRun);

        // Run tallies live here (not thread-local per fixer) so the after-run summary can call out the
        // dominant failure — e.g. "all 142 fixes failed with permission denied" — via a dashboard alert.
        var attempted = 0;
        var succeeded = 0;
        var failed = 0;
        var reasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        void RecordFailure(string reason)
        {
            failed++;
            var bucket = BucketReason(reason);
            reasonCounts[bucket] = reasonCounts.TryGetValue(bucket, out var n) ? n + 1 : 1;
        }

        if (allQueued.Count > 0 && queue.Count == 0)
        {
            // All queued issues belong to types the user has set to Off — nothing will run. Say so out
            // loud on the Errors tab, because otherwise the button appears broken.
            Api.Diagnostics.Record(
                "FixTask.NoRunnable",
                allQueued.Count + " issue(s) are approved but every one belongs to a type set to 'Off' in Settings → What to fix. Switch the type to 'Ask me first' or 'Automatic' to let them run, or dismiss the issues.");
        }
        else if (queue.Count > 0 && offCount > 0)
        {
            Api.Diagnostics.Record(
                "FixTask.SomeSkipped",
                offCount + " approved issue(s) will not run because their type is set to 'Off' in Settings. The other " + queue.Count + " will run.");
        }

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

            attempted++;
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
                    succeeded++;
                    _db.UpdateIssueStatus(issue.Id, IssueStatus.Fixed);
                }
                else if (!result.Success)
                {
                    RecordFailure(result.Message);
                    _logger.LogWarning("Fix failed for {Path}: {Message}", issue.Path, result.Message);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                RecordFailure("Permission denied");
                // Very common on Linux servers where library files aren't owned by the jellyfin user.
                // Not a plugin bug — surface it with an actionable message and record the failed attempt
                // in History so the user sees it alongside successful fixes.
                _logger.LogWarning(ex, "Permission denied fixing {Path}", issue.Path);
                var message = "Jellyfin lacks write access to " + issue.Path + ". Check that the file (and its folder) is owned by or read+writable by the user Jellyfin runs as (typically 'jellyfin' on Linux).";
                Api.Diagnostics.Record("FixTask.PermissionDenied", message);
                _db.AddHistory(new HistoryEntry
                {
                    IssueId = issue.Id,
                    Type = issue.Type,
                    Path = issue.Path,
                    Action = "Fix failed — permission denied. " + issue.Path + " isn't writable by the Jellyfin user.",
                    BytesFreed = 0,
                    FixedAtUtc = DateTime.UtcNow,
                    WasDryRun = false
                });
            }
            catch (System.IO.IOException ex)
            {
                RecordFailure("I/O error");
                _logger.LogWarning(ex, "I/O error fixing {Path}", issue.Path);
                Api.Diagnostics.Record("FixTask.IOError", issue.Path + ": " + ex.Message);
                _db.AddHistory(new HistoryEntry
                {
                    IssueId = issue.Id,
                    Type = issue.Type,
                    Path = issue.Path,
                    Action = "Fix failed — " + ex.Message,
                    BytesFreed = 0,
                    FixedAtUtc = DateTime.UtcNow,
                    WasDryRun = false
                });
            }
            catch (Exception ex)
            {
                RecordFailure("Unexpected error");
                _logger.LogError(ex, "Unexpected error fixing {Path}", issue.Path);
                Api.Diagnostics.Record("FixTask", $"{issue.Path}: {ex.Message}");
            }

            progress.Report((i + 1) * 100.0 / queue.Count);
        }

        _recycleBin.Purge(config.RecycleBinRetentionDays);
        Plugin.CurrentActivity = null;

        // Post the run summary so the dashboard can pop a single alert on completion instead of leaving the
        // user to click into Errors themselves and count messages.
        var topReason = reasonCounts
            .OrderByDescending(kv => kv.Value)
            .Select(kv => (KeyValuePair<string, int>?)kv)
            .FirstOrDefault();
        Plugin.LastFixRun = new Api.FixRunSummary
        {
            FinishedAtUtc = DateTime.UtcNow,
            Attempted = attempted,
            Succeeded = succeeded,
            Failed = failed,
            TopFailureReason = topReason?.Key,
            TopFailureCount = topReason?.Value ?? 0
        };

        progress.Report(100);
    }

    // Fold a specific error message into a short reason-family so 142 permission-denied failures collapse to
    // a single bucket in the summary. Anything unrecognised is capped at 60 chars so long ffmpeg errors
    // don't turn the alert into a wall of text.
    private static string BucketReason(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return "Unknown error";
        }

        if (message.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("lacks write access", StringComparison.OrdinalIgnoreCase)
            || message.Contains("can't write to", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cannot write to", StringComparison.OrdinalIgnoreCase))
        {
            return "Permission denied";
        }

        if (message.Contains("not enough free space", StringComparison.OrdinalIgnoreCase)
            || message.Contains("filled up mid-move", StringComparison.OrdinalIgnoreCase))
        {
            return "Not enough free disk space";
        }

        if (message.Contains("no longer exists", StringComparison.OrdinalIgnoreCase))
        {
            return "File or folder went missing between scan and fix";
        }

        if (message.Contains("outside your library folders", StringComparison.OrdinalIgnoreCase)
            || message.Contains("isn't inside a Jellyfin library", StringComparison.OrdinalIgnoreCase))
        {
            return "Target sits outside your libraries";
        }

        if (message.Contains("would be larger than the original", StringComparison.OrdinalIgnoreCase))
        {
            return "Re-encoded output would be larger than the original";
        }

        if (message.Contains("verification", StringComparison.OrdinalIgnoreCase))
        {
            return "Re-encoded output failed verification";
        }

        return message.Length > 60 ? message[..60] + "…" : message;
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
