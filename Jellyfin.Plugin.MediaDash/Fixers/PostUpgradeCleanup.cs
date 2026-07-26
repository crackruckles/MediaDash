using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// One-shot cleanup for users moving from Jellyfin 10.x to 12.x. Jellyfin 12's library rewrite drops
/// orphaned metadata rows going forward but does nothing for pre-existing debris on disk. The most
/// visible pre-existing debris is orphaned trickplay images: subfolders under the trickplay data
/// directory named after item GUIDs that no longer resolve to any BaseItem. These can accumulate to
/// gigabytes on long-lived servers.
/// </summary>
/// <remarks>
/// Intentionally minimal: filesystem walk + GUID lookup + delete. No EF Core queries, no reflection
/// over Jellyfin-version-specific APIs. Runs synchronously off the request thread but reports total
/// bytes freed so the UI can log it as one History entry.
/// </remarks>
public sealed class PostUpgradeCleanup
{
    private readonly IApplicationPaths _appPaths;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PostUpgradeCleanup> _logger;

    /// <summary>Initializes a new instance of the <see cref="PostUpgradeCleanup"/> class.</summary>
    /// <param name="appPaths">Jellyfin's application paths (used to locate the trickplay data dir).</param>
    /// <param name="libraryManager">Used to resolve GUIDs back to items.</param>
    /// <param name="logger">Logger.</param>
    public PostUpgradeCleanup(IApplicationPaths appPaths, ILibraryManager libraryManager, ILogger<PostUpgradeCleanup> logger)
    {
        _appPaths = appPaths;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Runs the sweep. Returns quickly on small libraries; can take a few seconds on large ones because
    /// each candidate GUID triggers a GetItemById lookup.
    /// </summary>
    /// <returns>The sweep result.</returns>
    public Task<PostUpgradeCleanupResult> RunAsync()
    {
        // Trickplay lives under <data>/trickplay in every Jellyfin version we support. IApplicationPaths.DataPath
        // is the base MediaDash already uses for its own SQLite + recycle bin, so no new dependency.
        var trickplayRoot = Path.Combine(_appPaths.DataPath, "trickplay");
        var errors = new List<string>();
        var deleted = 0;
        long bytes = 0;

        if (!Directory.Exists(trickplayRoot))
        {
            _logger.LogInformation("PostUpgradeCleanup: trickplay dir does not exist ({Path}); nothing to sweep.", trickplayRoot);
            return Task.FromResult(new PostUpgradeCleanupResult(0, 0, errors));
        }

        foreach (var dir in Directory.EnumerateDirectories(trickplayRoot))
        {
            var name = Path.GetFileName(dir);
            // Trickplay subfolders are item GUIDs. Anything that doesn't parse as a GUID isn't ours to touch.
            if (!Guid.TryParse(name, out var itemId))
            {
                continue;
            }

            if (_libraryManager.GetItemById(itemId) is not null)
            {
                continue;
            }

            long folderBytes = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    folderBytes += new FileInfo(file).Length;
                }

                Directory.Delete(dir, recursive: true);
                deleted++;
                bytes += folderBytes;
                _logger.LogInformation("PostUpgradeCleanup: removed orphaned trickplay folder {Path} ({Bytes} bytes)", dir, folderBytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var msg = $"{name}: {ex.Message}";
                errors.Add(msg);
                _logger.LogWarning(ex, "PostUpgradeCleanup: could not remove {Path}", dir);
                Api.Diagnostics.Record("PostUpgradeCleanup.DeleteFailed", msg);
            }
        }

        _logger.LogInformation(
            "PostUpgradeCleanup: swept {Root}. Removed {Deleted} orphaned folders, freed {Bytes} bytes.",
            trickplayRoot,
            deleted,
            bytes);

        return Task.FromResult(new PostUpgradeCleanupResult(deleted, bytes, errors));
    }
}
