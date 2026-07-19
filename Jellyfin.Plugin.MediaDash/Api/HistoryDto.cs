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
    /// Gets or sets the plain-language description of what was done.
    /// </summary>
    public string Action { get; set; } = string.Empty;

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
    /// Gets or sets a value indicating whether the file can still be restored from the recycle bin.
    /// </summary>
    public bool CanRestore { get; set; }

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
            BytesFreed = entry.BytesFreed,
            FixedAtUtc = entry.FixedAtUtc,
            WasDryRun = entry.WasDryRun,
            CanRestore = !entry.Restored && !string.IsNullOrEmpty(entry.RecyclePath) && System.IO.File.Exists(entry.RecyclePath)
        };
    }
}
