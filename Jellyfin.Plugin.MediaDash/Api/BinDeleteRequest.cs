namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Body of POST RecycleBin/Items/Delete. Identifies the recycled file to permanently delete by
/// bin path (from RecycleBinItem.BinPath). One item per request; the UI already confirms with the
/// user that deletion is irreversible before calling.
/// </summary>
public sealed class BinDeleteRequest
{
    /// <summary>Gets or sets the bin path of the file to delete.</summary>
    public string BinPath { get; set; } = string.Empty;
}
