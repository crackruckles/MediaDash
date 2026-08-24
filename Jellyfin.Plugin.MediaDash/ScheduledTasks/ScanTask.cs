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
    public string Name => I18n.I18nCatalog.GetHtml(System.Globalization.CultureInfo.CurrentUICulture.Name, "task.scan.name", "Scan libraries for issues");

    /// <inheritdoc />
    public string Key => "MediaDashScan";

    /// <inheritdoc />
    public string Description => I18n.I18nCatalog.GetHtml(System.Globalization.CultureInfo.CurrentUICulture.Name, "task.scan.description", "Looks for duplicates, unplayable files, oversized encodes, unwanted language tracks, misplaced files and videos missing subtitles.");

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

        // ponytail: widened for v0.9 to include non-video kinds (music, audiobook, book, comic)
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes =
            [
                BaseItemKind.Movie,
                BaseItemKind.Episode,
                BaseItemKind.Audio,
                BaseItemKind.AudioBook,
                BaseItemKind.Book,
                BaseItemKind.MusicVideo
            ],
            IsVirtualItem = false,
            Recursive = true
        });

        // Jellyfin indexes sidecar theme.mp3 / themevideo.* as ordinary Audio/Video items alongside
        // the parent series/movie. They should not be checked as library content — no subs, no
        // duplicates, no "unplayable" — so drop them before the scanners see them.
        items = items.Where(i => !i.IsThemeMedia).ToList();

        var scanIsScoped = false;
        var enabledLibraries = Plugin.Instance!.Configuration.EnabledLibraries;
        if (enabledLibraries.Length > 0)
        {
            scanIsScoped = true;
            var idLookup = Scanners.VirtualFolderIdentity.BuildIdLookup(_libraryManager);
            var enabledLocations = _libraryManager.GetVirtualFolders()
                .Where(f => enabledLibraries.Contains(Scanners.VirtualFolderIdentity.GetId(f, idLookup), StringComparer.OrdinalIgnoreCase))
                .SelectMany(f => f.Locations)
                .Select(l => System.IO.Path.TrimEndingDirectorySeparator(l) + System.IO.Path.DirectorySeparatorChar)
                .ToList();
            items = items.Where(i => !string.IsNullOrEmpty(i.Path)
                && enabledLocations.Any(l => i.Path.StartsWith(l, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        // NFS/SMB safety net: a hard-mounted network share whose server is offline will hang every
        // syscall — File.Exists, FileInfo, ffprobe, all of it — indefinitely. Without this pre-flight,
        // the scanners would fan out thousands of stats against the dead mount, exhaust the ThreadPool,
        // and take the whole Jellyfin instance down (users report LXC restarts, GitHub issue class:
        // "scan locks up Jellyfin"). Probe each library root once with a short timeout; drop items on
        // any unreachable root so the scan finishes cleanly.
        // ponytail: hard-coded 5s timeout, add a config knob if users report false positives on
        // slow-but-reachable storage.
        var unreachableRoots = await FindUnreachableRootsAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        if (unreachableRoots.Count > 0)
        {
            var before = items.Count;
            items = items.Where(i => !string.IsNullOrEmpty(i.Path)
                && !unreachableRoots.Any(r => i.Path.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            foreach (var root in unreachableRoots)
            {
                var msg = "Library folder '" + root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + "' did not respond within 5s. Items under it were skipped this scan. If it's an NFS/SMB share, remount with soft,timeo=30,retrans=3 so a stalled server doesn't block Jellyfin.";
                _logger.LogWarning("ScanTask skipping unreachable library root: {Root}", root);
                Api.Diagnostics.Record("ScanTask.UnreachableRoot", msg);
            }

            _logger.LogInformation("Skipped {Skipped} item(s) under {Count} unreachable root(s).", before - items.Count, unreachableRoots.Count);
        }

        var scannedPaths = scanIsScoped
            ? items.SelectMany(MediaFileHelper.GetFilePaths).ToList()
            : null;
        // Reset the doomed-file set at scan start so the previous run's flags don't leak in.
        Plugin.ClearDoomed();

        // Honour FixMode.Off: the enum's own docstring is "The scanner does not run for this fix type."
        // Without this filter every registered scanner ran on every scheduled scan regardless of the
        // user's settings — field reports of PlayabilityScanner / AudioLanguageScanner chewing CPU
        // while the user had only EmbeddedCoverArt enabled trace to here.
        var config = Plugin.Instance!.Configuration;
        var scanners = _scanners.Where(s => config.GetFixMode(s.Type) != Configuration.FixMode.Off).ToList();
        _logger.LogInformation("MediaDash scan starting: {ItemCount} items, {ScannerCount} scanners ({SkippedCount} skipped as Off)", items.Count, scanners.Count, _scanners.Count() - scanners.Count);
        try
        {
            for (var i = 0; i < scanners.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scanner = scanners[i];
                var baseProgress = i * 100.0 / scanners.Count;
                var slice = 100.0 / scanners.Count;
                var scannerProgress = new Progress<double>(p => progress.Report(baseProgress + (p * slice / 100.0)));

                // Coarse label for scanners that don't set per-file activity (Duplicate, MediaGrouper,
                // Nfo, Artwork, StaleContent, …). ProbingScannerBase-derived scanners overwrite this
                // per file with the same class name, so no flicker.
                Plugin.CurrentActivityLabel = scanner.GetType().Name;
                Plugin.CurrentActivity = null;

                var issues = await scanner.ScanAsync(items, scannerProgress, cancellationToken).ConfigureAwait(false);

                // Feed the doomed-file set from scanners whose fix deletes the file, so later
                // probing scanners can skip it. Duplicate (loser copies) is by far the biggest
                // saver — a library with many dupes was previously ffprobing + thorough-decoding
                // both copies before the fixer deleted one.
                if (scanner.Type is Data.IssueType.Duplicate or Data.IssueType.MalwareRisk or Data.IssueType.OrphanedDebris)
                {
                    foreach (var issue in issues)
                    {
                        Plugin.MarkDoomed(issue.Path);
                    }
                }

                // Scanners that emit non-video-file paths (orphan folders, trickplay dirs, subtitle sidecars)
                // opt out of the scoped-delete branch — otherwise stale rows sit in the DB forever when the
                // user has EnabledLibraries set, because scannedPaths only contains video file paths.
                var pathsForReplace = scanner.AlwaysUnscoped ? null : scannedPaths;
                _db.ReplaceDetectedIssues(scanner.Type, issues, pathsForReplace);
                _logger.LogInformation("MediaDash scanner {Type} found {Count} issues", scanner.Type, issues.Count);
            }
        }
        finally
        {
            Plugin.CurrentActivity = null;
            Plugin.CurrentActivityLabel = null;
        }

        // Refresh the redownload-warning list. Compares each recent successful re-encode against the
        // file currently at the same path — if the file is close to the size of the original still in
        // the recycle bin, something replaced our shrunk copy (Sonarr/Radarr redownload, or a manual
        // restore). Cheap: at most a couple stats per recent history row.
        try
        {
            Plugin.RedownloadWarnings = Api.RedownloadDetector.Detect(_db, TimeSpan.FromDays(30));
            if (Plugin.RedownloadWarnings.Count > 0)
            {
                _logger.LogInformation("MediaDash detected {Count} file(s) that appear to have been replaced after a successful re-encode.", Plugin.RedownloadWarnings.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redownload detection failed.");
            Api.Diagnostics.Record("ScanTask.RedownloadDetect", "Redownload detection failed: " + ex.Message + ". Recycle-bin redownload warnings will be stale until the next successful scan.");
        }

        progress.Report(100);
    }

    // Enumerates every library root and races a Directory.Exists() call against a timeout.
    // Returns any root whose syscall didn't return in time (i.e. mount is hung). The probe thread
    // is orphaned if the syscall never returns — bounded to one leaked worker per unreachable root
    // per scan, which is finite in practice (users don't have many broken mounts at once).
    private async Task<List<string>> FindUnreachableRootsAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var roots = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => System.IO.Path.TrimEndingDirectorySeparator(l) + System.IO.Path.DirectorySeparatorChar)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unreachable = new List<string>();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await ProbeReachableAsync(root, timeout, cancellationToken).ConfigureAwait(false))
            {
                unreachable.Add(root);
            }
        }

        return unreachable;
    }

    // True if Directory.Exists returned within timeout (regardless of its bool result — a legitimately
    // missing root is a different problem the scanners already handle). False only means "syscall
    // hung past deadline", which is the NFS-lockup signature.
    internal static async Task<bool> ProbeReachableAsync(string root, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var probe = Task.Run(() =>
        {
            try
            {
                System.IO.Directory.Exists(root);
                return true;
            }
            catch
            {
                // Any thrown exception (unauthorized, invalid path, etc.) still means the syscall
                // returned in time — pass; the scanners will surface the real problem downstream.
                return true;
            }
        });

        var winner = await Task.WhenAny(probe, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        return winner == probe;
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
