using System;
using System.Collections.Generic;
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
    // Every IssueType that has a fixer registered. Stale is intentionally omitted (no fixer, it's an
    // informational category). MissingSubtitles / Ungrouped / HeavyTranscode / FailedTranscode /
    // EmbeddedCoverArt used to be missing here, silently no-op'ing FixMode.Automatic for those types.
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
        IssueType.Ungrouped,
        IssueType.LargeTrickplay,
        IssueType.SubtitleFonts,
        IssueType.OrphanedDebris,
        IssueType.CorruptNfo,
        IssueType.HeavyTranscode,
        IssueType.FailedTranscode,
        IssueType.EmbeddedCoverArt
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
                    PauseReason = msg;
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
                PauseReason = floorMsg;
            }

            progress.Report(100);
            return;
        }

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
                var queued = _db.QueueDetectedIssues(type, gate);
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
        // Primary sort: fixer rank (see FixerRankOrder for the D2 hard constraints — Suspicious /
        // Playability before content rewrites, Duplicate before Track/Transcode so we don't rebuild
        // a file about to be deleted, MissingSubtitle before Transcode so subs land pre-encode,
        // TrickplayOptimize last because BIF must match the final video).
        // Tiebreaker: smallest files first so early re-encodes free disk space for the bigger ones
        // behind them. Missing files sort to the front (size 0) so they fail fast.
        var allQueued = _db.GetIssues(status: IssueStatus.Queued).ToList();
        var offCount = allQueued.Count(i => config.GetFixMode(i.Type) == FixMode.Off);
        var queue = allQueued
            .Where(i => config.GetFixMode(i.Type) != FixMode.Off)
            .OrderBy(i => FixerRank(i.Type))
            .ThenBy(GetFileSizeOrZero)
            .ToList();

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
        // transcodeCompanions maps the TRANSCODE issue id → its companion track issues on the
        // same path. transcodeCompanionIds is every claimed track issue id — the main loop skips
        // those, and the combined-pairs block below also excludes them so a track pair can't be
        // routed twice.
        var transcodeCompanions = BuildTranscodeCompanions(queue);
        var transcodeCompanionIds = new HashSet<long>(transcodeCompanions.SelectMany(kv => kv.Value.Select(i => i.Id)));

        if (transcodeCompanions.Count > 0)
        {
            _logger.LogInformation(
                "Transcode-companion routing: {Files} file(s) have transcode + track issues queued — the transcode's re-encode already drops unwanted tracks, so {Companions} track issue(s) will be resolved by the transcode pass instead of a separate remux.",
                transcodeCompanions.Count,
                transcodeCompanionIds.Count);
        }

        // Combined-pass detection: same file with BOTH AudioLanguage AND SubtitleLanguage queued.
        // The TrackFixer can drop both categories in ONE ffmpeg remux instead of two back-to-back
        // (which would read the source twice and produce two intermediate bin entries). Cuts wall
        // time roughly in half on TV episodes and 60–70% on Blu-ray remuxes. Users hit this every
        // time they set both AllowedAudioLanguages and AllowedSubtitleLanguages tightly on a multi-
        // language rip.
        // combinedPairs maps the AUDIO issue id → SUBTITLE partner issue on the same path.
        // combinedPartners is every SUBTITLE issue that's been paired — the main loop skips those
        // (they get handled as the audio side's companion, not on their own).
        // Issues already claimed by transcodeCompanions above are excluded so the transcode
        // routing wins the tie: the transcode will drop the tracks anyway, and running a combined
        // remux first would just re-do work that's about to happen inside the transcode.
        var combinedPairs = new Dictionary<long, Issue>();
        var combinedPartners = new HashSet<long>();
        {
            var trackPairs = queue
                .Where(i => (i.Type == IssueType.AudioLanguage || i.Type == IssueType.SubtitleLanguage)
                            && !transcodeCompanionIds.Contains(i.Id))
                .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var pathGroup in trackPairs)
            {
                var audio = pathGroup.FirstOrDefault(i => i.Type == IssueType.AudioLanguage);
                var subtitle = pathGroup.FirstOrDefault(i => i.Type == IssueType.SubtitleLanguage);
                if (audio is not null && subtitle is not null)
                {
                    combinedPairs[audio.Id] = subtitle;
                    combinedPartners.Add(subtitle.Id);
                }
            }
        }

        if (combinedPairs.Count > 0)
        {
            _logger.LogInformation("Combined-pass eligible: {Count} file(s) have both AudioLanguage and SubtitleLanguage queued — running as one ffmpeg per file instead of two.", combinedPairs.Count);
        }

        // Run tallies live here (not thread-local per fixer) so the after-run summary can call out the
        // dominant failure — e.g. "all 142 fixes failed with permission denied" — via a dashboard alert.
        var attempted = 0;
        var succeeded = 0;
        var failed = 0;
        var reasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);
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

            // Re-check the free-space floor between items — a single big remux can shift the bin
            // volume by many GB. If we cross the floor mid-run, drop the remaining queue rather
            // than fill the disk to empty.
            if (!config.DryRun && IsBinVolumeCriticallyFull(out var midRunFloorMsg))
            {
                _logger.LogInformation("Pausing fix run mid-queue: {Msg}", midRunFloorMsg);
                Api.Diagnostics.Record("FixTask.BinVolumeCriticallyFull", midRunFloorMsg);
                if (isManualRun)
                {
                    PauseReason = midRunFloorMsg;
                }

                break;
            }

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

            // Re-check status against the DB before applying — the queue snapshot is minutes to hours
            // old on big libraries, and a user who pressed "Ignore" mid-run expects the file to be
            // skipped. Without this, the fix runs on stale intent and the user's ignore is silently lost.
            var currentStatus = _db.GetIssueStatus(issue.Id);
            if (currentStatus != IssueStatus.Queued)
            {
                _logger.LogInformation("Skipping {Path}: status changed to {Status} since queue snapshot.", issue.Path, currentStatus?.ToString() ?? "gone");
                continue;
            }

            if (issue.Type == IssueType.MissingSubtitles && subtitleProviderQuotaExhausted)
            {
                subtitleQuotaSkipped++;
                continue;
            }

            // Combined-pass: skip subtitle issues that were paired to an audio issue on the same
            // path — they'll be processed together with the audio side in one ffmpeg pass. This
            // runs BEFORE the fixer lookup so the "no fixer" branch doesn't trigger.
            if (combinedPartners.Contains(issue.Id))
            {
                continue;
            }

            // Transcode-companion: skip Audio/SubtitleLanguage issues whose file also has a
            // transcode-family issue queued. The transcode's re-encode (see TranscodeFixer.BuildArgs)
            // already drops unwanted tracks via the language allow-list, so running a separate
            // TrackFixer pass is either wasted IO (if it runs first) or a "nothing to remove"
            // failure (if it runs second). The transcode issue's own iteration below writes the
            // companion's history row and flips its status via transcodeCompanions.
            if (transcodeCompanionIds.Contains(issue.Id))
            {
                continue;
            }

            var fixer = _fixers.FirstOrDefault(f => f.CanFix(issue.Type));
            if (fixer is null)
            {
                continue;
            }

            var itemIndex = i;
            var slot = 100.0 / queue.Count;
            progress.Report(itemIndex * slot);
            Plugin.CurrentActivityLabel = fixer.GetType().Name;
            Plugin.CurrentActivity = issue.Path;
            // Synchronous IProgress: Progress<T> queues callbacks and can reorder reports, leading to a jittery bar.
            var itemProgress = new SynchronousProgress(fraction => progress.Report((itemIndex + Math.Clamp(fraction, 0, 1)) * slot));

            attempted++;
            // Combined-pass detection is decided outside the try so both branches share the same
            // exception handling below (OperationCanceled, UnauthorizedAccess, IOException, etc.).
            var isCombinedPair = issue.Type == IssueType.AudioLanguage
                && combinedPairs.TryGetValue(issue.Id, out var subtitlePartner)
                && fixer is Fixers.TrackFixer;
            var partner = isCombinedPair ? combinedPairs[issue.Id] : null;
            try
            {
                Fixers.FixResult result;
                if (isCombinedPair)
                {
                    // One ffmpeg pass drops both categories at once; the caller records TWO history
                    // rows (one per source issue) below so both bin-tab and Issues-tab views are
                    // consistent.
                    result = await ((Fixers.TrackFixer)fixer!).FixCombinedAsync(issue, partner!, itemProgress, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    result = await RunFixWithSharingRetryAsync(fixer, issue, itemProgress, cancellationToken).ConfigureAwait(false);
                }

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

                // Second history row for the subtitle partner in a combined pass — same bin path,
                // same message, so both bin-tab rows link back to the same file and both
                // Issues-tab rows have their own record. Only emitted on success; failure surfaces
                // through the audio issue's history row already.
                if (isCombinedPair && result.Success && partner is not null)
                {
                    _db.AddHistory(new HistoryEntry
                    {
                        IssueId = partner.Id,
                        Type = partner.Type,
                        Path = partner.Path,
                        Action = result.Message,
                        BytesFreed = 0,
                        RecyclePath = result.RecyclePath,
                        FixedAtUtc = DateTime.UtcNow,
                        WasDryRun = result.WasDryRun,
                        Success = true
                    });
                    if (!result.WasDryRun)
                    {
                        _db.UpdateIssueStatus(partner.Id, IssueStatus.Fixed);
                    }
                }

                // Transcode-companion resolution: when a transcode-family issue with companion
                // audio/subtitle-language issues succeeds, each companion was resolved
                // implicitly by the re-encode's map-filter (TranscodeFixer.BuildArgs). Emit a
                // history row per companion so the Issues-tab audit trail is complete, and flip
                // the companion to Fixed. Matches the combined-pass symmetry above: history is
                // written on any success (including dry-run), status flip only on real runs so
                // the queue survives dry-run inspection.
                if (result.Success && transcodeCompanions.TryGetValue(issue.Id, out var claimedCompanions))
                {
                    foreach (var companion in claimedCompanions)
                    {
                        _db.AddHistory(new HistoryEntry
                        {
                            IssueId = companion.Id,
                            Type = companion.Type,
                            Path = companion.Path,
                            Action = "Resolved by transcode pass: " + result.Message,
                            BytesFreed = 0,
                            RecyclePath = result.RecyclePath,
                            FixedAtUtc = DateTime.UtcNow,
                            WasDryRun = result.WasDryRun,
                            Success = true
                        });
                        if (!result.WasDryRun)
                        {
                            _db.UpdateIssueStatus(companion.Id, IssueStatus.Fixed);
                        }
                    }
                }

                // Per-sidecar history rows so the Recycle Bin tab renders a Restore button next to each
                // recycled sidecar. Without these, external subtitle files / pre-strip audio originals
                // show "no history" and dead-end the user.
                if (result.Success && !result.WasDryRun && result.AdditionalRecycled is { Count: > 0 })
                {
                    foreach (var extra in result.AdditionalRecycled)
                    {
                        _db.AddHistory(new HistoryEntry
                        {
                            IssueId = issue.Id,
                            Type = issue.Type,
                            Path = extra.OriginalPath,
                            Action = extra.Action,
                            BytesFreed = 0,
                            RecyclePath = extra.RecyclePath,
                            FixedAtUtc = DateTime.UtcNow,
                            WasDryRun = false,
                            Success = true
                        });
                    }
                }

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
                    // F-206: only advance status when NOT in dry-run. During dry-run the row must stay Queued
                    // so the user can re-approve after inspecting; flipping to Fixed silently loses the queue.
                    if (IsStaleFailure(result.Message) && !config.DryRun)
                    {
                        _db.UpdateIssueStatus(issue.Id, IssueStatus.Fixed);
                    }

                    if (issue.Type == IssueType.MissingSubtitles && IsSubtitleProviderQuotaExhausted(result.Message))
                    {
                        subtitleProviderQuotaExhausted = true;
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
            catch (System.IO.FileNotFoundException ex)
            {
                // The file went missing between the scan and this fix. Same treatment as message-based
                // stale failures: mark Fixed so the 15-min retry stops chasing a ghost; next scan re-detects.
                RecordFailure("File went missing between scan and fix");
                _logger.LogInformation(ex, "File missing at fix time: {Path}", issue.Path);
                Api.Diagnostics.Record("FixTask.FileMissing", issue.Path + ": " + ex.Message);
                _db.AddHistory(new HistoryEntry
                {
                    IssueId = issue.Id,
                    Type = issue.Type,
                    Path = issue.Path,
                    Action = "Fix skipped — the file went missing between scan and fix. Re-scan to refresh.",
                    BytesFreed = 0,
                    FixedAtUtc = DateTime.UtcNow,
                    WasDryRun = false,
                    Success = false
                });
                _db.UpdateIssueStatus(issue.Id, IssueStatus.Fixed);
            }
            catch (System.IO.DirectoryNotFoundException ex)
            {
                RecordFailure("File went missing between scan and fix");
                _logger.LogInformation(ex, "Directory missing at fix time: {Path}", issue.Path);
                Api.Diagnostics.Record("FixTask.FileMissing", issue.Path + ": " + ex.Message);
                _db.AddHistory(new HistoryEntry
                {
                    IssueId = issue.Id,
                    Type = issue.Type,
                    Path = issue.Path,
                    Action = "Fix skipped — the containing folder went missing between scan and fix. Re-scan to refresh.",
                    BytesFreed = 0,
                    FixedAtUtc = DateTime.UtcNow,
                    WasDryRun = false,
                    Success = false
                });
                _db.UpdateIssueStatus(issue.Id, IssueStatus.Fixed);
            }
            catch (System.IO.IOException ex) when (IsSharingViolation(ex))
            {
                // RunFixWithSharingRetryAsync already retried 3× over ~7.5s. If it still throws, whatever
                // holds the file is long-lived — surface as a lock error, not a "disk error".
                RecordFailure("File was held open by another process (likely Jellyfin trickplay / chapter thumbs / active playback)");
                _logger.LogWarning(ex, "File held open after retries: {Path}", issue.Path);
                Api.Diagnostics.Record("FixTask.FileLocked", issue.Path + ": still held open after 3 retries (~7.5s). " + ex.Message);
                _db.AddHistory(new HistoryEntry
                {
                    IssueId = issue.Id,
                    Type = issue.Type,
                    Path = issue.Path,
                    Action = "Fix failed — file was locked by another process even after retrying. " + ex.Message,
                    BytesFreed = 0,
                    FixedAtUtc = DateTime.UtcNow,
                    WasDryRun = false,
                    Success = false
                });
            }
            catch (System.IO.IOException ex) when (IsDiskFull(ex))
            {
                RecordFailure("Not enough free disk space");
                _logger.LogWarning(ex, "Disk full while fixing {Path}", issue.Path);
                Api.Diagnostics.Record("FixTask.DiskFull", issue.Path + ": " + ex.Message);
                _db.AddHistory(new HistoryEntry
                {
                    IssueId = issue.Id,
                    Type = issue.Type,
                    Path = issue.Path,
                    Action = "Fix failed — the drive ran out of space mid-fix. Free some space and it'll retry on the next run.",
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
                // Write a History row for unexpected exceptions too — without this the user sees the
                // diagnostic but has no audit trail of which issue actually failed.
                _db.AddHistory(new HistoryEntry
                {
                    IssueId = issue.Id,
                    Type = issue.Type,
                    Path = issue.Path,
                    Action = "Fix failed — unexpected error: " + ex.Message,
                    BytesFreed = 0,
                    FixedAtUtc = DateTime.UtcNow,
                    WasDryRun = false,
                    Success = false
                });
            }

            progress.Report((i + 1) * 100.0 / queue.Count);
        }

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
        }

        if (subtitleQuotaSkipped > 0)
        {
            _logger.LogInformation("Subtitle provider download quota reached; skipped {Count} queued MissingSubtitles issue(s). They stay queued and retry on the next run after quota reset.", subtitleQuotaSkipped);
        }

        Plugin.CurrentActivity = null;
        Plugin.CurrentActivityLabel = null;

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

    // Windows sharing/lock violations (HRESULTs 0x80070020, 0x80070021) show up when Jellyfin's own
    // subsystems still hold a read handle on a library file we're about to move/delete — trickplay
    // generation, chapter thumbnails, cover-art extraction, an active playback session, or a
    // sibling MediaDash scanner's ffprobe whose child process handle the OS hasn't finalised yet.
    // Almost always clears within a second or two. Delete / Move / rename are all idempotent on
    // failure (source still there, target absent), so re-running the fixer's FixAsync is safe.
    // ponytail: three tries with progressive backoff. If it still fails, the file really is held
    // by something long-lived (a running transcode) — surface the IOException as before.
    private static async Task<FixResult> RunFixWithSharingRetryAsync(IFixer fixer, Issue issue, IProgress<double> progress, CancellationToken cancellationToken)
    {
        int[] delaysMs = [500, 2000, 5000];
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await fixer.FixAsync(issue, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (System.IO.IOException ex) when (attempt < delaysMs.Length && IsSharingViolation(ex))
            {
                await Task.Delay(delaysMs[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsSharingViolation(System.IO.IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        // 32 = ERROR_SHARING_VIOLATION, 33 = ERROR_LOCK_VIOLATION (Windows). Linux EBUSY (16) also
        // manifests as IOException when another process holds an exclusive lock (rare but real on
        // networked filesystems like SMB and cifs), so include it too.
        return code == 32 || code == 33 || code == 16;
    }

    // Distinguishes real ENOSPC / ERROR_DISK_FULL from other IOExceptions so the Errors tab shows
    // "drive full" only when it actually is. Users kept seeing "disk error" on files that were
    // really sharing violations (post-retry exhaustion) or missing paths (Sonarr/Radarr moves).
    internal static bool IsDiskFull(System.IO.IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        // Windows: 112 ERROR_DISK_FULL, 39 ERROR_HANDLE_DISK_FULL. Linux: 28 ENOSPC.
        return code == 112 || code == 39 || code == 28;
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

    // Whether the fixer's reason indicates the subtitle provider is out of downloads for now.
    // Matches OpenSubtitles' free-tier "download limit reached" and API "download quota" wording;
    // both signal that every remaining MissingSubtitles issue in this run will fail the same way.
    // ponytail: string match, upgrade to a provider-typed error only if a second provider joins.
    internal static bool IsSubtitleProviderQuotaExhausted(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        return message.Contains("download limit reached", StringComparison.OrdinalIgnoreCase)
            || message.Contains("download quota", StringComparison.OrdinalIgnoreCase);
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
    /// Groups every queued transcode-family issue (Quality / HeavyTranscode / FailedTranscode)
    /// with any AudioLanguage / SubtitleLanguage issue on the same path so those track issues
    /// can be resolved as companions of the transcode. TranscodeFixer.BuildArgs already filters
    /// mapped audio + subtitle streams by the configured language allow-lists during the
    /// re-encode, so the unwanted tracks are dropped incidentally and no separate remux is
    /// needed. Returns the transcode-issue-id → companion-list map; callers derive the
    /// companion-id set from the values. If a path has multiple transcode-family issues (rare
    /// but possible: Quality + FailedTranscode co-detected), the first by natural queue order
    /// wins — the second stays a standalone entry and picks up the tracks on a later run if the
    /// first left them behind.
    /// </summary>
    /// <param name="queue">The current fix-run queue after Off-type filtering and rank ordering.</param>
    /// <returns>Map of transcode issue id → companion audio/subtitle issues on the same path.</returns>
    internal static Dictionary<long, List<Issue>> BuildTranscodeCompanions(IReadOnlyList<Issue> queue)
    {
        var result = new Dictionary<long, List<Issue>>();
        var pathGroups = queue
            .Where(i => i.Type is IssueType.Quality
                                  or IssueType.HeavyTranscode
                                  or IssueType.FailedTranscode
                                  or IssueType.AudioLanguage
                                  or IssueType.SubtitleLanguage)
            .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var pathGroup in pathGroups)
        {
            var transcode = pathGroup.FirstOrDefault(i => i.Type is IssueType.Quality
                                                                   or IssueType.HeavyTranscode
                                                                   or IssueType.FailedTranscode);
            if (transcode is null)
            {
                continue;
            }

            var companions = pathGroup
                .Where(i => i.Type is IssueType.AudioLanguage or IssueType.SubtitleLanguage)
                .ToList();
            if (companions.Count == 0)
            {
                continue;
            }

            result[transcode.Id] = companions;
        }

        return result;
    }

    private static int FixerRank(Data.IssueType type)
    {
        // User override: if config.FixerOrder is set (via the Overview "Fix order" dialog), the
        // user's ordering wins. Unlisted types fall to the end via DefaultFixerRank so a new fixer
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
        Data.IssueType.Misplaced => 4,
        Data.IssueType.Ungrouped => 5,
        Data.IssueType.MissingSubtitles => 6,
        Data.IssueType.SubtitleLanguage => 7,
        Data.IssueType.AudioLanguage => 8,
        Data.IssueType.Quality => 9,
        Data.IssueType.HeavyTranscode => 10,
        Data.IssueType.FailedTranscode => 11,
        Data.IssueType.SubtitleFonts => 12,
        Data.IssueType.EmbeddedCoverArt => 13,
        Data.IssueType.CorruptNfo => 14,
        Data.IssueType.CorruptArtwork => 15,
        Data.IssueType.Stale => 16,
        Data.IssueType.LargeTrickplay => 17,
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
