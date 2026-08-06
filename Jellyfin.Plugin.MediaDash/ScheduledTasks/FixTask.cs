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
        IssueType.CorruptArtwork,
        IssueType.Duplicate,
        IssueType.Quality,
        IssueType.SubtitleLanguage,
        IssueType.AudioLanguage,
        IssueType.Playability,
        IssueType.Misplaced,
        IssueType.MalwareRisk
    ];

    /// <summary>
    /// How often the fix task wakes up to check whether the server is idle. The task fires on this cadence
    /// and returns immediately when someone is watching or was active in the last 15 minutes (see <see cref="IdleCheck"/>);
    /// no queued issues stay queued and nothing else changes. When the server is genuinely idle, all queued fixes run.
    /// </summary>
    internal static readonly TimeSpan FixInterval = TimeSpan.FromMinutes(15);

    private readonly MediaDashDb _db;
    private readonly IEnumerable<IFixer> _fixers;
    private readonly RecycleBin _recycleBin;
    private readonly ISessionManager _sessionManager;
    private readonly Analytics.AnalyticsReporter _analytics;
    private readonly LibraryGuard _libraryGuard;
    private readonly ILogger<FixTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FixTask"/> class.
    /// </summary>
    /// <param name="db">The plugin database.</param>
    /// <param name="fixers">All registered fixers.</param>
    /// <param name="recycleBin">The recycle bin.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="analytics">The opt-in analytics reporter.</param>
    /// <param name="libraryGuard">Library guard, used to enumerate library roots when sweeping orphan sidecars.</param>
    /// <param name="logger">The logger.</param>
    public FixTask(MediaDashDb db, IEnumerable<IFixer> fixers, RecycleBin recycleBin, ISessionManager sessionManager, Analytics.AnalyticsReporter analytics, LibraryGuard libraryGuard, ILogger<FixTask> logger)
    {
        _db = db;
        _fixers = fixers;
        _recycleBin = recycleBin;
        _sessionManager = sessionManager;
        _analytics = analytics;
        _libraryGuard = libraryGuard;
        _logger = logger;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the next run skips the initial server-idle check.
    /// Set by the dashboard's "Run fixes now" button — the person clicking it is themselves an active session,
    /// so the check would otherwise refuse to start. Only affects the first check; the per-file activity check
    /// still runs so a manual run yields to a viewer that appeared after the run began.
    /// </summary>
    internal static bool BypassIdleCheckOnce { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the currently-running manual fix run should ignore per-file
    /// activity checks. Flipped by the "Ignore activity for this run" button in the dashboard when the user
    /// wants the queue to drain regardless of viewers. Reset at the start of every run.
    /// </summary>
    internal static bool IgnoreActivityForCurrentRun { get; set; }

    /// <summary>
    /// Gets or sets a human-readable reason the current fix run is paused, or null when the run is not paused.
    /// Only set on manual runs — scheduled runs still break out of the loop and requeue.
    /// </summary>
    internal static string? PauseReason { get; set; }

    /// <inheritdoc />
    public string Name => I18n.I18nCatalog.GetHtml(System.Globalization.CultureInfo.CurrentUICulture.Name, "task.fix.name", "Apply approved fixes");

    /// <inheritdoc />
    public string Key => "MediaDashFix";

    /// <inheritdoc />
    public string Description => I18n.I18nCatalog.GetHtml(System.Globalization.CultureInfo.CurrentUICulture.Name, "task.fix.description", "Applies approved and automatic fixes: removes duplicates, re-encodes oversized files, strips unwanted tracks.");

    /// <inheritdoc />
    public string Category => "MediaDash";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var isManualRun = BypassIdleCheckOnce;
        BypassIdleCheckOnce = false;
        IgnoreActivityForCurrentRun = false;
        PauseReason = null;

        if (config.PauseDuringPlayback && !isManualRun && IdleCheck.IsServerBusy(_sessionManager))
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

            if (config.PauseDuringPlayback && !IgnoreActivityForCurrentRun && IdleCheck.IsServerBusy(_sessionManager))
            {
                if (isManualRun)
                {
                    // Manual run: the user is standing at the dashboard waiting for this to finish, so pause
                    // rather than break. Re-poll every 30 s. The user can bail out via Stop, unstick the loop
                    // by flipping IgnoreActivityForCurrentRun, or wait for the viewer to finish.
                    PauseReason = "Paused: someone is watching. Click 'Ignore activity' to resume anyway.";
                    _logger.LogInformation("Fix run paused for viewer activity; waiting for idle or ignore-flag.");
                    while (config.PauseDuringPlayback
                        && !IgnoreActivityForCurrentRun
                        && IdleCheck.IsServerBusy(_sessionManager))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    }

                    PauseReason = null;
                    _logger.LogInformation("Fix run resuming.");
                }
                else
                {
                    _logger.LogInformation("Pausing fix run: someone started using the server. Remaining issues stay queued.");
                    break;
                }
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
                    WasDryRun = result.WasDryRun,
                    Success = result.Success
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

                    // Stale failure: the file was renamed/rebuilt/removed by an external tool (Sonarr, Radarr,
                    // manual edit) between the scan and this fix run. Retrying every 15 minutes won't help —
                    // move the issue out of Queued so the loop stops. Next scan re-detects if still applicable.
                    if (IsStaleFailure(result.Message))
                    {
                        _db.UpdateIssueStatus(issue.Id, IssueStatus.Fixed);
                    }
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
                    WasDryRun = false,
                    Success = false
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
                    WasDryRun = false,
                    Success = false
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

        // Orphan-sidecar sweep: at end of a fix run no encode is active, so any *.mediadash.tmp* /
        // *.mediadash.new* file sitting in a library folder is a leftover from a hard-killed encode
        // (SIGKILL, container restart, Jellyfin crash) where the fixer's finally couldn't clean up.
        try
        {
            var orphans = _libraryGuard.SweepOrphanSidecars();
            if (orphans.Count > 0)
            {
                var freed = orphans.Sum(o => o.Bytes);
                _logger.LogInformation("Removed {Count} orphan MediaDash sidecar file(s), reclaimed {Bytes} bytes.", orphans.Count, freed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orphan-sidecar sweep failed; will retry next run.");
        }

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

        // Opt-in analytics: month-to-date totals get pushed after every run so a mid-month opt-in
        // (or a run that only fixed a couple of files) still contributes accurate numbers. Reporter
        // is fire-and-forget with graceful failure — never blocks progress reporting.
        await _analytics.ReportMonthToDateAsync(cancellationToken).ConfigureAwait(false);

        progress.Report(100);
    }

    // Failures that mean "the underlying state moved between scan and fix". Retrying is guaranteed to
    // hit the same wall until a fresh scan re-detects (or doesn't). Exposed internal for direct testing.
    internal static bool IsStaleFailure(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        return message.Contains("no longer exists", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Nothing to remove any more", StringComparison.OrdinalIgnoreCase);
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
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = FixInterval.Ticks
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
