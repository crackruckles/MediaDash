using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Enumerates the physical files behind a library item, including merged alternate versions.
/// </summary>
public static class MediaFileHelper
{
    /// <summary>
    /// Gets all file paths for an item: its primary path plus any merged alternate versions.
    /// </summary>
    /// <param name="item">The library item.</param>
    /// <returns>The file paths.</returns>
    public static IEnumerable<string> GetFilePaths(BaseItem item)
    {
        if (!string.IsNullOrEmpty(item.Path))
        {
            yield return item.Path;
        }

        if (item is Video video && video.LocalAlternateVersions is { Length: > 0 } versions)
        {
            foreach (var version in versions)
            {
                if (!string.IsNullOrEmpty(version))
                {
                    yield return version;
                }
            }
        }
    }
}
