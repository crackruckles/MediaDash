using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
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
    private readonly IServerApplicationPaths _appPaths;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PostUpgradeCleanup> _logger;

    /// <summary>Initializes a new instance of the <see cref="PostUpgradeCleanup"/> class.</summary>
    /// <param name="appPaths">Jellyfin's application paths (used to locate the trickplay data dir).</param>
    /// <param name="libraryManager">Used to resolve GUIDs back to items.</param>
    /// <param name="logger">Logger.</param>
    public PostUpgradeCleanup(IServerApplicationPaths appPaths, ILibraryManager libraryManager, ILogger<PostUpgradeCleanup> logger)
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
        // Jellyfin's trickplay layout has shifted across versions: 10.x kept per-item GUID folders
        // under <data>/trickplay OR <metadata>/trickplay, while 12.x puts trickplay next to media in
        // *-trickplay folders (that layout is handled by OrphanCleanupScanner during normal scans).
        // Try every plausible per-GUID root — if none exist the sweep reports 0, and the library-
        // adjacent layout is picked up by the regular scan.
        var candidateRoots = new[]
        {
            Path.Combine(_appPaths.DataPath, "trickplay"),
            Path.Combine(_appPaths.DataPath, "metadata", "trickplay"),
            Path.Combine(_appPaths.InternalMetadataPath, "trickplay")
        };

        var errors = new List<string>();
        var deleted = 0;
        long bytes = 0;
        var swept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var trickplayRoot in candidateRoots)
        {
            if (!Directory.Exists(trickplayRoot) || !swept.Add(Path.GetFullPath(trickplayRoot)))
            {
                continue;
            }

            foreach (var dir in Directory.EnumerateDirectories(trickplayRoot))
            {
                var name = Path.GetFileName(dir);
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
        }

        if (swept.Count == 0)
        {
            _logger.LogInformation("PostUpgradeCleanup: no per-GUID trickplay root exists under DataPath or InternalMetadataPath; nothing to sweep.");
        }

        _logger.LogInformation(
            "PostUpgradeCleanup: swept {RootCount} root(s). Removed {Deleted} orphaned folders, freed {Bytes} bytes.",
            swept.Count,
            deleted,
            bytes);

        return Task.FromResult(new PostUpgradeCleanupResult(deleted, bytes, errors));
    }
}
