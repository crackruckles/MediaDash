using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>
/// Scheduled task that runs all enabled scanners across the media libraries.
/// </summary>
public sealed class ScanTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IEnumerable<IScanner> _scanners;
    private readonly MediaDashDb _db;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<ScanTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScanTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="scanners">All registered scanners.</param>
    /// <param name="db">The plugin database.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{ScanTask}"/> interface.</param>
    public ScanTask(ILibraryManager libraryManager, IEnumerable<IScanner> scanners, MediaDashDb db, ISessionManager sessionManager, ILogger<ScanTask> logger)
    {
        _libraryManager = libraryManager;
        _scanners = scanners;
        _db = db;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the next run skips the server-idle check.
    /// Set by the dashboard's "Scan now" button — the person clicking it is themselves an active session.
    /// </summary>
    internal static bool BypassIdleCheckOnce { get; set; }

    /// <inheritdoc />
    public string Name => "Scan libraries for issues";

    /// <inheritdoc />
    public string Key => "MediaDashScan";

    /// <inheritdoc />
    public string Description => "Looks for duplicates, unplayable files, oversized encodes and unwanted language tracks.";

    /// <inheritdoc />
    public string Category => "MediaDash";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var bypassIdleCheck = BypassIdleCheckOnce;
        BypassIdleCheckOnce = false;
        if (Plugin.Instance!.Configuration.PauseDuringPlayback && !bypassIdleCheck && IdleCheck.IsServerBusy(_sessionManager))
        {
            _logger.LogInformation("Skipping scheduled scan: someone is watching or was recently active.");
            progress.Report(100);
            return;
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            IsVirtualItem = false,
            Recursive = true
        });

        var enabledLibraries = Plugin.Instance!.Configuration.EnabledLibraries;
        if (enabledLibraries.Length > 0)
        {
            var enabledLocations = _libraryManager.GetVirtualFolders()
                .Where(f => enabledLibraries.Contains(f.ItemId, StringComparer.OrdinalIgnoreCase))
                .SelectMany(f => f.Locations)
                .Select(l => System.IO.Path.TrimEndingDirectorySeparator(l) + System.IO.Path.DirectorySeparatorChar)
                .ToList();
            items = items.Where(i => !string.IsNullOrEmpty(i.Path)
                && enabledLocations.Any(l => i.Path.StartsWith(l, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        _logger.LogInformation("MediaDash scan starting: {ItemCount} items, {ScannerCount} scanners", items.Count, _scanners.Count());

        var scanners = _scanners.ToList();
        for (var i = 0; i < scanners.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scanner = scanners[i];
            var baseProgress = i * 100.0 / scanners.Count;
            var slice = 100.0 / scanners.Count;
            var scannerProgress = new Progress<double>(p => progress.Report(baseProgress + (p * slice / 100.0)));

            var issues = await scanner.ScanAsync(items, scannerProgress, cancellationToken).ConfigureAwait(false);
            _db.ReplaceDetectedIssues(scanner.Type, issues);
            _logger.LogInformation("MediaDash scanner {Type} found {Count} issues", scanner.Type, issues.Count);
        }

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
                TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
            }
        ];
    }
}
