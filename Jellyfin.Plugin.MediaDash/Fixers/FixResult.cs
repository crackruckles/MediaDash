using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Outcome of a fix attempt.
/// </summary>
public sealed class FixResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the fix succeeded (or would succeed, in dry-run).
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the typed classification of what happened. Defaults to
    /// <see cref="FixOutcome.Unspecified"/> while producers are migrated in 01-B;
    /// scheduling policy (<see cref="FixResultPolicy"/>) treats Unspecified as
    /// "unclassified — never impersonate a terminal outcome".
    /// </summary>
    internal FixOutcome Outcome { get; set; } = FixOutcome.Unspecified;

    /// <summary>
    /// Gets or sets the typed reason accompanying <see cref="Outcome"/>. See
    /// <see cref="FixReason"/>. Defaults to <see cref="FixReason.None"/>.
    /// </summary>
    internal FixReason Reason { get; set; } = FixReason.None;

    /// <summary>
    /// Gets or sets the plain-language description of what happened.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the raw technical detail (ffmpeg stderr, exception text, decoder dump)
    /// that produced this outcome. Null when the outcome has no technical backing. The UI
    /// renders this behind a "Technical detail" disclosure so the primary message stays
    /// readable; the raw text stays available for troubleshooting and support tickets.
    /// </summary>
    public string? TechnicalDetail { get; set; }

    /// <summary>
    /// Gets or sets the bytes freed.
    /// </summary>
    public long BytesFreed { get; set; }

    /// <summary>
    /// Gets or sets the pre-fix source size when the fix RESCUED a file that would otherwise
    /// have been deleted (e.g. Playability repair ladder). Zero for every other outcome. The
    /// Overview "Broken files repaired" panel sums this to report "GB saved from deletion" —
    /// distinct from <see cref="BytesFreed"/> which counts actual disk reclaim.
    /// </summary>
    public long SavedBytes { get; set; }

    /// <summary>
    /// Gets or sets the recycle bin path of the removed file, when recycled.
    /// </summary>
    public string? RecyclePath { get; set; }

    /// <summary>
    /// Gets or sets the path where a staged output survived when the final rename could not
    /// complete after the original was disposed. Non-null signals a partial commit: the user
    /// must move this file back manually. Mirrors <c>ReplacementResult.RecoveryPath</c>
    /// semantics so history UI can render a restore instruction.
    /// </summary>
    public string? RecoveryPath { get; set; }

    /// <summary>Gets or sets the durable move operation, including partial work without History.</summary>
    public string? MoveOperationId { get; set; }

    /// <summary>
    /// Gets or initializes additional files that were recycled during this fix (external subtitle
    /// sidecars, etc.). Each entry gets its own history row so the Recycle Bin tab can render a
    /// Restore button for it — a fix that recycled three files should yield three restorable rows,
    /// not one.
    /// </summary>
    public IReadOnlyList<RecycledSidecar> AdditionalRecycled { get; init; } = System.Array.Empty<RecycledSidecar>();

    /// <summary>
    /// Gets or sets a value indicating whether this was a dry run.
    /// </summary>
    public bool WasDryRun { get; set; }

    /// <summary>
    /// Creates a failure result. Legacy shape used by not-yet-migrated producers — leaves
    /// <see cref="Outcome"/> at <see cref="FixOutcome.Unspecified"/>. Prefer the typed
    /// overload <see cref="Fail(FixReason, string, string?)"/> in new/updated code.
    /// </summary>
    /// <param name="message">Why the fix failed (plain language).</param>
    /// <param name="technicalDetail">Raw technical text (ffmpeg stderr, exception message).
    /// Optional — pass null when there is no separable technical backing. When non-null,
    /// keep <paramref name="message"/> friendly and free of ffmpeg output; the UI shows this
    /// behind a "Technical detail" disclosure.</param>
    /// <returns>The result.</returns>
    public static FixResult Fail(string message, string? technicalDetail = null)
        => new() { Success = false, Message = message, TechnicalDetail = technicalDetail };

    /// <summary>
    /// Creates a <see cref="FixOutcome.Failed"/> result tagged with a typed reason. Scheduling
    /// policy consults <see cref="Reason"/>, not <paramref name="message"/>.
    /// </summary>
    /// <param name="reason">Typed classification of why the fix failed.</param>
    /// <param name="message">Plain-language description (still shown to the user).</param>
    /// <param name="technicalDetail">Optional raw technical text.</param>
    /// <returns>The result.</returns>
    internal static FixResult Fail(FixReason reason, string message, string? technicalDetail = null)
        => new() { Success = false, Outcome = FixOutcome.Failed, Reason = reason, Message = message, TechnicalDetail = technicalDetail };

    /// <summary>
    /// Creates a <see cref="FixOutcome.Refused"/> result: a hard precondition failed, so the
    /// fixer would not act (DRM, LibraryGuard, unsupported format, collision, invalid request).
    /// </summary>
    /// <param name="reason">Typed classification of the refusal.</param>
    /// <param name="message">Plain-language explanation.</param>
    /// <param name="technicalDetail">Optional raw technical text.</param>
    /// <returns>The result.</returns>
    internal static FixResult Refused(FixReason reason, string message, string? technicalDetail = null)
        => new() { Success = false, Outcome = FixOutcome.Refused, Reason = reason, Message = message, TechnicalDetail = technicalDetail };

    /// <summary>
    /// Creates a <see cref="FixOutcome.NoChange"/> result: the scanner condition is no longer
    /// present or nothing needed doing. Not a failure — the queue advances so the retry loop
    /// stops.
    /// </summary>
    /// <param name="reason">Typed classification of the no-op.</param>
    /// <param name="message">Plain-language explanation.</param>
    /// <returns>The result.</returns>
    internal static FixResult NoChange(FixReason reason, string message)
        => new() { Success = false, Outcome = FixOutcome.NoChange, Reason = reason, Message = message };

    /// <summary>
    /// Creates a <see cref="FixOutcome.Deferred"/> result: the fixer chose to try again later
    /// (provider quota, transient contention). Stays queued.
    /// </summary>
    /// <param name="reason">Typed classification of the deferral.</param>
    /// <param name="message">Plain-language explanation.</param>
    /// <param name="technicalDetail">Optional raw technical text.</param>
    /// <returns>The result.</returns>
    internal static FixResult Deferred(FixReason reason, string message, string? technicalDetail = null)
        => new() { Success = false, Outcome = FixOutcome.Deferred, Reason = reason, Message = message, TechnicalDetail = technicalDetail };

    /// <summary>
    /// Creates a dry-run result describing what would have happened. Sets
    /// <see cref="Outcome"/> to <see cref="FixOutcome.Preview"/> so migrated consumers can
    /// distinguish a successful preview from a real mutation.
    /// </summary>
    /// <param name="message">The planned action.</param>
    /// <param name="bytesFreed">The bytes that would be freed.</param>
    /// <returns>The result.</returns>
    public static FixResult DryRun(string message, long bytesFreed) => new()
    {
        Success = true,
        WasDryRun = true,
        Outcome = FixOutcome.Preview,
        // Field report A10: users read the old "DRY RUN — would have: moved X → Y" and their eye
        // latched onto "moved" past-tense, believing the file actually moved. Lead with the fact
        // that nothing changed so that framing sticks before the action verb arrives.
        Message = "Preview only — no files were changed. Would have " + message,
        BytesFreed = bytesFreed
    };
}
