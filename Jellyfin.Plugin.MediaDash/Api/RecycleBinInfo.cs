namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Recycle bin contents summary.
/// </summary>
public sealed class RecycleBinInfo
{
    /// <summary>
    /// Gets or sets the number of files currently held.
    /// </summary>
    public int FileCount { get; set; }

    /// <summary>
    /// Gets or sets the total size in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>Gets or sets a value indicating whether an "empty bin" run is currently in progress.</summary>
    public bool IsEmptying { get; set; }

    /// <summary>Gets or sets the number of top-level batches that have already been deleted in the current empty run.</summary>
    public int EmptyingDone { get; set; }

    /// <summary>Gets or sets the total number of top-level batches to delete in the current empty run.</summary>
    public int EmptyingTotal { get; set; }

    /// <summary>Gets or sets the last error message captured during the current or most-recent empty run, or null on success.</summary>
    public string? EmptyingError { get; set; }
}
