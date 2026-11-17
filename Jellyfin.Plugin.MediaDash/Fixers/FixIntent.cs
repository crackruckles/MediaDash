namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// The intent under which <see cref="FixPolicyEvaluator.Evaluate"/> is being consulted.
/// Distinguishing intents lets the evaluator gate the ManualApprove-vs-Automatic split without
/// requiring the caller to hand-roll status checks.
/// </summary>
internal enum FixIntent
{
    /// <summary>
    /// Scheduled opportunistic run — the row was auto-queued or already Queued and the fixer is
    /// running unattended. ManualApprove types refuse; restored paths refuse.
    /// </summary>
    Scheduled,

    /// <summary>
    /// User explicitly approved this issue (via <c>/Issues/{id}/Approve</c>) and the fixer is
    /// running against that approval. Restored-path gate does not apply (manual is stronger
    /// than the auto-queue block).
    /// </summary>
    Approved,

    /// <summary>
    /// Companion cleanup — a same-path AudioLanguage or SubtitleLanguage cleanup being folded
    /// into a Transcode run. ManualApprove requires the companion issue to actually be queued
    /// on the same path; a nonempty language allow-list is not enough.
    /// </summary>
    Companion,
}
