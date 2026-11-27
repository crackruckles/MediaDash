using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Scanners;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("Diagnostics")]
public sealed class MediaMoveExecutionTests : IDisposable
{
    private readonly Plugin? _previous = Plugin.Instance;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-move-execute-" + Guid.NewGuid().ToString("N"));
    private readonly PluginConfiguration _config = new() { DryRun = false, UngroupedFixMode = FixMode.ManualApprove, MisplacedFixMode = FixMode.ManualApprove };
    private readonly List<BaseItem> _items = [];
    private readonly List<string> _locations = [];
    private string? _externalTarget;
    private bool _catalogFailure;
    private bool _detachedPathLookups;
    private bool _saveLocalMetadata;
    private readonly MediaDashDb _db;
    private readonly ILibraryManager _manager;
    private readonly LibraryGuard _guard;
    private readonly ProtectionService _protection;
    private readonly MediaMovePlanner _planner;
    private readonly MediaMoveService _service;

    public MediaMoveExecutionTests()
    {
        Directory.CreateDirectory(Movies);
        Directory.CreateDirectory(Tv);
        _locations.AddRange([Movies, Tv]);
        _config.MoviesTargetPath = Movies;
        _config.TvTargetPath = Tv;
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, _config);
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, plugin);
        _manager = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)_manager).Call = (method, args) => method.Name switch
        {
            "GetVirtualFolders" => new List<VirtualFolderInfo> { new() { Locations = _locations.ToArray() } },
            "GetItemList" => _items,
            "GetLibraryOptions" => new MediaBrowser.Model.Configuration.LibraryOptions { SaveLocalMetadata = _saveLocalMetadata },
            "FindByPath" => FindNativePath(args![0] as string),
            "GetItemById" => _catalogFailure ? throw new InvalidOperationException("Native catalog unavailable")
                : _items.FirstOrDefault(item => args![0] is Guid id && item.Id == id),
            _ => null
        };
        _db = new MediaDashDb(Path.Combine(_root, "moves.db"));
        _guard = new LibraryGuard(_manager);
        _protection = new ProtectionService(_db.Protection, _manager);
        _planner = new MediaMovePlanner(_guard, _protection, _manager, _db.Moves);
        var paths = DispatchProxy.Create<MediaBrowser.Common.Configuration.IApplicationPaths, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)paths).Call = (_, _) => _root;
        _service = new MediaMoveService(_db, _guard, _protection, _manager, NullLogger<MediaMoveService>.Instance,
            recycleBin: new RecycleBin(paths, NullLogger<RecycleBin>.Instance, _db.History));
    }

    private BaseItem? FindNativePath(string? path)
    {
        var item = _items.FirstOrDefault(candidate => candidate.Path == path);
        return _detachedPathLookups && item is Movie movie ? new Movie { Id = movie.Id, Name = movie.Name, Path = movie.Path } : item;
    }

    private string Movies => Path.Combine(_root, "movies");
    private string Tv => Path.Combine(_root, "tv");

    [Fact]
    public void SameFilesystemCompatibilityMoveKeepsAnOpenWritersBytesAccessibleAtDestination()
    {
        var source = Write(Path.Combine(Movies, "writer.opaque"), "approved payload");
        var target = Path.Combine(Tv, "writer.opaque");
        using var writer = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        Api.FileBrowserController.CrossDeviceMove(source, target, sourceIsDir: false);
        writer.Position = 0;
        writer.Write(System.Text.Encoding.UTF8.GetBytes("UPDATED!"));
        writer.Flush();
        using var input = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(input);
        Assert.StartsWith("UPDATED!", reader.ReadToEnd(), StringComparison.Ordinal);
        Assert.False(File.Exists(source));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task UnsupportedCopyDeleteMoveIsBlockedInPreviewAndOldApprovalsBeforeJournal(bool directory, bool oldApproval)
    {
        var source = directory ? Path.Combine(Movies, "selected-folder") : Path.Combine(Movies, "selected.opaque");
        var payload = Write(directory ? Path.Combine(source, "payload.opaque") : source, "approved payload");
        var targetRoot = CreateCopyDeleteRefusalRoot();
        _locations.Add(targetRoot);
        var destination = Path.Combine(targetRoot, Path.GetFileName(source));
        var plan = await _planner.BuildExplicitAsync([(source, destination)], _items, _config, CancellationToken.None);
        if (!oldApproval)
        {
            Assert.Contains(plan.BlockingReasons, reason => reason.Contains("different drive", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            plan = (plan with { BlockingReasons = [] }).Seal();
            var result = await _service.ExecuteExplicitAsync(plan, plan.Fingerprint, null, CancellationToken.None);
            Assert.False(result.Success);
            Assert.Equal(FixReason.MoveNeedsReview, result.Reason);
            Assert.Contains("different drive", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(0, OperationCount());
        Assert.Equal("approved payload", File.ReadAllText(payload));
        Assert.False(File.Exists(destination));
        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.GetFileSystemEntries(_externalTarget!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossFilesystemRecycleRefusesBeforeCreatingABatchAndPreservesSource(bool directory)
    {
        var root = CreateCopyDeleteRefusalRoot();
        var source = directory ? Path.Combine(root, "owned-folder") : Path.Combine(root, "owned.opaque");
        var payload = Write(directory ? Path.Combine(source, "payload") : source, "original bytes");
        var dataRoot = Path.Combine(_root, "data");
        var paths = DispatchProxy.Create<MediaBrowser.Common.Configuration.IApplicationPaths, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)paths).Call = (method, _) => method.Name == "get_DataPath" ? dataRoot : null;
        var bin = new RecycleBin(paths, NullLogger<RecycleBin>.Instance, _db.History);

        var error = Assert.Throws<IOException>(() => bin.MoveToBin(source));

        Assert.Contains("different drive", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("original bytes", File.ReadAllText(payload));
        Assert.False(Directory.Exists(dataRoot));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameFilesystemRecycleAndRestoreStillPreserveCompletePayloads(bool directory)
    {
        var source = directory ? Path.Combine(Movies, "owned-folder") : Path.Combine(Movies, "owned.opaque");
        var payload = Write(directory ? Path.Combine(source, "payload") : source, "original bytes");
        if (directory) Directory.CreateDirectory(Path.Combine(source, "empty"));
        var paths = DispatchProxy.Create<MediaBrowser.Common.Configuration.IApplicationPaths, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)paths).Call = (method, _) => method.Name == "get_DataPath" ? _root : null;
        var bin = new RecycleBin(paths, NullLogger<RecycleBin>.Instance, _db.History);

        var recycled = bin.MoveToBin(source);
        Assert.False(File.Exists(source) || Directory.Exists(source));
        Assert.Equal("original bytes", File.ReadAllText(directory ? Path.Combine(recycled, "payload") : recycled));
        bin.Restore(recycled, source);

        Assert.Equal("original bytes", File.ReadAllText(payload));
        if (directory) Assert.True(Directory.Exists(Path.Combine(source, "empty")));
        Assert.False(File.Exists(recycled) || Directory.Exists(recycled));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinuxBindMountRecycleAndRestoreUseTheSourceMount(bool libraryRootFallback)
    {
        var mount = Environment.GetEnvironmentVariable("MEDIADASH_TEST_RECYCLE_BIND_MOUNT");
        if (mount is null)
        {
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new InvalidOperationException("The recycle bind mount fixture requires Linux.");
        }

        Assert.Equal("MediaDash owned recycle mount", File.ReadAllText(Path.Combine(mount, ".owned-fixtures")).Trim());
        var library = Path.Combine(mount, Guid.NewGuid().ToString("N"));
        _locations.Add(library);
        var source = Path.Combine(library, libraryRootFallback ? "movie" : "original.mkv");
        var payload = Write(libraryRootFallback ? Path.Combine(source, "original.mkv") : source, "original bytes");
        var paths = DispatchProxy.Create<MediaBrowser.Common.Configuration.IApplicationPaths, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)paths).Call = (method, _) => method.Name == "get_DataPath" ? _root : null;
        RecycleBin OpenBin() => new(paths, NullLogger<RecycleBin>.Instance, _db.History, _manager);

        var permissions = File.GetUnixFileMode(mount);
        if (libraryRootFallback)
        {
            File.SetUnixFileMode(mount, permissions & ~UnixFileMode.UserWrite);
        }

        try
        {
            var recycled = OpenBin().MoveToBin(source);
            Assert.True(LibraryGuard.IsUnder(recycled, libraryRootFallback ? library : mount));
            Assert.False(File.Exists(source) || Directory.Exists(source));
            Assert.False(_guard.IsInsideLibrary(recycled));
            Assert.True(_protection.IsScannerBlocked(recycled, IssueType.Duplicate));
            var reopened = OpenBin();
            Assert.True(reopened.IsOwnedRecoveryPath(recycled, source));
            Assert.Equal(recycled, Assert.Single(reopened.ListContents()).BinPath);
            Assert.Equal(1, reopened.GetContents().FileCount);
            File.WriteAllText(source, "new occupant");
            Assert.Throws<IOException>(() => reopened.Restore(recycled, source));
            Assert.Equal("new occupant", File.ReadAllText(source));
            File.Delete(source);
            reopened.Restore(recycled, source);
            Assert.Equal("original bytes", File.ReadAllText(payload));
            Assert.Empty(reopened.ListContents());

            var expired = reopened.MoveToBin(source);
            Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(expired)!, DateTime.UtcNow.AddDays(-2));
            OpenBin().Purge(1);
            Assert.False(File.Exists(expired) || Directory.Exists(expired));
        }
        finally
        {
            if (libraryRootFallback)
            {
                File.SetUnixFileMode(mount, permissions);
            }
        }
    }

    [Fact]
    public void LinuxNativeRenameExplainsCrossMountRefusalAndPreservesSource()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var source = Write(Path.Combine(Movies, "native-refusal.mkv"), "original bytes");
        var target = Path.Combine(CreateCopyDeleteRefusalRoot(), "native-refusal.mkv");
        var error = Assert.Throws<IOException>(() => MediaMoveSafety.RenameNoCopy(source, target, directory: false));
        Assert.Equal(MediaMoveSafety.UnsafeSourceDeletionMessage, error.Message);
        Assert.Equal("original bytes", File.ReadAllText(source));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void ManagedMetadataUsesLocalRecycleWhenMediaBinIsOnAnotherMount()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var dataRoot = CreateCopyDeleteRefusalRoot();
        _config.RecycleBinPath = Path.Combine(_root, "media-recycle");
        var paths = DispatchProxy.Create<MediaBrowser.Common.Configuration.IApplicationPaths, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)paths).Call = (method, _) => method.Name == "get_DataPath" ? dataRoot : null;
        var bin = new RecycleBin(paths, NullLogger<RecycleBin>.Instance, _db.History);
        var metadata = Path.Combine(dataRoot, "metadata", "owned");
        var source = Write(Path.Combine(metadata, "poster.jpg"), "metadata bytes");
        var media = Write(Path.Combine(_root, "movie.mkv"), "media bytes");
        Assert.Throws<IOException>(() => bin.MoveToBin(metadata));
        Assert.True(File.Exists(source));

        var metadataEntry = bin.MoveManagedMetadataToBin(metadata);
        var mediaEntry = bin.MoveToBin(media);
        Assert.True(LibraryGuard.IsUnder(metadataEntry, Path.Combine(dataRoot, "mediadash", "metadata-recycle")));
        Assert.True(bin.IsOwnedRecoveryPath(metadataEntry, metadata));
        Assert.Equal(2, bin.ListContents().Count);
        Assert.Equal(2, bin.GetContents().FileCount);
        _db.History.AddHistory(new HistoryEntry
        {
            Type = IssueType.OrphanedDebris,
            Path = metadata,
            RecyclePath = metadataEntry,
            Action = "Recycled metadata",
            Success = true
        });
        Assert.Empty(bin.DiscoverOtherBinRoots(_db.History));
        bin.Restore(metadataEntry, metadata);
        bin.Restore(mediaEntry, media);
        Assert.Equal("metadata bytes", File.ReadAllText(source));
        Assert.Equal("media bytes", File.ReadAllText(media));
        Assert.Empty(bin.ListContents());

        var foreign = Write(Path.Combine(dataRoot, "mediadash", "metadata-recycle", "20260101-000000-000-01234567", "foreign.txt"), "foreign bytes");
        metadataEntry = bin.MoveManagedMetadataToBin(metadata);
        mediaEntry = bin.MoveToBin(media);
        bin.EmptyAll();
        Assert.False(Directory.Exists(metadataEntry));
        Assert.False(File.Exists(mediaEntry));
        Assert.Equal("foreign bytes", File.ReadAllText(foreign));
    }

    private string CreateCopyDeleteRefusalRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            _externalTarget = Path.Combine(_root, "unc-refusal-target");
            Directory.CreateDirectory(_externalTarget);
            return @"\\localhost\C$\" + _externalTarget[3..];
        }
        else
        {
            Assert.True(OperatingSystem.IsLinux() && Directory.Exists("/dev/shm"));
            _externalTarget = Path.Combine("/dev/shm", "md-refusal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_externalTarget);
            return _externalTarget;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MoveSuppressesItsKnownWatcherPathsAndRestoresStateOnlyAfterBoundedIndex(bool indexFails, bool destinationMissingFromCatalog)
    {
        var issue = await Approve(companions: true);
        var plan = MediaMovePlan.ReadIssue(issue)!;
        var user = new MoveTestUser(Guid.NewGuid());
        var data = new MoveTestUserData();
        var sourceItem = _items.Single();
        data.GetUserData(user, sourceItem).Played = true;
        var preserver = new MediaMoveUserData(_manager, _db.Moves, new MoveTestUsers(user), data, NullLogger<MediaMoveUserData>.Instance);
        var watch = new List<(string Method, string Path, bool Refresh)>();
        var monitor = DispatchProxy.Create<ILibraryMonitor, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)monitor).Call = (method, args) =>
        {
            watch.Add((method.Name, (string)args![0]!, args.Length > 1 && (bool)args[1]!));
            return null;
        };
        var root = new BoundedMoveFolder { Id = Guid.NewGuid(), Path = Movies };
        Movie? targetItem = null;
        root.Index = () =>
        {
            if (indexFails) throw new IOException("owned native indexing failed");
            _items.Remove(sourceItem);
            var destination = plan.Entries.Single(entry => entry.Source == issue.Path).Destination;
            targetItem = new Movie { Id = Guid.NewGuid(), Path = destination, Name = "Film" };
            _items.Add(new BoundedMoveFolder { Id = Guid.NewGuid(), Path = Path.GetDirectoryName(destination)! });
            if (!destinationMissingFromCatalog)
            {
                _items.Add(targetItem);
            }
        };
        _items.Add(root);
        var files = DispatchProxy.Create<MediaBrowser.Model.IO.IFileSystem, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)files).Call = (method, args) => method.Name switch
        {
            "GetFileSystemEntries" => Directory.EnumerateFileSystemEntries((string)args![0]!).Select(path => new MediaBrowser.Model.IO.FileSystemMetadata
                { FullName = path, Name = Path.GetFileName(path), Exists = true, IsDirectory = Directory.Exists(path) }).ToArray(),
            "GetFileSystemEntryPaths" => Directory.EnumerateFileSystemEntries((string)args![0]!),
            _ => null
        };
        var metadataFinished = false;
        using var native = new NativeLibraryRefresher(_manager, files, (item, _, _) =>
        {
            Assert.False(data.GetUserData(user, item).Played);
            metadataFinished = true;
            return Task.CompletedTask;
        });
        var service = new MediaMoveService(_db, _guard, _protection, _manager, NullLogger<MediaMoveService>.Instance,
            destinationMissingFromCatalog ? null : preserver, native, monitor);
        var result = await service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.All(plan.Entries, entry => Assert.Contains(watch, call => call.Method == "ReportFileSystemChangeBeginning" && call.Path == entry.Source));
        var beginnings = watch.Where(call => call.Method == "ReportFileSystemChangeBeginning").Select(call => call.Path).Order().ToArray();
        var completions = watch.Where(call => call.Method == "ReportFileSystemChangeComplete").ToArray();
        Assert.Equal(beginnings, completions.Select(call => call.Path).Order());
        Assert.All(completions, call => Assert.False(call.Refresh));
        Assert.DoesNotContain(watch, call => call.Method == "ReportFileSystemChanged");
        Assert.DoesNotContain(beginnings, path => path == Movies);
        if (destinationMissingFromCatalog)
        {
            Assert.False(metadataFinished);
            Assert.Contains("finish indexing", result.Message, StringComparison.Ordinal);
        }
        else if (indexFails)
        {
            Assert.False(metadataFinished);
            Assert.Single(_db.Moves.GetPendingUserData(null));
            Assert.Contains("index", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.True(metadataFinished);
            Assert.True(data.GetUserData(user, targetItem!).Played);
            Assert.Empty(_db.Moves.GetPendingUserData(null));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WholeFolderMoveRefreshesTheBoundCatalogPayloadWhenTheNativeResolverDoesNotCreateAFolder(bool audio, bool metadataFails)
    {
        var directory = Path.Combine(Movies, "Owned complete item");
        var destination = Path.Combine(Movies, "Renamed complete item");
        var filename = audio ? "Book.m4b" : "Comic.cbz";
        var source = Write(Path.Combine(directory, filename), "owned primary payload");
        Write(Path.Combine(directory, "notes.txt"), "owned user notes");
        BaseItem original = audio ? new MediaBrowser.Controller.Entities.Audio.Audio() : new Book();
        original.Id = Guid.NewGuid();
        original.Path = source;
        original.Name = "Owned complete item";
        _items.Add(original);
        var root = new BoundedMoveFolder { Id = Guid.NewGuid(), Path = Movies };
        _items.Add(root);
        var unrelated = new Book { Id = Guid.NewGuid(), Path = Write(Path.Combine(Movies, "Unrelated", "Other.epub"), "unrelated book") };
        _items.Add(unrelated);
        var plan = await _planner.BuildExplicitAsync([(directory, destination)], _items, _config, CancellationToken.None);
        Assert.Empty(plan.BlockingReasons);
        Assert.Equal(source, Assert.Single(plan.ItemBindings).Path);
        var targetPath = Path.Combine(destination, filename);
        root.Index = () =>
        {
            _items.Remove(original);
            if (_items.Any(item => item.Path == targetPath)) return;
            BaseItem target = audio ? new MediaBrowser.Controller.Entities.Audio.Audio() : new Book();
            target.Id = Guid.NewGuid();
            target.Path = targetPath;
            target.Name = "Owned complete item";
            _items.Add(target);
        };
        var files = DispatchProxy.Create<MediaBrowser.Model.IO.IFileSystem, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)files).Call = (method, args) => method.Name switch
        {
            "GetFileSystemEntries" => Directory.EnumerateFileSystemEntries((string)args![0]!).Select(path => new MediaBrowser.Model.IO.FileSystemMetadata
                { FullName = path, Name = Path.GetFileName(path), Exists = true, IsDirectory = Directory.Exists(path) }).ToArray(),
            "GetFileSystemEntryPaths" => Directory.EnumerateFileSystemEntries((string)args![0]!),
            _ => null
        };
        var refreshed = new List<string>();
        using var native = new NativeLibraryRefresher(_manager, files, (item, _, _) =>
        {
            refreshed.Add(item.Path);
            if (metadataFails) throw new IOException("owned target metadata unavailable");
            return Task.CompletedTask;
        });
        var service = new MediaMoveService(_db, _guard, _protection, _manager, NullLogger<MediaMoveService>.Instance, nativeRefresh: native);

        var result = await service.ExecuteExplicitAsync(plan, plan.Fingerprint, null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal("owned primary payload", File.ReadAllText(targetPath));
        Assert.Equal("owned user notes", File.ReadAllText(Path.Combine(destination, "notes.txt")));
        Assert.Equal(new[] { targetPath }, refreshed);
        Assert.Equal("unrelated book", File.ReadAllText(unrelated.Path));
        if (metadataFails) Assert.Contains("finish indexing", result.Message, StringComparison.Ordinal);
        else Assert.DoesNotContain("finish indexing", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NativeMoveRefreshAvoidsForcedNfoSaveAndAllowsImmediateUndo(bool saveLocalMetadata, bool userDataFailure)
    {
        _saveLocalMetadata = saveLocalMetadata;
        var directory = Path.Combine(Movies, "Owned native metadata");
        var destination = Path.Combine(Movies, "Renamed native metadata");
        var source = Write(Path.Combine(directory, "Book.epub"), "owned book bytes");
        var sidecar = Write(Path.Combine(directory, "Book.nfo"), "owned original metadata");
        var original = new Book { Id = Guid.NewGuid(), Name = "Owned book", Path = source };
        _items.Add(original);
        var user = new MoveTestUser(Guid.NewGuid());
        var data = new MoveTestUserData { FailSaves = userDataFailure };
        data.GetUserData(user, original).Played = true;
        var preserver = new MediaMoveUserData(_manager, _db.Moves, new MoveTestUsers(user), data, NullLogger<MediaMoveUserData>.Instance);
        var root = new BoundedMoveFolder { Id = Guid.NewGuid(), Path = Movies };
        _items.Add(root);
        var target = Path.Combine(destination, "Book.epub");
        root.Index = () =>
        {
            _items.RemoveAll(item => item.Path == source || item.Path == target);
            var path = File.Exists(target) ? target : source;
            _items.Add(new Book { Id = Guid.NewGuid(), Name = "Owned book", Path = path });
        };
        var files = DispatchProxy.Create<MediaBrowser.Model.IO.IFileSystem, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)files).Call = (method, args) => method.Name switch
        {
            "GetFileSystemEntries" => Directory.EnumerateFileSystemEntries((string)args![0]!).Select(path => new MediaBrowser.Model.IO.FileSystemMetadata
                { FullName = path, Name = Path.GetFileName(path), Exists = true, IsDirectory = Directory.Exists(path) }).ToArray(),
            "GetFileSystemEntryPaths" => Directory.EnumerateFileSystemEntries((string)args![0]!),
            _ => null
        };
        var providers = DispatchProxy.Create<MediaBrowser.Controller.Providers.IProviderManager, OriginalLanguageFlowTests.CallProxy>();
        var refreshes = 0;
        ((OriginalLanguageFlowTests.CallProxy)(object)providers).Call = (method, args) =>
        {
            Assert.Equal("RefreshSingleItem", method.Name);
            var item = (BaseItem)args![0]!;
            var options = (MediaBrowser.Controller.Providers.MetadataRefreshOptions)args[1]!;
            refreshes++;
            // Native savers write local metadata when refresh forces a metadata download.
            if (saveLocalMetadata || options.ForceSave || options.MetadataRefreshMode > MediaBrowser.Controller.Providers.MetadataRefreshMode.ValidationOnly)
            {
                File.WriteAllText(Path.ChangeExtension(item.Path, ".nfo"), "native rewritten metadata");
            }

            return Task.FromResult(ItemUpdateType.MetadataImport);
        };
        var oldProviders = BaseItem.ProviderManager;
        try
        {
            BaseItem.ProviderManager = providers;
            using var native = new NativeLibraryRefresher(_manager, files, NativeLibraryRefresher.RefreshTargetAsync,
                moveRefresh: NativeLibraryRefresher.RefreshMoveTargetAsync);
            var service = new MediaMoveService(_db, _guard, _protection, _manager, NullLogger<MediaMoveService>.Instance, preserver, nativeRefresh: native);
            var plan = await _planner.BuildExplicitAsync([(directory, destination)], _items, _config, CancellationToken.None);
            var moved = await service.ExecuteExplicitAsync(plan, plan.Fingerprint, null, CancellationToken.None);
            Assert.True(moved.Success, moved.Message);
            if (userDataFailure)
            {
                Assert.Contains("watched state", moved.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(MediaMoveManifest.TryRead(_db.Moves.Read(moved.MoveOperationId!)!.ManifestJson, moved.MoveOperationId!)!.UserData, snapshot => !snapshot.Applied);
            }
            if (saveLocalMetadata)
            {
                Assert.Equal(0, refreshes);
                Assert.Contains("sidecars", moved.Message, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                Assert.True(refreshes > 0);
            }
            Assert.Equal("owned original metadata", File.ReadAllText(Path.Combine(destination, "Book.nfo")));
            var undo = await service.UndoAsync(moved.MoveOperationId!, null, CancellationToken.None);
            Assert.True(undo.Success, undo.Message);
            Assert.Equal("owned book bytes", File.ReadAllText(source));
            Assert.Equal("owned original metadata", File.ReadAllText(sidecar));
            await native.RefreshAsync([source], [], CancellationToken.None);
            Assert.Equal("native rewritten metadata", File.ReadAllText(sidecar));
        }
        finally
        {
            BaseItem.ProviderManager = oldProviders;
        }
    }

    private sealed class BoundedMoveFolder : Folder
    {
        internal Action? Index { get; set; }

        protected override Task ValidateChildrenInternal(IProgress<double> progress, bool recursive, bool refreshChildMetadata, bool allowRemoveRoot,
            MediaBrowser.Controller.Providers.MetadataRefreshOptions refreshOptions, MediaBrowser.Controller.Providers.IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            Assert.False(recursive);
            Assert.False(refreshChildMetadata);
            Assert.False(allowRemoveRoot);
            Index?.Invoke();
            return Task.CompletedTask;
        }
    }

    private static string Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<Issue> Approve(bool directory = false, bool companions = false, bool misplaced = false, Action<string>? beforeScan = null)
    {
        var source = directory ? Path.Combine(Tv, "Film", "Film.mkv") : Path.Combine(misplaced ? Tv : Movies, "Film.mkv");
        _items.Add(new Movie { Id = Guid.NewGuid(), Name = "Film", Path = Write(source, "original video") });
        if (companions)
        {
            Write(Path.ChangeExtension(source, ".en.srt"), "original subtitles");
            Write(Path.ChangeExtension(source, ".nfo"), "original metadata");
        }

        if (directory)
        {
            Write(Path.Combine(Tv, "Film", "notes.txt"), "user notes");
            Directory.CreateDirectory(Path.Combine(Tv, "Film", "empty"));
        }

        beforeScan?.Invoke(source);

        var type = directory || misplaced ? IssueType.Misplaced : IssueType.Ungrouped;
        var found = await _planner.ScanAsync(type, _items, _config, new Progress<double>(), CancellationToken.None);
        var issue = Assert.Single(found);
        Assert.Empty(MediaMovePlan.ReadIssue(issue)!.BlockingReasons);
        _db.Issues.ReplaceDetectedIssues(type, [issue]);
        issue = Assert.Single(_db.Issues.GetIssues(type));
        Assert.Equal(StatusTransitionResult.Applied, _db.Issues.TryTransitionIssueStatus(issue.Id, [IssueStatus.Detected], IssueStatus.Queued));
        return _db.Issues.GetIssue(issue.Id)!;
    }

    [Fact]
    public async Task SidecarRepairRunsAtItsOriginalLocationBeforeAnApprovedFolderMove()
    {
        var move = await Approve(companions: true);
        var sidecar = Path.ChangeExtension(move.Path, ".nfo");
        _db.Issues.ReplaceDetectedIssues(IssueType.CorruptNfo, [new Issue { Type = IssueType.CorruptNfo, Path = sidecar, ItemId = move.ItemId }]);
        var repair = Assert.Single(_db.Issues.GetIssues(IssueType.CorruptNfo));
        _config.NfoDisposal = DisposalMethod.Permanent;
        var plans = FixPlanBuilder.Build([move, repair], type => type == IssueType.Ungrouped ? 0 : 1, _ => 0, StringComparer.Ordinal);
        Assert.Equal(new[] { repair.Id, move.Id }, plans.Select(plan => plan.Primary.Id));
        Assert.True(File.Exists(sidecar));
        var repaired = await new NfoFixer(_guard, null!, NullLogger<NfoFixer>.Instance).FixAsync(plans[0].Primary, null, CancellationToken.None);
        Assert.True(repaired.Success, repaired.Message);
        Assert.False(File.Exists(sidecar));

        var moved = await _service.ExecuteAsync(plans[1].Primary, null, CancellationToken.None);
        Assert.False(moved.Success);
        Assert.Equal(FixReason.SourceChanged, moved.Reason);
        Assert.True(File.Exists(move.Path));
        Assert.Equal("original video", File.ReadAllText(move.Path));
        Assert.Equal("original subtitles", File.ReadAllText(Path.ChangeExtension(move.Path, ".en.srt")));
        Assert.False(Directory.Exists(Path.Combine(Movies, "Film")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task NativeReindexAndRestartRestoreOnlyTheRecordedMovesUserData(bool restart, bool clearedAfterMove)
    {
        var issue = await Approve(companions: true);
        var user = new MoveTestUser(Guid.NewGuid());
        var users = new MoveTestUsers(user);
        var data = new MoveTestUserData();
        var original = _items.Single();
        data.GetUserData(user, original).Played = true;
        data.GetUserData(user, original).PlayCount = 3;
        data.GetUserData(user, original).IsFavorite = true;
        var preserver = new MediaMoveUserData(_manager, _db.Moves, users, data, NullLogger<MediaMoveUserData>.Instance);
        var service = new MediaMoveService(_db, _guard, _protection, _manager, NullLogger<MediaMoveService>.Instance, preserver);
        var result = await service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        var pending = Assert.Single(_db.Moves.GetPendingUserData(null));
        Assert.True(Api.MoveOperationDto.FromOperation(pending).UserStatePending);
        var snapshotManifest = MediaMoveManifest.TryRead(pending.ManifestJson, pending.Id)!;
        var snapshot = Assert.Single(snapshotManifest.UserData);
        Assert.True(snapshot.Data.Played);
        Assert.False(_db.Moves.SaveUserDataProgress(pending, snapshotManifest with
        {
            UserData = [snapshot with { Data = new UserItemData { Key = "tampered" }, Applied = true }]
        }));
        var target = new Movie { Id = Guid.NewGuid(), Path = Path.Combine(Movies, "Film", "Film.mkv"), Name = "Film" };
        _items.Remove(original);
        _items.Add(target);
        if (restart)
        {
            var reopened = new MediaDashDb(Path.Combine(_root, "moves.db"));
            preserver = new MediaMoveUserData(_manager, reopened.Moves, users, data, NullLogger<MediaMoveUserData>.Instance);
            await preserver.StartAsync(CancellationToken.None);
            Assert.False(data.GetUserData(user, target).Played);
            Assert.Single(reopened.Moves.GetPendingUserData(null));
            preserver.Reconcile(target);
        }
        else
        {
            preserver.Reconcile(target);
        }

        Assert.True(data.GetUserData(user, target).Played);
        Assert.Equal(3, data.GetUserData(user, target).PlayCount);
        Assert.True(data.GetUserData(user, target).IsFavorite);
        Assert.Empty(_db.Moves.GetPendingUserData(null));
        Assert.False(Api.MoveOperationDto.FromOperation(_db.Moves.Read(pending.Id)!).UserStatePending);
        preserver.Reconcile(target);
        Assert.Equal(1, data.Saves);
        data.GetUserData(user, target).PlaybackPositionTicks = clearedAfterMove ? 0 : 321;
        if (clearedAfterMove)
        {
            data.GetUserData(user, target).Played = false;
            data.GetUserData(user, target).PlayCount = 0;
            data.GetUserData(user, target).IsFavorite = false;
        }

        var undo = await service.UndoAsync(result.MoveOperationId!, null, CancellationToken.None);
        Assert.True(undo.Success, undo.Message);
        _items.Remove(target);
        var restored = new Movie { Id = original.Id, Path = original.Path, Name = "Film" };
        _items.Add(restored);
        preserver.Reconcile(restored);
        Assert.Equal(!clearedAfterMove, data.GetUserData(user, restored).Played);
        Assert.Equal(clearedAfterMove ? 0 : 321, data.GetUserData(user, restored).PlaybackPositionTicks);
        Assert.Equal(2, data.Saves);
        Assert.Empty(_db.Moves.GetPendingUserData(null));
        await preserver.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UserStateRestoresTheCanonicalApiItemRatherThanOnlyADetachedPathLookup()
    {
        var issue = await Approve();
        var user = new MoveTestUser(Guid.NewGuid());
        var data = new MoveTestUserData(instanceCache: true);
        var original = _items.Single();
        data.GetUserData(user, original).Played = true;
        data.SaveUserData(user, original, data.GetUserData(user, original), UserDataSaveReason.Import, CancellationToken.None);
        var initialSaves = data.Saves;
        _detachedPathLookups = true;
        var preserver = new MediaMoveUserData(_manager, _db.Moves, new MoveTestUsers(user), data, NullLogger<MediaMoveUserData>.Instance);
        var service = new MediaMoveService(_db, _guard, _protection, _manager, NullLogger<MediaMoveService>.Instance, preserver);
        var result = await service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        _items.Remove(original);
        var canonical = new Movie { Id = Guid.NewGuid(), Path = Path.Combine(Movies, "Film", "Film.mkv") };
        _items.Add(canonical);
        Assert.False(data.GetUserData(user, canonical).Played);
        preserver.Reconcile();
        Assert.True(data.GetUserData(user, canonical).Played);
        Assert.Equal(initialSaves + 1, data.Saves);
        Assert.Empty(_db.Moves.GetPendingUserData(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCatalogFailureBeforeOrAfterPublishKeepsEveryByteAndTruthfulReview(bool afterPublish)
    {
        var issue = await Approve();
        var plan = MediaMovePlan.ReadIssue(issue)!;
        if (afterPublish)
        {
            _service.Checkpoint = (point, _) => { if (point == "Published") _catalogFailure = true; };
        }
        else _catalogFailure = true;
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(FixReason.MoveNeedsReview, result.Reason);
        Assert.Equal(0, result.BytesFreed);
        var destination = plan.Entries[0].Destination;
        Assert.True((File.Exists(issue.Path) && File.ReadAllText(issue.Path) == "original video")
            || (File.Exists(destination) && File.ReadAllText(destination) == "original video"));
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(issue.Id)!.Status);
        if (afterPublish)
        {
            Assert.NotNull(result.MoveOperationId);
            Assert.Equal("NeedsReview", _db.Moves.Read(result.MoveOperationId!)!.State);
            Assert.True(_db.Moves.HasActiveConflict(destination));
        }
        else Assert.Empty(_db.Moves.GetVisible());
    }

    [Fact]
    public async Task UserStateWaitsForSuccessfulNativeLibraryScanAndUnsubscribesOnStop()
    {
        var issue = await Approve();
        var user = new MoveTestUser(Guid.NewGuid());
        var data = new MoveTestUserData();
        var original = _items.Single();
        data.GetUserData(user, original).Played = true;
        EventHandler<TaskCompletionEventArgs>? completed = null;
        var tasks = DispatchProxy.Create<ITaskManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)tasks).Call = (method, args) =>
        {
            if (method.Name == "add_TaskCompleted") completed += (EventHandler<TaskCompletionEventArgs>)args![0]!;
            if (method.Name == "remove_TaskCompleted") completed -= (EventHandler<TaskCompletionEventArgs>)args![0]!;
            return null;
        };
        var preserver = new MediaMoveUserData(_manager, _db.Moves, new MoveTestUsers(user), data, NullLogger<MediaMoveUserData>.Instance, tasks);
        await preserver.StartAsync(CancellationToken.None);
        Assert.NotNull(completed);
        var service = new MediaMoveService(_db, _guard, _protection, _manager, NullLogger<MediaMoveService>.Instance, preserver);
        var result = await service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        var target = new Movie { Id = Guid.NewGuid(), Path = Path.Combine(Movies, "Film", "Film.mkv") };
        _items.Remove(original);
        _items.Add(target);
        var worker = DispatchProxy.Create<IScheduledTaskWorker, OriginalLanguageFlowTests.CallProxy>();
        var nativeTask = new MoveNativeTask("OtherTask");
        ((OriginalLanguageFlowTests.CallProxy)(object)worker).Call = (method, _) => method.Name == "get_ScheduledTask" ? nativeTask : null;
        completed!(tasks, new(worker, new() { Status = TaskCompletionStatus.Completed }));
        Assert.Equal(0, data.Saves);
        nativeTask = new MoveNativeTask("RefreshLibrary");
        completed(tasks, new(worker, new() { Status = TaskCompletionStatus.Failed }));
        Assert.Equal(0, data.Saves);
        Assert.Single(_db.Moves.GetPendingUserData(null));
        completed(tasks, new(worker, new() { Status = TaskCompletionStatus.Completed }));
        Assert.True(data.GetUserData(user, target).Played);
        Assert.Equal(1, data.Saves);
        Assert.Empty(_db.Moves.GetPendingUserData(null));
        await preserver.StopAsync(CancellationToken.None);
        Assert.Null(completed);
    }

    private sealed class MoveNativeTask(string key) : IScheduledTask
    {
        public string Key => key;
        public string Name => key;
        public string Description => key;
        public string Category => "Owned tests";
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task NewImportAtRecordedDestinationCannotReceiveTheOldItemsUserState()
    {
        var issue = await Approve();
        var user = new MoveTestUser(Guid.NewGuid());
        var data = new MoveTestUserData();
        data.GetUserData(user, _items.Single()).Played = true;
        var preserver = new MediaMoveUserData(_manager, _db.Moves, new MoveTestUsers(user), data, NullLogger<MediaMoveUserData>.Instance);
        var service = new MediaMoveService(_db, _guard, _protection, _manager, NullLogger<MediaMoveService>.Instance, preserver);
        var result = await service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        var path = Path.Combine(Movies, "Film", "Film.mkv");
        File.Delete(path);
        Write(path, "foreign new import with different payload");
        var target = new Movie { Id = Guid.NewGuid(), Path = path };
        preserver.Reconcile(target);
        Assert.False(data.GetUserData(user, target).Played);
        Assert.Equal(0, data.Saves);
        Assert.Single(_db.Moves.GetPendingUserData(null));
        Assert.Equal("foreign new import with different payload", File.ReadAllText(path));
    }

    public sealed record MoveTestUser(Guid Id);

    public sealed class MoveTestUsers(MoveTestUser user)
    {
        public MoveTestUser[] GetUsers() => [user];
    }

    public sealed class MoveTestUserData(bool instanceCache = false)
    {
        private readonly Dictionary<(Guid User, Guid Item), MediaBrowser.Controller.Entities.UserItemData> _values = [];
        private readonly Dictionary<Guid, Dictionary<BaseItem, UserItemData>> _instances = [];
        public int Saves { get; private set; }
        public bool FailSaves { get; init; }
        public MediaBrowser.Controller.Entities.UserItemData GetUserData(MoveTestUser user, BaseItem item)
        {
            if (!_values.TryGetValue((user.Id, item.Id), out var value))
            {
                _values[(user.Id, item.Id)] = value = new() { Key = item.Id.ToString("N") };
            }

            if (instanceCache)
            {
                if (!_instances.TryGetValue(user.Id, out var perUser))
                {
                    _instances[user.Id] = perUser = new(ReferenceEqualityComparer.Instance);
                }

                if (!perUser.TryGetValue(item, out var cached))
                {
                    perUser[item] = cached = System.Text.Json.JsonSerializer.Deserialize<UserItemData>(System.Text.Json.JsonSerializer.Serialize(value))!;
                }

                return cached;
            }

            return value;
        }

        public void SaveUserData(MoveTestUser user, BaseItem item, MediaBrowser.Controller.Entities.UserItemData data,
            UserDataSaveReason reason, CancellationToken cancellationToken)
        {
            if (FailSaves) throw new IOException("Owned user-state save failure");
            _values[(user.Id, item.Id)] = data;
            Saves++;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExplicitFileMoveUsesTheJournalDryRunAndCompleteUndo(bool dryRun, bool changeExtension)
    {
        var source = Write(Path.Combine(Movies, "selected.opaque"), "explicit bytes");
        var target = Path.Combine(Tv, changeExtension ? "renamed.mkv" : "renamed.opaque");
        _config.DryRun = dryRun;
        var plan = await _planner.BuildExplicitAsync([(source, target)], _items, _config, CancellationToken.None);
        var result = await _service.ExecuteExplicitAsync(plan, plan.Fingerprint, null, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.Equal(dryRun, result.WasDryRun);
        if (dryRun)
        {
            Assert.Equal(0, OperationCount());
            Assert.Equal("explicit bytes", File.ReadAllText(source));
            Assert.False(File.Exists(target));
            return;
        }

        Assert.Equal("explicit bytes", File.ReadAllText(target));
        Assert.False(File.Exists(source));
        Assert.Equal("Completed", _db.Moves.Read(result.MoveOperationId!)!.State);
        var unrelated = Write(Path.Combine(Tv, "Other.nfo"), "unrelated metadata");
        var undo = await _service.UndoAsync(result.MoveOperationId!, null, CancellationToken.None);
        Assert.True(undo.Success, undo.Message);
        Assert.Equal("explicit bytes", File.ReadAllText(source));
        Assert.False(File.Exists(target));
        Assert.Equal("unrelated metadata", File.ReadAllText(unrelated));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExplicitMovieRenameRequiresItsCurrentPreviewAndMovesCompanionsTogether(bool declaredExternal, bool sameFolder)
    {
        var source = Write(Path.Combine(Movies, "Film.mkv"), "movie bytes");
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Film", Path = source };
        _items.Add(movie);
        var subtitle = Write(Path.ChangeExtension(source, ".fr.srt"), "subtitles");
        var audio = Write(Path.ChangeExtension(source, ".fr.aac"), "external audio");
        if (declaredExternal)
        {
            movie.SubtitleFiles = [subtitle];
            movie.AudioFiles = [audio];
        }

        var target = Path.Combine(sameFolder ? Movies : Tv, "Renamed.mkv");
        var plan = await _planner.BuildExplicitAsync([(source, target)], _items, _config, CancellationToken.None);
        var stale = await _service.ExecuteExplicitAsync(plan, new string('0', 64), null, CancellationToken.None);
        Assert.False(stale.Success);
        Assert.Equal(0, OperationCount());
        var result = await _service.ExecuteExplicitAsync(plan, plan.Fingerprint, null, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.Equal("movie bytes", File.ReadAllText(target));
        Assert.Equal("subtitles", File.ReadAllText(Path.ChangeExtension(target, ".fr.srt")));
        Assert.False(File.Exists(source));
        Assert.False(File.Exists(Path.ChangeExtension(source, ".fr.srt")));
        Assert.Equal("external audio", File.ReadAllText(Path.ChangeExtension(target, ".fr.aac")));
        var undo = await _service.UndoAsync(result.MoveOperationId!, null, CancellationToken.None);
        Assert.True(undo.Success, undo.Message);
        Assert.Equal("movie bytes", File.ReadAllText(source));
        Assert.Equal("subtitles", File.ReadAllText(subtitle));
        Assert.Equal("external audio", File.ReadAllText(audio));
    }

    [Fact]
    public async Task ExplicitGenericFileConsentDoesNotIncludeUnrelatedMetadataArrivingInItsParent()
    {
        var source = Write(Path.Combine(Movies, "selected.opaque"), "explicit bytes");
        var target = Path.Combine(Tv, "selected.opaque");
        var plan = await _planner.BuildExplicitAsync([(source, target)], _items, _config, CancellationToken.None);
        var unrelated = Write(Path.Combine(Movies, "Other.nfo"), "new unrelated metadata");
        var result = await _service.ExecuteExplicitAsync(plan, plan.Fingerprint, null, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.Equal("explicit bytes", File.ReadAllText(target));
        Assert.Equal("new unrelated metadata", File.ReadAllText(unrelated));
    }

    [Theory]
    [InlineData("part")]
    [InlineData("audio")]
    [InlineData("subtitle")]
    public async Task NewlyDeclaredPayloadMetadataInvalidatesConsentEvenWhenNoFileWasAddedAfterPreview(string kind)
    {
        var other = Write(Path.Combine(Tv, "Other", kind == "part" ? "part.mkv" : kind == "audio" ? "dub.mka" : "subtitle.srt"), "existing other payload");
        var issue = await Approve();
        var owner = Assert.IsType<Movie>(_items[0]);
        if (kind == "part") owner.AdditionalParts = [other];
        if (kind == "audio") owner.AudioFiles = [other];
        if (kind == "subtitle") owner.SubtitleFiles = [other];
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(0, OperationCount());
        Assert.Equal("original video", File.ReadAllText(issue.Path));
        Assert.Equal("existing other payload", File.ReadAllText(other));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WholeAlbumConsentBindsEveryKnownTracksCurrentFamilyMetadata(bool changeChild)
    {
        _config.MusicTargetPath = Path.Combine(Movies, "music");
        Directory.CreateDirectory(_config.MusicTargetPath);
        var first = new MediaBrowser.Controller.Entities.Audio.Audio { Id = Guid.NewGuid(), Album = "Owner", AlbumArtists = ["Artist"], Name = "One",
            Path = Write(Path.Combine(Tv, "Owner", "Disc 1", "01.flac"), "first audio") };
        var child = new MediaBrowser.Controller.Entities.Audio.Audio { Id = Guid.NewGuid(), Album = "Owner", AlbumArtists = ["Artist"], Name = "Two",
            Path = Write(Path.Combine(Tv, "Owner", "Disc 2", "02.flac"), "second audio") };
        _items.AddRange([first, child]);
        var found = Assert.Single(await _planner.ScanAsync(IssueType.Misplaced, _items, _config, new Progress<double>(), CancellationToken.None));
        Assert.False(found.HasBlockingWarnings);
        _db.Issues.ReplaceDetectedIssues(found.Type, [found]);
        _db.Issues.QueueDetectedIssues(found.Type);
        var issue = Assert.Single(_db.Issues.GetIssues(found.Type));
        var primary = (MediaBrowser.Controller.Entities.Audio.Audio)_items.Single(item => item.Id == issue.ItemId);
        var other = (MediaBrowser.Controller.Entities.Audio.Audio)_items.Single(item => item.Id != issue.ItemId);
        (changeChild ? other : primary).Album = "Different family";
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(0, OperationCount());
        Assert.Equal("first audio", File.ReadAllText(first.Path));
        Assert.Equal("second audio", File.ReadAllText(child.Path));
    }

    [Fact]
    public async Task AlbumMoveAndUndoInvalidateEveryKnownChildsExternalMetadataConsent()
    {
        _config.MusicTargetPath = Path.Combine(Movies, "music");
        Directory.CreateDirectory(_config.MusicTargetPath);
        var first = new MediaBrowser.Controller.Entities.Audio.Audio { Id = Guid.NewGuid(), Album = "Owner", AlbumArtists = ["Artist"], Name = "One",
            Path = Write(Path.Combine(Tv, "Owner", "Disc 1", "01.flac"), "first audio") };
        var second = new MediaBrowser.Controller.Entities.Audio.Audio { Id = Guid.NewGuid(), Album = "Owner", AlbumArtists = ["Artist"], Name = "Two",
            Path = Write(Path.Combine(Tv, "Owner", "Disc 2", "02.flac"), "second audio") };
        _items.AddRange([first, second]);
        var found = Assert.Single(await _planner.ScanAsync(IssueType.Misplaced, _items, _config, new Progress<double>(), CancellationToken.None));
        _db.Issues.ReplaceDetectedIssues(found.Type, [found]);
        _db.Issues.QueueDetectedIssues(found.Type);
        var issue = Assert.Single(_db.Issues.GetIssues(found.Type));
        var child = _items.Single(item => item.Id != issue.ItemId);
        var metadata = Write(Path.Combine(_root, "metadata", "poster.png"), "managed image");
        void QueueMetadata()
        {
            _db.Issues.ReplaceDetectedIssues(IssueType.CorruptArtwork, [new Issue { Type = IssueType.CorruptArtwork,
                ItemId = child.Id, Path = metadata, DetailsJson = "{\"stale\":true}" }]);
            _db.Issues.QueueDetectedIssues(IssueType.CorruptArtwork);
        }

        QueueMetadata();
        var moved = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.True(moved.Success, moved.Message);
        Assert.True(Assert.Single(_db.Issues.GetIssues(IssueType.CorruptArtwork)).RequiresRescan);
        QueueMetadata();
        var undone = await _service.UndoAsync(moved.MoveOperationId!, null, CancellationToken.None);
        Assert.True(undone.Success, undone.Message);
        var finding = Assert.Single(_db.Issues.GetIssues(IssueType.CorruptArtwork));
        Assert.True(finding.RequiresRescan);
        Assert.Equal(IssueStatus.Detected, finding.Status);
        Assert.Equal("managed image", File.ReadAllText(metadata));
        Assert.Equal("first audio", File.ReadAllText(first.Path));
        Assert.Equal("second audio", File.ReadAllText(second.Path));
    }

    [Fact]
    public async Task NativeMetadataCanFollowTheApprovedPathsWithoutInvalidatingTheInFlightMove()
    {
        var issue = await Approve(companions: true, beforeScan: source => ((Movie)_items[0]).SubtitleFiles = [Path.ChangeExtension(source, ".en.srt")]);
        var plan = MediaMovePlan.ReadIssue(issue)!;
        _service.Checkpoint = (point, index) =>
        {
            if (point != "Published" || plan.Entries[index].Source != plan.ItemPath) return;
            var owner = (Movie)_items[0];
            owner.Path = Path.Combine(Movies, "Film", "Film.mkv");
            owner.SubtitleFiles = [Path.Combine(Movies, "Film", "Film.en.srt")];
        };
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.Equal("original video", File.ReadAllText(Path.Combine(Movies, "Film", "Film.mkv")));
    }

    [Fact]
    public async Task VerifiedOutputRenameBindsTheKnownCatalogOwnerAndExcludesOnlyItsCompletingIssue()
    {
        var old = Write(Path.Combine(Movies, "Film.mkv"), "old input");
        var owner = new Movie { Id = Guid.NewGuid(), Name = "Film", Path = old };
        _items.Add(owner);
        _db.Issues.ReplaceDetectedIssues(IssueType.Quality, [new Issue { Type = IssueType.Quality, ItemId = owner.Id, Path = old }]);
        _db.Issues.QueueDetectedIssues(IssueType.Quality);
        var completing = Assert.Single(_db.Issues.GetIssues(IssueType.Quality));
        _db.Issues.ReplaceDetectedIssues(IssueType.AudioLanguage, [new Issue { Type = IssueType.AudioLanguage, ItemId = owner.Id, Path = old }]);
        _db.Issues.QueueDetectedIssues(IssueType.AudioLanguage);
        var verified = Write(Path.ChangeExtension(old, ".mp4"), "verified output");
        File.Delete(old);
        var target = Path.Combine(Movies, "Film (2026) - 720p.mp4");
        var plan = (await _planner.BuildExplicitAsync([(verified, target)], _items, _config, CancellationToken.None) with
        {
            ItemId = owner.Id, ItemPath = verified, CatalogPath = old, Identity = MediaMovePlanner.Identity(owner)
        }).Seal();
        var result = await _service.ExecuteExplicitAsync(plan, plan.Fingerprint, null, CancellationToken.None, completing.Id);
        Assert.True(result.Success, result.Message);
        Assert.Equal("verified output", File.ReadAllText(target));
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(completing.Id)!.Status);
        var audio = Assert.Single(_db.Issues.GetIssues(IssueType.AudioLanguage));
        Assert.Equal(IssueStatus.Detected, audio.Status);
        Assert.True(audio.RequiresRescan);
        Assert.Equal(target, audio.Path);
    }

    private long OperationCount()
    {
        using var connection = _db.ConnectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM media_move_operations";
        return (long)command.ExecuteScalar()!;
    }

    [Theory]
    [InlineData("same-container")]
    [InlineData("changed-container")]
    [InlineData("folder-artwork")]
    public async Task OptionalCanonicalRenameIsASeparateCompleteUndoableOperation(string mode)
    {
        var originalPath = Path.Combine(Movies, "Film.mkv");
        var current = mode == "changed-container" ? Path.ChangeExtension(originalPath, ".mp4") : originalPath;
        Write(current, "verified encode");
        var subtitle = Write(Path.ChangeExtension(originalPath, ".fr.srt"), "subtitle payload");
        var owner = new Movie { Id = Guid.NewGuid(), Name = "Film", ProductionYear = 2026, Path = originalPath };
        _items.Add(owner);
        if (mode == "folder-artwork") Write(Path.Combine(Movies, "folder.jpg"), "folder artwork");
        _db.Issues.ReplaceDetectedIssues(IssueType.Quality, [new Issue { Type = IssueType.Quality, ItemId = owner.Id, Path = originalPath }]);
        _db.Issues.QueueDetectedIssues(IssueType.Quality);
        var quality = Assert.Single(_db.Issues.GetIssues(IssueType.Quality));
        _config.RenameAfterTranscode = true;
        var monitor = DispatchProxy.Create<ILibraryMonitor, OriginalLanguageFlowTests.CallProxy>();
        var fixer = new TranscodeFixer(null!, null!, null!, _guard, null!, new PluginConfigurationProvider(() => _config), monitor, _manager,
            null!, _db.Issues, _protection, NullLogger<TranscodeFixer>.Instance, _planner, _service, _db.History);
        var renamed = await fixer.TryRenameToCanonicalAsync(current, quality, 720, Path.GetExtension(current), CancellationToken.None);
        var target = Path.Combine(Movies, "Film (2026) - 720p" + Path.GetExtension(current));
        Assert.True(target == renamed.Path, renamed.Warning);
        Assert.Null(renamed.Warning);
        Assert.Equal("verified encode", File.ReadAllText(target));
        Assert.Equal("subtitle payload", File.ReadAllText(Path.ChangeExtension(target, ".fr.srt")));
        var history = Assert.Single(_db.History.GetHistory());
        Assert.Equal(IssueType.ManualMove, history.Type);
        Assert.NotNull(history.MoveOperationId);
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(quality.Id)!.Status);
        _db.Issues.RelocateIssuePaths(originalPath, target);
        _db.Issues.TryTransitionIssueStatus(quality.Id, [IssueStatus.Queued], IssueStatus.Fixed);
        var undone = await _service.UndoAsync(history.MoveOperationId!, null, CancellationToken.None);
        Assert.True(undone.Success, undone.Message);
        Assert.Equal("verified encode", File.ReadAllText(current));
        Assert.Equal("subtitle payload", File.ReadAllText(subtitle));
        var refreshed = _db.Issues.GetIssue(quality.Id)!;
        Assert.Equal(current, refreshed.Path);
        Assert.True(refreshed.RequiresRescan);
        if (mode == "changed-container") Assert.False(File.Exists(originalPath));
        if (mode == "folder-artwork") Assert.Equal("folder artwork", File.ReadAllText(Path.Combine(Movies, "folder.jpg")));
    }

    [Theory]
    [InlineData("collision")]
    [InlineData("protection")]
    [InlineData("declared-missing")]
    [InlineData("setting-changed")]
    [InlineData("cancel")]
    public async Task OptionalRenameRefusalPreservesTheVerifiedEncodeAndEveryCompanion(string mode)
    {
        var source = Write(Path.Combine(Movies, "Film.mkv"), "verified encode");
        var subtitle = Write(Path.ChangeExtension(source, ".fr.srt"), "subtitle payload");
        var owner = new Movie { Id = Guid.NewGuid(), Name = "Film", ProductionYear = 2026, Path = source };
        _items.Add(owner);
        _db.Issues.ReplaceDetectedIssues(IssueType.Quality, [new Issue { Type = IssueType.Quality, ItemId = owner.Id, Path = source }]);
        _db.Issues.QueueDetectedIssues(IssueType.Quality);
        var quality = Assert.Single(_db.Issues.GetIssues(IssueType.Quality));
        _config.RenameAfterTranscode = true;
        var target = Path.Combine(Movies, "Film (2026) - 720p.mkv");
        if (mode == "collision") Write(target, "foreign existing file");
        if (mode == "protection")
        {
            _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = source, FixersMask = "ManualDelete", ScannersMask = "*" });
            _protection.Invalidate();
        }
        if (mode == "declared-missing") owner.AdditionalParts = [Path.Combine(Tv, "missing.mkv")];
        using var cancellation = new CancellationTokenSource();
        _service.Checkpoint = (point, _) =>
        {
            if (point != "BeforeEntry") return;
            if (mode == "setting-changed") _config.RenameAfterTranscode = false;
            if (mode == "cancel") cancellation.Cancel();
        };
        var monitor = DispatchProxy.Create<ILibraryMonitor, OriginalLanguageFlowTests.CallProxy>();
        var fixer = new TranscodeFixer(null!, null!, null!, _guard, null!, new PluginConfigurationProvider(() => _config), monitor, _manager,
            null!, _db.Issues, _protection, NullLogger<TranscodeFixer>.Instance, _planner, _service, _db.History);
        var result = await fixer.TryRenameToCanonicalAsync(source, quality, 720, ".mkv", cancellation.Token);
        Assert.Null(result.Path);
        Assert.False(string.IsNullOrWhiteSpace(result.Warning));
        Assert.Equal("verified encode", File.ReadAllText(source));
        Assert.Equal("subtitle payload", File.ReadAllText(subtitle));
        if (mode == "collision") Assert.Equal("foreign existing file", File.ReadAllText(target));
        else Assert.False(File.Exists(target));
        if (mode is "setting-changed" or "cancel")
        {
            Assert.Equal("NeedsReview", Assert.Single(_db.Moves.GetVisible()).State);
            Assert.True(_db.Moves.HasActiveConflict(source));
        }
        else Assert.Empty(_db.Moves.GetVisible());
    }

    [Fact]
    public async Task AutomaticMoviePlanDoesNotMergeIntoANonemptyIndependentDestination()
    {
        var source = Write(Path.Combine(Tv, "Film.mkv"), "owned movie");
        _items.Add(new Movie { Id = Guid.NewGuid(), Name = "Film", Path = source });
        var foreign = Write(Path.Combine(Movies, "Film", "unrelated-notes.txt"), "independent destination content");
        var issue = Assert.Single(await _planner.ScanAsync(IssueType.Misplaced, _items, _config, new Progress<double>(), CancellationToken.None));
        Assert.True(issue.HasBlockingWarnings);
        Assert.Equal("independent destination content", File.ReadAllText(foreign));
        Assert.Equal("owned movie", File.ReadAllText(source));
        Assert.Empty(_db.Moves.GetVisible());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("before-execute")]
    [InlineData("after-publish")]
    public async Task AutomaticMovieDestinationKeepsForeignArrivalsAndAllowsAnEmptyOwnedBundle(string arrival)
    {
        var target = Path.Combine(Movies, "Film");
        Directory.CreateDirectory(target);
        var issue = await Approve(misplaced: true, companions: true);
        var plan = MediaMovePlan.ReadIssue(issue)!;
        var foreign = Path.Combine(target, "new-import.mkv");
        if (arrival == "before-execute") Write(foreign, "foreign import");
        if (arrival == "after-publish")
        {
            _service.Checkpoint = (point, _) => { if (point == "Published" && !File.Exists(foreign)) Write(foreign, "foreign import"); };
        }

        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.Equal(arrival == "none", result.Success);
        Assert.Equal(0, result.BytesFreed);
        if (arrival == "none")
        {
            foreach (var entry in plan.Entries) MediaMoveSnapshot.VerifyInventory(entry.Destination, entry.Inventory, true, CancellationToken.None);
        }
        else
        {
            Assert.Equal("foreign import", File.ReadAllText(foreign));
            foreach (var entry in plan.Entries)
            {
                var survivor = File.Exists(entry.Source) ? entry.Source : entry.Destination;
                MediaMoveSnapshot.VerifyInventory(survivor, entry.Inventory, true, CancellationToken.None);
            }
            Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(issue.Id)!.Status);
            if (arrival == "after-publish") Assert.Equal("NeedsReview", _db.Moves.Read(result.MoveOperationId!)!.State);
            else Assert.Empty(_db.Moves.GetVisible());
        }
    }

    [Fact]
    public async Task ApprovedBundleMovesEveryPayloadAndRecordsDurableCompletion()
    {
        var issue = await Approve(companions: true);
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.Equal(0, result.BytesFreed);
        Assert.Equal("original video", File.ReadAllText(Path.Combine(Movies, "Film", "Film.mkv")));
        Assert.Equal("original subtitles", File.ReadAllText(Path.Combine(Movies, "Film", "Film.en.srt")));
        Assert.Equal("original metadata", File.ReadAllText(Path.Combine(Movies, "Film", "Film.nfo")));
        Assert.False(File.Exists(issue.Path));
        Assert.NotNull(result.MoveOperationId);
        var operation = _db.Moves.Read(result.MoveOperationId)!;
        Assert.Equal("Completed", operation.State);
        Assert.True(operation.CleanupComplete);
        Assert.False(_db.Moves.HasActiveConflict(issue.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothRegisteredFixersExecuteTheApprovedDurablePlan(bool sorter)
    {
        var issue = await Approve(directory: sorter, companions: true);
        IFixer fixer = sorter ? new MediaSorterFixer(_service) : new MediaGrouperFixer(_service);
        Assert.True(fixer.CanFix(issue.Type));
        var result = await fixer.FixAsync(issue, null, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.MoveOperationId);
        Assert.Equal("Completed", _db.Moves.Read(result.MoveOperationId!)!.State);
        Assert.Equal("original video", File.ReadAllText(Path.Combine(Movies, "Film", "Film.mkv")));
        Assert.Equal("original subtitles", File.ReadAllText(Path.Combine(Movies, "Film", "Film.en.srt")));
    }

    [Fact]
    public async Task SchedulerRecorderLinksTheActualMoveOperationAndHistoryAtomically()
    {
        var issue = await Approve(companions: true);
        var result = await new MediaGrouperFixer(_service).FixAsync(issue, null, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        var scheduled = new PlannedFix(PlannedFixKind.Single, issue, []);
        var execution = new FixExecutionResult(result, issue.Id, [issue.Id]);
        new FixResultRecorder(_db, NullLogger<FixResultRecorder>.Instance).Record(scheduled, execution, DateTime.UtcNow);
        var history = Assert.Single(_db.History.GetHistory());
        Assert.Equal(result.MoveOperationId, history.MoveOperationId);
        Assert.Equal(history.Id, _db.Moves.Read(result.MoveOperationId!)!.HistoryId);
        Assert.Equal(IssueStatus.Fixed, _db.Issues.GetIssue(issue.Id)!.Status);
    }

    private Api.MediaDashController MoveController() => new(_db, _db.Issues, _db.History, null!, null!, null!, _manager,
        null!, [], [], null!, _guard, new RunState(), null!, null!, _protection, moves: _service);

    [Fact]
    public async Task UndoApiUsesTheRecordedOperationAndKeepsRecycleRestoreSeparate()
    {
        var issue = await Approve(companions: true);
        var moved = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.True(moved.Success, moved.Message);
        var controller = MoveController();
        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>((await controller.UndoMove(moved.MoveOperationId!, CancellationToken.None)).Result);
        Assert.True(Assert.IsType<FixResult>(response.Value).Success);
        Assert.Equal("original video", File.ReadAllText(issue.Path));
        Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>((await controller.UndoMove(moved.MoveOperationId!, CancellationToken.None)).Result);
        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>((await controller.UndoMove("../not-an-operation", CancellationToken.None)).Result);
        Assert.Contains(typeof(Api.MediaDashController).GetCustomAttributes<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>(), attribute => attribute.Policy == "RequiresElevation");
    }

    [Fact]
    public void LegacyMoveCannotAcquireApprovalWithoutTheCompletePreview()
    {
        var source = Write(Path.Combine(Movies, "Legacy.mkv"), "owned legacy source");
        _db.Issues.ReplaceDetectedIssues(IssueType.Ungrouped, [new Issue { Type = IssueType.Ungrouped, Path = source,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { action = "MoveFile", source, target = Path.Combine(Movies, "Legacy", "Legacy.mkv") }) }]);
        var issue = Assert.Single(_db.Issues.GetIssues(IssueType.Ungrouped));
        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(MoveController().ApproveIssue(issue.Id));
        Assert.Contains("scan", Assert.IsType<string>(response.Value), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(IssueStatus.Detected, _db.Issues.GetIssue(issue.Id)!.Status);
        Assert.Equal("owned legacy source", File.ReadAllText(source));
    }

    [Fact]
    public async Task MissingHistoryAfterCommittedMoveStillHasAVisiblePrivateTokenFreeRecord()
    {
        var issue = await Approve(companions: true);
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        using (var connection = _db.ConnectionFactory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER reject_history BEFORE INSERT ON history BEGIN SELECT RAISE(ABORT,'controlled history outage'); END";
            command.ExecuteNonQuery();
        }

        var scheduled = new PlannedFix(PlannedFixKind.Single, issue, []);
        var execution = new FixExecutionResult(result, issue.Id, [issue.Id]);
        Assert.Throws<SqliteException>(() => new FixResultRecorder(_db, NullLogger<FixResultRecorder>.Instance).Record(scheduled, execution, DateTime.UtcNow));
        Assert.Empty(_db.History.GetHistory());
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(issue.Id)!.Status);
        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(MoveController().GetMoves().Result);
        var visible = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<Api.MoveOperationDto>>(response.Value));
        Assert.Equal(result.MoveOperationId, visible.Id);
        Assert.Equal("Completed", visible.State);
        Assert.Null(visible.HistoryId);
        Assert.True(visible.CleanupComplete);
        Assert.Contains(issue.Path, visible.Sources);
        var serialized = System.Text.Json.JsonSerializer.Serialize(visible);
        Assert.DoesNotContain("OwnerToken", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_db.Moves.Read(visible.Id)!.OwnerToken, serialized, StringComparison.Ordinal);
        Assert.Equal("original video", File.ReadAllText(Path.Combine(Movies, "Film", "Film.mkv")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearedHistoryHidesRecordedMovesButRetainsRecoveryAndShowsLaterUpdates(bool interrupted)
    {
        var issue = await Approve();
        if (interrupted)
            _service.Checkpoint = (point, _) => { if (point == "Published") throw new IOException("controlled interruption"); };
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        var controller = MoveController();
        IReadOnlyList<Api.MoveOperationDto> Visible() => Assert.IsAssignableFrom<IReadOnlyList<Api.MoveOperationDto>>(
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(controller.GetMoves().Result).Value);
        var move = Assert.Single(Visible());
        _config.HistoryHiddenBeforeUtcTicks = DateTime.UtcNow.Ticks;

        Assert.Empty(Visible());
        Assert.NotNull(_db.Moves.Read(move.Id));
        using var connection = _db.ConnectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE media_move_operations SET updated_utc_ticks=@ticks WHERE operation_id=@id";
        command.Parameters.AddWithValue("@ticks", _config.HistoryHiddenBeforeUtcTicks + 1);
        command.Parameters.AddWithValue("@id", move.Id);
        command.ExecuteNonQuery();
        Assert.Equal(move.Id, Assert.Single(Visible()).Id);
    }

    // Production 2026-11-22: a move stopped part-way and Jellyfin regenerated a moved companion at its
    // old path, so the rollback stopped too and the parked move reserved the series. "Move anyway"
    // finishes it forward when the video already moved and the rest is unchanged.
    [Fact]
    public async Task MoveAnywayFinishesAParkedMoveForwardWithoutOverwriting()
    {
        var issue = await Approve(companions: true, beforeScan: source => Write(Path.Combine(Path.GetDirectoryName(source)!, "Film-thumb.jpg"), "thumb"));
        var plan = MediaMovePlan.ReadIssue(issue)!;
        var thumb = plan.Entries.Single(entry => entry.Source.EndsWith("Film-thumb.jpg", StringComparison.Ordinal));
        _service.Checkpoint = (point, index) =>
        {
            if (point == "Published" && plan.Entries[index] == thumb)
            {
                Write(thumb.Source, "jellyfin regenerated thumb");
                throw new IOException("controlled interruption");
            }
        };
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        var parked = Assert.Single(_db.Moves.GetNeedsReview());
        Assert.Contains(_db.Moves.FindParkedBlocking(plan), operation => operation.Id == parked.Id);

        var detail = _service.ResolveParked(parked);

        Assert.StartsWith("Finished by Move anyway", detail, StringComparison.Ordinal);
        Assert.Empty(_db.Moves.GetNeedsReview());
        Assert.Empty(_db.Moves.FindParkedBlocking(plan));
        Assert.All(plan.Entries, entry => Assert.True(File.Exists(entry.Destination), entry.Destination));
        Assert.Equal("original video", File.ReadAllText(plan.Entries[0].Destination));
        Assert.Equal("thumb", File.ReadAllText(thumb.Destination));
        Assert.Equal("jellyfin regenerated thumb", File.ReadAllText(thumb.Source));
        Assert.False(File.Exists(plan.Entries.Single(entry => entry.Source.EndsWith(".nfo", StringComparison.Ordinal)).Source));
    }

    [Fact]
    public async Task MoveAnywayReleasesAParkedMoveItCannotFinishAndTouchesNoFile()
    {
        var issue = await Approve(companions: true);
        var plan = MediaMovePlan.ReadIssue(issue)!;
        _service.Checkpoint = (point, _) => { if (point == "Published") throw new IOException("controlled interruption"); };
        Assert.False((await _service.ExecuteAsync(issue, null, CancellationToken.None)).Success);
        var parked = Assert.Single(_db.Moves.GetNeedsReview());
        var before = plan.Entries.ToDictionary(entry => entry.Source, entry => File.Exists(entry.Source));

        var detail = _service.ResolveParked(parked);

        Assert.StartsWith("Released by Move anyway", detail, StringComparison.Ordinal);
        Assert.Empty(_db.Moves.FindParkedBlocking(plan));
        Assert.All(plan.Entries, entry => Assert.Equal(before[entry.Source], File.Exists(entry.Source)));
        Assert.Equal("Released", _db.Moves.Read(parked.Id)!.State);
    }

    [Fact]
    public async Task PartialMoveHistoryKeepsApprovalAndExposesTheDurableReviewState()
    {
        var issue = await Approve();
        _service.Checkpoint = (point, _) => { if (point == "Published") throw new IOException("controlled interruption"); };
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        var scheduled = new PlannedFix(PlannedFixKind.Single, issue, []);
        new FixResultRecorder(_db, NullLogger<FixResultRecorder>.Instance).Record(scheduled, new(result, issue.Id, []), DateTime.UtcNow);
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(issue.Id)!.Status);
        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(MoveController().GetHistory().Result);
        var history = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<Api.HistoryDto>>(response.Value));
        Assert.False(history.Success);
        Assert.Equal(result.MoveOperationId, history.MoveOperationId);
        Assert.Equal("NeedsReview", history.MoveState);
        Assert.Equal(0, history.BytesFreed);
        Assert.False(history.CanRestore);
        Assert.False(history.CanUndoMove);
    }

    [Fact]
    public async Task CompleteDirectoryMovePreservesUnknownFilesAndEmptyDirectories()
    {
        var issue = await Approve(directory: true);
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.False(Directory.Exists(issue.Path));
        Assert.Equal("original video", File.ReadAllText(Path.Combine(Movies, "Film", "Film.mkv")));
        Assert.Equal("user notes", File.ReadAllText(Path.Combine(Movies, "Film", "notes.txt")));
        Assert.True(Directory.Exists(Path.Combine(Movies, "Film", "empty")));
        Assert.Equal("Completed", _db.Moves.Read(result.MoveOperationId!)!.State);
    }

    [Fact]
    public async Task PreviewCreatesNoOperationOrDestinationAndRetainsApproval()
    {
        var issue = await Approve(companions: true);
        _config.DryRun = true;
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.True(result.WasDryRun);
        Assert.Null(result.MoveOperationId);
        Assert.Equal(0, OperationCount());
        Assert.False(Directory.Exists(Path.Combine(Movies, "Film")));
        Assert.Equal("original video", File.ReadAllText(issue.Path));
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(issue.Id)!.Status);
    }

    [Fact]
    public async Task ExecutorProgressUsesTheSchedulersZeroToOneScale()
    {
        var issue = await Approve(companions: true);
        var progress = new RecordedProgress();
        var result = await _service.ExecuteAsync(issue, progress, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.NotEmpty(progress.Values);
        Assert.All(progress.Values, value => Assert.InRange(value, 0, 1));
        Assert.Equal(1, progress.Values[^1]);
    }

    [Theory]
    [InlineData("dismiss")]
    [InlineData("disable")]
    [InlineData("protect-companion")]
    [InlineData("item-year")]
    public async Task MidMoveConsentAndIdentityChangesStopFurtherCompanions(string change)
    {
        var issue = await Approve(companions: true);
        _service.Checkpoint = (point, _) =>
        {
            if (point != "Published") return;
            if (change == "dismiss") _db.Issues.TryTransitionIssueStatus(issue.Id, [IssueStatus.Queued], IssueStatus.Dismissed);
            if (change == "disable") _config.FixerDisabled = ["Ungrouped"];
            if (change == "item-year") _items[0].ProductionYear = 1984;
            if (change == "protect-companion")
            {
                _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = Path.ChangeExtension(issue.Path, ".en.srt"), ScannersMask = "*", FixersMask = "*" });
                _protection.Invalidate();
            }
        };
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("original video", File.ReadAllText(Path.Combine(Movies, "Film", "Film.mkv")));
        Assert.Equal("original subtitles", File.ReadAllText(Path.ChangeExtension(issue.Path, ".en.srt")));
        Assert.Equal("NeedsReview", _db.Moves.Read(result.MoveOperationId!)!.State);
    }

    [Fact]
    public async Task ReplacedPayloadWithSameLengthAndTimestampRefusesBeforeJournalPreparation()
    {
        var issue = await Approve();
        var timestamp = File.GetLastWriteTimeUtc(issue.Path);
        var replacement = issue.Path + ".replacement";
        File.WriteAllText(replacement, "changed  video");
        Assert.Equal(new FileInfo(issue.Path).Length, new FileInfo(replacement).Length);
        File.SetLastWriteTimeUtc(replacement, timestamp);
        File.Delete(issue.Path);
        File.Move(replacement, issue.Path);
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("changed  video", File.ReadAllText(issue.Path));
        Assert.Equal(0, OperationCount());
        Assert.False(Directory.Exists(Path.Combine(Movies, "Film")));
    }

    [Fact]
    public async Task StaleMoveConsentCanBeRescannedAndApprovedForTheCurrentPayload()
    {
        var issue = await Approve();
        Write(issue.Path, "new source bytes");
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        var stale = _db.Issues.GetIssue(issue.Id)!;
        Assert.Equal(IssueStatus.Detected, stale.Status);
        Assert.True(stale.RequiresRescan);
        Assert.Equal(0, OperationCount());
        var findings = await _planner.ScanAsync(issue.Type, _items, _config, new Progress<double>(), CancellationToken.None);
        _db.Issues.ReplaceDetectedIssues(issue.Type, findings, [issue.Path]);
        var refreshed = Assert.Single(_db.Issues.GetIssues(issue.Type));
        Assert.False(refreshed.RequiresRescan);
        Assert.IsType<Microsoft.AspNetCore.Mvc.NoContentResult>(MoveController().ApproveIssue(refreshed.Id));
        var moved = await _service.ExecuteAsync(_db.Issues.GetIssue(refreshed.Id)!, null, CancellationToken.None);
        Assert.True(moved.Success, moved.Message);
        Assert.Equal("new source bytes", File.ReadAllText(Path.Combine(Movies, "Film", "Film.mkv")));
    }

    [Fact]
    public async Task ScopedMoverScanClearsItsOwnOldContainerFindingAndPreservesOtherLibraries()
    {
        _items.Add(new Movie { Id = Guid.NewGuid(), Name = "Film", Path = Write(Path.Combine(Movies, "Container", "Film.mkv"), "current film") });
        var included = Path.Combine(Movies, "Container");
        var excluded = Path.Combine(Tv, "Keep");
        _db.Issues.ReplaceDetectedIssues(IssueType.Misplaced, [new Issue { Type = IssueType.Misplaced, Path = included }, new Issue { Type = IssueType.Misplaced, Path = excluded }]);
        var library = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)library).Call = (method, _) => method.Name switch
        {
            "GetItemList" => _items,
            "GetVirtualFolders" => new List<VirtualFolderInfo> { new() { ItemId = "first", Locations = [Movies] }, new() { ItemId = "second", Locations = [Tv] } },
            _ => null
        };
        _config.EnabledLibraries = ["first"];
        _config.PauseDuringPlayback = false;
        _db.SetState("ContentAwareDefaultsChecked", "true");
        var state = new RunState();
        state.ArmBypassIdleCheck(RunRole.Scan);
        var manager = DispatchProxy.Create<MediaBrowser.Model.Tasks.ITaskManager, LibraryScanCancellationTests.ManagerProxy>();
        var previousState = Plugin.AttachedRunState;
        try
        {
            await new ScanTask(library, [new EmptyMoverScanner()], _db, null!, manager, state, new PluginConfigurationProvider(() => _config),
                new ProtectionService(_db.Protection, library), NullLogger<ScanTask>.Instance).ExecuteAsync(new Progress<double>(), CancellationToken.None);
            Assert.Equal(excluded, Assert.Single(_db.Issues.GetIssues(IssueType.Misplaced)).Path);
        }
        finally
        {
            Plugin.AttachedRunState = previousState;
        }
    }

    private sealed class EmptyMoverScanner : IScanner
    {
        public IssueType Type => IssueType.Misplaced;
        public Task<IReadOnlyList<Issue>> ScanAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Issue>>([]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnavailableLibraryRootsKeepTheirFindingsAndCannotTriggerAWholeRootWalker(bool wholeRootScanner, bool selectedOnlyHealthy)
    {
        var missing = Path.Combine(_root, "unavailable");
        var healthyPath = Write(Path.Combine(Movies, "healthy.mkv"), "healthy");
        var missingPath = Path.Combine(missing, "missing.mkv");
        var type = wholeRootScanner ? IssueType.CorruptNfo : IssueType.AudioLanguage;
        _items.AddRange([new Movie { Id = Guid.NewGuid(), Path = healthyPath }, new Movie { Id = Guid.NewGuid(), Path = missingPath }]);
        _db.Issues.ReplaceDetectedIssues(type, [new Issue { Type = type, Path = healthyPath }, new Issue { Type = type, Path = missingPath }]);
        var missingIssue = _db.Issues.GetIssues(type).Single(issue => issue.Path == missingPath);
        var library = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)library).Call = (method, _) => method.Name switch
        {
            "GetItemList" => _items,
            "GetVirtualFolders" => new List<VirtualFolderInfo> { new() { ItemId = "first", Locations = [Movies] }, new() { ItemId = "second", Locations = [missing] } },
            _ => null
        };
        _config.PauseDuringPlayback = false;
        _config.EnabledLibraries = selectedOnlyHealthy ? ["first"] : [];
        _db.SetState("ContentAwareDefaultsChecked", "true");
        var state = new RunState(); state.ArmBypassIdleCheck(RunRole.Scan);
        var manager = DispatchProxy.Create<MediaBrowser.Model.Tasks.ITaskManager, LibraryScanCancellationTests.ManagerProxy>();
        var scanner = new AvailabilityScanner(type, wholeRootScanner);
        var previousState = Plugin.AttachedRunState;
        try
        {
            await new ScanTask(library, [scanner], _db, null!, manager, state, new PluginConfigurationProvider(() => _config),
                new ProtectionService(_db.Protection, library), NullLogger<ScanTask>.Instance).ExecuteAsync(new Progress<double>(), CancellationToken.None);
            Assert.Contains(_db.Issues.GetIssues(type), issue => issue.Id == missingIssue.Id && issue.Path == missingPath);
            Assert.Equal(!wholeRootScanner || selectedOnlyHealthy, scanner.Called);
            if (wholeRootScanner && !selectedOnlyHealthy) Assert.Equal(2, _db.Issues.GetIssues(type).Count);
            else Assert.Equal(healthyPath, Assert.Single(scanner.Seen).Path);
        }
        finally
        {
            Plugin.AttachedRunState = previousState;
        }
    }

    private sealed class AvailabilityScanner(IssueType type, bool wholeRootScanner) : IScanner
    {
        public IssueType Type => type;
        public bool AlwaysUnscoped => wholeRootScanner;
        public bool Called { get; private set; }
        public IReadOnlyList<BaseItem> Seen { get; private set; } = [];
        public Task<IReadOnlyList<Issue>> ScanAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken cancellationToken)
        {
            Called = true; Seen = items;
            return Task.FromResult<IReadOnlyList<Issue>>([]);
        }
    }

    [Fact]
    public async Task ACompanionCollisionStopsTheEntireBatchBeforeMutation()
    {
        var issue = await Approve(companions: true);
        var foreign = Write(Path.Combine(Movies, "Film", "Film.en.srt"), "new import");
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("original video", File.ReadAllText(issue.Path));
        Assert.Equal("new import", File.ReadAllText(foreign));
        Assert.Equal(0, OperationCount());
    }

    [Fact]
    public async Task CancellationBeforeExecutionLeavesNoJournalOrFilesystemChanges()
    {
        var issue = await Approve();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ExecuteAsync(issue, null, cancelled.Token));
        Assert.Equal(0, OperationCount());
        Assert.Equal("original video", File.ReadAllText(issue.Path));
    }

    [Theory]
    [InlineData("BeforeEntry", 1)]
    [InlineData("BeforePublish", 1)]
    [InlineData("Published", 0)]
    [InlineData("BeforeComplete", -1)]
    public async Task InterruptedBundleRollsBackOnlyItsOwnUnchangedPayload(string boundary, int selected)
    {
        var issue = await Approve(companions: true);
        _service.Checkpoint = (point, index) =>
        {
            if (point == boundary && index == selected)
            {
                throw new IOException("controlled interruption");
            }
        };
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("original video", File.ReadAllText(issue.Path));
        Assert.Equal("original subtitles", File.ReadAllText(Path.ChangeExtension(issue.Path, ".en.srt")));
        Assert.Equal("original metadata", File.ReadAllText(Path.ChangeExtension(issue.Path, ".nfo")));
        Assert.Equal("NeedsReview", _db.Moves.Read(result.MoveOperationId!)!.State);
        Assert.True(_db.Moves.HasActiveConflict(issue.Path));
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(issue.Id)!.Status);
        Assert.Equal(0, result.BytesFreed);
    }

    [Fact]
    public async Task NewImportAtOriginalPathSurvivesInterruptedMove()
    {
        var issue = await Approve();
        _service.Checkpoint = (point, _) =>
        {
            if (point == "Published")
            {
                Write(issue.Path, "new import");
                throw new IOException("controlled interruption");
            }
        };
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("new import", File.ReadAllText(issue.Path));
        Assert.Equal("original video", File.ReadAllText(Path.Combine(Movies, "Film", "Film.mkv")));
        Assert.Equal("NeedsReview", _db.Moves.Read(result.MoveOperationId!)!.State);
    }

    [Fact]
    public async Task ChangedDestinationIsPreservedInsteadOfRolledBackOrDeleted()
    {
        var issue = await Approve();
        var destination = Path.Combine(Movies, "Film", "Film.mkv");
        _service.Checkpoint = (point, _) =>
        {
            if (point == "Published")
            {
                Write(destination, "user changed destination");
                throw new IOException("controlled interruption");
            }
        };
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("user changed destination", File.ReadAllText(destination));
        Assert.False(File.Exists(issue.Path));
        Assert.Equal("NeedsReview", _db.Moves.Read(result.MoveOperationId!)!.State);
    }

    [Fact]
    public async Task PreparationDatabaseFailureChangesNoFilesystemPayload()
    {
        var issue = await Approve();
        using (var connection = _db.ConnectionFactory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER reject_move BEFORE INSERT ON media_move_operations BEGIN SELECT RAISE(ABORT,'controlled prepare failure'); END";
            command.ExecuteNonQuery();
        }

        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(0, OperationCount());
        Assert.Equal("original video", File.ReadAllText(issue.Path));
        Assert.False(Directory.Exists(Path.Combine(Movies, "Film")));
    }

    [Fact]
    public async Task FailedCompletionWriteKeepsDurableClaimAndRestoresBundle()
    {
        var issue = await Approve(companions: true);
        using (var connection = _db.ConnectionFactory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER reject_complete BEFORE UPDATE ON media_move_operations WHEN NEW.state='Completed' BEGIN SELECT RAISE(ABORT,'controlled complete failure'); END";
            command.ExecuteNonQuery();
        }

        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("original video", File.ReadAllText(issue.Path));
        Assert.Equal("original subtitles", File.ReadAllText(Path.ChangeExtension(issue.Path, ".en.srt")));
        Assert.Equal("NeedsReview", _db.Moves.Read(result.MoveOperationId!)!.State);
        Assert.True(_db.Moves.HasActiveConflict(issue.Path));
    }

    [Fact]
    public async Task CancellationAfterFirstPublishRestoresBundleAndRemainsReviewable()
    {
        var issue = await Approve(companions: true);
        using var cancellation = new CancellationTokenSource();
        _service.Checkpoint = (point, _) =>
        {
            if (point == "Published")
            {
                cancellation.Cancel();
            }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ExecuteAsync(issue, null, cancellation.Token));
        Assert.Equal("original video", File.ReadAllText(issue.Path));
        Assert.Single(_db.Moves.GetNeedsReview());
        Assert.True(_db.Moves.HasActiveConflict(issue.Path));
    }

    [Fact]
    public async Task DestinationDirectoryReplacementCannotReceiveRemainingCompanions()
    {
        var issue = await Approve(companions: true);
        var destination = Path.Combine(Movies, "Film");
        var displaced = Path.Combine(Movies, "displaced");
        var wroteIntoReplacement = false;
        _service.Checkpoint = (point, index) =>
        {
            if (point == "Published" && index == 1)
            {
                wroteIntoReplacement = true;
            }

            if (point == "BeforeEntry" && index == 1)
            {
                Directory.Move(destination, displaced);
                Directory.CreateDirectory(destination);
                Write(Path.Combine(destination, "foreign.txt"), "new destination owner");
            }
        };
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.False(wroteIntoReplacement);
        Assert.False(File.Exists(Path.Combine(destination, "Film.en.srt")));
        Assert.False(File.Exists(Path.Combine(destination, "Film.nfo")));
        Assert.Equal("new destination owner", File.ReadAllText(Path.Combine(destination, "foreign.txt")));
        Assert.Equal("original video", File.ReadAllText(Path.Combine(displaced, "Film.mkv")));
        Assert.Equal("NeedsReview", _db.Moves.Read(result.MoveOperationId!)!.State);
    }

    [Fact]
    public async Task ChangedJellyfinSourceBindingInvalidatesApproval()
    {
        var issue = await Approve();
        _items[0].Path = Write(Path.Combine(Movies, "new-import.mkv"), "new import");
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(0, OperationCount());
        Assert.Equal("original video", File.ReadAllText(issue.Path));
    }

    [Fact]
    public async Task ReplacedTargetRootInvalidatesEarlierApproval()
    {
        var issue = await Approve(directory: true);
        Directory.Move(Movies, Path.Combine(_root, "previous-movies"));
        Directory.CreateDirectory(Movies);
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(0, OperationCount());
        Assert.True(Directory.Exists(issue.Path));
        Assert.False(Directory.Exists(Path.Combine(Movies, "Film")));
    }

    [Fact]
    public async Task MetadataChangeWithSameMediaBytesAndTimestampRequiresNewApproval()
    {
        var issue = await Approve();
        var modified = File.GetLastWriteTimeUtc(issue.Path);
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(issue.Path, File.GetAttributes(issue.Path) | FileAttributes.Hidden);
        }
        else
        {
            File.SetUnixFileMode(issue.Path, File.GetUnixFileMode(issue.Path) ^ UnixFileMode.GroupWrite);
        }

        File.SetLastWriteTimeUtc(issue.Path, modified);
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("original video", File.ReadAllText(issue.Path));
        Assert.Equal(0, OperationCount());
    }

    [Fact]
    public async Task NewCompanionAfterApprovalRequiresNewCompletePreview()
    {
        var issue = await Approve(companions: true);
        var added = Write(Path.ChangeExtension(issue.Path, ".fr.srt"), "new subtitles");
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(0, OperationCount());
        Assert.Equal("original video", File.ReadAllText(issue.Path));
        Assert.Equal("new subtitles", File.ReadAllText(added));
        Assert.False(Directory.Exists(Path.Combine(Movies, "Film")));
    }

    [Fact]
    public async Task NewCompanionDuringBundleMoveStopsAndRecoversEarlierPayloads()
    {
        var issue = await Approve(companions: true);
        string? added = null;
        _service.Checkpoint = (point, _) =>
        {
            if (point == "Published") added = Write(Path.ChangeExtension(issue.Path, ".fr.srt"), "new subtitles");
        };
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("new subtitles", File.ReadAllText(added!));
        var operation = _db.Moves.Read(result.MoveOperationId!)!;
        Assert.Equal("NeedsReview", operation.State);
        Assert.True(File.Exists(issue.Path) || File.Exists(Path.Combine(Movies, "Film", "Film.mkv")));
        Assert.Equal("original subtitles", File.ReadAllText(Path.ChangeExtension(issue.Path, ".en.srt")));
    }

    [Fact]
    public async Task IndependentApprovedMoviesInOneSharedFolderCanBothComplete()
    {
        _items.Add(new Movie { Id = Guid.NewGuid(), Name = "One", Path = Write(Path.Combine(Movies, "One.mkv"), "first film") });
        _items.Add(new Movie { Id = Guid.NewGuid(), Name = "Two", Path = Write(Path.Combine(Movies, "Two.mkv"), "second film") });
        var findings = await _planner.ScanAsync(IssueType.Ungrouped, _items, _config, new Progress<double>(), CancellationToken.None);
        Assert.Equal(2, findings.Count);
        Assert.All(findings, issue => Assert.False(issue.HasBlockingWarnings));
        _db.Issues.ReplaceDetectedIssues(IssueType.Ungrouped, findings);
        _db.Issues.QueueDetectedIssues(IssueType.Ungrouped);
        foreach (var issue in _db.Issues.GetIssues(IssueType.Ungrouped))
        {
            var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
            Assert.True(result.Success, result.Message);
        }

        Assert.Equal("first film", File.ReadAllText(Path.Combine(Movies, "One", "One.mkv")));
        Assert.Equal("second film", File.ReadAllText(Path.Combine(Movies, "Two", "Two.mkv")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupedEpisodeIgnoresAnEmptiedSiblingContainerButRechecksItsOwnCompanions(bool newCompanion)
    {
        Directory.CreateDirectory(Path.Combine(Tv, "MobLand (2025)"));
        var sibling = Write(Path.Combine(Tv, "Other release", "Other.S01E01.mkv"), "other episode");
        var series = new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), Name = "MobLand", Path = Tv,
            PremiereDate = new DateTime(2025, 1, 1) };
        var episode = new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), SeriesId = series.Id,
            SeriesName = "MobLand", ParentIndexNumber = 2, Path = Write(Path.Combine(Tv, "MobLand.S02E02.mkv"), "episode") };
        _items.AddRange([series, episode]);
        var findings = await _planner.ScanAsync(IssueType.Ungrouped, _items, _config, new Progress<double>(), CancellationToken.None);
        Assert.False(Assert.Single(findings).HasBlockingWarnings);
        Assert.NotNull(MediaMovePlan.ReadIssue(findings[0]));
        Assert.Null(MediaMovePlan.ReadIssue(findings[0])!.SourceCleanup);
        Assert.DoesNotContain("\"sourceCleanup\"", findings[0].DetailsJson, StringComparison.Ordinal);
        _db.Issues.ReplaceDetectedIssues(IssueType.Ungrouped, findings);
        _db.Issues.QueueDetectedIssues(IssueType.Ungrouped);
        Directory.CreateDirectory(Path.Combine(Tv, "Other (2025)"));
        File.Move(sibling, Path.Combine(Tv, "Other (2025)", "Other.S01E01.mkv"));
        if (newCompanion) Write(Path.ChangeExtension(episode.Path, ".en.srt"), "new subtitle");

        var result = await _service.ExecuteAsync(Assert.Single(_db.Issues.GetIssues(IssueType.Ungrouped)), null, CancellationToken.None);
        Assert.True(result.Success == !newCompanion, result.Message);
        Assert.Equal(newCompanion, File.Exists(episode.Path));
        if (newCompanion) Assert.Equal("new subtitle", File.ReadAllText(Path.ChangeExtension(episode.Path, ".en.srt")));
    }

    [Fact]
    public async Task CompletedMoveHandsIndexingToAnActiveLibraryScanWithoutRecordingAnError()
    {
        var issue = await Approve();
        var tasks = DispatchProxy.Create<ITaskManager, LibraryScanCancellationTests.ManagerProxy>();
        ((LibraryScanCancellationTests.ManagerProxy)(object)tasks).Workers = [LibraryScanCancellationTests.Worker("RefreshLibrary", TaskState.Running)];
        var files = DispatchProxy.Create<MediaBrowser.Model.IO.IFileSystem, OriginalLanguageFlowTests.CallProxy>();
        using var native = new NativeLibraryRefresher(_manager, files, (_, _, _) => Task.CompletedTask, tasks);
        var service = new MediaMoveService(_db, _guard, _protection, _manager, NullLogger<MediaMoveService>.Instance, nativeRefresh: native);
        var before = Api.Diagnostics.RecentAll().Where(entry => entry.Source == "Move.NativeIndex").Sum(entry => entry.Count);
        var result = await service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.Contains("running library scan", result.Message, StringComparison.Ordinal);
        Assert.Equal("original video", File.ReadAllText(Path.Combine(Movies, "Film", "Film.mkv")));
        Assert.Equal(before, Api.Diagnostics.RecentAll().Where(entry => entry.Source == "Move.NativeIndex").Sum(entry => entry.Count));
        Assert.Equal("Completed", _db.Moves.Read(result.MoveOperationId!)!.State);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task GroupedEpisodesRecycleRedundantSourceMetadataAndUndoRestoresIt(bool lateCollision, bool identityChanged)
    {
        var target = Path.Combine(Tv, "MobLand (2025)", "Season 02");
        var existing = Write(Path.Combine(target, "MobLand.S02E01.mkv"), "existing episode");
        var existingMetadata = Write(Path.Combine(Tv, "MobLand (2025)", "tvshow.nfo"), "existing metadata");
        var artwork = new[] { "backdrop.jpg", "folder.jpg", "landscape.jpg", "logo.png" };
        foreach (var name in artwork) Write(Path.Combine(Tv, "MobLand (2025)", name), "destination " + name);
        for (var number = 2; number <= 3; number++)
        {
            var folder = Path.Combine(Tv, "MobLand release " + number);
            var series = new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), Name = "MobLand", Path = folder,
                PremiereDate = new DateTime(2025, 1, 1) };
            series.ProviderIds["Tvdb"] = "same series";
            var episode = new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), SeriesId = series.Id,
                SeriesName = "MobLand", ParentIndexNumber = 2, IndexNumber = number,
                Path = Write(Path.Combine(folder, "MobLand.S02E0" + number + ".mkv"), "episode " + number) };
            Write(Path.ChangeExtension(episode.Path, ".en.srt"), "subtitles " + number);
            Write(Path.Combine(folder, "tvshow.nfo"), "source metadata");
            foreach (var name in artwork) Write(Path.Combine(folder, name), "source " + name);
            _items.AddRange([series, episode]);
        }

        var findings = await _planner.ScanAsync(IssueType.Ungrouped, _items, _config, new Progress<double>(), CancellationToken.None);
        Assert.Equal(2, findings.Count);
        Assert.All(findings, issue => Assert.False(issue.HasBlockingWarnings, issue.DetailsJson));
        _db.Issues.ReplaceDetectedIssues(IssueType.Ungrouped, findings);
        _db.Issues.QueueDetectedIssues(IssueType.Ungrouped);
        if (lateCollision) Write(Path.Combine(target, "MobLand.S02E02.en.srt"), "new import");
        if (identityChanged)
        {
            var changed = new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), Name = "MobLand", Path = Path.Combine(Tv, "MobLand (2025)") };
            changed.ProviderIds["Tvdb"] = "different series";
            _items.Add(changed);
        }
        var completed = new List<(Issue Issue, string Operation)>();
        foreach (var issue in _db.Issues.GetIssues(IssueType.Ungrouped))
        {
            var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
            if (identityChanged)
            {
                Assert.False(result.Success);
                Assert.True(File.Exists(issue.Path));
                continue;
            }
            if (lateCollision && issue.Path.EndsWith("02.mkv", StringComparison.Ordinal))
            {
                Assert.False(result.Success);
                Assert.True(File.Exists(issue.Path));
                Assert.Equal("new import", File.ReadAllText(Path.Combine(target, "MobLand.S02E02.en.srt")));
                continue;
            }

            Assert.True(result.Success, result.Message);
            Assert.False(File.Exists(issue.Path));
            Assert.True(File.Exists(Path.Combine(target, Path.GetFileName(issue.Path))));
            Assert.False(Directory.Exists(Path.GetDirectoryName(issue.Path)));
            var recorded = MediaMoveManifest.TryRead(_db.Moves.Read(result.MoveOperationId!)!.ManifestJson, result.MoveOperationId!)!;
            Assert.NotNull(recorded.RecycledSource);
            Assert.Equal("source metadata", File.ReadAllText(Path.Combine(recorded.RecycledSource.BinPath!, "tvshow.nfo")));
            completed.Add((issue, result.MoveOperationId!));
        }

        foreach (var move in completed.AsEnumerable().Reverse())
        {
            var undo = await _service.UndoAsync(move.Operation, null, CancellationToken.None);
            Assert.True(undo.Success, undo.Message);
            Assert.True(File.Exists(move.Issue.Path));
            Assert.Equal("source metadata", File.ReadAllText(Path.Combine(Path.GetDirectoryName(move.Issue.Path)!, "tvshow.nfo")));
            foreach (var name in artwork) Assert.Equal("source " + name, File.ReadAllText(Path.Combine(Path.GetDirectoryName(move.Issue.Path)!, name)));
        }

        Assert.Equal("existing episode", File.ReadAllText(existing));
        Assert.Equal("existing metadata", File.ReadAllText(existingMetadata));
    }

    [Theory]
    [InlineData("The Mentalist (2008)", "The Mentalist", "www.UIndex.org - The Mentalist S04E03 1080p", "www UIndex org - The Mentalist", 2008, "82459")]
    [InlineData("WAR (2026)", "WAR", "www.UIndex.org - War.2026.S01E07.1080p.WEB.h264-ETHEL", "www UIndex org - War 2026", 2026, "477406")]
    [InlineData("Miraculous - Tales of Ladybug & Cat Noir", "Miraculous - Tales of Ladybug & Cat Noir", "Miraculous Tales of Ladybug and Cat Noir S06E18 Heartfixer 1080p DSNP WEB-DL", "Miraculous Tales of Ladybug and Cat Noir", 2015, "302257")]
    public async Task TvdbMasterGroupsADifferentlyNamedFolderAndRecyclesItsLeftovers(string masterName, string title, string releaseName, string releaseTitle, int year, string tvdbId)
    {
        var master = Path.Combine(Tv, masterName);
        Write(Path.Combine(master, "tvshow.nfo"), "master metadata");
        var existing = Write(Path.Combine(master, "Season 01", "The.Mentalist.S01E01.mkv"), "episode 1");
        var release = Path.Combine(Tv, releaseName);
        var episodePath = Write(Path.Combine(release, "The.Mentalist.S04E03.1080p.mkv"), "episode 403");
        Write(Path.Combine(release, "RARBG.txt"), "release notes");
        Write(Path.Combine(release, "Sample", "sample.mkv"), "sample");
        var identified = new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), Name = title, Path = master, PremiereDate = new DateTime(year, 1, 1) };
        identified.ProviderIds["Tvdb"] = tvdbId;
        var unidentified = new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), Name = releaseTitle, Path = release };
        _items.Add(identified);
        _items.Add(unidentified);
        _items.Add(new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), SeriesId = identified.Id, SeriesName = title, ParentIndexNumber = 1, IndexNumber = 1, Path = existing });
        _items.Add(new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), SeriesId = unidentified.Id, SeriesName = releaseTitle, ParentIndexNumber = 4, IndexNumber = 3, Path = episodePath });
        var tvdb = new MediaIdentityTests.FakeTvdb();
        using var client = new Metadata.TvdbClient(() => new System.Net.Http.HttpClient(tvdb, false), NullLogger.Instance, TimeSpan.Zero);
        var planner = new MediaMovePlanner(_guard, _protection, _manager, _db.Moves, new Metadata.MediaIdentityService(_db.Identities, client, NullLogger.Instance));
        _config.TvdbApiKey = "key";

        var issue = Assert.Single(await planner.ScanAsync(IssueType.Ungrouped, _items, _config, new Progress<double>(), CancellationToken.None));
        var plan = MediaMovePlan.ReadIssue(issue)!;
        Assert.Empty(plan.BlockingReasons);
        var destination = Path.Combine(master, "Season 04", "The.Mentalist.S04E03.1080p.mkv");
        Assert.Equal(destination, plan.Entries[0].Destination);

        _db.Issues.ReplaceDetectedIssues(IssueType.Ungrouped, [issue]);
        _db.Issues.QueueDetectedIssues(IssueType.Ungrouped);
        var moved = await _service.ExecuteAsync(Assert.Single(_db.Issues.GetIssues(IssueType.Ungrouped)), null, CancellationToken.None);
        Assert.True(moved.Success, moved.Message);
        Assert.Equal("episode 403", File.ReadAllText(destination));
        Assert.False(Directory.Exists(release));
        Assert.Equal("master metadata", File.ReadAllText(Path.Combine(master, "tvshow.nfo")));
        Assert.False(File.Exists(Path.Combine(master, "RARBG.txt")));

        var undo = await _service.UndoAsync(moved.MoveOperationId!, null, CancellationToken.None);
        Assert.True(undo.Success, undo.Message);
        Assert.Equal("episode 403", File.ReadAllText(episodePath));
        Assert.Equal("release notes", File.ReadAllText(Path.Combine(release, "RARBG.txt")));
        Assert.Equal("sample", File.ReadAllText(Path.Combine(release, "Sample", "sample.mkv")));
    }

    [Theory]
    [InlineData("clean")]
    [InlineData("missing-destination-art")]
    [InlineData("new-file")]
    [InlineData("changed-metadata")]
    [InlineData("unknown-file")]
    [InlineData("nested-seasons")]
    [InlineData("pending-receipt")]
    [InlineData("dry-run-undo")]
    [InlineData("new-episode")]
    [InlineData("outside-library-container")]
    [InlineData("malformed-receipt")]
    [InlineData("named-companions")]
    public async Task GroupingCleansOnlyApprovedRedundantMetadataAfterTheFinalEpisode(string mode)
    {
        var folder = Path.Combine(Tv, "MobLand release");
        var canonical = Path.Combine(Tv, "MobLand (2025)");
        Write(Path.Combine(canonical, "tvshow.nfo"), "destination metadata");
        var sourceMetadata = Write(Path.Combine(folder, "tvshow.nfo"), "source metadata");
        if (mode is "missing-destination-art" or "nested-seasons") Write(Path.Combine(folder, "logo.png"), "unique artwork");
        if (mode == "unknown-file") Write(Path.Combine(folder, "notes.txt"), "keep this");
        var series = new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), Name = "MobLand", Path = mode == "outside-library-container" ? _root : folder, PremiereDate = new DateTime(2025, 1, 1) };
        _items.Add(series);
        for (var number = 1; number <= 2; number++)
        {
            var episodePath = Write(Path.Combine(mode == "nested-seasons" ? Path.Combine(folder, "Season 01") : folder, "MobLand.S01E0" + number + ".mkv"), "episode " + number);
            _items.Add(new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), SeriesId = series.Id,
                SeriesName = "MobLand", ParentIndexNumber = 1, IndexNumber = number,
                Path = episodePath });
            if (mode == "named-companions") Write(Path.ChangeExtension(episodePath, ".jpg"), "episode artwork " + number);
        }

        var findings = await _planner.ScanAsync(IssueType.Ungrouped, _items, _config, new Progress<double>(), CancellationToken.None);
        Assert.Equal(2, findings.Count);
        foreach (var issue in findings)
        {
            Assert.Empty(MediaMovePlan.ReadIssue(issue)!.BlockingReasons);
        }

        _db.Issues.ReplaceDetectedIssues(IssueType.Ungrouped, findings);
        _db.Issues.QueueDetectedIssues(IssueType.Ungrouped);
        var approved = _db.Issues.GetIssues(IssueType.Ungrouped).OrderBy(issue => issue.Path, StringComparer.Ordinal).ToArray();
        var first = await _service.ExecuteAsync(approved[0], null, CancellationToken.None);
        Assert.True(first.Success, first.Message);
        Assert.True(Directory.Exists(folder));
        if (mode == "new-file") Write(Path.Combine(folder, "new.jpg"), "new artwork");
        if (mode == "new-episode") Write(Path.Combine(folder, "MobLand.S01E03.mkv"), "new episode");
        if (mode == "changed-metadata") File.WriteAllText(sourceMetadata, "changed source metadata");
        var last = await _service.ExecuteAsync(approved[1], null, CancellationToken.None);
        Assert.True(last.Success, last.Message);
        // Leftover files the scan saw (notes.txt) are recycled with the folder; only files that
        // appeared or changed after the scan, another episode or an out-of-scope container keep it.
        var preserve = mode is "new-file" or "changed-metadata" or "new-episode" or "outside-library-container";
        Assert.Equal(preserve, Directory.Exists(folder));
        Assert.Equal("destination metadata", File.ReadAllText(Path.Combine(canonical, "tvshow.nfo")));
        if (mode is "missing-destination-art" or "nested-seasons") Assert.Equal("unique artwork", File.ReadAllText(Path.Combine(canonical, "logo.png")));
        if (!preserve)
        {
            if (mode == "malformed-receipt")
            {
                var operation = _db.Moves.Read(last.MoveOperationId!)!;
                var manifest = MediaMoveManifest.TryRead(operation.ManifestJson, operation.Id)!;
                var malformed = (manifest with { RecycledSource = manifest.RecycledSource! with { Inventory = [null!] } }).Serialize();
                Assert.Null(MediaMoveManifest.TryRead(malformed, operation.Id));
                Assert.False(_db.Moves.Update(operation.Id, operation.OwnerToken, "Completed", "Completed", malformed));
                Assert.False(Directory.Exists(folder));
                Assert.True(Directory.Exists(manifest.RecycledSource!.BinPath));
            }

            if (mode == "pending-receipt")
            {
                var operation = _db.Moves.Read(last.MoveOperationId!)!;
                var manifest = MediaMoveManifest.TryRead(operation.ManifestJson, operation.Id)!;
                Assert.True(_db.Moves.Update(operation.Id, operation.OwnerToken, "Completed", "Completed",
                    (manifest with { RecycledSource = manifest.RecycledSource! with { BinPath = null } }).Serialize()));
            }

            if (mode == "dry-run-undo")
            {
                _config.DryRun = true;
                var preview = await _service.UndoAsync(last.MoveOperationId!, null, CancellationToken.None);
                Assert.True(preview.Success, preview.Message);
                Assert.True(preview.WasDryRun);
                Assert.False(Directory.Exists(folder));
                _config.DryRun = false;
            }

            var undoLast = await _service.UndoAsync(last.MoveOperationId!, null, CancellationToken.None);
            Assert.True(undoLast.Success, undoLast.Message);
            var undoFirst = await _service.UndoAsync(first.MoveOperationId!, null, CancellationToken.None);
            Assert.True(undoFirst.Success, undoFirst.Message);
            Assert.Equal("source metadata", File.ReadAllText(sourceMetadata));
            if (mode == "unknown-file") Assert.Equal("keep this", File.ReadAllText(Path.Combine(folder, "notes.txt")));
            if (mode is "missing-destination-art" or "nested-seasons") Assert.Equal("unique artwork", File.ReadAllText(Path.Combine(folder, "logo.png")));
        }
    }

    [Fact]
    public async Task OrphanCleanupCannotConsumeAReservedMovePayload()
    {
        var issue = await Approve();
        Assert.NotNull(_db.Moves.TryBegin(MediaMovePlan.ReadIssue(issue)!, issue.Id));
        var guard = new LibraryGuard(_manager, moves: _db.Moves);
        var orphan = new Issue { Type = IssueType.OrphanedDebris, Path = issue.Path, DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { kind = OrphanCleanupScanner.KindOrphanSubtitle }) };
        var result = await new OrphanCleanupFixer(guard, null!, NullLogger<OrphanCleanupFixer>.Instance).FixAsync(orphan, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(FixReason.MoveNeedsReview, result.Reason);
        Assert.Equal("original video", File.ReadAllText(issue.Path));
    }

    [Fact]
    public async Task SidecarSweepPreservesFilesInsideReservedDestination()
    {
        var issue = await Approve();
        Assert.NotNull(_db.Moves.TryBegin(MediaMovePlan.ReadIssue(issue)!, issue.Id));
        var survivor = Write(Path.Combine(Movies, "Film", "owned.mediadash.tmp"), "reserved payload");
        File.SetLastWriteTimeUtc(survivor, DateTime.UtcNow.AddDays(-30));
        var guard = new LibraryGuard(_manager, moves: _db.Moves);
        Assert.Empty(guard.SweepOrphanSidecars());
        Assert.Equal("reserved payload", File.ReadAllText(survivor));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnotherScheduledFixerCannotStartOnAReservedMovePath(bool externalMetadata)
    {
        var movedIssue = await Approve();
        var findingPath = externalMetadata ? Write(Path.Combine(_root, "metadata", "poster.png"), "owned metadata") : movedIssue.Path;
        _db.Issues.ReplaceDetectedIssues(IssueType.AudioLanguage, [new Issue { Type = IssueType.AudioLanguage, ItemId = movedIssue.ItemId, Path = findingPath }]);
        _db.Issues.QueueDetectedIssues(IssueType.AudioLanguage);
        var audio = Assert.Single(_db.Issues.GetIssues(IssueType.AudioLanguage));
        Assert.NotNull(_db.Moves.TryBegin(MediaMovePlan.ReadIssue(movedIssue)!, movedIssue.Id));
        var fixer = new RecordedFixer();
        var executor = new FixExecutor(_db.Issues, [fixer], new PluginConfigurationProvider(() => _config), TimeProvider.System, _protection,
            NullLogger<FixExecutor>.Instance, _db.Moves);
        var execution = await executor.ExecuteAsync(new PlannedFix(PlannedFixKind.Single, audio, []), true, new RecordedProgress(), CancellationToken.None);
        Assert.NotNull(execution);
        Assert.False(fixer.Called);
        Assert.Equal(FixReason.MoveNeedsReview, execution.Result.Reason);
        Assert.Empty(execution.ResolvedIssueIds);
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(audio.Id)!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecycleRestorePreservesItsBinCopyAndCurrentOccupantWhenAMoveReservesTheTarget(bool force)
    {
        var issue = await Approve();
        _config.RecycleBinPath = Path.Combine(_root, "bin");
        var paths = DispatchProxy.Create<MediaBrowser.Common.Configuration.IApplicationPaths, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)paths).Call = (method, _) => method.Name == "get_DataPath" ? _root : null;
        var bin = new RecycleBin(paths, NullLogger<RecycleBin>.Instance, _db.History);
        var recycled = bin.MoveToBin(issue.Path);
        _db.History.AddHistory(new HistoryEntry { Type = IssueType.Quality, Path = issue.Path, RecyclePath = recycled, FixedAtUtc = DateTime.UtcNow, Success = true });
        var history = Assert.Single(_db.History.GetHistory());
        if (force) Write(issue.Path, "new import");
        var incoming = Write(Path.Combine(Movies, "Incoming.mkv"), "incoming movie");
        var plan = new MediaMovePlan
        {
            Type = IssueType.Ungrouped, ItemId = Guid.NewGuid(),
            Entries = [new(incoming, issue.Path, false, MediaMoveSnapshot.Capture(incoming).Export())]
        }.Seal();
        Assert.NotNull(_db.Moves.TryBegin(plan));
        var monitor = DispatchProxy.Create<ILibraryMonitor, OriginalLanguageFlowTests.CallProxy>();
        var restore = new RestoreService(_db, _db.History, _db.RestoreOps, bin, _guard, monitor, NullLogger<RestoreService>.Instance, _protection);
        var result = restore.RestoreFromHistory(history.Id, force);
        Assert.False(result.Success);
        Assert.Equal(FixReason.MoveNeedsReview, result.Reason);
        Assert.Equal("original video", File.ReadAllText(recycled));
        Assert.Equal("incoming movie", File.ReadAllText(incoming));
        if (force) Assert.Equal("new import", File.ReadAllText(issue.Path));
        else Assert.False(File.Exists(issue.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedMoveInvalidatesOldApprovalsKeeperReferencesAndPathCaches(bool directory)
    {
        var issue = await Approve(directory: directory);
        var source = directory ? Path.Combine(issue.Path, "Film.mkv") : issue.Path;
        var destination = Path.Combine(Movies, "Film", "Film.mkv");
        var duplicate = Write(Path.Combine(Movies, "UnrelatedCopy.mkv"), "separate candidate");
        _db.Issues.ReplaceDetectedIssues(IssueType.AudioLanguage, [new Issue { Type = IssueType.AudioLanguage, ItemId = issue.ItemId, Path = source,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { removeLanguages = new[] { "eng" }, approvedSource = source }) }]);
        _db.Issues.QueueDetectedIssues(IssueType.AudioLanguage);
        var audio = Assert.Single(_db.Issues.GetIssues(IssueType.AudioLanguage));
        _db.Issues.ReplaceDetectedIssues(IssueType.Duplicate, [new Issue { Type = IssueType.Duplicate, Path = duplicate,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { keeperPath = source }) }]);
        _db.Issues.QueueDetectedIssues(IssueType.Duplicate);
        var duplicateIssue = Assert.Single(_db.Issues.GetIssues(IssueType.Duplicate));
        var probes = new ProbeCacheStore(_db.ConnectionFactory);
        var size = new FileInfo(source).Length;
        var modified = File.GetLastWriteTimeUtc(source).Ticks;
        probes.StoreProbe(source, size, modified, "old source probe");
        probes.StoreProbe(destination, size, modified, "old destination probe");
        probes.StoreProbe(duplicate, 1, 2, "unrelated probe");
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        var changedAudio = _db.Issues.GetIssue(audio.Id)!;
        Assert.Equal(IssueStatus.Detected, changedAudio.Status);
        Assert.Equal(destination, changedAudio.Path);
        Assert.DoesNotContain("removeLanguages", changedAudio.DetailsJson);
        Assert.True(changedAudio.HasBlockingWarnings);
        var changedDuplicate = _db.Issues.GetIssue(duplicateIssue.Id)!;
        Assert.Equal(IssueStatus.Detected, changedDuplicate.Status);
        Assert.Equal(duplicate, changedDuplicate.Path);
        Assert.DoesNotContain("keeperPath", changedDuplicate.DetailsJson);
        Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(MoveController().ApproveIssue(audio.Id));
        Assert.Null(probes.GetCachedProbe(source, size, modified));
        Assert.Null(probes.GetCachedProbe(destination, size, modified));
        Assert.Equal("unrelated probe", probes.GetCachedProbe(duplicate, 1, 2));
        Write(source, "new import");
        Assert.Equal(destination, _db.Issues.GetIssue(audio.Id)!.Path);
        Assert.Equal("new import", File.ReadAllText(source));
    }

    [Theory]
    [InlineData("issues")]
    [InlineData("probe_cache")]
    public async Task InvalidationWriteFailureRollsBackAllFindingAndCacheChangesAndRetainsMoveClaims(string table)
    {
        var issue = await Approve();
        _db.Issues.ReplaceDetectedIssues(IssueType.AudioLanguage, [new Issue { Type = IssueType.AudioLanguage, ItemId = issue.ItemId,
            Path = issue.Path, DetailsJson = "{\"approved\":true}" }]);
        _db.Issues.QueueDetectedIssues(IssueType.AudioLanguage);
        var audio = Assert.Single(_db.Issues.GetIssues(IssueType.AudioLanguage));
        var probes = new ProbeCacheStore(_db.ConnectionFactory);
        probes.StoreProbe(issue.Path, 1, 2, "old probe");
        using (var connection = _db.ConnectionFactory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = table == "issues"
                ? "CREATE TRIGGER reject_invalidation BEFORE UPDATE ON issues BEGIN SELECT RAISE(ABORT,'controlled invalidation failure'); END"
                : "CREATE TRIGGER reject_invalidation BEFORE DELETE ON probe_cache BEGIN SELECT RAISE(ABORT,'controlled invalidation failure'); END";
            command.ExecuteNonQuery();
        }

        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(FixReason.MoveNeedsReview, result.Reason);
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(audio.Id)!.Status);
        Assert.Equal(audio.DetailsJson, _db.Issues.GetIssue(audio.Id)!.DetailsJson);
        Assert.Equal(issue.Path, _db.Issues.GetIssue(audio.Id)!.Path);
        Assert.Equal("old probe", probes.GetCachedProbe(issue.Path, 1, 2));
        Assert.True(_db.Moves.HasActiveConflict(issue.Path));
        Assert.Equal("NeedsReview", _db.Moves.Read(result.MoveOperationId!)!.State);
        Assert.Equal("original video", File.ReadAllText(Path.Combine(Movies, "Film", "Film.mkv")));
    }

    [Fact]
    public async Task OrphanScannerExcludesReservedRecoveryTreeAndItsParentsButStillReportsUnrelatedDebris()
    {
        var source = Path.Combine(Movies, "Reserved", "Recovery");
        Write(Path.Combine(source, "lost.srt"), "owned subtitles");
        Write(Path.Combine(source, "lost.trickplay", "sprite.bin"), "owned sprite");
        Write(Path.Combine(Movies, "Unrelated", "notes.nfo"), "unrelated debris");
        var plan = new MediaMovePlan { Type = IssueType.Ungrouped, ItemId = Guid.NewGuid(),
            Entries = [new(source, Path.Combine(Tv, "Recovered"), true, MediaMoveSnapshot.Capture(source).Export())] }.Seal();
        Assert.NotNull(_db.Moves.TryBegin(plan));
        _config.OrphanScanEmptyFolders = true;
        _config.OrphanScanSubtitles = true;
        _config.OrphanScanTrickplay = true;
        _config.OrphanScanMetadata = false;
        var paths = DispatchProxy.Create<MediaBrowser.Controller.IServerApplicationPaths, OriginalLanguageFlowTests.CallProxy>();
        var scanner = new OrphanCleanupScanner(paths, _manager, NullLogger<OrphanCleanupScanner>.Instance, _protection, moves: _db.Moves);
        var issues = await scanner.ScanAsync([], new Progress<double>(), CancellationToken.None);
        Assert.Contains(issues, finding => finding.Path == Path.Combine(Movies, "Unrelated"));
        Assert.DoesNotContain(issues, finding => MediaMovePlanner.Overlap(finding.Path, source));
        Assert.Equal("owned subtitles", File.ReadAllText(Path.Combine(source, "lost.srt")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ScanPersistencePreservesReservedFindingsAndDoesNotIntroduceConflictingNewFindings(bool scoped, bool externalMetadata)
    {
        var issue = await Approve();
        var findingPath = externalMetadata ? Write(Path.Combine(_root, "metadata", "poster.png"), "metadata") : issue.Path;
        var other = Write(Path.Combine(Movies, "Other.mkv"), "other film");
        _db.Issues.ReplaceDetectedIssues(IssueType.AudioLanguage,
            [new Issue { Type = IssueType.AudioLanguage, ItemId = issue.ItemId, Path = findingPath, DetailsJson = "{\"old\":true}" },
             new Issue { Type = IssueType.AudioLanguage, Path = other, DetailsJson = "{\"old\":true}" }]);
        var held = _db.Issues.GetIssues(IssueType.AudioLanguage).Single(row => row.Path == findingPath);
        var plan = MediaMovePlan.ReadIssue(issue)!;
        Assert.NotNull(_db.Moves.TryBegin(plan, issue.Id));
        var destination = externalMetadata ? Path.Combine(_root, "metadata", "new.png") : plan.Entries[0].Destination;
        _db.Issues.ReplaceDetectedIssues(IssueType.AudioLanguage,
            [new Issue { Type = IssueType.AudioLanguage, ItemId = issue.ItemId, Path = destination, DetailsJson = "{\"stale\":true}" },
             new Issue { Type = IssueType.AudioLanguage, Path = other, DetailsJson = "{\"new\":true}" }],
            scoped ? [findingPath, destination, other] : null);
        var findings = _db.Issues.GetIssues(IssueType.AudioLanguage);
        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, row => row.Id == held.Id && row.Path == findingPath && row.DetailsJson == held.DetailsJson);
        Assert.Contains(findings, row => row.Path == other && row.DetailsJson == "{\"new\":true}");
        Assert.DoesNotContain(findings, row => row.Path == destination);
    }

    [Fact]
    public async Task ScanDispatchExcludesReservedItemAndAlternateButContinuesUnrelatedItems()
    {
        var issue = await Approve();
        Assert.NotNull(_db.Moves.TryBegin(MediaMovePlan.ReadIssue(issue)!, issue.Id));
        _items.Add(new Movie { Id = Guid.NewGuid(), Path = Write(Path.Combine(Movies, "Other.mkv"), "unrelated") });
        _items.Add(new Movie { Id = Guid.NewGuid(), Path = Write(Path.Combine(Movies, "Alternate.mkv"), "alternate"), LocalAlternateVersions = [issue.Path] });
        _config.PauseDuringPlayback = false;
        _db.SetState("ContentAwareDefaultsChecked", "true");
        var state = new RunState();
        state.ArmBypassIdleCheck(RunRole.Scan);
        var manager = DispatchProxy.Create<MediaBrowser.Model.Tasks.ITaskManager, LibraryScanCancellationTests.ManagerProxy>();
        var scanner = new CapturingScanner();
        var previousState = Plugin.AttachedRunState;
        try
        {
            var task = new ScanTask(_manager, [scanner], _db, null!, manager, state, new PluginConfigurationProvider(() => _config), _protection, NullLogger<ScanTask>.Instance);
            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
            var seen = Assert.Single(scanner.Seen);
            Assert.Equal(Path.Combine(Movies, "Other.mkv"), seen.Path);
        }
        finally
        {
            Plugin.AttachedRunState = previousState;
        }
    }

    private sealed class CapturingScanner : IScanner
    {
        public IssueType Type => IssueType.AudioLanguage;
        public IReadOnlyList<BaseItem> Seen { get; private set; } = [];
        public Task<IReadOnlyList<Issue>> ScanAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken cancellationToken)
        {
            Seen = items;
            return Task.FromResult<IReadOnlyList<Issue>>([]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UndoRestoresTheCompleteRecordedOperationAndPreventsAutomaticReapplication(bool directory)
    {
        var issue = await Approve(directory: directory, companions: true);
        var moved = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.True(moved.Success, moved.Message);
        var undone = await _service.UndoAsync(moved.MoveOperationId!, null, CancellationToken.None);
        Assert.True(undone.Success, undone.Message);
        Assert.Equal(0, undone.BytesFreed);
        var source = directory ? Path.Combine(issue.Path, "Film.mkv") : issue.Path;
        Assert.Equal("original video", File.ReadAllText(source));
        Assert.Equal("original subtitles", File.ReadAllText(Path.ChangeExtension(source, ".en.srt")));
        Assert.Equal("original metadata", File.ReadAllText(Path.ChangeExtension(source, ".nfo")));
        if (directory)
        {
            Assert.Equal("user notes", File.ReadAllText(Path.Combine(issue.Path, "notes.txt")));
            Assert.True(Directory.Exists(Path.Combine(issue.Path, "empty")));
        }

        Assert.Equal("Undone", _db.Moves.Read(moved.MoveOperationId!)!.State);
        Assert.False(Directory.Exists(Path.Combine(Movies, "Film")));
        Assert.NotEqual(moved.MoveOperationId, undone.MoveOperationId);
        Assert.Equal("Completed", _db.Moves.Read(undone.MoveOperationId!)!.State);
        Assert.True(_db.WasPathRestored(source, IssueType.Ungrouped));
        Assert.NotEqual(IssueStatus.Queued, _db.Issues.GetIssue(issue.Id)!.Status);
        var history = Assert.Single(_db.History.GetHistory());
        Assert.Equal(undone.MoveOperationId, history.MoveOperationId);
        Assert.Equal(history.Id, _db.Moves.Read(undone.MoveOperationId!)!.HistoryId);
        var repeated = await _service.UndoAsync(moved.MoveOperationId!, null, CancellationToken.None);
        Assert.False(repeated.Success);
    }

    [Theory]
    [InlineData("destination-edit")]
    [InlineData("occupied-original")]
    [InlineData("protected-destination")]
    [InlineData("new-companion")]
    public async Task UnsafeUndoPreservesEveryCurrentPayloadWithoutPreparingAnotherOperation(string change)
    {
        var issue = await Approve(companions: true);
        var moved = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.True(moved.Success, moved.Message);
        var destination = Path.Combine(Movies, "Film", "Film.mkv");
        if (change == "destination-edit") Write(destination, "user edit");
        if (change == "occupied-original") Write(issue.Path, "new import");
        if (change == "new-companion") Write(Path.Combine(Movies, "Film", "Film.fr.srt"), "new companion");
        if (change == "protected-destination")
        {
            _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = destination, ScannersMask = "*", FixersMask = "*" });
            _protection.Invalidate();
        }

        var result = await _service.UndoAsync(moved.MoveOperationId!, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(1, OperationCount());
        Assert.Equal(change == "destination-edit" ? "user edit" : "original video", File.ReadAllText(destination));
        if (change == "occupied-original") Assert.Equal("new import", File.ReadAllText(issue.Path));
        if (change == "new-companion") Assert.Equal("new companion", File.ReadAllText(Path.Combine(Movies, "Film", "Film.fr.srt")));
        Assert.Equal("Completed", _db.Moves.Read(moved.MoveOperationId!)!.State);
    }

    [Fact]
    public async Task UndoPreviewCreatesNoJournalOrFilesystemChanges()
    {
        var issue = await Approve(companions: true);
        var moved = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.True(moved.Success, moved.Message);
        _config.DryRun = true;
        var preview = await _service.UndoAsync(moved.MoveOperationId!, null, CancellationToken.None);
        Assert.True(preview.Success, preview.Message);
        Assert.True(preview.WasDryRun);
        Assert.Equal(1, OperationCount());
        Assert.False(File.Exists(issue.Path));
        Assert.Equal("original video", File.ReadAllText(Path.Combine(Movies, "Film", "Film.mkv")));
        Assert.Equal("Completed", _db.Moves.Read(moved.MoveOperationId!)!.State);
    }

    [Fact]
    public async Task UndoKeepsAnUnrelatedNewImportAndItsDestinationFolder()
    {
        var issue = await Approve(companions: true);
        var moved = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.True(moved.Success, moved.Message);
        var imported = Write(Path.Combine(Movies, "Film", "Different.mkv"), "new import");
        var undone = await _service.UndoAsync(moved.MoveOperationId!, null, CancellationToken.None);
        Assert.True(undone.Success, undone.Message);
        Assert.Equal("new import", File.ReadAllText(imported));
        Assert.Equal("original video", File.ReadAllText(issue.Path));
        Assert.Single(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(imported)!));
    }

    [Theory]
    [InlineData("new-child")]
    [InlineData("replace-folder")]
    [InlineData("protection")]
    [InlineData("disabled-library")]
    [InlineData("cancel")]
    public async Task UndoCleanupRacePreservesForeignContentAndRetainsReviewClaims(string change)
    {
        var issue = await Approve(companions: true);
        var moved = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.True(moved.Success, moved.Message);
        var folder = Path.Combine(Movies, "Film");
        using var cancellation = new CancellationTokenSource();
        _service.Checkpoint = (point, _) =>
        {
            if (point != "BeforeUndoDirectoryDelete") return;
            if (change == "replace-folder")
            {
                Directory.Move(folder, folder + ".previous");
                Directory.CreateDirectory(folder);
            }
            if (change == "new-child" || change == "replace-folder") Write(Path.Combine(folder, "foreign.bin"), "new content");
            if (change == "protection")
            {
                _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = folder, FixersMask = "*", ScannersMask = "*" });
                _protection.Invalidate();
            }
            if (change == "disabled-library") _config.EnabledLibraries = [Guid.NewGuid().ToString("N")];
            if (change == "cancel") cancellation.Cancel();
        };
        if (change == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.UndoAsync(moved.MoveOperationId!, null, cancellation.Token));
        else Assert.False((await _service.UndoAsync(moved.MoveOperationId!, null, cancellation.Token)).Success);
        Assert.True(Directory.Exists(folder));
        if (change == "new-child" || change == "replace-folder") Assert.Equal("new content", File.ReadAllText(Path.Combine(folder, "foreign.bin")));
        Assert.Equal("original video", File.ReadAllText(issue.Path));
        Assert.Equal("NeedsReview", _db.Moves.Read(moved.MoveOperationId!)!.State);
        Assert.True(_db.Moves.HasActiveConflict(folder));
    }

    [Theory]
    [InlineData("BeforeEntry")]
    [InlineData("Published")]
    [InlineData("BeforeComplete")]
    public async Task InterruptedUndoKeepsBothOperationsReviewableAndEveryPayloadRecorded(string boundary)
    {
        var issue = await Approve(companions: true);
        var moved = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.True(moved.Success, moved.Message);
        _service.Checkpoint = (point, index) =>
        {
            if (point == boundary && (point != "BeforeEntry" || index == 1)) throw new IOException("controlled Undo interruption");
        };
        var undone = await _service.UndoAsync(moved.MoveOperationId!, null, CancellationToken.None);
        Assert.False(undone.Success);
        Assert.Equal("NeedsReview", _db.Moves.Read(moved.MoveOperationId!)!.State);
        Assert.Equal("NeedsReview", _db.Moves.Read(undone.MoveOperationId!)!.State);
        Assert.True(_db.Moves.HasActiveConflict(issue.Path));
        Assert.True(File.Exists(issue.Path) || File.Exists(Path.Combine(Movies, "Film", "Film.mkv")));
    }

    [Fact]
    public async Task FailedUndoBookkeepingRollsBackMarkersAndRetainsTheParentClaim()
    {
        var issue = await Approve(companions: true);
        var moved = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.True(moved.Success, moved.Message);
        using (var connection = _db.ConnectionFactory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER reject_marker BEFORE INSERT ON restored_paths BEGIN SELECT RAISE(ABORT,'controlled Undo bookkeeping failure'); END";
            command.ExecuteNonQuery();
        }

        var undone = await _service.UndoAsync(moved.MoveOperationId!, null, CancellationToken.None);
        Assert.False(undone.Success);
        Assert.Equal("original video", File.ReadAllText(issue.Path));
        Assert.False(_db.WasPathRestored(issue.Path, IssueType.Ungrouped));
        Assert.Equal("NeedsReview", _db.Moves.Read(moved.MoveOperationId!)!.State);
        Assert.True(_db.Moves.HasActiveConflict(issue.Path));
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(issue.Id)!.Status);
    }

    [Fact]
    public async Task ConcurrentUndoRequestsHaveOnlyOneReverseOwner()
    {
        var issue = await Approve(companions: true);
        var moved = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.True(moved.Success, moved.Message);
        var results = await Task.WhenAll(Task.Run(() => _service.UndoAsync(moved.MoveOperationId!, null, CancellationToken.None)),
            Task.Run(() => _service.UndoAsync(moved.MoveOperationId!, null, CancellationToken.None)));
        Assert.Single(results, result => result.Success);
        Assert.Equal(2, OperationCount());
        Assert.Equal("original video", File.ReadAllText(issue.Path));
        Assert.Equal("Undone", _db.Moves.Read(moved.MoveOperationId!)!.State);
    }

    [Fact]
    public async Task JournalWriteFailureAfterPublishPreservesKnownDestinationForRestartReview()
    {
        var issue = await Approve();
        _service.Checkpoint = (point, _) =>
        {
            if (point == "Published")
            {
                using var connection = _db.ConnectionFactory.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TRIGGER reject_updates BEFORE UPDATE ON media_move_operations BEGIN SELECT RAISE(ABORT,'controlled journal outage'); END";
                command.ExecuteNonQuery();
            }
        };
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.NotNull(result.MoveOperationId);
        Assert.Equal("original video", File.ReadAllText(Path.Combine(Movies, "Film", "Film.mkv")));
        Assert.False(File.Exists(issue.Path));
        var operation = _db.Moves.Read(result.MoveOperationId)!;
        Assert.Equal("Executing", operation.State);
        Assert.Equal("Publishing", MediaMoveManifest.TryRead(operation.ManifestJson, operation.Id)!.Steps[0].Phase);
        Assert.True(_db.Moves.HasActiveConflict(issue.Path));
        using (var connection = _db.ConnectionFactory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER reject_updates";
            command.ExecuteNonQuery();
        }

        _db.Moves.ReconcileAtStartup();
        Assert.Equal("NeedsReview", _db.Moves.Read(operation.Id)!.State);
        Assert.Equal("original video", File.ReadAllText(Path.Combine(Movies, "Film", "Film.mkv")));
    }

    [Fact]
    public async Task PolicyChangeAfterPublishPreservesPayloadWithoutFurtherMutation()
    {
        var issue = await Approve(companions: true);
        _service.Checkpoint = (point, _) =>
        {
            if (point == "Published")
            {
                _config.DryRun = true;
            }
        };
        var result = await _service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("original video", File.ReadAllText(Path.Combine(Movies, "Film", "Film.mkv")));
        Assert.Equal("original subtitles", File.ReadAllText(Path.ChangeExtension(issue.Path, ".en.srt")));
        Assert.Equal("NeedsReview", _db.Moves.Read(result.MoveOperationId!)!.State);
        Assert.True(_db.Moves.HasActiveConflict(issue.Path));
    }

    internal async Task RunOwnedLinuxFilesystemFault(string fault)
    {
        Assert.True(OperatingSystem.IsLinux());
        var targetRoot = Environment.GetEnvironmentVariable("MEDIADASH_TEST_FAULT_MOUNT");
        Assert.False(string.IsNullOrEmpty(targetRoot));
        var ownerRoot = Path.GetDirectoryName(targetRoot)!;
        Assert.Equal("/tmp", Path.GetDirectoryName(ownerRoot));
        Assert.StartsWith("md-owned-fs-fault-", Path.GetFileName(ownerRoot));
        Assert.Equal("MediaDash owned private mount fixture", File.ReadAllText(Path.Combine(ownerRoot, ".owned-fixtures")).Trim());
        Assert.Equal(Path.Combine(ownerRoot, "mount"), targetRoot);
        _externalTarget = Path.Combine(targetRoot!, "md-move-execute-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_externalTarget);
        _config.MoviesTargetPath = _externalTarget;
        _locations.Add(_externalTarget);
        Assert.True(MediaMoveSafety.IsCrossVolume(Tv, _externalTarget));
        var source = Path.Combine(Movies, "Owned bundle");
        var payload = Write(Path.Combine(source, "Film.mkv"), "owned primary");
        Write(Path.Combine(source, "Film.en.srt"), "owned companion");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        if (fault == "enospc")
        {
            using var file = File.OpenWrite(payload);
            file.SetLength(2 * 1024 * 1024);
            Assert.True(new DriveInfo(targetRoot!).AvailableFreeSpace < file.Length);
        }

        var inventory = MediaMoveSnapshot.Capture(source, CancellationToken.None).Export();
        var destination = Path.Combine(_externalTarget, "Owned bundle");
        if (fault == "permission" && OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(_externalTarget, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(_externalTarget, "permission-probe"), "owned probe"));
        }

        try
        {
            var plan = await _planner.BuildExplicitAsync([(source, destination)], _items, _config, CancellationToken.None);
            Assert.Contains(MediaMoveSafety.UnsafeSourceDeletionMessage, plan.BlockingReasons);

            var olderApproval = (plan with { BlockingReasons = [] }).Seal();
            var result = await _service.ExecuteExplicitAsync(olderApproval, olderApproval.Fingerprint, null, CancellationToken.None);
            Assert.False(result.Success);
            Assert.Equal(FixReason.MoveNeedsReview, result.Reason);
            Assert.Equal(0, result.BytesFreed);
            Assert.Equal(0, OperationCount());
            Assert.Empty(Directory.GetFileSystemEntries(_externalTarget));
            MediaMoveSnapshot.VerifyInventory(source, inventory, true, CancellationToken.None);
        }
        finally
        {
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(_externalTarget, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    private sealed class RecordedProgress : IProgress<double>
    {
        internal List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }

    private sealed class RecordedFixer : IFixer
    {
        internal bool Called { get; private set; }

        public bool CanFix(IssueType type) => true;

        public Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Called = true;
            return Task.FromResult(new FixResult { Success = true });
        }
    }

    public void Dispose()
    {
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, _previous);
        SqliteConnection.ClearAllPools();
        if (_externalTarget is not null && Directory.Exists(_externalTarget) && !LibraryGuard.IsUnder(_externalTarget, _root))
        {
            Directory.Delete(_externalTarget, recursive: true);
        }

        Directory.Delete(_root, recursive: true);
    }
}
