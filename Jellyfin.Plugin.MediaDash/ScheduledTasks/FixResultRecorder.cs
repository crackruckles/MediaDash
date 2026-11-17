using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>
/// Persists the effects of one completed fix operation: history fan-out (primary + resolved
/// companions + recycled sidecars) plus conditional <c>Queued → Fixed</c> status transitions,
/// all inside one short DB transaction.
///
/// Contract:
/// <list type="bullet">
///   <item>Primary row ALWAYS gets a history entry — success or failure — because history reports
///     reality, not authorised intent.</item>
///   <item>Companion history + status transitions run only for ids present in
///     <see cref="FixExecutionResult.ResolvedIssueIds"/>. Revoked/dismissed companions never get a
///     "successfully resolved" row and never have their status flipped.</item>
///   <item>Status transitions run in real runs only. Dry-run success writes history (with
///     <c>WasDryRun = true</c>) but leaves the queue untouched (F-206 gate).</item>
///   <item>The DB write is one transaction. If it throws AFTER the fixer has committed file
///     mutations, the recorder emits a <c>FixResultRecorder.PersistFailed</c> diagnostic naming the
///     issue + path and rethrows. The plan explicitly forbids auto-replay — an operator must
///     reconcile manually.</item>
/// </list>
/// </summary>
public sealed class FixResultRecorder
{
    // Only Queued rows may transition to Fixed by the fixer. Dismissed / Detected / already-Fixed
    // rows stay put (they represent user intent that outlives a mid-run state race).
    private static readonly IReadOnlyCollection<IssueStatus> FixedFromQueued = new[] { IssueStatus.Queued };

    // After a Failed+InsufficientSavings outcome flips Queued → Fixed via RecordFixOutcome, we
    // promote the row to Dismissed so the scanner's re-insert NOT EXISTS clause (which suppresses
    // only Queued/Dismissed) stops re-detecting the same file on every rescan. The user can
    // un-dismiss from the Issues tab if they lower the savings threshold and want to retry.
    private static readonly IReadOnlyCollection<IssueStatus> DismissedFromFixed = new[] { IssueStatus.Fixed };

