using System;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// An open issue whose (type, path) was already marked Fixed at least once before — the fix ran,
/// yet a later scan found the same problem at the same path. Either something outside MediaDash
/// (Sonarr / Radarr, Jellyfin's metadata saver, a sync or backup tool) keeps recreating it, or the
/// fix reported "done" without changing anything on disk.
/// </summary>
public sealed class IssueRecurrence
{
    /// <summary>Gets or sets the id of the open (Detected or Queued) issue that came back.</summary>
    public long IssueId { get; set; }

    /// <summary>Gets or sets the issue type shared by the open issue and its earlier fixes.</summary>
    public IssueType Type { get; set; }

    /// <summary>Gets or sets the path shared by the open issue and its earlier fixes.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets how many earlier issues at this path and type were marked Fixed.</summary>
    public int TimesFixed { get; set; }

    /// <summary>Gets or sets when the earliest of those fixes ran.</summary>
    public DateTime FirstFixedAtUtc { get; set; }

    /// <summary>Gets or sets when the most recent of those fixes ran.</summary>
    public DateTime LastFixedAtUtc { get; set; }

    /// <summary>Gets or sets what the most recent fix reported.</summary>
    public string LastFixMessage { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the most recent fix reported success. False means the fixer declined or changed nothing, yet the issue was closed.</summary>
    public bool LastFixSucceeded { get; set; }
}
