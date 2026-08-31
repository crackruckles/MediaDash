namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Body of POST RecycleBin/Items/Restore. Identifies the recycled file(s) by bin path so items
/// with no HistoryEntry (manifest-only) are restorable — the manifest supplies the original path.
/// <para>
/// Accepts both single-item shape (<see cref="BinPath"/>) and batch shape
/// (<see cref="BinPaths"/>). At least one must be set — the controller returns 400 otherwise.
/// Legacy clients that sent <c>{ids: []}</c> or similar shapes now get an explicit 400 with a
/// message pointing at the correct field names (F-207 / issue #26).
/// </para>
/// </summary>
public sealed class BinRestoreRequest
{
    /// <summary>Gets or sets the bin path of the file to restore (from RecycleBinItem.BinPath).</summary>
    public string BinPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a batch of bin paths to restore in one call. When both this and
    /// <see cref="BinPath"/> are set, the controller processes BinPaths and treats
    /// BinPath as the first entry of a de-duplicated list.
    /// </summary>
#pragma warning disable CA1819 // Properties should not return arrays -- this is a JSON DTO; array shape matches wire.
    public string[]? BinPaths { get; set; }
#pragma warning restore CA1819
}
