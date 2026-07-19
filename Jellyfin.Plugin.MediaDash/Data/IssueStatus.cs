namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// Lifecycle state of a detected issue.
/// </summary>
public enum IssueStatus
{
    /// <summary>Found by a scan; awaiting a decision.</summary>
    Detected = 0,

    /// <summary>Approved (or auto-approved) and waiting for the fix task.</summary>
    Queued = 1,

    /// <summary>The fix was applied successfully.</summary>
    Fixed = 2,

    /// <summary>The user chose to ignore this issue; re-scans will not re-report it.</summary>
    Dismissed = 3
}
