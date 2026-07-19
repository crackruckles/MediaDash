using System;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// A single problem detected by a scanner, tracked through its fix lifecycle.
/// </summary>
public sealed class Issue
{
    /// <summary>
    /// Gets or sets the database row id.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the issue category.
    /// </summary>
    public IssueType Type { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin library item id the file belongs to.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the full path of the affected file.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets scanner-specific details as a JSON document.
    /// </summary>
    public string DetailsJson { get; set; } = "{}";

    /// <summary>
    /// Gets or sets a human-readable description of the suggested fix.
    /// </summary>
    public string SuggestedFix { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the estimated bytes reclaimed if the fix is applied.
    /// </summary>
    public long SizeSavings { get; set; }

    /// <summary>
    /// Gets or sets the lifecycle status.
    /// </summary>
    public IssueStatus Status { get; set; }

    /// <summary>
    /// Gets or sets when the issue was detected (UTC).
    /// </summary>
    public DateTime DetectedAtUtc { get; set; }
}
