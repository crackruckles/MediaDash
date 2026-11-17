using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>
/// Owns per-operation execution for one <see cref="PlannedFix"/>: the last-mile consent recheck
/// against the current config snapshot, combined-pair partner status refresh, fixer dispatch
/// (including the sharing-violation retry loop), and exception → typed <see cref="FixResult"/>
/// translation. Returns a <see cref="FixExecutionResult"/> the caller hands to
/// <see cref="FixResultRecorder"/>. Extracted from <see cref="FixTask"/> in 04-C so the task
/// itself stays a scheduling adapter — schedule/window/idle/task metadata plus one loop over
/// planned operations.
///
/// Contract:
/// <list type="bullet">
///   <item>Returns <c>null</c> when the operation should be silently skipped (no fixer registered,
///     primary's status changed mid-run, policy re-check refused, subtitle quota exhausted). Silent
///     skips write no history and do not tick failure counters.</item>
///   <item>Returns a populated <see cref="FixExecutionResult"/> for every attempted operation —
///     success or failure. The recorder's job is atomic persistence; the executor's job is to
///     produce a truthful (Result, PrimaryIssueId, ResolvedIssueIds) triple.</item>
///   <item>Never catches <see cref="OperationCanceledException"/> — cancellation propagates so
///     the caller can distinguish "user cancelled" from "operation succeeded/failed".</item>
///   <item>Sharing violations retry three times with backoff [500ms, 2000ms, 5000ms]. The delay
///     source is <see cref="TimeProvider"/> so tests can advance a fake clock instead of waiting
///     ~7.5 s wall time.</item>
/// </list>
/// </summary>
public sealed class FixExecutor
{
    // Move-compatible with the pre-refactor FixTask.RunFixWithSharingRetryAsync — same count,
    // same backoff ordering. Changing these on the fly re-tunes every fixer's lock behaviour;
    // ship a targeted knob if that's ever needed.
    private static readonly int[] SharingRetryDelaysMs = new[] { 500, 2000, 5000 };

    private readonly IssueStore _issues;
    private readonly IEnumerable<IFixer> _fixers;
    private readonly IPluginConfigurationProvider _configProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FixExecutor> _logger;
    private readonly Fixers.ProtectionService _protection;
    private readonly MediaMoveStore? _moves;

    /// <summary>
    /// Initializes a new instance of the <see cref="FixExecutor"/> class.
    /// </summary>
    /// <param name="issues">Issue-table store (05-C.2) — read only for the per-item status recheck.</param>
    /// <param name="fixers">Registered fixers, used for <see cref="IFixer.CanFix"/> dispatch.</param>
    /// <param name="configProvider">Configuration snapshot provider — the executor re-reads current config per operation so a mid-run save takes effect on the next item.</param>
    /// <param name="timeProvider">Clock used for sharing-violation retry delays. Injected so tests can fake-tick.</param>
    /// <param name="protection">Never-modify protection gate. Roadmap i-110-protected-media.</param>
    /// <param name="logger">Diagnostics logger.</param>
    /// <param name="moves">Durable move reservations checked before dispatch.</param>
    public FixExecutor(
        IssueStore issues,
        IEnumerable<IFixer> fixers,
        IPluginConfigurationProvider configProvider,
        TimeProvider timeProvider,
        Fixers.ProtectionService protection,
        ILogger<FixExecutor> logger,
        MediaMoveStore? moves = null)
    {
        _issues = issues;
        _fixers = fixers;
        _configProvider = configProvider;
        _timeProvider = timeProvider;
        _protection = protection;
        _logger = logger;
        _moves = moves;
    }

    /// <summary>
    /// Runs one planned fix operation. See the class-level contract for the null / non-null
    /// return semantics.
    /// </summary>
    /// <param name="plan">The plan produced by <see cref="FixPlanBuilder"/>.</param>
    /// <param name="withinEnabledLibrary">Caller-computed library-scope check for the primary path (typically <c>LibraryGuard.IsInEnabledLocations(primary.Path, currentEnabledLocations)</c>). Passed in rather than recomputed so the executor stays free of the <c>ILibraryManager</c> DI graph — massive testability win, zero behaviour change.</param>
    /// <param name="itemProgress">Per-item progress sink — the fixer reports 0–1 as it runs.</param>
    /// <param name="cancellationToken">Cancellation token propagated to the fixer and the retry-delay loop.</param>
    /// <returns>An execution result, or null when the operation was silently skipped.</returns>
    internal Task<FixExecutionResult?> ExecuteAsync(
        PlannedFix plan,
        bool withinEnabledLibrary,
        IProgress<double> itemProgress,
        CancellationToken cancellationToken)
        => ExecuteAsync(plan, withinEnabledLibrary, itemProgress, silentSkipTally: null, cancellationToken);