    private readonly MediaDashDb _db;
    private readonly ILogger<FixResultRecorder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FixResultRecorder"/> class.
    /// </summary>
    /// <param name="db">Plugin database.</param>
    /// <param name="logger">Diagnostics logger.</param>
    public FixResultRecorder(MediaDashDb db, ILogger<FixResultRecorder> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Records history and applies conditional status transitions for one completed operation.
    /// </summary>
    /// <param name="plan">The planner's operation shape. Primary is always recorded; companions are recorded only when they are also in <paramref name="execution"/>.ResolvedIssueIds.</param>
    /// <param name="execution">Executor's outcome. Drives success/failure branching and the set of ids that receive success rows + status transitions.</param>
    /// <param name="fixedAtUtc">Wall-clock completion time to stamp on every emitted history row. A single value keeps a fan-out cluster grouped in the History tab.</param>
    internal void Record(PlannedFix plan, FixExecutionResult execution, DateTime fixedAtUtc)
    {
        var result = execution.Result;
        var resolvedIds = execution.ResolvedIssueIds;
        var resolvedSet = resolvedIds is ISet<long> asSet ? asSet : new HashSet<long>(resolvedIds);

        var historyRows = BuildHistoryRows(plan, execution, resolvedSet, fixedAtUtc);
        var repairFailed = !result.WasDryRun && result.Reason == Fixers.FixReason.RepairFailed
            && plan.Primary.Type == IssueType.Playability;

        // Dry-run gate: F-206 — a dry-run leaves the queue exactly as it found it. Every id in
        // ResolvedIssueIds still writes history above (with WasDryRun = true) but the status
        // transition slot is empty so the user can re-approve after inspecting the preview.
        IReadOnlyCollection<long> toTransition = result.WasDryRun
            ? Array.Empty<long>()
            : repairFailed ? new[] { plan.Primary.Id } : resolvedIds;

        IReadOnlyDictionary<long, bool> transitions;
        try
        {
            transitions = _db.RecordFixOutcome(historyRows, toTransition, FixedFromQueued, repairFailed ? IssueStatus.Detected : IssueStatus.Fixed, repairFailed ? plan.Primary : null);
        }
        catch (Exception ex)
        {
            // Fixer already committed the file mutation before we ever entered this method (see
            // VerifiedReplacementService protocol). A DB failure here means "the world changed
            // but our books say it didn't" — an operator needs to reconcile. Never auto-replay.
            Api.Diagnostics.Record(
                "FixResultRecorder.PersistFailed",
                "Fix outcome persistence failed for issue " + execution.PrimaryIssueId
                    + " on " + plan.Primary.Path
                    + ". The file mutation (if any) has already committed; the DB record is missing. "
                    + "Do NOT re-run the fix — it is not idempotent. Reconcile manually. "
                    + ex.Message);
            _logger.LogError(
                ex,
                "Recorder persistence failed for issue {IssueId} on {Path}. Reconciliation required.",
                execution.PrimaryIssueId,
                plan.Primary.Path);
            throw;
        }

        // Per-id conflict log matches the pre-refactor MarkFixed log so operator dashboards keep
        // reading "status changed mid-run" the way they do today.
        foreach (var pair in transitions)
        {
            if (!pair.Value)
            {
                _logger.LogInformation(
                    "Skipped Fixed-status write for issue {Id}: status changed mid-run (likely dismissed or reverted from the Issues tab).",
                    pair.Key);
            }
        }

        // Auto-dismiss on Failed+InsufficientSavings so an identical rescan doesn't re-queue and
        // re-encode the same input. Skip dry-run: no real transition happened above, and dry-run
        // must never mutate persistent state. Best-effort: if this call throws, the retry loop
        // reappears on the next scan (the pre-fix behaviour), which is safe — never worse.
        if (!result.WasDryRun
            && result.Outcome == Fixers.FixOutcome.Failed
            && result.Reason == Fixers.FixReason.InsufficientSavings
            && transitions.TryGetValue(execution.PrimaryIssueId, out var flipped)
            && flipped)
        {
            try
            {
                var dismissResult = _db.TryTransitionIssueStatus(execution.PrimaryIssueId, DismissedFromFixed, IssueStatus.Dismissed);
                if (dismissResult != StatusTransitionResult.Applied)
                {
                    _logger.LogInformation(
                        "Auto-dismiss on InsufficientSavings for issue {Id} did not apply ({Result}); the retry-suppression relies on the Fixed→Dismissed hop.",
                        execution.PrimaryIssueId,
                        dismissResult);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Auto-dismiss on InsufficientSavings for issue {Id} failed; next scan may re-queue the same encode.",
                    execution.PrimaryIssueId);
            }
        }
    }

    private static List<HistoryEntry> BuildHistoryRows(
        PlannedFix plan,
        FixExecutionResult execution,
        ISet<long> resolvedSet,
        DateTime fixedAtUtc)
    {
        var result = execution.Result;
        var rows = new List<HistoryEntry>(1 + plan.Companions.Count + (result.AdditionalRecycled?.Count ?? 0));

        // Primary: always emitted. Bytes are only attributed on a real-run success — dry-run and
        // failure both report zero to keep the Overview "GB freed" totals accurate. A failed
        // operation's message text still lands here so the History tab can explain the failure.
        rows.Add(new HistoryEntry
        {
            IssueId = plan.Primary.Id,
            Type = plan.Primary.Type,
            Path = plan.Primary.Path,
            Action = result.Message,
            BytesFreed = result.Success && !result.WasDryRun ? result.BytesFreed : 0,
            SavedBytes = result.Success && !result.WasDryRun ? result.SavedBytes : 0,
            RecyclePath = result.RecyclePath,
            MoveOperationId = result.MoveOperationId,
            FixedAtUtc = fixedAtUtc,
            WasDryRun = result.WasDryRun,
            Success = result.Success,
        });

        // Companions: only for ids the executor resolved. A combined-pass partner whose subtitle
        // was dismissed mid-run is absent from ResolvedIssueIds and gets no row. A failed transcode
        // has an empty resolved set for its companions and therefore emits zero companion rows.
        // The action prefix follows the plan's relationship shape: "Resolved by transcode pass: "
        // for TranscodeWithTracks (matches the pre-refactor grep guard in
        // FixTaskTranscodeCompanionTests.FixTaskSourceStillWritesResolvedByTranscodeHistory);
        // verbatim message for combined-pair partners (mirrors the pre-refactor combined-pair block).
        foreach (var companion in plan.Companions)
        {
            if (!resolvedSet.Contains(companion.Id))
            {
                continue;
            }

            var action = plan.Kind == PlannedFixKind.TranscodeWithTracks
                ? "Resolved by transcode pass: " + result.Message
                : result.Message;

            rows.Add(new HistoryEntry
            {
                IssueId = companion.Id,
                Type = companion.Type,
                Path = companion.Path,
                Action = action,
                BytesFreed = 0,
                RecyclePath = result.RecyclePath,
                FixedAtUtc = fixedAtUtc,
                WasDryRun = result.WasDryRun,
                Success = true,
            });
        }

        // Sidecars: real-run success only. Each recycled external file (subtitle sidecar,
        // pre-strip original) gets its own row so the Recycle Bin tab can render a Restore button
        // per file. BytesFreed stays on the primary — sidecars carry zero savings to avoid
        // double-counting.
        if (result.Success && !result.WasDryRun && result.AdditionalRecycled is { Count: > 0 })
        {
            foreach (var extra in result.AdditionalRecycled)
            {
                rows.Add(new HistoryEntry
                {
                    IssueId = plan.Primary.Id,
                    Type = plan.Primary.Type,
                    Path = extra.OriginalPath,
                    Action = extra.Action,
                    BytesFreed = 0,
                    RecyclePath = extra.RecyclePath,
                    FixedAtUtc = fixedAtUtc,
                    WasDryRun = false,
                    Success = true,
                });
            }
        }

        return rows;
    }
}
