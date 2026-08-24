using System.Collections.Generic;
using System.IO;
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

    /// <summary>
    /// Opens a library file for reading while allowing OTHER processes to move, rename, or delete
    /// it — the exact opposite of <see cref="File.OpenRead"/>'s default (FileShare.Read), which
    /// blocks moves/deletes and manifests as ERROR_SHARING_VIOLATION on Windows when a fixer
    /// touches a file a scanner is mid-probe on. Use everywhere a scanner or probe reads a media
    /// file — writer-scanners are the exception, not the rule.
    /// </summary>
    /// <param name="path">Absolute path to the file.</param>
    /// <returns>A read-only FileStream that does not block concurrent moves/deletes.</returns>
    public static FileStream OpenSharedRead(string path)
        => new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete
        });
}
