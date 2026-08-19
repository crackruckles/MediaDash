using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Body for the bulk issue-status endpoint.
/// </summary>
public sealed class BulkIssueRequest
{
    /// <summary>
    /// Gets or sets the issue ids to update.
    /// </summary>
    public IReadOnlyList<long> Ids { get; set; } = System.Array.Empty<long>();

    /// <summary>
    /// Gets or sets the target action ("Approve" queues, "Dismiss" hides).
    /// </summary>
    public string Action { get; set; } = string.Empty;
}