    /// <summary>
    /// Overload that tallies silent-skip reasons so <see cref="FixTask"/> can surface a
    /// NoWorkReason when every queue item hits a per-item recheck (status changed, policy
    /// refused, protected, no fixer registered). Without this, a non-empty queue of items
    /// that all silently skip produces "Completed after 0 seconds" with zero explanation —
    /// the exact failure mode reported on #81 upgrades.
    /// </summary>
    /// <param name="plan">The plan produced by <see cref="FixPlanBuilder"/>.</param>
    /// <param name="withinEnabledLibrary">Caller-computed library-scope check.</param>
    /// <param name="itemProgress">Per-item progress sink.</param>
    /// <param name="silentSkipTally">Optional sink for silent-skip reason tallies (key = reason label, value = count). Null disables tallying — tests pass null to keep existing assertions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="onFixStarted">Captures affected libraries before a real fixer can move or delete its source.</param>
    /// <returns>An execution result, or null when the operation was silently skipped.</returns>
    internal async Task<FixExecutionResult?> ExecuteAsync(
        PlannedFix plan,
        bool withinEnabledLibrary,
        IProgress<double> itemProgress,
        IDictionary<string, int>? silentSkipTally,
        CancellationToken cancellationToken,
        Action<Issue>? onFixStarted = null)
    {
        // FU-4 (2026-09-19) preserved: re-read the CURRENT config per item. The run-start
        // snapshot can drift on a long queue — user disables a fixer / unticks a library / turns
        // Dry-run on halfway through, and the executor honors the change on the next item.
        // Rechecking here (rather than in FixTask) keeps FixTask's outer config reference stable
        // for the scheduling gates (window, bin cap, bin free space) that must not flicker.
        var currentConfig = _configProvider.GetSnapshot();

        // Primary consent recheck. The queue snapshot is minutes to hours old on big libraries.
        var primary = plan.Primary;
        var current = _issues.GetIssue(primary.Id);
        var currentStatus = current?.Status;
        if (currentStatus != IssueStatus.Queued)
        {
            _logger.LogInformation(
                "Skipping {Path}: status changed to {Status} since queue snapshot.",
                primary.Path,
                currentStatus?.ToString() ?? "gone");
            TallySilentSkip(silentSkipTally, "status changed between approval and run");
            return null;
        }

        if (current!.RequiresRescan)
        {
            TallySilentSkip(silentSkipTally, "finding requires a fresh scan after a move");
            return null;
        }

        if (current.IsReportOnly || plan.Companions.Any(companion => _issues.GetIssue(companion.Id)?.IsReportOnly == true))
        {
            TallySilentSkip(silentSkipTally, "this finding has no available repair");
            return null;
        }

        // Policy recheck against the CURRENT snapshot. 02-B: same evaluator as the run-start
        // filter, so refusal reasons stay consistent (TypeDisabled vs LibraryDisabled vs
        // ManualApprovalRequired are distinguishable in the operator log).
        var recheck = FixPolicyEvaluator.Evaluate(
            currentConfig,
            primary.Type,
            IssueStatus.Queued,
            FixIntent.Approved,
            withinEnabledLibrary: withinEnabledLibrary,
            autoQueueRestoredBlocked: false,
            hasApprovedCompanionOnPath: false);
        if (!recheck.Allowed)
        {
            _logger.LogInformation(
                "Skipping {Path}: policy re-check refused with {Reason} (was runnable at run-start snapshot).",
                primary.Path,
                recheck.Reason);
            TallySilentSkip(silentSkipTally, "settings changed mid-run (" + recheck.Reason + ")");
            return null;
        }

        // Roadmap i-110-protected-media: fix-time recheck of the persistent Never-modify list.
        // Protection added between plan build and now must stop the fixer. Silent skip keeps the
        // Issue queued so removing protection later re-tries automatically.
        if (_protection.IsFixerBlocked(primary.Path, primary.Type))
        {
            _logger.LogInformation(
                "Skipping {Path}: protected against {Type} fixer.",
                primary.Path,
                primary.Type);
            TallySilentSkip(silentSkipTally, "protected from modification");
            return null;
        }

        // Fixer dispatch — silent skip when nothing handles the type (matches pre-refactor
        // "if (fixer is null) continue").
        if (_moves is not null && (_moves.HasActiveConflict(primary.Path) || _moves.HasActiveItemConflict(primary.ItemId)))
        {
            return new FixExecutionResult(
                FixResult.Refused(FixReason.MoveNeedsReview, "This item or path is reserved by an unfinished move. Review the recorded move before running another fix on its files."),
                primary.Id,
                []);
        }

        var fixer = _fixers.FirstOrDefault(f => f.CanFix(primary.Type));
        if (fixer is null)
        {
            TallySilentSkip(silentSkipTally, "no fixer registered for this issue type");
            return null;
        }

        Plugin.CurrentActivityLabel = fixer.GetType().Name;
        Plugin.CurrentActivity = primary.Path;

        // Combined-pass partner recheck. The plan carries the partner; the executor decides
        // whether the pair is still valid at execution time. If the subtitle was dismissed
        // between plan build and now, fall back to audio-only through the same TrackFixer path.
        Issue? partner = null;
        var isCombinedPair = false;
        if (plan.Kind == PlannedFixKind.TrackPair && plan.Companions.Count > 0 && fixer is TrackFixer)
        {
            partner = plan.Companions[0];
            var partnerStatus = _issues.GetIssueStatus(partner.Id);
            if (partnerStatus == IssueStatus.Queued
                && TranscodeFixer.IsTypeAuthorized(currentConfig, partner.Type)
                && !_protection.IsFixerBlocked(partner.Path, partner.Type))
            {
                isCombinedPair = true;
            }
            else
            {
                _logger.LogInformation(
                    "Combined pair audio {AudioId} + subtitle {PartnerId}: partner is no longer authorized — running audio-only fix instead.",
                    primary.Id,
                    partner.Id);
                partner = null;
            }
        }

        // Dispatch. Exception translation lives in DispatchAsync — one place per catch shape so
        // the message wording stays consistent with the pre-refactor Diagnostics.Record entries.
        if (!currentConfig.DryRun)
        {
            onFixStarted?.Invoke(primary);
        }

        using var diagnostics = RunDiagnostics.Start(_logger, "fix", fixer.GetType().Name);
        var outcome = "Failed";
        int? resolvedCount = null;
        try
        {
            var result = await DispatchAsync(fixer, primary, partner, isCombinedPair, diagnostics?.Observe(itemProgress) ?? itemProgress, cancellationToken).ConfigureAwait(false);
            var resolvedIds = ComputeResolvedIds(plan, result, currentConfig, isCombinedPair, partner);
            outcome = result.WasDryRun ? "Preview" : result.Outcome != FixOutcome.Unspecified ? result.Outcome.ToString() : result.Success ? "Applied" : "Failed";
            resolvedCount = result.Success && !result.WasDryRun ? resolvedIds.Length : 0;
            return new FixExecutionResult(result, primary.Id, resolvedIds);
        }
        catch (OperationCanceledException)
        {
            outcome = "Cancelled";
            throw;
        }
        finally
        {
            diagnostics?.Finish(outcome, null, null, isCombinedPair ? 2 : 1, resolvedCount);
        }
    }

