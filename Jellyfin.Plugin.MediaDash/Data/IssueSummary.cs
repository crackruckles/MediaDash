using System;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// Aggregated counts for one issue type.
/// </summary>
public sealed class IssueSummary
{
    /// <summary>
    /// Gets or sets the issue type.
    /// </summary>
    public IssueType Type { get; set; }

    /// <summary>
    /// Gets or sets the number of issues awaiting a decision.
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Gets or sets the total bytes that could be reclaimed.
    /// </summary>
    public long PotentialSavings { get; set; }

    /// <summary>
    /// Gets or sets the newest detection time (UTC).
    /// </summary>
    public DateTime NewestDetectedUtc { get; set; }
}
