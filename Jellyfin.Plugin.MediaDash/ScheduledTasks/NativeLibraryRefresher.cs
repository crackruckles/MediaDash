using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Fixers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>Validates changed native membership before refreshing only explicitly affected targets.</summary>
public sealed class NativeLibraryRefresher : IDisposable
{
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly ILibraryManager _library;
    private readonly IFileSystem _files;
    private readonly ITaskManager? _tasks;
    private readonly Func<BaseItem, NativeFolderDirectoryService, CancellationToken, Task> _refresh;
    private readonly Func<BaseItem, NativeFolderDirectoryService, CancellationToken, Task> _refreshMove;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="NativeLibraryRefresher"/> class.</summary>
    /// <param name="library">Canonical native items and configured roots.</param>
    /// <param name="files">Native filesystem listings.</param>
    /// <param name="tasks">Native scan cancellation coordination.</param>
    public NativeLibraryRefresher(ILibraryManager library, IFileSystem files, ITaskManager tasks)
        : this(library, files, RefreshTargetAsync, tasks, RefreshMoveTargetAsync)
    {
    }

    internal NativeLibraryRefresher(
        ILibraryManager library,
        IFileSystem files,
        Func<BaseItem, NativeFolderDirectoryService, CancellationToken, Task> refresh,
        ITaskManager? tasks = null,
        Func<BaseItem, NativeFolderDirectoryService, CancellationToken, Task>? moveRefresh = null)
    {
        _library = library;
        _files = files;
        _tasks = tasks;
        _refresh = refresh;
        _refreshMove = moveRefresh ?? refresh;
    }

    internal Task RefreshAsync(IEnumerable<string> changedPaths, IEnumerable<Guid> itemIds, CancellationToken cancellationToken)
        => RefreshCoreAsync(changedPaths, itemIds, _refresh, preserveMovePayload: false, cancellationToken);

    internal Task RefreshMoveAsync(IEnumerable<string> changedPaths, IEnumerable<Guid> itemIds, CancellationToken cancellationToken)
        => RefreshCoreAsync(changedPaths, itemIds, _refreshMove, preserveMovePayload: true, cancellationToken);

