namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// One historical bin-root location that isn't the currently-configured one. Discovered by
/// walking distinct <see cref="Data.HistoryEntry.RecyclePath"/> values back up to their bin roots.
/// The Recycle bin tab uses this to offer a "consolidate everything here" action.
/// </summary>
public sealed class OtherBinLocation
{
    /// <summary>Gets or sets the absolute path of the other bin root.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of MediaDash-shaped batch folders still present under this root.</summary>
    public int BatchCount { get; set; }

    /// <summary>Gets or sets the total byte size of files inside those batches.</summary>
    public long SizeBytes { get; set; }
}
