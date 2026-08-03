using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Cross-ABI-safe library summary served by <c>GET /MediaDash/Libraries</c>. Field shape mirrors
/// Jellyfin's own <c>/Library/VirtualFolders</c> response so the frontend can drop-in swap the
/// endpoint URL, but <see cref="ItemId"/> is populated via <see cref="Scanners.VirtualFolderIdentity.GetId"/>
/// so it stays non-empty on both v10.11 (native <c>ItemId</c>) and v12 (reflected <c>Id</c>).
/// </summary>
public sealed class LibraryInfo
{
    /// <summary>Gets or sets the stable identity string used by <c>EnabledLibraries</c> and <c>StaleExcludedLibraryIds</c>.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the library's display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets Jellyfin's collection-type tag (e.g. <c>movies</c>, <c>tvshows</c>) or null for mixed.</summary>
    public string? CollectionType { get; set; }

    /// <summary>Gets or sets the on-disk folders the library resolves to.</summary>
    public IReadOnlyList<string> Locations { get; set; } = [];
}
