using System;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Where the bin item's metadata came from — determines what the UI can promise about restore.
/// </summary>
public enum RecycleProvenance
{
    /// <summary>
    /// A HistoryEntry row references this file — the fix run that recycled it is fully recorded
    /// (issue type, action verbatim, timestamp). Restore is a one-click operation.
    /// </summary>
    History = 0,

    /// <summary>
    /// No HistoryEntry, but the batch's origin manifest sidecar remembers the source path. Happens
    /// for user-initiated Files-tab deletes (which don't fill History) and for any recycle whose
    /// history row was later cleared. Restore still works via BinPath.
    /// </summary>
    Manifest = 1,

    /// <summary>
    /// Neither HistoryEntry nor manifest — recycled by a pre-manifest MediaDash build. The user
    /// has to use the Files tab's Recycle bin shortcut to move the file back manually.
    /// </summary>
    Orphan = 2,
}

/// <summary>
/// One file held in the recycle bin, with everything the UI needs to explain what it is, why it's
/// there, and how the user can recover it. Fields are pre-computed server-side so the UI is a
/// pure renderer.
/// </summary>
public sealed class RecycleBinItem
{
    /// <summary>Gets or sets the recycled file's name (no path).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the file size in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Gets or sets when the file was recycled (UTC).</summary>
    public DateTime RecycledAtUtc { get; set; }

    /// <summary>
    /// Gets or sets when this file will be permanently deleted by the retention purge (UTC). Null
    /// when retention days is 0 (bin never auto-purges) or when the recycle timestamp couldn't be
    /// parsed. The UI renders "auto-deletes on YYYY-MM-DD" from this so users know their deadline.
    /// </summary>
    public DateTime? AutoPurgesAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the id of the history entry that recycled this file. Null when the bin file has no
    /// matching history row - either it was recycled by a pre-history build, or the row was purged.
    /// The UI uses this to decide whether the per-item Restore button routes through History/{id}/Restore.
    /// </summary>
    public long? HistoryId { get; set; }

    /// <summary>
    /// Gets or sets the original path this file would be restored to. Populated either from a
    /// matching HistoryEntry or from the bin batch's origin manifest sidecar.
    /// </summary>
    public string? OriginalPath { get; set; }

    /// <summary>
    /// Gets or sets the bin path — populated for every row so callers can always POST
    /// RecycleBin/Items/Restore with this value. Frontends that want a one-click restore for
    /// items that also have a HistoryId should still prefer History/{id}/Restore, which carries
    /// richer state for the restored HistoryEntry. Populating BinPath unconditionally closes
    /// F-207 / issue #26: clients previously had no way to obtain the BinPath for History rows.
    /// </summary>
    public string? BinPath { get; set; }

    /// <summary>
    /// Gets or sets where the file's metadata came from — drives whether the UI can promise a
    /// one-click restore, a manifest-fallback restore, or a manual move.
    /// </summary>
    public RecycleProvenance Provenance { get; set; }

    /// <summary>
    /// Gets or sets a short human-readable reason the file was recycled — derived from the
    /// underlying IssueType when a HistoryEntry is present, or "Manual delete via Files tab" for
    /// user-initiated deletes. Used as a badge on each row.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the raw IssueType name (e.g. "Duplicate", "SubtitleLanguage") for rows that
    /// have a HistoryEntry. Empty for manifest-only / orphan rows. The UI uses this to pick a
    /// colour + icon consistent with the Issues tab.
    /// </summary>
    public string IssueType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the verbatim Action string from the HistoryEntry — the exact human description
    /// the fixer emitted (e.g. "Removed 2 unwanted audio tracks."). Rendered under the reason so
    /// the user sees precisely what MediaDash did before they decide to restore.
    /// </summary>
    public string ActionText { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets plain-language recovery guidance for this specific row. Non-empty for every
    /// row so the UI can render it verbatim in a tooltip / expand-out panel next to the Restore
    /// button. Examples: "Click Restore to put the file back at &lt;original path&gt;.",
    /// "This file was recycled by an older MediaDash build; use the Files tab to move it back.".
    /// </summary>
    public string RestoreHint { get; set; } = string.Empty;
}
