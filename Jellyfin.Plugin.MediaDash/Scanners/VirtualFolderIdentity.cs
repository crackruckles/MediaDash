using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Resolves a stable string identity for a <see cref="VirtualFolderInfo"/> across Jellyfin ABIs.
/// </summary>
/// <remarks>
/// v10.11 populates <see cref="VirtualFolderInfo.ItemId"/> (hex Guid string, direct identity of the
/// backing <c>CollectionFolder</c> BaseItem). v12.0 leaves <c>ItemId</c> null and drops the property
/// entirely from the JSON surface; the only stable identity on v12 lives on the backing
/// <c>CollectionFolder</c> BaseItem, reachable via <c>ILibraryManager.GetItemList</c>.
/// <para>
/// This helper exposes a two-part API: <see cref="BuildIdLookup"/> builds a
/// <see cref="VirtualFolderInfo"/> → id map by scanning CollectionFolders once, and
/// <see cref="GetId(VirtualFolderInfo, IReadOnlyDictionary{string, string}?)"/> resolves a single
/// folder — preferring the fast native <c>ItemId</c>, falling back to the map. Callers build the
/// lookup once per scan and pass it into every per-folder resolve so the DB query isn't repeated
/// inside a filter loop.
/// </para>
/// </remarks>
internal static class VirtualFolderIdentity
{
    /// <summary>
    /// Builds a lookup of library-name → identity strings by enumerating <c>CollectionFolder</c>
    /// BaseItems on the current Jellyfin host. Cheap enough to call once per scan (single DB query,
    /// tens of libraries) but too expensive to call per-item — use in tandem with
    /// <see cref="GetId(VirtualFolderInfo, IReadOnlyDictionary{string, string}?)"/>.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <returns>A case-insensitive dictionary keyed on <see cref="MakeKey"/>.</returns>
    internal static IReadOnlyDictionary<string, string> BuildIdLookup(ILibraryManager libraryManager)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var folders = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.CollectionFolder],
            IsVirtualItem = false,
            Recursive = false,
        });

        foreach (var folder in folders)
        {
            if (folder is null || folder.Id == Guid.Empty)
            {
                continue;
            }

            var key = MakeKey(folder.Name);
            if (!string.IsNullOrEmpty(key))
            {
                // Jellyfin refuses duplicate library names via the dashboard, so name is a unique key
                // in practice. First-in wins if two ever slip through so the map stays deterministic
                // across scans rather than flip-flopping.
                map.TryAdd(key, folder.Id.ToString("N"));
            }
        }

        return map;
    }

    /// <summary>
    /// Returns the folder's identity string, or <c>null</c> when neither the native ItemId nor the
    /// lookup yields one. Prefers the native <see cref="VirtualFolderInfo.ItemId"/> so v10.11 hosts
    /// never pay for the lookup even when a caller passes it.
    /// </summary>
    /// <param name="folder">The virtual folder to identify.</param>
    /// <param name="lookup">Optional lookup built by <see cref="BuildIdLookup"/>. Required to resolve identity on v12.</param>
    /// <returns>A non-empty identity string, or <c>null</c>.</returns>
    internal static string? GetId(VirtualFolderInfo folder, IReadOnlyDictionary<string, string>? lookup = null)
    {
        ArgumentNullException.ThrowIfNull(folder);

        if (!string.IsNullOrEmpty(folder.ItemId))
        {
            return folder.ItemId;
        }

        if (lookup is null)
        {
            return null;
        }

        var key = MakeKey(folder.Name);
        return string.IsNullOrEmpty(key) ? null : lookup.GetValueOrDefault(key);
    }

    /// <summary>
    /// Normalises a library name into a lookup key. Name alone: the <c>CollectionFolder</c>'s
    /// <c>Path</c> is Jellyfin's internal <c>root/default/&lt;Name&gt;</c> shortcut, not the on-disk
    /// media location that <see cref="VirtualFolderInfo.Locations"/> exposes, so path-matching never
    /// aligns the two sides. Jellyfin refuses duplicate library names via the dashboard so name is a
    /// unique key in practice. Exposed to the internal tests so the round-trip can be exercised
    /// without a live <see cref="ILibraryManager"/>.
    /// </summary>
    /// <param name="name">The library display name.</param>
    /// <returns>The trimmed name, or empty when the name is missing.</returns>
    internal static string MakeKey(string? name)
    {
        return name?.Trim() ?? string.Empty;
    }
}
