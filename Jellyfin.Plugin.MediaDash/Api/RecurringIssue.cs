using System;
using Jellyfin.Plugin.MediaDash.Data;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Overview payload for one issue that came back at the same path after being fixed. Feeds the
/// "These issues keep coming back" banner.
/// </summary>
public sealed class RecurringIssue
{
    /// <summary>Gets or sets the open issue's id.</summary>
    public long IssueId { get; set; }

    /// <summary>Gets or sets the issue type name.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the path that keeps coming back.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets how many times an issue at this path and type was marked Fixed before.</summary>
    public int TimesFixed { get; set; }

    /// <summary>Gets or sets when the earliest earlier fix ran.</summary>
    public DateTime FirstFixedAtUtc { get; set; }

    /// <summary>Gets or sets when the most recent earlier fix ran.</summary>
    public DateTime LastFixedAtUtc { get; set; }

    /// <summary>Gets or sets what the most recent earlier fix reported.</summary>
    public string LastFixMessage { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the most recent earlier fix reported success.</summary>
    public bool LastFixSucceeded { get; set; }

    /// <summary>Maps a store row to the API shape.</summary>
    /// <param name="recurrence">The store row.</param>
    /// <returns>The API row.</returns>
    public static RecurringIssue From(IssueRecurrence recurrence)
    {
        ArgumentNullException.ThrowIfNull(recurrence);
        return new RecurringIssue
        {
            IssueId = recurrence.IssueId,
            Type = recurrence.Type.ToString(),
            Path = recurrence.Path,
            TimesFixed = recurrence.TimesFixed,
            FirstFixedAtUtc = recurrence.FirstFixedAtUtc,
            LastFixedAtUtc = recurrence.LastFixedAtUtc,
            LastFixMessage = recurrence.LastFixMessage,
            LastFixSucceeded = recurrence.LastFixSucceeded
        };
    }
}
