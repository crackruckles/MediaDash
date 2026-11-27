using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Configuration;
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
    /// <summary>
    /// Grace window a user-triggered scan grants the reviewer before opportunistic fixes are
    /// allowed to run. Long enough to actually read the Issues tab and decide, short enough that
    /// forgotten-tab scenarios still get their auto-fix on the next scheduled fix window.
    /// </summary>
    internal static readonly TimeSpan ManualScanReviewGrace = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<string, Lazy<Task<bool>>> RootProbes = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly ILibraryManager _libraryManager;
    private readonly IEnumerable<IScanner> _scanners;
    private readonly MediaDashDb _db;
    private readonly ISessionManager _sessionManager;
    private readonly ITaskManager _taskManager;
    private readonly RunState _runState;
    private readonly IPluginConfigurationProvider _configProvider;
    private readonly Fixers.ProtectionService _protection;
    private readonly ILogger<ScanTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScanTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="scanners">All registered scanners.</param>
    /// <param name="db">The plugin database.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="taskManager">Jellyfin's scheduled-task manager, used to detect its own library scan.</param>
    /// <param name="runState">The shared run-state holder (02-A).</param>
    /// <param name="configProvider">Configuration snapshot provider (02-A).</param>
    /// <param name="protection">Never-modify protection gate. Roadmap i-110-protected-media.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{ScanTask}"/> interface.</param>
    public ScanTask(
        ILibraryManager libraryManager,
        IEnumerable<IScanner> scanners,
        MediaDashDb db,
        ISessionManager sessionManager,
        ITaskManager taskManager,
        RunState runState,
        IPluginConfigurationProvider configProvider,
        Fixers.ProtectionService protection,
        ILogger<ScanTask> logger)
    {
        _libraryManager = libraryManager;
        _scanners = scanners;
        _db = db;
        _sessionManager = sessionManager;
        _taskManager = taskManager;
        _runState = runState;
        _configProvider = configProvider;
        _protection = protection;
        _logger = logger;
        // 02-A: bridge the DI singleton to Plugin.AttachedRunState so the legacy Plugin.CurrentActivity /
        // ScanScannersDone/Total facades (still used by ProbingScannerBase and the older tests) reach
        // this same instance. Idempotent — FixTask's ctor also does this on first resolution.
        Plugin.AttachedRunState = runState;
    }

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
        var typeFilter = _runState.ConsumeNextScanTypeFilter();
        var bypassIdleCheck = _runState.ConsumeBypassIdleCheck(RunRole.Scan);
        // 02-A: capture a coherent snapshot once at run start so a config save mid-scan can't
        // flip the idle policy or Off-type filter halfway through. Later per-item checks that
        // legitimately want to observe drift (ScanTask has none today; FixTask has them at
        // line ~575) re-call the provider.
        var runConfig = _configProvider.GetSnapshot();
        if (runConfig.PauseDuringPlayback && !bypassIdleCheck && IdleCheck.IsServerBusy(_sessionManager))
        {
            _logger.LogInformation("Skipping scheduled scan: someone is watching or was recently active.");
            progress.Report(100);
            return;
        }

        // #72: don't double up on disk I/O with Jellyfin's own library scan. The two walk the same
        // files (Jellyfin for thumbnails/trickplay/metadata, MediaDash for probe+hash), and on
        // spinning disks or SMB shares the overlap makes the whole host unresponsive. Manual-run
        // bypass respects an explicit "Scan now" click — user intent is a stronger signal.
        if (!bypassIdleCheck && IdleCheck.IsLibraryScanRunning(_taskManager))
        {
            _logger.LogInformation("Skipping scheduled scan: Jellyfin's own library scan is running.");
            progress.Report(100);
            return;
        }

        if (!_runState.TryBegin(RunRole.Scan))
        {
            _logger.LogInformation("Skipping scheduled scan: another scan is already running in this process.");
            progress.Report(100);
            return;
        }

        using var scanRecording = RunDiagnosticsRecording.Start(_db, _logger, runConfig.DiagnosticsEnabled);
        using var diagnostics = RunDiagnostics.Start(_logger, "scan", "ScanTask");
        // See FixTask: Jellyfin's library scan waits for MediaDash rather than the other way round.
        using var libraryScanHold = JellyfinLibraryScanHold.Acquire(_taskManager, _logger, cancelRunning: bypassIdleCheck);
        using var libraryScanCancellation = new LibraryScanCancellation(null, cancellationToken);
        var outcome = "Failed";
        string? failure = null;
        try
        {
            libraryScanCancellation.Token.ThrowIfCancellationRequested();
            await ExecuteScanBodyAsync(bypassIdleCheck, runConfig, typeFilter, diagnostics?.Observe(progress) ?? progress, libraryScanCancellation.Token).ConfigureAwait(false);
            outcome = "Completed";
        }
        catch (OperationCanceledException) when (libraryScanCancellation.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            outcome = "Cancelled";
            _logger.LogInformation("Stopped automatic scan: Jellyfin's library scan started. Incomplete scanner results were not saved.");
            progress.Report(100);
        }
        catch (OperationCanceledException)
        {
            outcome = "Cancelled";
            throw;
        }
        catch (Exception error)
        {
            failure = error.Message;
            throw;
        }
        finally
        {
            try
            {
                diagnostics?.Finish(outcome, null, null, null, null);
                scanRecording?.Complete(outcome, failure);
            }
            finally
            {
                _runState.Complete(RunRole.Scan);
            }
        }
    }

    private async Task ExecuteScanBodyAsync(bool bypassIdleCheck, PluginConfiguration runConfig, IReadOnlySet<IssueType>? typeFilter, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var materialization = Stopwatch.StartNew();
        // ponytail: widened for v0.9 to include non-video kinds (music, audiobook, book, comic).
        // E2E audit E2E-04 / E2E-09: Photo added so BMP/TIFF/JPEG/PNG items already indexed by
        // Jellyfin surface to FormatCompatibilityScanner (healthy browser-incompatible images)
        // AND to PlayabilityScanner (corrupt JPEG/PNG). The unindexed-sweep in
        // FormatCompatibilityScanner can't compensate for already-indexed Photo items because
        // ServerIndexes=true in the matrix row.
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes =
            [
                BaseItemKind.Movie,
                BaseItemKind.Episode,
                BaseItemKind.Audio,
                BaseItemKind.AudioBook,
                BaseItemKind.Book,
                BaseItemKind.MusicVideo,
                BaseItemKind.Photo
            ],
            IsVirtualItem = false,
            Recursive = true
        });

        _logger.LogInformation("MediaDash scan phase {Phase}: {ElapsedMilliseconds} ms; {ItemCount} items.", "ItemMaterialization", materialization.Elapsed.TotalMilliseconds, items.Count);
        materialization.Restart();

        // Jellyfin indexes sidecar theme.mp3 / themevideo.* as ordinary Audio/Video items alongside
        // the parent series/movie. They should not be checked as library content — no subs, no
        // duplicates, no "unplayable" — so drop them before the scanners see them.
        items = items.Where(i => !i.IsThemeMedia).ToList();

        // One-time content-aware defaults sweep: for each scanner whose content type isn't present
        // in the user's libraries AND whose FixMode is still the untouched default (ManualApprove),
        // silence the scanner (Off). Stops empty scanner cycles and de-clutters the Settings UI.
        // Gated by a plugin_state key so it only ever runs once per install/upgrade — a user who
        // adds new content types later can re-enable the scanners in Settings.
        if (items.Count > 0 && _db.GetState("ContentAwareDefaultsChecked") is null)
        {
            var hasVideo = items.Any(i => i.GetBaseItemKind() is BaseItemKind.Movie or BaseItemKind.Episode or BaseItemKind.MusicVideo);
            var hasAudio = items.Any(i => i.GetBaseItemKind() is BaseItemKind.Audio or BaseItemKind.AudioBook);
            var scanConfig = Plugin.Instance!.Configuration;
            var silenced = new List<string>();

            // Audio-only defaults.
            if (!hasAudio && scanConfig.EmbeddedCoverFixMode == Configuration.FixMode.ManualApprove)
            {
                scanConfig.EmbeddedCoverFixMode = Configuration.FixMode.Off;
                silenced.Add("EmbeddedCoverArt");
            }

            // Video-only defaults — every scanner in this block operates on video streams,
            // subtitle sidecars for video, video-transcode logs, or trickplay sprites Jellyfin
            // only generates for video items. Books and audio have their own pipelines.
            if (!hasVideo)
            {
                void Silence(Func<Configuration.FixMode> get, Action<Configuration.FixMode> set, string label)
                {
                    if (get() == Configuration.FixMode.ManualApprove)
                    {
                        set(Configuration.FixMode.Off);
                        silenced.Add(label);
                    }
                }

                Silence(() => scanConfig.TranscodeFixMode, v => scanConfig.TranscodeFixMode = v, "Quality");
                Silence(() => scanConfig.SubtitleFixMode, v => scanConfig.SubtitleFixMode = v, "SubtitleLanguage");
                Silence(() => scanConfig.AudioFixMode, v => scanConfig.AudioFixMode = v, "AudioLanguage");
                Silence(() => scanConfig.MissingSubtitlesFixMode, v => scanConfig.MissingSubtitlesFixMode = v, "MissingSubtitles");
                Silence(() => scanConfig.SubtitleFontFixMode, v => scanConfig.SubtitleFontFixMode = v, "SubtitleFonts");
                Silence(() => scanConfig.TrickplayFixMode, v => scanConfig.TrickplayFixMode = v, "LargeTrickplay");
                Silence(() => scanConfig.HeavyTranscodeFixMode, v => scanConfig.HeavyTranscodeFixMode = v, "HeavyTranscode");
                Silence(() => scanConfig.FailedTranscodeFixMode, v => scanConfig.FailedTranscodeFixMode = v, "FailedTranscode");
                Silence(() => scanConfig.UngroupedFixMode, v => scanConfig.UngroupedFixMode = v, "Ungrouped");
            }

            if (silenced.Count > 0)
            {
                Plugin.Instance!.SaveConfiguration();
                _logger.LogInformation(
                    "Content-aware defaults sweep: silenced {Count} scanner(s) whose content type isn't present in this library (hasVideo={HasVideo}, hasAudio={HasAudio}): {Silenced}. Re-enable in Settings if you add the relevant content later.",
                    silenced.Count,
                    hasVideo,
                    hasAudio,
                    string.Join(", ", silenced));
            }

            _db.SetState("ContentAwareDefaultsChecked", DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }

        // Overlapping-library warning: users with a "Media" library covering /media plus separate
        // "Movies" (/media/movies) and "TV" (/media/tv) libraries produce duplicate metadata,
        // double-counted stats, and phantom Ungrouped / Misplaced findings that Jellyfin's own scans
        // trigger against overlapping trees. Emit a single dedup'd diagnostic per pair so the user
        // sees the collision. Cheap: N is small (~a dozen libraries at the extreme).
        var virtualFolders = _libraryManager.GetVirtualFolders();
        for (var i = 0; i < virtualFolders.Count; i++)
        {
            var outer = virtualFolders[i];
            foreach (var outerPath in outer.Locations ?? Array.Empty<string>())
            {
                var outerNormalised = System.IO.Path.TrimEndingDirectorySeparator(outerPath) + System.IO.Path.DirectorySeparatorChar;
                for (var j = 0; j < virtualFolders.Count; j++)
                {
                    if (j == i)
                    {
                        continue;
                    }

                    var inner = virtualFolders[j];
                    foreach (var innerPath in inner.Locations ?? Array.Empty<string>())
                    {
                        var innerNormalised = System.IO.Path.TrimEndingDirectorySeparator(innerPath) + System.IO.Path.DirectorySeparatorChar;
                        if (innerNormalised.StartsWith(outerNormalised, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                            && !string.Equals(innerNormalised, outerNormalised, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        {
                            Api.Diagnostics.Record(
                                "LibraryOverlap.Detected",
                                string.Format(
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    "Library '{0}' path '{1}' is inside library '{2}' path '{3}'. Overlapping libraries produce duplicate metadata, double-counted stats, and phantom Ungrouped / Misplaced findings. Split the parent library, narrow it, or remove one of the two in Dashboard → Libraries.",
                                    inner.Name,
                                    innerPath,
                                    outer.Name,
                                    outerPath));
                        }
                    }
                }
            }
        }

        var enabledLibraries = runConfig.EnabledLibraries;
        var enabledFolders = VirtualFolderIdentity.GetEnabledFolders(_libraryManager, enabledLibraries);
        var scanIsScoped = enabledLibraries.Length > 0 && enabledFolders.Count != _libraryManager.GetVirtualFolders().Count;
        var enabledLocations = enabledFolders
            .SelectMany(folder => folder.Locations).Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)) + System.IO.Path.DirectorySeparatorChar)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();
        if (enabledLibraries.Length > 0)
        {
            items = items.Where(i => !string.IsNullOrEmpty(i.Path)
                && enabledLocations.Any(root => Fixers.LibraryGuard.IsUnder(i.Path, root)))
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
        var unreachableRoots = await FindUnavailableRootsAsync(enabledLocations, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        if (unreachableRoots.Count > 0)
        {
            scanIsScoped = true;
            var before = items.Count;
            items = items.Where(i => !string.IsNullOrEmpty(i.Path)
                && !unreachableRoots.Any(root => Fixers.LibraryGuard.IsUnder(i.Path, root)))
                .ToList();
            foreach (var root in unreachableRoots)
            {
                var msg = "The library folder '" + root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + "' is missing or did not respond within 5 seconds, so it was skipped and its previous results are kept. Checks that look at whole libraries also wait until every enabled folder is available. Reconnect the drive or share, then run a scan.";
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
        // MD-008: a scanner runs when ANY of its produced types is enabled, not just its primary
        // one. Otherwise a multi-type scanner (TranscodeLogScanner produces both HeavyTranscode
        // and FailedTranscode) is skipped whenever the primary type is Off — silently disabling
        // the secondary feature.
        var scanners = _scanners.Where(s => AnyProducedTypeEnabled(s, runConfig)).ToList();

        // Targeted-rescan filter (issue #64). ConfigChangeRescanner arms this via RunState so a
        // single-setting edit only re-runs the scanner(s) that read that setting, not the whole
        // library sweep. Consumed once — a scheduled sweep after the config edit runs everything.
        if (typeFilter is not null && typeFilter.Count > 0)
        {
            scanners = scanners
                .Where(s => typeFilter.Contains(s.Type) || s.AdditionalTypes.Any(t => typeFilter.Contains(t)))
                .ToList();
            _logger.LogInformation("MediaDash scan narrowed to {Count} scanner(s) via NextRunTypeFilter: {Types}", scanners.Count, string.Join(", ", typeFilter));
        }

        _logger.LogInformation("MediaDash scan phase {Phase}: {ElapsedMilliseconds} ms; {ItemCount} items.", "Preparation", materialization.Elapsed.TotalMilliseconds, items.Count);
        _logger.LogInformation("MediaDash scan starting: {ItemCount} items, {ScannerCount} scanners ({SkippedCount} skipped as Off)", items.Count, scanners.Count, _scanners.Count() - scanners.Count);
        // Publish the run counts so the dashboard can render "N / M scanners done" alongside the
        // progress bar (same shape as fix runs' "N / M items left"). Cleared to null in the finally
        // below so the caption doesn't linger after the run finishes.
        Plugin.ScanScannersTotal = scanners.Count;
        Plugin.ScanScannersDone = 0;
        try
        {
            for (var i = 0; i < scanners.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scanner = scanners[i];
                if (unreachableRoots.Count > 0 && scanner.AlwaysUnscoped)
                {
                    Api.Diagnostics.Record("ScanTask." + scanner.GetType().Name + ".Deferred", "The " + scanner.Type + " check was skipped because one of your enabled library folders is unavailable. Its previous results are kept. Reconnect the drive or share, then run a scan.");
                    Plugin.ScanScannersDone = i + 1;
                    continue;
                }

                var baseProgress = i * 100.0 / scanners.Count;
                var slice = 100.0 / scanners.Count;
                var scannerProgress = new Progress<double>(p => progress.Report(baseProgress + (p * slice / 100.0)));

                // Coarse label for scanners that don't set per-file activity (Duplicate, MediaGrouper,
                // Nfo, Artwork, StaleContent, …). ProbingScannerBase-derived scanners overwrite this
                // per file with the same class name, so no flicker.
                Plugin.CurrentActivityLabel = scanner.GetType().Name;
                Plugin.CurrentActivity = null;

                // MD-006: per-scanner exception boundary. ProbingScannerBase already isolates
                // per-file probe failures, but a scanner-level exception (host-API drift, network
                // parser blowing up, LINQ NRE in an aggregation step) used to escape here and
                // abort the whole scan — every later scanner skipped, their prior Detected rows
                // left stale for another cycle. Now: rethrow cancellation, diagnose everything
                // else with the scanner's type + message, keep the earlier scanners' results
                // (we don't touch the DB when we didn't get issues), and move on.
                IReadOnlyList<Data.Issue>? issues = null;
                var scannerTimer = Stopwatch.StartNew();
                var scannerOutcome = "Completed";
                int? candidateCount = null;
                using var diagnostics = RunDiagnostics.Start(_logger, "scan", scanner.GetType().Name);
                try
                {
                    var reserved = _db.Moves.CaptureReservations();
                    var candidates = _protection.FilterScannable(items, scanner.Type)
                        .Where(item => !reserved.Item(item.Id) && !MediaFileHelper.GetMovePayloadPaths(item).Any(reserved.Path)).ToList();
                    candidateCount = candidates.Count;
                    issues = await scanner.ScanAsync(candidates, diagnostics?.Observe(scannerProgress) ?? scannerProgress, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    scannerOutcome = "Cancelled";
                    throw;
                }
                catch (Exception ex)
                {
                    scannerOutcome = "Failed";
                    _logger.LogWarning(ex, "MediaDash scanner {Type} failed; continuing with remaining scanners.", scanner.Type);
                    Api.Diagnostics.Record("ScanTask." + scanner.GetType().Name + ".Failed", "The " + scanner.Type + " check failed this scan, so its results were not updated (the previous results are still shown). The other checks ran normally, and it will try again on the next scan. Details: " + ex.GetType().Name + ": " + ex.Message);
                    Plugin.ScanScannersDone = i + 1;
                    continue;
                }
                finally
                {
                    diagnostics?.Finish(scannerOutcome, candidateCount, issues?.Count, null, null);
                    _logger.LogInformation(
                        "MediaDash scan phase {Phase}: {ElapsedMilliseconds} ms; {ItemCount} candidate items; {IssueCount} findings; {Outcome}.",
                        scanner.GetType().Name,
                        scannerTimer.Elapsed.TotalMilliseconds,
                        items.Count,
                        issues?.Count ?? 0,
                        scannerOutcome);
                }

                // Feed the doomed-file set from scanners whose fix deletes the file, so later
                // probing scanners can skip it. Duplicate (loser copies) is by far the biggest
                // saver — a library with many dupes was previously ffprobing + thorough-decoding
                // both copies before the fixer deleted one.
                if (scanner.Type is Data.IssueType.Duplicate or Data.IssueType.MalwareRisk or Data.IssueType.OrphanedDebris)
                {
                    var emittable = _protection.FilterEmittable(issues, scanner.Type);
                    var dismissed = _db.Issues.GetIssues(scanner.Type, IssueStatus.Dismissed).Select(issue => issue.Path);
                    foreach (var path in ComputeDoomedPaths(emittable, dismissed))
                    {
                        Plugin.MarkDoomed(path);
                    }
                }

                // Scanners that emit non-video-file paths (orphan folders, trickplay dirs, subtitle sidecars)
                // opt out of the scoped-delete branch — otherwise stale rows sit in the DB forever when the
                // user has EnabledLibraries set, because scannedPaths only contains video file paths.
                var pathsForReplace = scanner.AlwaysUnscoped && !scanIsScoped ? null : scannedPaths;
                // MD-008: group by Issue.Type and persist each group under its own type so
                // multi-type scanners (TranscodeLogScanner) don't misclassify secondary rows as
                // the primary type. Also replaces the primary type's rows even when the scanner
                // emits nothing of that type this run (empty list = "no findings, wipe stale").
                var groupedByType = issues.GroupBy(iss => iss.Type).ToDictionary(g => g.Key, g => (IReadOnlyList<Data.Issue>)g.ToList());
                var typesToReplace = new List<Data.IssueType> { scanner.Type };
                typesToReplace.AddRange(scanner.AdditionalTypes);

                var persistFailed = false;
                var persistence = Stopwatch.StartNew();
                foreach (var typeToReplace in typesToReplace)
                {
                    var groupIssues = groupedByType.TryGetValue(typeToReplace, out var g) ? g : (IReadOnlyList<Data.Issue>)System.Array.Empty<Data.Issue>();
                    // Roadmap i-110-protected-media: drop issues that are inside a protected
                    // folder/library with this scanner masked. The DELETE portion of
                    // ReplaceDetectedIssues still runs — Detected rows for protected paths get
                    // wiped, and no fresh rows replace them, so protection is idempotent even
                    // if it was added between the previous scan and this one.
                    groupIssues = _protection.FilterEmittable(groupIssues, typeToReplace);
                    var replacementPaths = pathsForReplace;
                    if (scanIsScoped)
                    {
                        // These findings can name containers or managed metadata outside the item
                        // paths. Clear only observations whose media scope was actually scanned.
                        replacementPaths = (pathsForReplace ?? []).Concat(_db.Issues.GetIssues(typeToReplace, IssueStatus.Detected)
                            .Where(issue => IsInScannedLibrary(issue, enabledLocations, unreachableRoots)).Select(issue => issue.Path))
                            .Concat(groupIssues.Select(issue => issue.Path)).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();
                    }

                    if (scanner is SuspiciousFileScanner suspicious)
                    {
                        replacementPaths = _db.Issues.GetIssues(typeToReplace, IssueStatus.Detected)
                            .Where(issue => suspicious.CompletedRoots.Any(root => Fixers.LibraryGuard.IsUnder(issue.Path, root)))
                            .Select(issue => issue.Path).Concat(groupIssues.Select(issue => issue.Path))
                            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();
                    }

                    try
                    {
                        _db.ReplaceDetectedIssues(typeToReplace, groupIssues, replacementPaths);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Persisting scanner {Type} results failed; retaining prior Detected rows.", typeToReplace);
                        Api.Diagnostics.Record("ScanTask." + scanner.GetType().Name + ".PersistFailed", "The " + typeToReplace + " check ran, but its results could not be saved, so the previous results are still shown. It will try again on the next scan. Details: " + ex.GetType().Name + ": " + ex.Message);
                        persistFailed = true;
                        break;
                    }
                }

                _logger.LogInformation(
                    "MediaDash scan phase {Phase}: {ElapsedMilliseconds} ms; {Scanner}; {IssueCount} findings; {Outcome}.",
                    "Persistence",
                    persistence.Elapsed.TotalMilliseconds,
                    scanner.GetType().Name,
                    issues.Count,
                    persistFailed ? "Failed" : "Completed");

                if (persistFailed)
                {
                    Plugin.ScanScannersDone = i + 1;
                    continue;
                }

                _logger.LogInformation("MediaDash scanner {Type} found {Count} issues", scanner.Type, issues.Count);
                Plugin.ScanScannersDone = i + 1;
            }
        }
        finally
        {
            Plugin.CurrentActivity = null;
            Plugin.CurrentActivityLabel = null;
            Plugin.ScanScannersDone = null;
            Plugin.ScanScannersTotal = null;
        }

        // Refresh the redownload-warning list. Compares each recent successful re-encode against the
        // file currently at the same path — if the file is close to the size of the original still in
        // the recycle bin, something replaced our shrunk copy (Sonarr/Radarr redownload, or a manual
        // restore). Cheap: at most a couple stats per recent history row.
        try
        {
            Plugin.RedownloadWarnings = Api.RedownloadDetector.Detect(_db.History, TimeSpan.FromDays(30));
            if (Plugin.RedownloadWarnings.Count > 0)
            {
                _logger.LogInformation("MediaDash detected {Count} file(s) that appear to have been replaced after a successful re-encode.", Plugin.RedownloadWarnings.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redownload detection failed.");
            Api.Diagnostics.Record("ScanTask.RedownloadDetect", "Checking whether recycled files were downloaded again failed, so the Recycle bin's re-download warnings may be out of date until the next scan. Details: " + ex.Message);
        }

        // Grant the fix-task grace period ONLY when this was a user click. Scheduled scans don't
        // set the timestamp — there's no user to shield from an opportunistic fix run.
        if (bypassIdleCheck)
        {
            _runState.RecordManualScanCompleted();
        }

        // UI-15: Overview's "Last scan" used to be derived from the newest issue's detection
        // timestamp. A clean library (no issues found) could never report a successful scan; an
        // older issue could make a recent scan read as the ancient one. Persist a dedicated
        // completion timestamp so the UI can show scan truth, independent of what the scan found.
        _db.SetState("LastScanCompletedUtc", DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        progress.Report(100);
    }

    internal static IEnumerable<string> ComputeDoomedPaths(IEnumerable<Issue> issues, IEnumerable<string> dismissedPaths)
    {
        var dismissed = dismissedPaths.ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        return issues.Select(issue => issue.Path).Where(path => !dismissed.Contains(path));
    }

    private bool IsInScannedLibrary(Issue issue, IReadOnlyList<string> roots, IReadOnlyList<string> unavailable)
    {
        if (Matches(issue.Path))
        {
            return true;
        }

        if (issue.ItemId == Guid.Empty)
        {
            return false;
        }

        try
        {
            return Matches(_libraryManager.GetItemById(issue.ItemId)?.Path);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning(error, "Preserving finding {IssueId}: its scanned media owner could not be verified", issue.Id);
            return false;
        }

        bool Matches(string? path) => !string.IsNullOrWhiteSpace(path) && System.IO.Path.IsPathFullyQualified(path)
            && roots.Any(root => Fixers.LibraryGuard.IsUnder(path, root)) && !unavailable.Any(root => Fixers.LibraryGuard.IsUnder(path, root));
    }

    // Probe enabled roots only. An unfinished native stat is shared across later scans, so a
    // permanently blocked mount cannot accumulate another blocked worker on every retry.
    private static async Task<List<string>> FindUnavailableRootsAsync(IReadOnlyList<string> roots, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var unreachable = new List<string>();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await ProbeAvailableAsync(root, timeout, cancellationToken).ConfigureAwait(false))
            {
                unreachable.Add(root);
            }
        }

        return unreachable;
    }

    internal static async Task<bool> ProbeAvailableAsync(string root, TimeSpan timeout, CancellationToken cancellationToken, Func<string, Task<bool>>? stat = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout <= TimeSpan.Zero)
        {
            return false;
        }

        try
        {
            var probe = SharedRootProbe(root, stat);
            return await probe.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static Task<bool> SharedRootProbe(string path, Func<string, Task<bool>>? stat)
    {
        var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        while (true)
        {
            Lazy<Task<bool>>? candidate = null;
            candidate = new Lazy<Task<bool>>(
                () =>
            {
                var work = stat?.Invoke(root) ?? Task.Run(() => System.IO.Directory.Exists(root), CancellationToken.None);
                _ = work.ContinueWith(
                    completed =>
                {
                    if (completed.IsFaulted)
                    {
                        _ = completed.Exception;
                    }

                    RemoveRootProbe(root, candidate!);
                },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return work;
            },
                LazyThreadSafetyMode.ExecutionAndPublication);
            var shared = RootProbes.GetOrAdd(root, candidate);
            if (!ReferenceEquals(shared, candidate) && shared.IsValueCreated && shared.Value.IsCompleted)
            {
                RemoveRootProbe(root, shared);
                continue;
            }

            try
            {
                return shared.Value;
            }
            catch
            {
                RemoveRootProbe(root, shared);
                throw;
            }
        }
    }

    private static void RemoveRootProbe(string root, Lazy<Task<bool>> probe) =>
        ((ICollection<KeyValuePair<string, Lazy<Task<bool>>>>)RootProbes).Remove(new(root, probe));

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
        // Midnight local time. The daily rescan only needs to catch a day's worth of change since
        // the last scan, so a single overnight sweep is enough; the idle-check gate keeps it from
        // fighting an actual viewer if one appears at 00:00. Users can still hit Scan now for a
        // manual pass, or edit the trigger from Dashboard → Scheduled Tasks.
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.Zero.Ticks
            }
        ];
    }

    // MD-008: returns true if the scanner's primary type OR any AdditionalTypes are enabled.
    // A multi-type scanner (TranscodeLogScanner produces both HeavyTranscode and FailedTranscode)
    // must run when either mode is set, otherwise setting the primary type to Off silently
    // disables the secondary feature.
    private static bool AnyProducedTypeEnabled(Scanners.IScanner scanner, Configuration.PluginConfiguration config)
    {
        if (config.GetFixMode(scanner.Type) != Configuration.FixMode.Off)
        {
            return true;
        }

        foreach (var extraType in scanner.AdditionalTypes)
        {
            if (config.GetFixMode(extraType) != Configuration.FixMode.Off)
            {
                return true;
            }
        }

        return false;
    }
}
