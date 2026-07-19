using System;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// A completed (or dry-run) fix action.
/// </summary>
public sealed class HistoryEntry
{
    /// <summary>
    /// Gets or sets the row id.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the issue this action resolved.
    /// </summary>
    public long IssueId { get; set; }

    /// <summary>
    /// Gets or sets the issue type.
    /// </summary>
    public IssueType Type { get; set; }

    /// <summary>
    /// Gets or sets the affected file path.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the plain-language description of what was done.
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the bytes freed by this action.
    /// </summary>
    public long BytesFreed { get; set; }

    /// <summary>
    /// Gets or sets the recycle bin location of the removed file, when recycled.
    /// </summary>
    public string? RecyclePath { get; set; }

    /// <summary>
    /// Gets or sets when the action ran (UTC).
    /// </summary>
    public DateTime FixedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this was a dry run that changed nothing.
    /// </summary>
    public bool WasDryRun { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the file has been restored from the recycle bin.
    /// </summary>
    public bool Restored { get; set; }
}
