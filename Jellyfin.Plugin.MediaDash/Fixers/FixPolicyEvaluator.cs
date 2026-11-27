using System;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Pure decision-only function that codifies the effective operation policy for a fix
/// candidate. Replaces the scattered <c>config.GetFixMode(...) != FixMode.Off</c> +
/// <c>FixerDisabled.Contains(...)</c> + <c>IsInEnabledLocations</c> checks that used to live
/// inline across <see cref="ScheduledTasks.FixTask"/>, <see cref="TranscodeFixer"/> and
/// <see cref="TrackFixer"/>.
///
/// Design invariants:
/// <list type="bullet">
///   <item>No <c>Plugin.Instance</c> reads. Every input is passed explicitly by the caller.</item>
///   <item>No I/O, no DB queries, no logging. Callers precompute the boolean inputs.</item>
///   <item>Empty <c>EnabledLibraries</c> is "all libraries" (existing semantics); callers pass
///     <c>withinEnabledLibrary = true</c> in that case.</item>
///   <item>Null <c>FixerDisabled</c> is treated as an empty list.</item>
///   <item>Unknown <see cref="IssueType"/> values (future additions or corrupt DB rows) refuse
///     with <see cref="FixReason.Unknown"/>.</item>
/// </list>
///
/// See <c>artifacts/refactor/02B/decision-table.md</c> for the rule table and the matrix of
/// scenarios the tests exercise.
/// </summary>
internal static class FixPolicyEvaluator
{
    /// <summary>
    /// Applies the decision table. Returns <see cref="PolicyDecision.Allow"/> when every rule
    /// passes; otherwise returns the first refusal rule's reason.
    /// </summary>
    /// <param name="config">The current configuration snapshot.</param>
    /// <param name="type">The issue type being evaluated.</param>
    /// <param name="status">The issue's current status.</param>
    /// <param name="intent">Why the caller is evaluating this operation.</param>
    /// <param name="withinEnabledLibrary">Precomputed <c>LibraryGuard.IsInEnabledLocations(path, ...)</c>. Pass <c>true</c> when <c>EnabledLibraries</c> is empty (all libraries).</param>
    /// <param name="autoQueueRestoredBlocked">Precomputed "this <c>(path, type)</c> is in <c>restored_paths</c>". Only meaningful when <paramref name="intent"/> is <see cref="FixIntent.Scheduled"/>.</param>
    /// <param name="hasApprovedCompanionOnPath">Only meaningful when <paramref name="intent"/> is <see cref="FixIntent.Companion"/>: is there a queued/approved companion issue on the same path.</param>
    /// <returns>Allow / Refuse decision.</returns>
    public static PolicyDecision Evaluate(
        PluginConfiguration config,
        IssueType type,
        IssueStatus status,
        FixIntent intent,
        bool withinEnabledLibrary,
        bool autoQueueRestoredBlocked,
        bool hasApprovedCompanionOnPath)
    {
        // Rule 9 first — an out-of-range enum value is a defective input, never authorized.
        if (!Enum.IsDefined(type))
        {
            return PolicyDecision.Refuse(FixReason.Unknown);
        }

        // Rule 1: library scope.
        if (!withinEnabledLibrary)
        {
            return PolicyDecision.Refuse(FixReason.LibraryDisabled);
        }

        // Rule 2: type is Off.
        var mode = config.GetFixMode(type);
        if (mode == FixMode.Off)
        {
            return PolicyDecision.Refuse(FixReason.TypeDisabled);
        }

        // Rule 3: on the FixerDisabled mute list.
        if (IsOnFixerDisabledList(config.FixerDisabled, type))
        {
            return PolicyDecision.Refuse(FixReason.TypeDisabled);
        }

        // Rule 4: scheduled auto-queue must skip restored paths (manual approve bypasses).
        if (intent == FixIntent.Scheduled && autoQueueRestoredBlocked)
        {
            return PolicyDecision.Refuse(FixReason.RestoredPath);
        }

        // Rule 6 & 7: ManualApprove requires an explicit signal.
        //   Scheduled: refuse — the auto-queue would need per-row consent that isn't automatic.
        //   Companion: refuse unless a companion issue is actually approved on the same path.
        //   Approved: allow — the user's Approve IS the explicit signal.
        if (mode == FixMode.ManualApprove)
        {
            if (intent == FixIntent.Scheduled)
            {
                return PolicyDecision.Refuse(FixReason.ManualApprovalRequired);
            }

            if (intent == FixIntent.Companion && !hasApprovedCompanionOnPath)
            {
                return PolicyDecision.Refuse(FixReason.ManualApprovalRequired);
            }
        }

        // Rule 8: an explicit Approved intent still requires the row to be actionable — a
        // Fixed or Dismissed row hitting the dispatch path indicates stale state.
        if (intent == FixIntent.Approved && status != IssueStatus.Queued && status != IssueStatus.Detected)
        {
            return PolicyDecision.Refuse(FixReason.SourceChanged);
        }

        return PolicyDecision.Allow;
    }

    private static bool IsOnFixerDisabledList(string[]? fixerDisabled, IssueType type)
    {
        if (fixerDisabled is null || fixerDisabled.Length == 0)
        {
            return false;
        }

        var name = type.ToString();
        for (var i = 0; i < fixerDisabled.Length; i++)
        {
            // Ordinal + case-sensitive to preserve pre-migration behaviour (line ~343 of the
            // legacy FixTask.ExecuteAsync). Unknown / renamed entries stay unmatched — the
            // silent no-op is the historic behaviour and callers rely on it.
            if (string.Equals(fixerDisabled[i], name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