    private async Task<FixResult> DispatchAsync(
        IFixer fixer,
        Issue primary,
        Issue? partner,
        bool isCombinedPair,
        IProgress<double> itemProgress,
        CancellationToken cancellationToken)
    {
        try
        {
            if (isCombinedPair && partner is not null && fixer is TrackFixer trackFixer)
            {
                return await trackFixer.FixCombinedAsync(
                    primary,
                    partner,
                    itemProgress,
                    () => _issues.GetIssueStatus(primary.Id) == IssueStatus.Queued && _issues.GetIssueStatus(partner.Id) == IssueStatus.Queued,
                    cancellationToken).ConfigureAwait(false);
            }

            return await RunFixWithSharingRetryAsync(fixer, primary, itemProgress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Never label cancellation as corruption. Propagate so the scheduler distinguishes
            // "user pressed Stop" from every other exit path. Per plan §04-C.
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Permission denied fixing {Path}", primary.Path);
            var message = "Jellyfin lacks write access to " + primary.Path + ". Check that the file (and its folder) is owned by or read+writable by the user Jellyfin runs as (typically 'jellyfin' on Linux).";
            Api.Diagnostics.Record("FixTask.PermissionDenied", message);
            return FixResult.Fail(
                FixReason.AccessDenied,
                "Fix failed — permission denied. " + primary.Path + " isn't writable by the Jellyfin user.",
                ex.Message);
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogInformation(ex, "File missing at fix time: {Path}", primary.Path);
            Api.Diagnostics.Record("FixTask.FileMissing", primary.Path + ": " + ex.Message);
            return FixResult.Fail(
                FixReason.SourceMissing,
                "Fix skipped — the file went missing between scan and fix. Re-scan to refresh.",
                ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            _logger.LogInformation(ex, "Directory missing at fix time: {Path}", primary.Path);
            Api.Diagnostics.Record("FixTask.FileMissing", primary.Path + ": " + ex.Message);
            return FixResult.Fail(
                FixReason.SourceMissing,
                "Fix skipped — the containing folder went missing between scan and fix. Re-scan to refresh.",
                ex.Message);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            _logger.LogWarning(ex, "File held open after retries: {Path}", primary.Path);
            Api.Diagnostics.Record("FixTask.FileLocked", primary.Path + ": still held open after 3 retries (~7.5s). " + ex.Message);
            return FixResult.Fail(
                FixReason.FileLocked,
                "Fix failed — file was locked by another process even after retrying. " + ex.Message,
                ex.Message);
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            _logger.LogWarning(ex, "Disk full while fixing {Path}", primary.Path);
            Api.Diagnostics.Record("FixTask.DiskFull", primary.Path + ": " + ex.Message);
            return FixResult.Fail(
                FixReason.InsufficientSpace,
                "Fix failed — the drive ran out of space mid-fix. Free some space and it'll retry on the next run.",
                ex.Message);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "I/O error fixing {Path}", primary.Path);
            Api.Diagnostics.Record("FixTask.IOError", primary.Path + ": " + ex.Message);
            return FixResult.Fail(
                FixReason.Unknown,
                "Fix failed — " + ex.Message,
                ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fixing {Path}", primary.Path);
            Api.Diagnostics.Record("FixTask", primary.Path + ": " + ex.Message);
            return FixResult.Fail(
                FixReason.Unknown,
                "Fix failed — unexpected error: " + ex.Message,
                ex.ToString());
        }
    }

    private async Task<FixResult> RunFixWithSharingRetryAsync(
        IFixer fixer,
        Issue issue,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await fixer.FixAsync(issue, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex) when (attempt < SharingRetryDelaysMs.Length && IsSharingViolation(ex))
            {
                // TimeProvider-aware Task.Delay so tests can advance a FakeTimeProvider instead
                // of paying ~7.5 s wall time per exhausted retry. Real deployments get real
                // clocks and the same backoff behaviour as pre-04-C.
                await Task.Delay(TimeSpan.FromMilliseconds(SharingRetryDelaysMs[attempt]), _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private long[] ComputeResolvedIds(
        PlannedFix plan,
        FixResult result,
        PluginConfiguration currentConfig,
        bool isCombinedPair,
        Issue? partner)
    {
        if (result.Success)
        {
            if (plan.Kind == PlannedFixKind.TrackPair && isCombinedPair && partner is not null)
            {
                return new[] { plan.Primary.Id, partner.Id };
            }

            if (plan.Kind == PlannedFixKind.TranscodeWithTracks)
            {
                return new[] { plan.Primary.Id }.Concat(plan.Companions.Where(c => _issues.GetIssueStatus(c.Id) == IssueStatus.Queued).Select(c => c.Id)).ToArray();
            }

            return new[] { plan.Primary.Id };
        }

        // CompleteLegacy failure branch. F-206: gate on config.DryRun to preserve the pre-04-C
        // exact-equivalence (a dry-run failure never advances status even if the reason would
        // otherwise be terminal).
        if (!currentConfig.DryRun
            && FixResultPolicy.GetQueueDisposition(result.Outcome, result.Reason) == QueueDisposition.CompleteLegacy)
        {
            return new[] { plan.Primary.Id };
        }

        return Array.Empty<long>();
    }

    private static void TallySilentSkip(IDictionary<string, int>? tally, string reason)
    {
        if (tally is null)
        {
            return;
        }

        tally.TryGetValue(reason, out var count);
        tally[reason] = count + 1;
    }

    // Windows sharing/lock violations (HRESULTs 0x80070020, 0x80070021) show up when Jellyfin's
    // own subsystems still hold a read handle on a library file we're about to move/delete —
    // trickplay generation, chapter thumbnails, cover-art extraction, an active playback session,
    // or a sibling MediaDash scanner's ffprobe whose child process handle the OS hasn't finalised
    // yet. Almost always clears within a second or two. Delete / Move / rename are all idempotent
    // on failure (source still there, target absent), so re-running the fixer's FixAsync is safe.
    internal static bool IsSharingViolation(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        // 32 = ERROR_SHARING_VIOLATION, 33 = ERROR_LOCK_VIOLATION (Windows). Linux EBUSY (16) also
        // manifests as IOException when another process holds an exclusive lock (rare but real on
        // networked filesystems like SMB and cifs), so include it too.
        return code == 32 || code == 33 || code == 16;
    }

    // Distinguishes real ENOSPC / ERROR_DISK_FULL from other IOExceptions so the Errors tab shows
    // "drive full" only when it actually is.
    internal static bool IsDiskFull(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        // Windows: 112 ERROR_DISK_FULL, 39 ERROR_HANDLE_DISK_FULL. Linux: 28 ENOSPC.
        return code == 112 || code == 39 || code == 28;
    }
}