    private async Task RefreshCoreAsync(
        IEnumerable<string> changedPaths,
        IEnumerable<Guid> itemIds,
        Func<BaseItem, NativeFolderDirectoryService, CancellationToken, Task> refresh,
        bool preserveMovePayload,
        CancellationToken cancellationToken)
    {
        using var nativeScan = new LibraryScanCancellation(_tasks, cancellationToken);
        var token = nativeScan.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var roots = _library.GetVirtualFolders().SelectMany(folder => folder.Locations ?? [])
                .Where(Path.IsPathFullyQualified).Select(Path.GetFullPath).Distinct(Paths).ToArray();
            var paths = changedPaths.Select(Absolute).Distinct(Paths).ToArray();
            var ids = itemIds.Where(id => id != Guid.Empty).Distinct().ToArray();
            if (paths.Any(path => !roots.Any(root => LibraryGuard.IsUnder(path, root)) || roots.Contains(path, Paths)))
            {
                throw new InvalidOperationException("Changed paths must identify items inside a configured library. A library root cannot be refreshed as a changed subtree.");
            }

            var indexed = new HashSet<string>(Paths);
            var sidecarOwners = new List<BaseItem>();
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                var parent = Path.GetDirectoryName(path)!;
                var chain = new Stack<string>();
                Folder? anchor = null;
                while (!string.IsNullOrEmpty(parent) && roots.Any(root => LibraryGuard.IsUnder(parent, root)))
                {
                    anchor = Canonical(parent) as Folder;
                    if (anchor is not null && Directory.Exists(parent))
                    {
                        break;
                    }

                    if (Directory.Exists(parent))
                    {
                        chain.Push(parent);
                    }

                    parent = Path.GetDirectoryName(parent);
                }

                if (anchor is null)
                {
                    throw new InvalidOperationException("The changed path has no existing native physical parent. Run Jellyfin's library scan to finish indexing.");
                }

                await IndexAsync(anchor).ConfigureAwait(false);
                while (chain.TryPop(out var child))
                {
                    var folder = Canonical(child) as Folder;
                    if (folder is null)
                    {
                        // Movie resolvers may represent a physical movie directory only
                        // through its contained native Movie. It needs no synthetic Folder
                        // or recursive ancestor refresh once an explicit target or the
                        // still-present owner of a removed sidecar is indexed.
                        if (paths.Where(path => LibraryGuard.IsUnder(path, child)).Select(Canonical)
                            .Concat(ids.Select(_library.GetItemById))
                            .Any(item => item is not null && !item.IsFolder && !string.IsNullOrWhiteSpace(item.Path)
                                && LibraryGuard.IsUnder(item.Path, child) && (File.Exists(item.Path) || Directory.Exists(item.Path))))
                        {
                            break;
                        }

                        if (!preserveMovePayload && Paths.Equals(child, Path.GetDirectoryName(path))
                            && Path.GetExtension(path).Equals(".nfo", StringComparison.OrdinalIgnoreCase) && !File.Exists(path))
                        {
                            var owners = Directory.EnumerateFiles(child, "*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint })
                                .Where(file => Paths.Equals(Path.GetFileNameWithoutExtension(file), Path.GetFileNameWithoutExtension(path)))
                                .Select(Canonical).OfType<Video>().Take(2).ToArray();
                            if (owners.Length == 1)
                            {
                                sidecarOwners.Add(owners[0]);
                                break;
                            }
                        }

                        // After indexing the real ancestor, a removed non-catalog file has no membership left to repair.
                        if (!preserveMovePayload && !File.Exists(path) && !Directory.Exists(path) && Canonical(path) is null)
                        {
                            break;
                        }

                        throw new InvalidOperationException($"Jellyfin did not index parent '{child}' for changed path '{path}'. Native indexing remains pending.");
                    }

                    await IndexAsync(folder).ConfigureAwait(false);
                }
            }

            foreach (var missing in paths.Where(path => !File.Exists(path) && !Directory.Exists(path)))
            {
                if (Canonical(missing) is not null)
                {
                    throw new InvalidOperationException("The missing source still has a native catalog entry. Native indexing remains pending.");
                }
            }

            // All affected parent membership must finish before provider/user-data work.
            var targets = paths.Select(Canonical).Where(item => item is not null).Cast<BaseItem>()
                .Concat(ids.Select(_library.GetItemById).Where(item => item is not null).Cast<BaseItem>())
                .Concat(sidecarOwners)
                .Where(item => !string.IsNullOrWhiteSpace(item.Path) && roots.Any(root => LibraryGuard.IsUnder(item.Path, root))
                    && !roots.Contains(item.Path, Paths) && (File.Exists(item.Path) || Directory.Exists(item.Path)))
                .DistinctBy(item => item.Id).ToArray();
            var subtreeTargets = targets.OfType<Folder>().Where(item => paths.Contains(item.Path, Paths)).ToArray();
            if (preserveMovePayload && targets.Any(item =>
            {
                var options = _library.GetLibraryOptions(item);
                return options is null || options.SaveLocalMetadata || options.MetadataSavers?.Length > 0;
            }))
            {
                // ValidationOnly still imports remote artwork from local NFO providers,
                // and repository updates can invoke local metadata savers. Keep the
                // indexed membership, but do not mutate the completed move inventory.
                throw new MoveMetadataRefreshDeferredException();
            }

            foreach (var item in targets)
            {
                token.ThrowIfCancellationRequested();
                // Ancestor IDs collected to repair membership do not authorize refreshing
                // their unrelated descendants. Only explicit changed folders are subtrees.
                if (item.IsFolder && !paths.Contains(item.Path, Paths))
                {
                    continue;
                }

                // Explicit folders are refreshed recursively. Their bound
                // children need no second provider pass, while separate
                // selected targets still receive their own metadata refresh.
                if (subtreeTargets.Any(folder => folder.Id != item.Id && LibraryGuard.IsUnder(item.Path, folder.Path)))
                {
                    continue;
                }

                var directory = item.IsFolder ? item.Path : Path.GetDirectoryName(item.Path)!;
                await refresh(item, NativeFolderDirectoryService.Create(directory, _files), token).ConfigureAwait(false);
            }

            async Task IndexAsync(Folder folder)
            {
                if (folder is CollectionFolder || folder.Id == Guid.Empty || !Directory.Exists(folder.Path))
                {
                    throw new InvalidOperationException("Native membership requires an existing canonical physical folder.");
                }

                if (indexed.Contains(folder.Path))
                {
                    return;
                }

                var listing = NativeFolderDirectoryService.Create(folder.Path, _files);
                if (roots.Contains(folder.Path, Paths) && !listing.Native.IsAccessible(folder.Path))
                {
                    throw new InvalidOperationException("Jellyfin skips empty or inaccessible library roots. Native indexing remains pending; no catalog removal was assumed.");
                }

                await NativeFolderIndex.IndexOnlyAsync(folder, listing, new Progress<double>(), token).ConfigureAwait(false);
                indexed.Add(folder.Path);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private BaseItem? Canonical(string path)
    {
        var found = _library.FindByPath(path, null);
        var item = found is null ? null : _library.GetItemById(found.Id);
        return item is not null && Paths.Equals(path, item.Path) ? item : null;
    }

    private static string Absolute(string path) => Path.IsPathFullyQualified(path) ? Path.GetFullPath(path)
        : throw new InvalidOperationException("Native refresh requires absolute changed paths.");

    internal static Task RefreshTargetAsync(BaseItem item, NativeFolderDirectoryService directory, CancellationToken token)
        => RefreshItemAsync(item, directory, preserveLocalMetadata: false, token);

    internal static Task RefreshMoveTargetAsync(BaseItem item, NativeFolderDirectoryService directory, CancellationToken token)
        => RefreshItemAsync(item, directory, preserveLocalMetadata: true, token);

    private static async Task RefreshItemAsync(BaseItem item, NativeFolderDirectoryService directory, bool preserveLocalMetadata, CancellationToken token)
    {
        var options = new MetadataRefreshOptions(directory.Native)
        {
            MetadataRefreshMode = preserveLocalMetadata ? MetadataRefreshMode.ValidationOnly : MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = preserveLocalMetadata ? MetadataRefreshMode.ValidationOnly : MetadataRefreshMode.Default,
            ForceSave = !preserveLocalMetadata
        };
        await item.RefreshMetadata(options, token).ConfigureAwait(false);
        if (item is Folder folder)
        {
            await folder.ValidateChildren(new Progress<double>(), options, recursive: true, allowRemoveRoot: false, cancellationToken: token).ConfigureAwait(false);
        }
    }

    internal sealed class MoveMetadataRefreshDeferredException : InvalidOperationException
    {
        internal MoveMetadataRefreshDeferredException()
            : base("Native membership was updated, but metadata refresh is pending because media-folder writing is enabled or its settings are unavailable.")
        {
        }
    }
}
