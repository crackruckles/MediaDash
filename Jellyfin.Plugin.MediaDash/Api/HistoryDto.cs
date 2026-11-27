using System;
using Jellyfin.Plugin.MediaDash.Data;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// A history entry as shown in the dashboard.
/// </summary>
public sealed class HistoryDto
{
    /// <summary>
    /// Gets or sets the history entry id.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the issue type name.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file name.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the Jellyfin library the affected file belongs to.
    /// Empty when the path isn't inside a known virtual folder (e.g. post-upgrade cleanup rows).
    /// </summary>
    public string Library { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the plain-language description of what was done.
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the raw technical text (ffmpeg stderr, exception message) that produced
    /// the outcome. Null when there is no technical backing. Rendered by the UI behind a
    /// "Technical detail" disclosure so the primary Action stays readable.
    /// </summary>
    public string? TechnicalDetail { get; set; }

    /// <summary>
    /// Gets or sets the bytes freed.
    /// </summary>
    public long BytesFreed { get; set; }

    /// <summary>
    /// Gets or sets when the action ran (UTC).
    /// </summary>
    public DateTime FixedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this was a dry run.
    /// </summary>
    public bool WasDryRun { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the fix completed without error. Drives the
    /// History-tab filter chips (All / Succeeded / Failed / Dry run).
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the file can still be restored from the recycle bin.
    /// </summary>
    public bool CanRestore { get; set; }

    /// <summary>Gets or sets the durable move operation, including recovery after a failed run.</summary>
    public string? MoveOperationId { get; set; }

    /// <summary>Gets or sets the current move state.</summary>
    public string? MoveState { get; set; }

    /// <summary>Gets or sets a value indicating whether complete move Undo is available.</summary>
    public bool CanUndoMove { get; set; }

    /// <summary>
    /// Maps a database entry to the DTO.
    /// </summary>
    /// <param name="entry">The history entry.</param>
    /// <returns>The DTO.</returns>
    public static HistoryDto FromEntry(HistoryEntry entry)
    {
        return new HistoryDto
        {
            Id = entry.Id,
            Type = entry.Type.ToString(),
            FileName = System.IO.Path.GetFileName(entry.Path),
            Action = entry.Action,
            TechnicalDetail = entry.TechnicalDetail,
            BytesFreed = entry.BytesFreed,
            FixedAtUtc = entry.FixedAtUtc,
            WasDryRun = entry.WasDryRun,
            Success = entry.Success,
            MoveOperationId = entry.MoveOperationId,
            // Directory-recycled entries (orphan cleanup, Files-tab folder delete) go through the
            // same RestoreFromHistory endpoint, which already accepts both file and directory
            // paths via `File.Exists || Directory.Exists`. Mirror that here — otherwise the UI
            // hides the Restore button on a directory history row even though the server would
            // happily restore it, stranding the entry.
            CanRestore = !entry.Restored
                && !string.IsNullOrEmpty(entry.RecyclePath)
                && (System.IO.File.Exists(entry.RecyclePath) || System.IO.Directory.Exists(entry.RecyclePath))
        };
    }
}
