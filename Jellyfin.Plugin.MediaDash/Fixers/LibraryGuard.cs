using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Authorizes media within configured libraries and managed metadata through resource-specific checks.
/// </summary>
public sealed class LibraryGuard
{
    private static readonly HashSet<string> ManagedImageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "poster", "folder", "backdrop", "clearart", "back", "landscape", "disc", "cdart",
        "logo", "banner", "box", "menu", "screenshot"
    };

    private static readonly string[] SidecarPatterns =
    [
        "*.mediadash.tmp*",
        "*.mediadash.repair.*",
        "*.mediadash.new*",
        "*.mediadash.swap*",
        "*.mediadash.strip*",
        "*.mediadash.upload.tmp",
        "mediadash.tmp.*",
        "mediadash.new.*"
    ];

    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationPaths? _applicationPaths;
    private readonly ITaskManager? _taskManager;
    private readonly TimeProvider _timeProvider;
    private readonly MediaMoveStore? _moves;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryGuard"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="applicationPaths">Managed artwork root, when available.</param>
    /// <param name="taskManager">Host library-refresh state, when available.</param>
    /// <param name="timeProvider">Clock for validating completed host-scan timestamps.</param>
    /// <param name="moves">Durable move reservations, when available.</param>
    public LibraryGuard(ILibraryManager libraryManager, IServerApplicationPaths? applicationPaths = null, ITaskManager? taskManager = null, TimeProvider? timeProvider = null, MediaMoveStore? moves = null)
    {
        _libraryManager = libraryManager;
        _applicationPaths = applicationPaths;
        _taskManager = taskManager;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _moves = moves;
    }

    internal bool IsMoveReserved(string path, Guid itemId = default) => _moves is not null && (_moves.HasActiveConflict(path) || _moves.HasActiveItemConflict(itemId));

    /// <summary>Checks media, owned artwork and missing-item metadata using their respective scopes.</summary>
    /// <param name="issue">Issue whose target is being authorized.</param>
    /// <param name="locations">Enabled library roots.</param>
    /// <returns>Whether the resource is in scope.</returns>
    internal bool IsIssueInEnabledLocations(Issue issue, IReadOnlyList<string> locations)
    {
        if (issue.Type == IssueType.CorruptArtwork)
        {
            return IsArtworkInEnabledLocations(issue.Path, issue.ItemId, locations, requireImage: true);
        }

        if (issue.Type == IssueType.OrphanedDebris && OrphanCleanupFixer.ReadKind(issue.DetailsJson) == Scanners.OrphanCleanupScanner.KindOrphanMetadata)
        {
            return IsOrphanMetadataInEnabledLocations(issue.Path, issue.ItemId, locations) && HasOnlyManagedMetadata(issue.Path);
        }

        return IsInEnabledLocations(issue.Path, locations);
    }

    internal bool IsOrphanMetadataPath(string path, Guid itemId)
    {
        try
        {
            if (_applicationPaths is null || itemId == Guid.Empty || string.IsNullOrWhiteSpace(_applicationPaths.InternalMetadataPath))
            {
                return false;
            }

            var id = itemId.ToString("N");
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var expected = Path.GetFullPath(Path.Combine(_applicationPaths.InternalMetadataPath, "library", id[..2], id));
            if (!string.Equals(full, expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || HasReparsePointAncestor(full))
            {
                return false;
            }

            return !HasReparsePoints(full);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    internal static bool HasReparsePoints(string path)
    {
        return HasReparsePointAncestor(Path.GetFullPath(path)) || (Directory.Exists(path)
            && Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false
            }).Any(entry => (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0));
    }

    internal bool IsOrphanMetadataInEnabledLocations(string path, Guid itemId, IReadOnlyList<string> locations)
    {
        try
        {
            // Missing owners cannot be mapped back to a selected library. This is the explicit
            // global metadata pass; a resolving owner must never be treated as an orphan.
            return locations.Count > 0 && itemId != Guid.Empty && IsLibraryRefreshIdle()
                && _libraryManager.GetItemById(itemId) is null
                && _libraryManager.GetItemList(new InternalItemsQuery { ItemIds = [itemId] }).Count == 0
                && _libraryManager.GetItemById(itemId) is null
                && IsOrphanMetadataPath(path, itemId) && IsLibraryRefreshIdle();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private bool IsLibraryRefreshIdle()
    {
        return _taskManager is null || !_taskManager.ScheduledTasks.Any(worker =>
            string.Equals(worker.ScheduledTask.Key, ScheduledTasks.IdleCheck.JellyfinLibraryScanKey, StringComparison.Ordinal)
            && worker.State != TaskState.Idle);
    }

    internal bool HasConfirmingMetadataScan(Issue issue)
    {
        try
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            if (_taskManager is null || issue.DetectedAtUtc <= DateTime.MinValue || issue.DetectedAtUtc > now)
            {
                return false;
            }

            return _taskManager.ScheduledTasks.Any(worker =>
                string.Equals(worker.ScheduledTask.Key, ScheduledTasks.IdleCheck.JellyfinLibraryScanKey, StringComparison.Ordinal)
                && worker.State == TaskState.Idle
                && worker.LastExecutionResult is { Status: TaskCompletionStatus.Completed } result
                && string.Equals(result.Key, ScheduledTasks.IdleCheck.JellyfinLibraryScanKey, StringComparison.Ordinal)
                && result.StartTimeUtc > issue.DetectedAtUtc && result.EndTimeUtc >= result.StartTimeUtc && result.EndTimeUtc <= now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    internal static bool HasOnlyManagedMetadata(string path)
    {
        // Jellyfin ImageSaver writes these flat cache names; unknown files or subdirectories need review.
        try
        {
            if (!Directory.Exists(path))
            {
                return !File.Exists(path);
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                if ((File.GetAttributes(entry) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    return false;
                }

                var extension = Path.GetExtension(entry);
                if (!Scanners.MediaFormats.Pictures.Contains(extension) && !extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var name = Path.GetFileNameWithoutExtension(entry);
                if (ManagedImageNames.Contains(name))
                {
                    continue;
                }

                var prefix = name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                if (!prefix.Equals("backdrop", StringComparison.OrdinalIgnoreCase) && !prefix.Equals("screenshot", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Authorizes only managed artwork bound to an item in an enabled media library.</summary>
    /// <param name="path">Managed artwork path.</param>
    /// <param name="itemId">Owning item.</param>
    /// <param name="locations">Enabled media roots.</param>
    /// <param name="requireImage">Whether the owner's live image list must still reference the path.</param>
    /// <returns>Whether the artwork is in scope.</returns>
    internal bool IsArtworkInEnabledLocations(string path, Guid itemId, IReadOnlyList<string> locations, bool requireImage)
    {
        try
        {
            if (_applicationPaths is null || itemId == Guid.Empty
                || !IsUnder(Path.GetFullPath(path), _applicationPaths.InternalMetadataPath)
                || HasReparsePointAncestor(Path.GetFullPath(path)))
            {
                return false;
            }

            var item = _libraryManager.GetItemById(itemId);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return item is not null && !string.IsNullOrEmpty(item.Path)
                && IsInsideLibrary(item.Path) && IsInEnabledLocations(item.Path, locations)
                && (!requireImage || item.ImageInfos?.Any(image => image is not null
                    && string.Equals(image.Path, path, comparison)) == true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether a path is inside one of the server's library folders.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <returns>True when the path is inside a library.</returns>
    public bool IsInsideLibrary(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (MountedRecycleRoots.Contains(fullPath))
        {
            return false;
        }

        var libraryRoot = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations)
            .Where(location => IsUnder(fullPath, location))
            .OrderByDescending(location => location.Length).FirstOrDefault();
        if (libraryRoot is null)
        {
            return false;
        }

        // Lexical check passed; now defend against symlinks / NTFS junctions. Path.GetFullPath is purely
        // lexical, so a link planted inside a library that targets /etc or C:\Windows slips through.
        // Configured roots are trusted, including mounts or links used to reach them. Refuse links
        // strictly below the matched root so a planted link cannot escape the library.
        try
        {
            return !HasReparsePointAncestor(fullPath, Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot)));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "This method IS the security check — inspecting the caller-supplied path for reparse points is its purpose. Callers pass here specifically to validate the path before touching it.")]
    internal static bool HasReparsePointAncestor(string fullPath, string? libraryRoot = null)
    {
        var current = fullPath;
        while (!string.IsNullOrEmpty(current)
            && !string.Equals(current, libraryRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            FileSystemInfo? info = File.Exists(current) ? new FileInfo(current)
                : Directory.Exists(current) ? new DirectoryInfo(current)
                : null;

            if (info is not null && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                return false;
            }

            current = parent;
        }

        return false;
    }

    /// <summary>
    /// Returns the location prefixes (trailing-separator normalised) of every library the user has
    /// currently enabled via <c>PluginConfiguration.EnabledLibraries</c>. Empty or null
    /// <paramref name="enabledLibraries"/> yields every library's locations, matching the historical
    /// "empty = all" convention used by scanners. Callers use this list for cheap
    /// <c>StartsWith</c> scope checks against issue paths — mirrors the pattern already established
    /// in <see cref="Jellyfin.Plugin.MediaDash.ScheduledTasks.ScanTask"/>.
    /// </summary>
    /// <param name="enabledLibraries">Enabled library IDs from plugin configuration.</param>
    /// <returns>Location prefixes, each ending with a directory separator so <c>StartsWith</c> boundary-matches.</returns>
    public IReadOnlyList<string> GetEnabledLibraryLocations(string[]? enabledLibraries)
    {
        return Scanners.VirtualFolderIdentity.GetEnabledFolders(_libraryManager, enabledLibraries)
            .SelectMany(f => f.Locations)
            .Select(l => Path.TrimEndingDirectorySeparator(l) + Path.DirectorySeparatorChar)
            .ToList();
    }

    /// <summary>
    /// Checks whether a folder is itself a configured library root. Sweep-style deletes must
    /// refuse to touch a library root — treating it as an "empty per-movie folder" would try to
    /// recycle every movie underneath.
    /// </summary>
    /// <param name="folder">The folder to check.</param>
    /// <returns>True when the folder matches one of Jellyfin's virtual folder locations.</returns>
    public bool IsLibraryRoot(string folder)
    {
        string normalized;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        foreach (var loc in _libraryManager.GetVirtualFolders().SelectMany(f => f.Locations))
        {
            var locNorm = Path.TrimEndingDirectorySeparator(Path.GetFullPath(loc));
            if (string.Equals(locNorm, normalized, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Walks every configured library root and deletes any MediaDash sidecar file
    /// (<c>*.mediadash.tmp*</c>, <c>*.mediadash.new*</c>, or the hash-fallback variants).
    /// Intended to run at end of a fix cycle when no encode is active — anything present is orphaned
    /// from a crash or a hard-killed plugin instance where <c>TranscodeFixer.FixAsync</c>'s finally
    /// block didn't get to run.
    /// </summary>
    /// <param name="enabledLibraries">Enabled library IDs from plugin configuration. Null or
    /// empty falls through to every library (historical behavior); a non-empty list scopes the
    /// sweep to just those libraries so a sidecar in an excluded library isn't touched.</param>
    /// <returns>Path and byte-count of each deleted orphan, empty on a clean library.</returns>
    public IReadOnlyList<(string Path, long Bytes)> SweepOrphanSidecars(string[]? enabledLibraries = null)
    {
        var deleted = new List<(string, long)>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        // Bug-pattern audit round 4 FU-1 (2026-09-19): the sweep walked every library root
        // regardless of the user's EnabledLibraries setting. Owned sidecars sitting in a
        // library the user had explicitly excluded from MediaDash's scope were still deleted
        // by maintenance. The advertised scope has to hold end-to-end — queue selection now
        // respects it (round 1 fix), the sweep must too. Empty/null enabledLibraries falls
        // through to every library, matching the historical "empty = all" convention every
        // scanner uses.
        var scopedRoots = Scanners.VirtualFolderIdentity
            .GetEnabledFolders(_libraryManager, enabledLibraries)
            .SelectMany(f => f.Locations);
        foreach (var root in scopedRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            // AttributesToSkip = ReparsePoint defends against symlink cycles under a library root —
            // /movies/all -> /movies would otherwise spin the sweep until thread-pool starvation on
            // every FixTask cycle. IgnoreInaccessible ensures one unreadable subfolder doesn't abort
            // the whole sweep mid-walk.
            var enumOpts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true
            };
            foreach (var pattern in SidecarPatterns)
            {
                IEnumerable<string> matches;
                try
                {
                    matches = Directory.EnumerateFiles(root, pattern, enumOpts);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var path in matches)
                {
                    if (!seen.Add(path))
                    {
                        continue;
                    }

                    // Defense in depth: enumeration is scoped to library roots, but re-check on the
                    // exact path before deletion. Every other destructive path in the plugin passes
                    // through IsInsideLibrary; leaving this one out is the only inconsistency.
                    if (!IsInsideLibrary(path) || IsMoveReserved(path))
                    {
                        continue;
                    }

                    // Review P1-1: never delete a preserved swap-recovery copy. TranscodeFixer /
                    // TrackFixer rename the sidecar to a *.mediadash-recover-* pattern when the
                    // original has been disposed but the final rename failed — that file is the
                    // user's only remaining copy of the content. Belt-and-braces check: sweep
                    // patterns above already exclude that infix, but if a legacy `.mediadash.new`
                    // sidecar drifts across a build with a preserve marker, we still stop.
                    if (SwapRecoveryHelper.IsRecoveryCopy(path))
                    {
                        continue;
                    }

                    try
                    {
                        var size = new FileInfo(path).Length;
                        File.Delete(path);
                        deleted.Add((path, size));
                    }
                    catch (Exception)
                    {
                        // Best effort; file may vanish between enumeration and delete, or a permission bump
                        // may block us. Nothing to do — the next cycle picks it up.
                    }
                }
            }
        }

        return deleted;
    }

    /// <summary>
    /// Cheap prefix membership test used by <see cref="Jellyfin.Plugin.MediaDash.ScheduledTasks.FixTask"/>
    /// to scope its queue to currently-enabled libraries. Empty or null <paramref name="path"/>
    /// short-circuits to true — issues without a path are surfaced elsewhere in the pipeline and
    /// scoping them here would silently swallow them. Callers must pre-normalise
    /// <paramref name="enabledLocations"/> with a trailing separator (see
    /// <see cref="GetEnabledLibraryLocations"/>) so <c>StartsWith</c> is a boundary check.
    /// </summary>
    /// <param name="path">The candidate path.</param>
    /// <param name="enabledLocations">Normalised location prefixes from <see cref="GetEnabledLibraryLocations"/>.</param>
    /// <returns>True when the path sits inside one of the enabled locations, or is empty.</returns>
    public static bool IsInEnabledLocations(string? path, IReadOnlyList<string> enabledLocations)
    {
        if (string.IsNullOrEmpty(path))
        {
            return true;
        }

        for (var i = 0; i < enabledLocations.Count; i++)
        {
            if (path.StartsWith(enabledLocations[i], OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsUnder(string fullPath, string root)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!fullPath.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return false;
        }

        // If fullRoot already ends with a separator (i.e. it's a Windows drive root like "C:\" — .NET's
        // TrimEndingDirectorySeparator preserves that trailing slash because "C:" alone isn't a valid
        // rooted path), any StartsWith match is inherently a boundary match. Skipping the boundary
        // check here fixes SMART/library-drive detection on Windows: previously
        // IsUnder("C:\\Users\\...", "C:\\") returned false because fullPath[3] was 'U' (from "Users"),
        // not '\\', so FindDriveForPath returned null for every subpath of a drive root.
        if (fullRoot.Length > 0
            && (fullRoot[^1] == Path.DirectorySeparatorChar || fullRoot[^1] == Path.AltDirectorySeparatorChar))
        {
            return true;
        }

        // "D:\Movies2\x" must not match root "D:\Movies".
        return fullPath.Length == fullRoot.Length || fullPath[fullRoot.Length] == Path.DirectorySeparatorChar || fullPath[fullRoot.Length] == Path.AltDirectorySeparatorChar;
    }
}
