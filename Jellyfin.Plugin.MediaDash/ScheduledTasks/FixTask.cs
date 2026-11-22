using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    // Every IssueType that has a fixer registered. Stale is approval-only (Automatic clamped by
    // NormaliseFixModes) but still fixable per-row. MissingSubtitles / Ungrouped / HeavyTranscode /
    // FailedTranscode / EmbeddedCoverArt used to be missing here, silently no-op'ing FixMode.Automatic
    // for those types.
    private static readonly IssueType[] FixableTypes =
    [
        IssueType.CorruptArtwork,
        IssueType.Duplicate,
        IssueType.Quality,
        IssueType.SubtitleLanguage,
        IssueType.AudioLanguage,
        IssueType.Playability,
        IssueType.Misplaced,
        IssueType.MissingSubtitles,
        IssueType.MalwareRisk,
        IssueType.Stale,
        IssueType.Ungrouped,
        IssueType.LargeTrickplay,
        IssueType.SubtitleFonts,
        IssueType.OrphanedDebris,
        IssueType.CorruptNfo,
        IssueType.HeavyTranscode,
        IssueType.FailedTranscode,
        IssueType.EmbeddedCoverArt,
        IssueType.FormatCompatibility
    ];

    /// <summary>
    /// How often the fix task wakes up to check whether the server is idle. The task fires on this cadence
    /// and returns immediately when someone is watching or was active in the last 15 minutes (see <see cref="IdleCheck"/>);
    /// no queued issues stay queued and nothing else changes. When the server is genuinely idle, all queued fixes run.
    /// </summary>
    // Free-space floor for the bin volume. Non-configurable — Jellyfin itself needs ~2 GB of
    // working headroom, plus a 1 GB safety window for the plugin data (SQLite, probe cache, active
    // sidecars). Below the floor, further fixes risk driving the disk to 0 free bytes mid-write.
    internal const long BinVolumeMinFreeBytes = 3L * 1024 * 1024 * 1024;

    internal static readonly TimeSpan FixInterval = TimeSpan.FromMinutes(30);

    private readonly IssueStore _issues;
    private readonly MediaDashDb _db;
    private readonly RecycleBin _recycleBin;
    private readonly ISessionManager _sessionManager;
    private readonly ITaskManager _taskManager;
    private readonly Analytics.AnalyticsReporter _analytics;
    private readonly LibraryGuard _libraryGuard;
    private readonly RunState _runState;
    private readonly IPluginConfigurationProvider _configProvider;
    private readonly FixExecutor _executor;
    private readonly FixResultRecorder _recorder;
    private readonly FixLibraryRefresher? _libraryRefresher;
    private readonly ILogger<FixTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FixTask"/> class.
    /// </summary>
    /// <param name="issues">Issue-table store (05-C.2). FixTask only touches issues directly; history/queue writes go through the recorder.</param>
    /// <param name="recycleBin">The recycle bin.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="taskManager">Jellyfin's scheduled-task manager, used to detect its own library scan.</param>
    /// <param name="analytics">The opt-in analytics reporter.</param>
    /// <param name="libraryGuard">Library guard, used to enumerate library roots when sweeping orphan sidecars.</param>
    /// <param name="runState">The shared run-state holder (02-A).</param>
    /// <param name="configProvider">Configuration snapshot provider (02-A).</param>
    /// <param name="executor">Per-operation executor (04-C). Owns consent recheck, fixer dispatch, sharing-retry and exception translation.</param>
    /// <param name="recorder">Transactional history + status recorder (04-B). Executor's output feeds directly into <see cref="FixResultRecorder.Record"/>.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="db">Plugin database for retained run diagnostics.</param>
    /// <param name="libraryRefresher">Queues one full refresh per affected library after the run.</param>
    public FixTask(
        IssueStore issues,
        RecycleBin recycleBin,
        ISessionManager sessionManager,
        ITaskManager taskManager,
        Analytics.AnalyticsReporter analytics,
        LibraryGuard libraryGuard,
        RunState runState,
        IPluginConfigurationProvider configProvider,
        FixExecutor executor,
        FixResultRecorder recorder,
        ILogger<FixTask> logger,
        MediaDashDb db,
        FixLibraryRefresher? libraryRefresher = null)
    {
        _issues = issues;
        _db = db;
        _recycleBin = recycleBin;
        _sessionManager = sessionManager;
        _taskManager = taskManager;
        _analytics = analytics;
        _libraryGuard = libraryGuard;
        _runState = runState;
        _configProvider = configProvider;
        _executor = executor;
        _recorder = recorder;
        _libraryRefresher = libraryRefresher;
        _logger = logger;
        // 02-A: bridge the DI singleton to Plugin.AttachedRunState so the legacy Plugin.CurrentActivity /
        // ScanScannersDone/Total facades on Plugin.cs reach this same instance. Idempotent — ScanTask
        // does the same on its own resolution.
        Plugin.AttachedRunState = runState;
    }

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
        var config = _configProvider.GetSnapshot();
        var isManualRun = _runState.ConsumeBypassIdleCheck(RunRole.Fix);

        if (config.PauseDuringPlayback && !isManualRun && IdleCheck.IsServerBusy(_sessionManager))
        {
            _logger.LogInformation("Skipping fix run: someone is watching or was recently active. Queued issues stay queued.");
            progress.Report(100);
            return;
        }

        // #72: don't overlap heavy ffmpeg/ffprobe work with Jellyfin's own library scan.
        // Explicit "Run fixes now" (isManualRun) bypasses — user intent beats the gate.
        if (!isManualRun && IdleCheck.IsLibraryScanRunning(_taskManager))
        {
            _logger.LogInformation("Skipping fix run: Jellyfin's own library scan is running. Queued issues stay queued.");
            progress.Report(100);
            return;
        }

        if (!_runState.TryBegin(RunRole.Fix))
        {
            _logger.LogInformation("Skipping fix run: another fix is already running in this process.");
            progress.Report(100);
            return;
        }

        using var fixRecording = RunDiagnosticsRecording.Start(_db, _logger, config.DiagnosticsEnabled, "fix");
        using var diagnostics = RunDiagnostics.Start(_logger, "fix", "FixTask");
        // Jellyfin's library scan waits for the run: one that starts meanwhile is paused and run once
        // afterwards, so it never indexes a half-moved library. An explicit run also pauses one already
        // running; a scheduled run never starts during one (checked above).
        using var libraryScanHold = JellyfinLibraryScanHold.Acquire(_taskManager, _logger, cancelRunning: isManualRun);
        using var libraryScanCancellation = new LibraryScanCancellation(null, cancellationToken);
        var touchedLibraries = new FixLibraryRefresher.Targets();
        var outcome = "Failed";
        string? failure = null;
        try
        {
            try
            {
                libraryScanCancellation.Token.ThrowIfCancellationRequested();
                await ExecuteFixBodyAsync(config, isManualRun, diagnostics?.Observe(progress) ?? progress, touchedLibraries, cancellationToken, libraryScanCancellation.Token).ConfigureAwait(false);
                outcome = "Completed";
            }
            finally
            {
                if (_libraryRefresher is not null)
                {
                    // A cancelled fixer may already have committed changes. Finish their
                    // membership independently; native scan coordination still applies.
                    await _libraryRefresher.QueueAsync(touchedLibraries, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (libraryScanCancellation.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            outcome = "Cancelled";
            _logger.LogInformation("Stopped automatic fixes: Jellyfin's library scan started. Remaining issues stay queued.");
            progress.Report(100);
        }
        catch (OperationCanceledException)
        {
            outcome = "Cancelled";
            throw;
        }
        catch (Exception error)
        {
            outcome = "Failed";
            failure = error.Message;
            throw;
        }
        finally
        {
            try
            {
                diagnostics?.Finish(outcome, null, null, null, null);
                fixRecording?.Complete(outcome, failure);
            }
            finally
            {
                _runState.Complete(RunRole.Fix);
            }
        }
    }

    private async Task ExecuteFixBodyAsync(PluginConfiguration config, bool isManualRun, IProgress<double> progress, FixLibraryRefresher.Targets touchedLibraries, CancellationToken cancellationToken, CancellationToken libraryScanCancellationToken)
    {
        // Manual-scan review grace. A user who just clicked "Scan" is presumed to be on the Issues
        // tab actively reviewing. Skip the opportunistic scheduled fix pass while RunState says
        // the last manual scan is still inside its ten-minute grace, otherwise auto-fix mutates
        // rows out from under the reviewer. isManualRun (explicit "Run fixes now") bypasses —
        // the user's opt-in is a stronger signal than the grace. Scheduled scans don't set the
        // timestamp, so this is a no-op for auto-only users. Producer is ScanTask.ExecuteAsync;
        // shape tests in ScanTaskManualScanGraceTests.
        if (!isManualRun && _runState.PeekManualScanCompleted() is { } lastManualScan)
        {
            var elapsed = DateTime.UtcNow - lastManualScan;
            if (elapsed < ScanTask.ManualScanReviewGrace)
            {
                var graceRemaining = ScanTask.ManualScanReviewGrace - elapsed;
                _logger.LogInformation(
                    "Skipping fix run: manual scan finished {Elapsed:0} min ago, still inside the {Grace:0}-min review grace ({Remaining:0} min left). Queued issues stay queued.",
                    elapsed.TotalMinutes,
                    ScanTask.ManualScanReviewGrace.TotalMinutes,
                    graceRemaining.TotalMinutes);
                progress.Report(100);
                return;
            }
        }

        // Time-of-day window. Empty strings = no window (existing behaviour). Manual runs bypass,
        // matching the idle-check bypass — the user's explicit "Run fixes now" click is a stronger
        // signal than the schedule. When inside the window we compute how much of it is left and
        // arm a CancellationTokenSource to fire at close — this cancels the currently-running
        // fixer via the token that already threads through FixAsync (see linked source below).
        TimeSpan? windowRemaining = null;
        if (!isManualRun && TryParseWindow(config.FixWindowStart, config.FixWindowEnd, out var winStart, out var winEnd))
        {
            var now = TimeOnly.FromDateTime(DateTime.Now);
            var check = WindowStatus(now, winStart, winEnd);
            if (!check.Inside)
            {
                _logger.LogInformation("Skipping fix run: outside the configured fix window ({Start}–{End}). Queued issues stay queued for the next run inside the window.", config.FixWindowStart, config.FixWindowEnd);
                progress.Report(100);
                return;
            }

            windowRemaining = check.TimeUntilClose;
        }

        // Wrap the incoming token with a linked source so we can cancel it when the window closes
        // without touching Jellyfin's task-scheduler token. Everything below uses the linked token.
        var incomingToken = cancellationToken;
        using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (windowRemaining is { } remaining && remaining > TimeSpan.Zero)
        {
            windowCts.CancelAfter(remaining);
        }

        cancellationToken = windowCts.Token;

        // Recycle-bin size cap. Non-zero limit AND real (non-dry-run) mode: refuse to fix until the
        // user empties the bin so we don't blow past the cap they set. Dry-run doesn't add bytes,
        // so it's exempt.
        var pauseGb = config.RecycleBinPauseFixesAtGb;
        if (pauseGb > 0 && !config.DryRun)
        {
            var binSizeBytes = 0L;
            try
            {
                binSizeBytes = _recycleBin.GetContents().SizeBytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not measure recycle bin size; letting the fix run proceed.");
            }

            var capBytes = (long)pauseGb * 1024L * 1024L * 1024L;
            if (binSizeBytes >= capBytes)
            {
                var msg = "Paused: recycle bin is at " + (binSizeBytes / (1024L * 1024L * 1024L)) + " GB (cap " + pauseGb + " GB). Empty it from the Recycle bin tab to resume.";
                _logger.LogInformation("Skipping fix run: {Msg}", msg);
                Api.Diagnostics.Record("FixTask.RecycleBinFull", msg);
                if (isManualRun)
                {
                    _runState.SetPauseReason(RunRole.Fix, msg);
                }

                progress.Report(100);
                return;
            }
        }

        // Free-space floor on the bin volume. Independent of the user-visible size cap above and
        // not disable-able — Jellyfin itself needs headroom, and recycling more files while the
        // volume is critically full risks corrupting the plugin data folder that lives alongside
        // the bin. 3 GB matches the Jellyfin runtime's ~2 GB working set plus a 1 GB safety.
        if (!config.DryRun && IsBinVolumeCriticallyFull(out var floorMsg))
        {
            _logger.LogInformation("Skipping fix run: {Msg}", floorMsg);
            Api.Diagnostics.Record("FixTask.BinVolumeCriticallyFull", floorMsg);
            if (isManualRun)
            {
                _runState.SetPauseReason(RunRole.Fix, floorMsg);
            }

            progress.Report(100);
            return;
        }

        // Track the exact IDs this pass moves from Detected to Queued so the consent rollback below
        // can revert only auto-queued rows. Any row a user manually approved via /Issues/{id}/Approve
        // is intentionally absent from this list and stays Queued regardless of its blocking-warning
        // flags — manual approval is a stronger signal than the type's default mode.
        var autoQueuedIds = new List<long>();

        try
        {
            foreach (var type in FixableTypes)
            {
                // Duplicate: Automatic mode temporarily disabled server-side too. The UI now hides the
                // Automatic segment for Duplicate, but a legacy config could still hold
                // DuplicateFixMode=Automatic; skipping the auto-queue step here ensures a stray auto-
                // delete can't slip through until the confidence-ladder rework is proven in the field.
                // Manual approval from the Issues tab still queues normally (that path bypasses this
                // whole loop).
                if (type == Data.IssueType.Duplicate && config.GetFixMode(type) == FixMode.Automatic)
                {
                    _logger.LogInformation("Duplicate is set to Automatic in config but auto-queue is disabled while the detection rework stabilises; issues remain Detected until manually approved.");
                    continue;
                }

                // OrphanedDebris: same server-side Automatic-disable as Duplicate. Users report
                // (issue #13, F-201) that when the scanner mis-classified a music/audiobook folder
                // as "empty of media" (video-extensions-only check), Automatic mode recycled the
                // whole tree without review. Until OrphanCleanupScanner's media-kind detector
                // covers audio+books+comics (PR-D scope), block auto-queue here. Manual approval
                // from the Issues tab still queues normally.
                if (type == Data.IssueType.OrphanedDebris && config.GetFixMode(type) == FixMode.Automatic)
                {
                    _logger.LogInformation("OrphanedDebris is set to Automatic but auto-queue is disabled server-side (F-201): the scanner's media-kind detector can misclassify non-video libraries. Issues stay Detected until manually approved.");
                    continue;
                }

                if (config.GetFixMode(type) == FixMode.Automatic)
                {
                    // Duplicate is the only confidence-gated type today. Below-threshold rows stay
                    // Detected so the user can still approve them manually from the Issues tab —
                    // manual approval is a stronger signal than the type's default mode.
                    double? gate = type == Data.IssueType.Duplicate ? config.DuplicateAutoFixConfidence : null;
                    var justQueued = _issues.QueueDetectedIssues(type, gate, requireConsent: true);
                    if (justQueued.Count > 0)
                    {
                        autoQueuedIds.AddRange(justQueued);
                        _logger.LogInformation("Auto-queued {Count} {Type} issues", justQueued.Count, type);
                    }
                }
            }
        }
        finally
        {
            // QueueDetectedIssues is a bulk UPDATE — it can't see the per-row DetailsJson to check
            // for blocking warnings (bitmap-subtitle removals, low-space overrides, etc.), so it
            // over-queues those rows. Revert them here before the fixer picks them up: consent-
            // gated data loss requires an explicit manual Approve from the Issues tab, even when
            // the type's mode is Automatic. Pairs with Issue.HasBlockingWarnings. Passing the exact
            // auto-queued ID set makes sure a concurrently-manually-approved blocking-warning row
            // is NOT swept up by this pass (its ID isn't in the list).
            var reverted = _issues.RollbackAutoQueuedBlockingWarnings(autoQueuedIds);
            if (reverted > 0)
            {
                _logger.LogInformation(
                    "Reverted {Count} auto-queued issue(s) back to Detected because they carry blocking warnings — manual approval required.",
                    reverted);
            }
        }

        // An issue reaches Queued status either because the auto-queue step above put it there (Automatic mode)
        // or because the user explicitly approved it in the UI. Approval does not bypass the current
        // type mode, fixer-disable list or library scope; report those refusals when no work can run.
        // FixPlanBuilder runs repairs before path-changing work and folder moves last.
        // User rank applies inside those phases; duplicates and subtitle downloads precede encodes.
        // Tiebreaker: smallest files first so early re-encodes free disk space for the bigger ones
        // behind them. Missing files sort to the front (size 0) so they fail fast.
        var allQueued = _issues.GetIssues(status: IssueStatus.Queued).ToList();
        var offCount = allQueued.Count(i => config.GetFixMode(i.Type) == FixMode.Off);
        // Per-fixer disable list from the "Fix order" dialog (icon next to the up/down arrows).
        // Independent of FixMode: the scanner still runs and the row still surfaces on Issues,
        // but the fixer skips this type until the user re-enables it (or Reset to default clears
        // the whole list). String-compared against the enum name; unknown entries no-op.
        var disabled = config.FixerDisabled ?? Array.Empty<string>();
        // Scope the queue to the currently-enabled libraries — an issue that was approved while its
        // library was ticked, then had its library unticked in Settings, must NOT be fixed. Without
        // this the fix loop would run against the old scope and mutate files the user has since
        // told MediaDash to leave alone. Empty EnabledLibraries falls back to "every library"
        // (same convention as scanners), so the common all-libraries case still passes through.
        // Pattern mirrors ScanTask's item-scoping block; the location prefixes already end with a
        // separator so StartsWith is a boundary match, not a prefix collision.
        var enabledLocations = _libraryGuard.GetEnabledLibraryLocations(config.EnabledLibraries);
        // 02-B: unified type-level gate via FixPolicyEvaluator. Every queued row already carries
        // an explicit user Approve (that's what Queued *means* here — auto-queued rows arrived
        // through QueueDetectedIssues, which respects restored_paths at the DB layer, so a
        // Scheduled intent here is equivalent to Approved for policy purposes). Library scope
        // still checked separately because IsInEnabledLocations is the shared guard.
        var queue = allQueued
            .Where(i => _libraryGuard.IsIssueInEnabledLocations(i, enabledLocations))
            .Where(i => FixPolicyEvaluator.Evaluate(
                config,
                i.Type,
                i.Status,
                FixIntent.Approved,
                withinEnabledLibrary: true,
                autoQueueRestoredBlocked: false,
                hasApprovedCompanionOnPath: false).Allowed)
            .ToList();
        // scopedOut: type is authorized but library is out of scope — the reason surfaces
        // separately in the log so users can distinguish "type off" from "library disabled".
        var scopedOut = allQueued.Count(i =>
            FixPolicyEvaluator.Evaluate(
                config,
                i.Type,
                i.Status,
                FixIntent.Approved,
                withinEnabledLibrary: true,
                autoQueueRestoredBlocked: false,
                hasApprovedCompanionOnPath: false).Allowed
            && !_libraryGuard.IsIssueInEnabledLocations(i, enabledLocations));

        if (scopedOut > 0)
        {
            _logger.LogInformation(
                "Skipping {Count} queued issue(s) whose library is no longer enabled in Settings → Libraries; they stay queued and will run again if the library is re-enabled.",
                scopedOut);
        }

        if (queue.Count == 0)
        {
            // Empty scheduled checks stay quiet; explicit clicks and blocked queues need an explanation.
            if (isManualRun || allQueued.Count > 0)
            {
                var reasons = new List<string>();
                var disabledCount = allQueued.Count(i => config.GetFixMode(i.Type) != FixMode.Off && disabled.Contains(i.Type.ToString(), StringComparer.Ordinal));
                if (offCount > 0)
                {
                    reasons.Add(offCount + " approved issue(s) have their type set to Off. Check Settings → What to fix.");
                }

                if (disabledCount > 0)
                {
                    reasons.Add(disabledCount + " approved issue(s) have their fixer disabled. Re-enable it in Settings → Fix order.");
                }

                if (scopedOut > 0)
                {
                    reasons.Add(scopedOut + " approved issue(s) are outside enabled libraries. Check Settings → Libraries; moved files may need a rescan.");
                }

                if (allQueued.Count > offCount + disabledCount + scopedOut)
                {
                    reasons.Add("Other approved issues were refused by the current fix policy. Review them on the Issues tab.");
                }

                if (allQueued.Count == 0)
                {
                    reasons.Add(_issues.GetIssues(status: IssueStatus.Detected).Any(i => FixableTypes.Contains(i.Type))
                        ? "No fixes are approved or automatically eligible. Review the Issues tab and approve the fixes you want; check their mode in Settings → What to fix."
                        : "There are no fixable issues waiting. Click Scan now to look for new issues.");
                }

                var noWorkReason = "No fixes ran. " + string.Join(" ", reasons);
                _runState.SetPauseReason(RunRole.Fix, noWorkReason);
                Plugin.LastFixRun = new Api.FixRunSummary
                {
                    FinishedAtUtc = DateTime.UtcNow,
                    SkippedLibraryDisabled = scopedOut,
                    NoWorkReason = noWorkReason
                };
                _logger.LogInformation("{Reason}", noWorkReason);
                if (allQueued.Count > 0)
                {
                    Api.Diagnostics.Record("FixTask.NoRunnable", noWorkReason);
                }
            }

            _logger.LogDebug("Skipping fix run: no queued issues to fix.");
            progress.Report(100);
            return;
        }

        _logger.LogInformation("MediaDash fix run: {Count} queued issues (dry-run: {DryRun})", queue.Count, config.DryRun);

        // Transcode-companion routing (issue-XX): same file with a transcode-family issue
        // (Quality / HeavyTranscode / FailedTranscode) queued alongside AudioLanguage and/or
        // SubtitleLanguage. TranscodeFixer.BuildArgs already filters mapped audio + subtitle
        // streams by the configured language allow-lists during the re-encode, so unwanted
        // tracks are dropped incidentally — no separate TrackFixer pass is needed. Running one
        // anyway would either (a) waste a full remux read if it ran first, or (b) fail with
        // "nothing to remove" if it ran second. Instead, we claim the AudioLanguage /
        // SubtitleLanguage issues as companions of the transcode issue and mark them Fixed
        // when the transcode succeeds.
        // 04-A: FixPlanBuilder is the single source of truth for grouping (transcode-with-tracks,
        // audio+subtitle combined pair, single). 04-C: the main loop iterates plans directly —
        // companions never come up as their own iteration because plans emit at most one entry
        // per primary. That kills two skip-set checks (transcodeCompanionIds, combinedPartners)
        // the pre-refactor loop had to run per queue item.
        var plans = FixPlanBuilder.Build(
            queue,
            FixerRank,
            GetFileSizeOrZero,
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var transcodePlanCount = plans.Count(p => p.Kind == PlannedFixKind.TranscodeWithTracks);
        var transcodeCompanionCount = plans
            .Where(p => p.Kind == PlannedFixKind.TranscodeWithTracks)
            .Sum(p => p.Companions.Count);
        var trackPairPlanCount = plans.Count(p => p.Kind == PlannedFixKind.TrackPair);

        if (transcodePlanCount > 0)
        {
            _logger.LogInformation(
                "Transcode-companion routing: {Files} file(s) have transcode + track issues queued — the transcode's re-encode already drops unwanted tracks, so {Companions} track issue(s) will be resolved by the transcode pass instead of a separate remux.",
                transcodePlanCount,
                transcodeCompanionCount);
        }

        if (trackPairPlanCount > 0)
        {
            _logger.LogInformation("Combined-pass eligible: {Count} file(s) have both AudioLanguage and SubtitleLanguage queued — running as one ffmpeg per file instead of two.", trackPairPlanCount);
        }

        // Run tallies live here (not thread-local per fixer) so the after-run summary can call out the
        // dominant failure — e.g. "all 142 fixes failed with permission denied" — via a dashboard alert.
        var attempted = 0;
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;
        var reasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        // Per-item silent-skip tally fed by FixExecutor. Catches the long-tail "queue has items but
        // every one is silently refused at per-item recheck" class — status changed between approval
        // and run, settings flipped mid-run, protection added, no fixer registered. Without this,
        // non-empty queues of all-skipped items produced "Completed after 0 seconds" with no
        // explanation (the exact user-visible symptom of #81 and its follow-ups).
        var silentSkips = new Dictionary<string, int>(StringComparer.Ordinal);
        // Subtitle-provider quotas (OpenSubtitles free tier: 5 downloads / 24 h) exhaust in a run
        // and hit the same wall on every remaining MissingSubtitles file. Track first detection,
        // skip subsequent items silently, log one summary. Keep them Queued so they retry after reset.
        var subtitleProviderQuotaExhausted = false;
        var subtitleQuotaSkipped = 0;
        void RecordFailure(string reason)
        {
            failed++;
            var bucket = BucketReason(reason);
            reasonCounts[bucket] = reasonCounts.TryGetValue(bucket, out var n) ? n + 1 : 1;
        }

        if (offCount > 0)
        {
            Api.Diagnostics.Record(
                "FixTask.SomeSkipped",
                offCount + " approved issue(s) will not run because their type is set to 'Off' in Settings. The other " + queue.Count + " will run.");
        }

        try
        {
            for (var i = 0; i < plans.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Finish the current replacement safely, then defer the remaining queue.
                libraryScanCancellationToken.ThrowIfCancellationRequested();

                // Re-check the free-space floor between items — a single big remux can shift the bin
                // volume by many GB. If we cross the floor mid-run, drop the remaining queue rather
                // than fill the disk to empty.
                if (!config.DryRun && IsBinVolumeCriticallyFull(out var midRunFloorMsg))
                {
                    _logger.LogInformation("Pausing fix run mid-queue: {Msg}", midRunFloorMsg);
                    Api.Diagnostics.Record("FixTask.BinVolumeCriticallyFull", midRunFloorMsg);
                    if (isManualRun)
                    {
                        _runState.SetPauseReason(RunRole.Fix, midRunFloorMsg);
                    }

                    break;
                }

                // Re-check the user-configured size cap between items. The start-of-run check refuses
                // to begin over the cap, but a run started under the cap can push over it after a few
                // large recycles. Without this, the cap is a one-time filter instead of a live limit.
                // User bug report: recycle bin blew past the cap because fixes kept running after each
                // recycle added GB.
                if (pauseGb > 0 && !config.DryRun)
                {
                    var midBinBytes = 0L;
                    try
                    {
                        midBinBytes = _recycleBin.GetContents().SizeBytes;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Read failure is not fatal; skip the mid-run check for this iteration.
                        _logger.LogDebug(ex, "Could not measure recycle bin size mid-run; skipping cap check.");
                        midBinBytes = 0;
                    }

                    var midCapBytes = (long)pauseGb * 1024L * 1024L * 1024L;
                    if (midBinBytes >= midCapBytes)
                    {
                        var msg = "Paused mid-run: recycle bin crossed the " + pauseGb + " GB cap (now "
                            + (midBinBytes / (1024L * 1024L * 1024L)) + " GB). Empty it from the Recycle bin tab to resume.";
                        _logger.LogInformation("Pausing fix run mid-queue: {Msg}", msg);
                        Api.Diagnostics.Record("FixTask.RecycleBinFull", msg);
                        if (isManualRun)
                        {
                            _runState.SetPauseReason(RunRole.Fix, msg);
                        }

                        break;
                    }
                }

                if (config.PauseDuringPlayback && !_runState.FixIgnoreActivity && IdleCheck.IsServerBusy(_sessionManager))
                {
                    // Discoverability hint for the reported "MediaDash pinned my HDD" class: if we're
                    // pausing mid-run because a user showed up, and Low system impact mode is OFF, this
                    // is the exact machine profile the setting was built for. One nudge per plugin
                    // lifetime — spamming a user who's already dismissed it is worse than silence.
                    TryEmitLowImpactHint(_runState, config.LowSystemImpactMode);

                    if (isManualRun)
                    {
                        // Manual run: the user is standing at the dashboard waiting for this to finish, so pause
                        // rather than break. Re-poll every 30 s. The user can bail out via Stop, unstick the loop
                        // by flipping _runState.FixIgnoreActivity, or wait for the viewer to finish.
                        _runState.SetPauseReason(RunRole.Fix, "Paused: someone is watching. Click 'Ignore activity' to resume anyway.");
                        _logger.LogInformation("Fix run paused for viewer activity; waiting for idle or ignore-flag.");
                        while (config.PauseDuringPlayback
                            && !_runState.FixIgnoreActivity
                            && IdleCheck.IsServerBusy(_sessionManager))
                        {
                            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                        }

                        _runState.SetPauseReason(RunRole.Fix, null);
                        _logger.LogInformation("Fix run resuming.");
                    }
                    else
                    {
                        _logger.LogInformation("Pausing fix run: someone started using the server. Remaining issues stay queued.");
                        break;
                    }
                }

                var plan = plans[i];
                var primary = plan.Primary;
                var slot = 100.0 / plans.Count;
                progress.Report(i * slot);
                // Synchronous IProgress: Progress<T> queues callbacks and can reorder reports, leading
                // to a jittery bar.
                var itemProgress = new SynchronousProgress(fraction =>
                {
                    var clamped = Math.Clamp(fraction, 0, 1);
                    progress.Report((i + clamped) * slot);
                    // Also surface the per-file % to the dashboard so users see "encoding this file 45%"
                    // alongside the queue-wide "N of M". Only fixers with real per-file signal
                    // (TranscodeFixer → ffmpeg -progress) report a fraction; the rest never call this.
                    _runState.SetCurrentOperationProgress(RunRole.Fix, clamped);
                });

                // Provider quota short-circuit: MissingSubtitleFixer emits Deferred/ProviderQuota when
                // OpenSubtitles' free-tier download limit is hit. Every remaining MissingSubtitles
                // issue in this run will hit the same wall, so skip them silently and log the count
                // at end-of-run rather than firing the fixer just to have it defer again.
                if (primary.Type == IssueType.MissingSubtitles && subtitleProviderQuotaExhausted)
                {
                    subtitleQuotaSkipped++;
                    continue;
                }

                // Library-scope recheck stays in FixTask (recomputed per-item from the CURRENT config
                // snapshot so a user unticking a library mid-run takes effect on the next item). The
                // executor consumes the bool result — this keeps ILibraryManager out of the executor's
                // DI graph, which in turn keeps FixExecutor trivially constructible in unit tests.
                var currentEnabledLocations = _libraryGuard.GetEnabledLibraryLocations(_configProvider.GetSnapshot().EnabledLibraries);
                var withinEnabledLibrary = _libraryGuard.IsIssueInEnabledLocations(primary, currentEnabledLocations);

                // 04-C: hand the plan to the executor. Executor owns per-item consent recheck,
                // combined-pair partner status refresh, fixer dispatch (with sharing-violation
                // retry), and exception → typed FixResult translation. Returns null when the
                // operation should be silently skipped (no fixer registered, primary's status
                // changed mid-run, policy re-check refused). Silent skips write no history and
                // do not tick the attempted / succeeded / failed counters.
                libraryScanCancellationToken.ThrowIfCancellationRequested();
                FixLibraryRefresher.Attempt? refreshAttempt = null;
                FixExecutionResult? execution = null;
                try
                {
                    execution = await _executor.ExecuteAsync(
                        plan,
                        withinEnabledLibrary,
                        itemProgress,
                        silentSkips,
                        cancellationToken,
                        issue => refreshAttempt = _libraryRefresher?.Capture(issue)).ConfigureAwait(false);
                }
                finally
                {
                    if (refreshAttempt is not null)
                    {
                        _libraryRefresher!.Complete(refreshAttempt, execution?.Result, touchedLibraries);
                    }
                }

                if (execution is null)
                {
                    continue;
                }

                attempted++;
                _recorder.Record(plan, execution, DateTime.UtcNow);

                if (execution.Result.Success && !execution.Result.WasDryRun)
                {
                    succeeded++;
                }
                else if (!execution.Result.Success)
                {
                    // Distinguish real failures (Failed / Unspecified) from safe non-actions
                    // (Refused / NoChange / Deferred). Users read "N failed" as "N bugs";
                    // "hard precondition refused" or "already fine" isn't a bug — that goes
                    // into Skipped. Only Failed contributes TopFailureReason too, so a run
                    // full of destination collisions doesn't crowd out real error messages.
                    var outcome = execution.Result.Outcome;
                    if (outcome == Fixers.FixOutcome.Refused
                        || outcome == Fixers.FixOutcome.NoChange
                        || outcome == Fixers.FixOutcome.Deferred)
                    {
                        skipped++;
                    }
                    else
                    {
                        RecordFailure(execution.Result.Message); // increments `failed` + records reason bucket
                        _logger.LogWarning("Fix failed for {Path}: {Message}", primary.Path, execution.Result.Message);
                    }

                    if (primary.Type == IssueType.MissingSubtitles && execution.Result.Reason == FixReason.ProviderQuota)
                    {
                        subtitleProviderQuotaExhausted = true;
                    }
                }

                progress.Report((i + 1) * slot);
            }
        }
        catch (OperationCanceledException) when (windowCts.IsCancellationRequested && !incomingToken.IsCancellationRequested && !libraryScanCancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Fix window closed; remaining issues stay queued.");
        }

        cancellationToken = incomingToken;

        // Both maintenance passes below physically delete files on disk (retention purge of the
        // recycle bin, sweep of stale ffmpeg .mediadash.tmp/.mediadash.new leftovers). During a
        // dry run we promise "no local file changes"; skipping them here keeps that promise
        // absolute. They'll run on the next real fix pass — nothing is lost, just deferred.
        if (!config.DryRun)
        {
            _recycleBin.Purge(config.RecycleBinRetentionDays);

            // Orphan-sidecar sweep: at end of a fix run no encode is active, so any *.mediadash.tmp* /
            // *.mediadash.new* file sitting in a library folder is a leftover from a hard-killed encode
            // (SIGKILL, container restart, Jellyfin crash) where the fixer's finally couldn't clean up.
            try
            {
                // FU-1 (2026-09-19): scope the sweep to the same EnabledLibraries the queue
                // selection above respects. Without this, an owned sidecar in a library the
                // user excluded from MediaDash still got deleted by end-of-run maintenance.
                var orphans = _libraryGuard.SweepOrphanSidecars(config.EnabledLibraries);
                if (orphans.Count > 0)
                {
                    foreach (var orphan in orphans)
                    {
                        _libraryRefresher?.Collect(new Issue { Path = orphan.Path }, touchedLibraries);
                    }

                    var freed = orphans.Sum(o => o.Bytes);
                    _logger.LogInformation("Removed {Count} orphan MediaDash sidecar file(s), reclaimed {Bytes} bytes.", orphans.Count, freed);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Orphan-sidecar sweep failed; will retry next run.");
            }
        }

        if (subtitleQuotaSkipped > 0)
        {
            _logger.LogInformation("Subtitle provider download quota reached; skipped {Count} queued MissingSubtitles issue(s). They stay queued and retry on the next run after quota reset.", subtitleQuotaSkipped);
        }

        Plugin.CurrentActivity = null;
        Plugin.CurrentActivityLabel = null;

        // Non-empty-queue silent-skip summary. The run-start empty-queue branch above already
        // explains Off / disabled / scoped-out at the queue-filter stage. This catches
        // the complementary case: queue had items, every one was refused by a per-item recheck
        // (status flipped, settings changed mid-run, protection added, no fixer). Prior behaviour
        // was "Completed after 0 seconds" with zero explanation — a silent-fail the user attributes
        // to the plugin being broken.
        string? silentSkipReason = null;
        if (attempted == 0 && silentSkips.Count > 0)
        {
            var breakdown = string.Join("; ", silentSkips.OrderByDescending(kv => kv.Value).Select(kv => kv.Value + " × " + kv.Key));
            silentSkipReason = "No fixes ran. " + silentSkips.Values.Sum() + " approved issue(s) were refused at execution: " + breakdown + ". Open the Issues tab or Settings → What to fix to review.";
            _runState.SetPauseReason(RunRole.Fix, silentSkipReason);
            Api.Diagnostics.Record("FixTask.NoRunnable", silentSkipReason);
            _logger.LogInformation("{Reason}", silentSkipReason);
        }

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
            Skipped = skipped,
            TopFailureReason = topReason?.Key,
            TopFailureCount = topReason?.Value ?? 0,
            SkippedLibraryDisabled = scopedOut,
            NoWorkReason = silentSkipReason
        };

        // Opt-in analytics: month-to-date totals get pushed after every run so a mid-month opt-in
        // (or a run that only fixed a couple of files) still contributes accurate numbers. Reporter
        // is fire-and-forget with graceful failure — never blocks progress reporting.
        await _analytics.ReportMonthToDateAsync(cancellationToken).ConfigureAwait(false);

        progress.Report(100);
    }

    // 04-C: RunFixWithSharingRetryAsync + MarkFixed + IsSharingViolation + IsDiskFull moved into
    // FixExecutor (which owns the sharing retry loop, exception translation and per-op recording
    // via FixResultRecorder). FixTaskDiskFullTests now targets FixExecutor.IsDiskFull.

    /// <summary>
    /// Parses the configured fix-window strings. Both empty returns false (no window). Any invalid
    /// or partial pair also returns false — the window opts in only when both endpoints are valid.
    /// </summary>
    /// <param name="start">Start of the window in 24-hour <c>HH:mm</c> format, or empty.</param>
    /// <param name="end">End of the window in 24-hour <c>HH:mm</c> format, or empty.</param>
    /// <param name="startTime">Parsed start when the return value is true.</param>
    /// <param name="endTime">Parsed end when the return value is true.</param>
    /// <returns>True when both endpoints parsed cleanly; false otherwise (caller should skip the window check).</returns>
    internal static bool TryParseWindow(string start, string end, out TimeOnly startTime, out TimeOnly endTime)
    {
        startTime = default;
        endTime = default;
        if (string.IsNullOrWhiteSpace(start) || string.IsNullOrWhiteSpace(end))
        {
            return false;
        }

        return TimeOnly.TryParseExact(start, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out startTime)
            && TimeOnly.TryParseExact(end, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out endTime);
    }

    /// <summary>
    /// Given a time-of-day and a window, reports whether we're inside it and — if so — how long
    /// until the window closes. Overnight windows (end &lt; start) wrap through midnight.
    /// start == end degenerates to "always inside" so a user who sets the same time on both sides
    /// gets a 24/7 window rather than a "never" window (safer default of the two).
    /// </summary>
    /// <param name="now">The current time-of-day.</param>
    /// <param name="start">Window start.</param>
    /// <param name="end">Window end.</param>
    /// <returns>Whether <paramref name="now"/> is inside the window, and how long remains until it closes.</returns>
    internal static (bool Inside, TimeSpan TimeUntilClose) WindowStatus(TimeOnly now, TimeOnly start, TimeOnly end)
    {
        if (start == end)
        {
            return (true, TimeSpan.FromDays(1));
        }

        bool inside;
        TimeSpan timeUntilClose;
        if (start < end)
        {
            inside = now >= start && now < end;
            timeUntilClose = inside ? end - now : TimeSpan.Zero;
        }
        else
        {
            // Overnight: window is [start..24:00) ∪ [00:00..end)
            inside = now >= start || now < end;
            if (!inside)
            {
                timeUntilClose = TimeSpan.Zero;
            }
            else if (now >= start)
            {
                // Same-day portion: close is tomorrow's `end`
                timeUntilClose = (TimeOnly.MaxValue - now) + TimeSpan.FromTicks(1) + (end - TimeOnly.MinValue);
            }
            else
            {
                // Post-midnight portion: close is today's `end`
                timeUntilClose = end - now;
            }
        }

        return (inside, timeUntilClose);
    }

    // Deleted in 01-B: IsStaleFailure(string). The scheduler now consults
    // FixResultPolicy.GetQueueDisposition(FixOutcome, FixReason) — see the caller of MarkFixed
    // in RunFixQueueAsync. Every fixer produces a typed Outcome+Reason so the phrase-matching
    // classifier has no internal callers. The retained phrase inventory now lives as a
    // presentation-only documentation test (FixTaskStaleFailureTests) that checks the same
    // canonical wording still appears in the fixer source files where a user will read it.

    // Reads free space on the bin volume; returns true when it's below the floor. Reason string is
    // set only when true, phrased for direct display in the dashboard's pause banner.
    private bool IsBinVolumeCriticallyFull(out string reason)
    {
        reason = string.Empty;
        try
        {
            var binRoot = _recycleBin.GetEffectiveRoot();
            var drive = RecycleBin.FindDriveForPath(binRoot);
            if (drive is null)
            {
                return false;
            }

            var free = drive.AvailableFreeSpace;
            if (free >= BinVolumeMinFreeBytes)
            {
                return false;
            }

            var freeGb = (int)(free / (1024L * 1024 * 1024));
            reason = "Paused: bin volume '" + drive.RootDirectory.FullName + "' has " + freeGb + " GB free. Jellyfin needs 2 GB of free space to run so MediaDash requires 3 GB to ensure nothing goes wrong. Free some space (empty the recycle bin, move files off this volume) and the next run will resume.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read free space on bin volume; letting the fix run proceed.");
            return false;
        }
    }

    // Deleted in 01-B: IsSubtitleProviderQuotaExhausted(string). MissingSubtitleFixer now parses
    // the provider exception at the third-party boundary (its LooksLikeProviderQuota helper) and
    // returns FixResult.Deferred(FixReason.ProviderQuota, ...). The scheduler tests result.Reason
    // directly; no phrase matching survives inside FixTask.

    // Fold a specific error message into a short reason-family so 142 permission-denied failures collapse to
    // a single bucket in the summary. Anything unrecognised is capped at 60 chars so long ffmpeg errors
    // don't turn the alert into a wall of text.
    internal static string BucketReason(string message)
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
            || message.Contains("not enough free disk space", StringComparison.OrdinalIgnoreCase)
            || message.Contains("filled up mid-move", StringComparison.OrdinalIgnoreCase))
        {
            return "Not enough free disk space";
        }

        if (message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Resource busy", StringComparison.OrdinalIgnoreCase))
        {
            return "File was held open by another process (likely Jellyfin trickplay / chapter thumbs / active playback)";
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

    // Field report D2 hard constraints, expressed as a fixed rank order. Lower runs first.
    //   SuspiciousFile / Playability first: don't rewrite content we'd otherwise flag for removal.
    //   Duplicate before Transcode/Track/SubtitleFont/EmbeddedCoverArt: don't rebuild a file about to be deleted.
    //   OrphanCleanup before movers so sidecar sweeps run against pre-move state.
    //   MissingSubtitle before Transcode: subs must land before an encode, else they're orphaned by rename.
    //   Track (cheap remux) before Transcode (full re-encode).
    //   TrickplayOptimize last: BIF file must match the FINAL video hash post-encode, or Jellyfin regenerates it.
    // Anything unlisted falls after the ranked types (rank = int.MaxValue), preserving today's order there.

    /// <summary>
    /// Emits the "consider Low system impact mode" diagnostic hint iff the setting is currently
    /// OFF and we haven't already emitted the hint this plugin lifetime. Returns true if the
    /// hint was emitted on this call, false if it was already emitted or the setting is on.
    /// Called from the mid-run per-file activity check — that's the exact signal that this
    /// machine is being used for daily work while a fix runs. The one-shot latch lives on
    /// <see cref="RunState"/> so a fresh instance in a test does not inherit a previous test's flip.
    /// </summary>
    /// <param name="runState">The shared run-state holder.</param>
    /// <param name="lowSystemImpactMode">The current LowSystemImpactMode config value.</param>
    /// <returns>True if the hint was recorded on this call.</returns>
    internal static bool TryEmitLowImpactHint(RunState runState, bool lowSystemImpactMode)
    {
        if (lowSystemImpactMode)
        {
            return false;
        }

        if (!runState.TryEmitLowImpactHint())
        {
            return false;
        }

        Api.Diagnostics.Record(
            "FixTask.LowImpactHint",
            "Fix run paused because someone started using the server. If MediaDash's ffmpeg is making this machine unresponsive (spinning HDDs pinned at 100 %, general lag), turn on Settings → Safety → Low system impact mode. Encodes take longer in exchange but the disk and CPU stay usable for daily work.");
        return true;
    }

    /// <summary>
    /// Groups every queued transcode-family issue (Quality / HeavyTranscode / FailedTranscode)
    /// with any AudioLanguage / SubtitleLanguage issue on the same path so those track issues
    /// can be resolved as companions of the transcode. 04-A: the grouping logic itself lives in
    /// <see cref="FixPlanBuilder.BuildTranscodeCompanionMap"/>; this method stays as a thin
    /// forwarding wrapper so existing direct-call tests keep exercising the same behaviour
    /// through a single entry point.
    /// </summary>
    /// <param name="queue">The current fix-run queue after Off-type filtering and rank ordering.</param>
    /// <returns>Map of transcode issue id → companion audio/subtitle issues on the same path.</returns>
    internal static Dictionary<long, List<Issue>> BuildTranscodeCompanions(IReadOnlyList<Issue> queue)
        => FixPlanBuilder.BuildTranscodeCompanionMap(queue, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static int FixerRank(Data.IssueType type)
    {
        // User override: if config.FixerOrder is set (via the Overview "Fix order" dialog), the
        // user's ordering wins within the planner's execution phases. Unlisted types fall to the end via DefaultFixerRank so a new fixer
        // added in a future version doesn't disappear from the queue just because the user's
        // saved order predates it.
        var custom = Plugin.Instance?.Configuration?.FixerOrder;
        if (custom is { Length: > 0 })
        {
            for (var i = 0; i < custom.Length; i++)
            {
                if (Enum.TryParse<Data.IssueType>(custom[i], ignoreCase: false, out var t) && t == type)
                {
                    return i;
                }
            }

            return 1000 + DefaultFixerRank(type);
        }

        return DefaultFixerRank(type);
    }

    private static int DefaultFixerRank(Data.IssueType type) => type switch
    {
        Data.IssueType.MalwareRisk => 0,
        Data.IssueType.Playability => 1,
        Data.IssueType.Duplicate => 2,
        Data.IssueType.OrphanedDebris => 3,
        Data.IssueType.MissingSubtitles => 4,
        Data.IssueType.SubtitleLanguage => 5,
        Data.IssueType.AudioLanguage => 6,
        Data.IssueType.SubtitleFonts => 7,
        Data.IssueType.EmbeddedCoverArt => 8,
        Data.IssueType.CorruptNfo => 9,
        Data.IssueType.CorruptArtwork => 10,
        Data.IssueType.Stale => 11,
        Data.IssueType.LargeTrickplay => 12,
        Data.IssueType.Quality => 13,
        Data.IssueType.HeavyTranscode => 14,
        Data.IssueType.FailedTranscode => 15,
        Data.IssueType.FormatCompatibility => 16,
        Data.IssueType.Misplaced => 17,
        Data.IssueType.Ungrouped => 18,
        _ => int.MaxValue
    };

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
