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
    /// Gets or sets the plain-language description of what happened.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the bytes freed.
    /// </summary>
    public long BytesFreed { get; set; }

    /// <summary>
    /// Gets or sets the recycle bin path of the removed file, when recycled.
    /// </summary>
    public string? RecyclePath { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this was a dry run.
    /// </summary>
    public bool WasDryRun { get; set; }

    /// <summary>
    /// Creates a failure result.
    /// </summary>
    /// <param name="message">Why the fix failed.</param>
    /// <returns>The result.</returns>
    public static FixResult Fail(string message) => new() { Success = false, Message = message };

    /// <summary>
    /// Creates a dry-run result describing what would have happened.
    /// </summary>
    /// <param name="message">The planned action.</param>
    /// <param name="bytesFreed">The bytes that would be freed.</param>
    /// <returns>The result.</returns>
    public static FixResult DryRun(string message, long bytesFreed) => new()
    {
        Success = true,
        WasDryRun = true,
        // Field report A10: users read the old "DRY RUN — would have: moved X → Y" and their eye
        // latched onto "moved" past-tense, believing the file actually moved. Lead with the fact
        // that nothing changed so that framing sticks before the action verb arrives.
        Message = "Preview only — no files were changed. Would have " + message,
        BytesFreed = bytesFreed
    };
}
