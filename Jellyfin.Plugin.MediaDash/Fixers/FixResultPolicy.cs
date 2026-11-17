namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Pure mapping from a typed (<see cref="FixOutcome"/>, <see cref="FixReason"/>) pair to
/// the scheduler's <see cref="QueueDisposition"/>. Split out so scheduling stops
/// depending on friendly-language substrings of <see cref="FixResult.Message"/>.
/// </summary>
/// <remarks>
/// Refusals remain visible for review; no-change outcomes can complete stale findings.
/// The conservative default is <see cref="QueueDisposition.KeepQueued"/>: an
/// outcome we did not migrate yet, or a Failed outcome without a matching
/// terminal reason, must remain visible on the queue rather than be silently
/// swallowed. Do NOT rewrite this to a big switch-expression fan-out; explicit
/// per-reason arms make retry-loop bugs bisectable and reviewable.
/// </remarks>
internal static class FixResultPolicy
{
    /// <summary>
    /// Decide whether the scheduler should keep the issue queued or advance it to
    /// legacy-complete based on the outcome/reason pair. Unknown / unmigrated
    /// combinations return <see cref="QueueDisposition.KeepQueued"/>.
    /// </summary>
    /// <param name="outcome">Typed outcome of the fix attempt.</param>
    /// <param name="reason">Typed reason accompanying the outcome.</param>
    /// <returns>The queue disposition the scheduler should apply.</returns>
    internal static QueueDisposition GetQueueDisposition(FixOutcome outcome, FixReason reason)
    {
        // A producer that hasn't been migrated to typed outcomes is never a terminal outcome.
        // Keep visible so the queue does not eat unclassified results.
        if (outcome == FixOutcome.Unspecified)
        {
            return QueueDisposition.KeepQueued;
        }

        // Success paths — Applied and Preview — never reach the retry-suppression seam in
        // production (they hit the success side of the branch first). The mapping still has to
        // be defined because policy is a pure function of (outcome, reason). Treat them as
        // CompleteLegacy so a producer bug that misroutes a successful outcome doesn't
        // accidentally reopen the retry loop; the F-206 dry-run gate lives in the caller.
        if (outcome == FixOutcome.Applied || outcome == FixOutcome.Preview)
        {
            return QueueDisposition.CompleteLegacy;
        }

        // NoChange: the scanner condition is no longer present. Every current phrase-based
        // stale-catch of this shape MarkFixes to break the retry loop.
        if (outcome == FixOutcome.NoChange)
        {
            return reason switch
            {
                FixReason.AlreadyHealthy => QueueDisposition.CompleteLegacy,
                FixReason.NothingToRemove => QueueDisposition.CompleteLegacy,
                FixReason.ExistingCover => QueueDisposition.CompleteLegacy,
                _ => QueueDisposition.KeepQueued
            };
        }

        // Deferred: the fixer intentionally chose to try again later (provider quota exhausted,
        // for now). Must stay queued or the reset never picks it up.
        if (outcome == FixOutcome.Deferred)
        {
            return QueueDisposition.KeepQueued;
        }

        // A refusal changed nothing; preserve the approval and explanation for review.
        if (outcome == FixOutcome.Refused)
        {
            return QueueDisposition.KeepQueued;
        }

        // Failed: the two current terminal shapes are SourceMissing (file/directory gone).
        // Everything else — permission denied, disk full, file locked, verification failed,
        // ffmpeg process failure, probe unavailable, cancelled, recovery-required — must
        // remain queued so the next run retries and so recovery-required stays loud.
        if (outcome == FixOutcome.Failed)
        {
            return reason switch
            {
                FixReason.SourceMissing => QueueDisposition.CompleteLegacy,
                FixReason.SourceChanged => QueueDisposition.CompleteLegacy,
                // A Quality re-encode that missed the savings threshold will produce the same
                // output on the next run — retrying wastes CPU. Terminal per roadmap
                // i-110-minimum-savings ("report insufficient savings as a skipped outcome
                // rather than an endless retry").
                FixReason.InsufficientSavings => QueueDisposition.CompleteLegacy,
                _ => QueueDisposition.KeepQueued
            };
        }

        return QueueDisposition.KeepQueued;
    }
}
