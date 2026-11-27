using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("Diagnostics")]
public sealed class NativeLibraryRefreshTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-native-changes-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, BaseItem> _items = new(StringComparer.Ordinal);
    private readonly List<string> _events = [];
    private readonly ILibraryManager _library;
    private readonly IFileSystem _files;
    private readonly RecordingFolder _folder;
    private readonly NativeLibraryRefresher _refresh;
    private bool _saveLocalMetadata;
    private string[] _metadataSavers = [];
    private bool _optionsUnavailable;

    public NativeLibraryRefreshTests()
    {
        Directory.CreateDirectory(_root);
        _folder = Folder(_root);
        _library = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)_library).Call = (method, args) => method.Name switch
        {
            "GetVirtualFolders" => new List<VirtualFolderInfo> { new() { Locations = [_root] } },
            "FindByPath" => _items.GetValueOrDefault((string)args![0]!),
            "GetItemById" => _items.Values.FirstOrDefault(item => item.Id == (Guid)args![0]!),
            "GetLibraryOptions" => _optionsUnavailable ? null : new MediaBrowser.Model.Configuration.LibraryOptions
                { SaveLocalMetadata = _saveLocalMetadata, MetadataSavers = _metadataSavers },
            _ => null
        };
        _files = DispatchProxy.Create<IFileSystem, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)_files).Call = (method, args) => method.Name switch
        {
            "GetFileSystemEntries" => Directory.EnumerateFileSystemEntries((string)args![0]!).Select(path => new FileSystemMetadata
                { FullName = path, Name = Path.GetFileName(path), Exists = true, IsDirectory = Directory.Exists(path) }).ToArray(),
            "GetFileSystemEntryPaths" => Directory.EnumerateFileSystemEntries((string)args![0]!),
            _ => null
        };
        _refresh = new NativeLibraryRefresher(_library, _files, (item, _, _) =>
        {
            _events.Add("metadata:" + item.Path);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task FixRunnerBatchUsesBoundedMembershipWithoutBroadMonitorNotifications()
    {
        var target = Path.Combine(_root, "movie.mkv");
        File.WriteAllText(target, "owned");
        var item = new Video { Id = Guid.NewGuid(), Path = target };
        _items[target] = item;
        var monitor = DispatchProxy.Create<ILibraryMonitor, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)monitor).Call = (_, _) => throw new InvalidOperationException("broad notifications forbidden");
        var providers = DispatchProxy.Create<IProviderManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)providers).Call = (_, _) => throw new InvalidOperationException("unawaited provider queue forbidden");
        var fixer = new FixLibraryRefresher(_library, providers, _files, NullLogger<FixLibraryRefresher>.Instance, monitor, _refresh);
        var issue = new Issue { Path = target, ItemId = item.Id };
        var attempt = fixer.Capture(issue);
        var batch = new FixLibraryRefresher.Targets();
        fixer.Complete(attempt, new FixResult { Outcome = FixOutcome.Applied }, batch);
        await fixer.QueueAsync(batch, CancellationToken.None);
        Assert.Equal(new[] { "index:" + _root, "metadata:" + target }, _events);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task MoveIndexesButDefersProvidersWhenMediaFolderWritesAreEnabledOrUnknown(bool saveLocal, bool saver, bool unknown)
    {
        _saveLocalMetadata = saveLocal;
        _metadataSavers = saver ? ["Nfo"] : [];
        _optionsUnavailable = unknown;
        var gone = Path.Combine(_root, "old.mkv");
        var target = Path.Combine(_root, "moved.mkv");
        File.WriteAllText(target, "owned");
        _items[gone] = new Video { Id = Guid.NewGuid(), Path = gone };
        _folder.Index = () =>
        {
            _items.Remove(gone);
            _items[target] = new Video { Id = Guid.NewGuid(), Path = target };
        };
        var error = await Assert.ThrowsAsync<NativeLibraryRefresher.MoveMetadataRefreshDeferredException>(() => _refresh.RefreshMoveAsync([gone, target], [], CancellationToken.None));
        Assert.Contains("metadata", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(_items.ContainsKey(gone));
        Assert.True(_items.ContainsKey(target));
        Assert.Equal(new[] { "index:" + _root }, _events);
    }

    [Fact]
    public async Task MoveStillRefreshesProvidersInLibrariesWithoutMediaFolderWrites()
    {
        var target = Path.Combine(_root, "moved.mkv");
        File.WriteAllText(target, "owned");
        _folder.Index = () => _items[target] = new Video { Id = Guid.NewGuid(), Path = target };
        await _refresh.RefreshMoveAsync([target], [], CancellationToken.None);
        Assert.Equal(new[] { "index:" + _root, "metadata:" + target }, _events);
    }

    [Fact]
    public async Task NewlyIndexedParentsPrecedeOnlyChangedTargetMetadata()
    {
        var directory = Path.Combine(_root, "new series");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "episode.mkv");
        File.WriteAllText(target, "owned");
        var sibling = Path.Combine(_root, "unrelated.mkv");
        File.WriteAllText(sibling, "unrelated");
        _items[sibling] = new Video { Id = Guid.NewGuid(), Path = sibling };
        _folder.Index = () => Folder(directory).Index = () => _items[target] = new Video { Id = Guid.NewGuid(), Path = target };

        await _refresh.RefreshAsync([target, target], [], CancellationToken.None);

        Assert.Equal(new[] { "index:" + _root, "index:" + directory, "metadata:" + target }, _events);
    }

    [Fact]
    public async Task MovieResolverCanIndexTheTargetWithoutCreatingAnIntermediateNativeFolder()
    {
        var directory = Path.Combine(_root, "owned movie");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "owned movie.mkv");
        File.WriteAllText(target, "owned");
        _folder.Index = () => _items[target] = new Video { Id = Guid.NewGuid(), Path = target };
        await _refresh.RefreshAsync([target, Path.ChangeExtension(target, ".nfo")], [], CancellationToken.None);
        Assert.Equal(new[] { "index:" + _root, "metadata:" + target }, _events);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SidecarWithoutNativeFolderRequiresItsBoundOwnerWhenPresent(bool bindOwningMovie)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "owned movie")).FullName;
        var moviePath = Path.Combine(directory, "movie.mkv");
        var removedSidecar = Path.Combine(directory, "movie.srt");
        var unrelatedPath = Path.Combine(_root, "unrelated.mkv");
        File.WriteAllText(moviePath, "owned video");
        File.WriteAllText(unrelatedPath, "unrelated video");
        var movie = new Video { Id = Guid.NewGuid(), Path = moviePath };
        var unrelated = new Video { Id = Guid.NewGuid(), Path = unrelatedPath };
        _items[moviePath] = movie;
        _items[unrelatedPath] = unrelated;

        if (bindOwningMovie)
        {
            await _refresh.RefreshAsync([removedSidecar], [movie.Id], CancellationToken.None);
            Assert.Equal(new[] { "index:" + _root, "metadata:" + moviePath }, _events);
        }
        else
        {
            File.WriteAllText(removedSidecar, "still present");
            await Assert.ThrowsAsync<InvalidOperationException>(() => _refresh.RefreshAsync([removedSidecar], [unrelated.Id], CancellationToken.None));
            Assert.Equal(new[] { "index:" + _root }, _events);
        }

        Assert.Equal("owned video", File.ReadAllText(moviePath));
        Assert.Equal("unrelated video", File.ReadAllText(unrelatedPath));
    }

    [Fact]
    public async Task RemovedNfoWithoutItemIdRefreshesItsMatchingMovie()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "owned movie")).FullName;
        var moviePath = Path.Combine(directory, "movie.mkv");
        var unrelatedPath = Path.Combine(directory, "other.mkv");
        File.WriteAllText(moviePath, "owned video");
        File.WriteAllText(unrelatedPath, "unrelated video");
        _items[moviePath] = new Video { Id = Guid.NewGuid(), Path = moviePath };
        _items[unrelatedPath] = new Video { Id = Guid.NewGuid(), Path = unrelatedPath };

        await _refresh.RefreshAsync([Path.ChangeExtension(moviePath, ".nfo")], [], CancellationToken.None);

        Assert.Equal(new[] { "index:" + _root, "metadata:" + moviePath }, _events);
    }

    [Fact]
    public async Task RemovedUnindexedScriptDoesNotBlockAnotherItemsRefresh()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "collapsed movie")).FullName;
        var removedScript = Path.Combine(directory, "install.ps1");
        var otherPath = Path.Combine(_root, "other.mkv");
        File.WriteAllText(otherPath, "unrelated video");
        var other = new Video { Id = Guid.NewGuid(), Path = otherPath };
        _items[otherPath] = other;

        await _refresh.RefreshAsync([removedScript, otherPath], [other.Id], CancellationToken.None);

        Assert.Equal(new[] { "index:" + _root, "metadata:" + otherPath }, _events);
    }

    [Fact]
    public async Task DeletedSourceOnlyIndexesItsExistingParent()
    {
        var gone = Path.Combine(_root, "gone.mkv");
        File.WriteAllText(Path.Combine(_root, "remaining.mkv"), "unrelated");
        _items[gone] = new Video { Id = Guid.NewGuid(), Path = gone };
        _folder.Index = () => _items.Remove(gone);
        await _refresh.RefreshAsync([gone], [], CancellationToken.None);
        Assert.Equal(new[] { "index:" + _root }, _events);
        Assert.False(_items.ContainsKey(gone));
    }

    [Fact]
    public async Task ExplicitFolderRefreshOwnsNestedMetadataWithoutDroppingSeparateTargets()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "selected series")).FullName;
        var child = Path.Combine(directory, "episode.mkv");
        var separate = Path.Combine(_root, "separate.mkv");
        File.WriteAllText(child, "selected episode");
        File.WriteAllText(separate, "separate selected item");
        _folder.Index = () =>
        {
            Folder(directory);
            _items[child] = new Video { Id = Guid.NewGuid(), Path = child };
            _items[separate] = new Video { Id = Guid.NewGuid(), Path = separate };
        };

        await _refresh.RefreshAsync([directory, child, separate], [], CancellationToken.None);

        Assert.Equal(new[] { "metadata:" + directory, "metadata:" + separate },
            _events.Where(item => item.StartsWith("metadata:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task NativeSkippingAnEmptyLibraryCannotReportSuccessfulRemoval()
    {
        var gone = Path.Combine(_root, "gone.mkv");
        _items[gone] = new Video { Id = Guid.NewGuid(), Path = gone };
        await Assert.ThrowsAsync<InvalidOperationException>(() => _refresh.RefreshAsync([gone], [], CancellationToken.None));
        Assert.Empty(_events);
        Assert.True(_items.ContainsKey(gone));
    }

    [Fact]
    public async Task APathOutsideLibraryScopeRefusesBeforeAnyIndexing()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _refresh.RefreshAsync(
            [Path.Combine(_root, "owned.mkv"), _root + "-outside/movie.mkv"], [], CancellationToken.None));
        Assert.Empty(_events);
    }

    [Fact]
    public async Task FailedCompleteListingDoesNotInvokeNativeIndexOrMetadata()
    {
        var calls = (OriginalLanguageFlowTests.CallProxy)(object)_files;
        calls.Call = (_, _) => throw new IOException("owned unreadable listing");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _refresh.RefreshAsync([Path.Combine(_root, "movie.mkv")], [], CancellationToken.None));
        Assert.Empty(_events);
    }

    [Fact]
    public async Task NativeMembershipFailurePreventsAllMetadataRefresh()
    {
        var target = Path.Combine(_root, "movie.mkv");
        File.WriteAllText(target, "owned");
        _items[target] = new Video { Id = Guid.NewGuid(), Path = target };
        _folder.Index = () => throw new IOException("native failed");
        await Assert.ThrowsAsync<IOException>(() => _refresh.RefreshAsync([target], [], CancellationToken.None));
        Assert.DoesNotContain(_events, value => value.StartsWith("metadata:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FullScanHandoffIsNotReportedAsAnIndexingFailureButOtherCancellationsAre(bool fullScan)
    {
        var path = Path.Combine(_root, "movie.mkv");
        File.WriteAllText(path, "owned");
        _items[path] = new Video { Id = Guid.NewGuid(), Path = path };
        var tasks = DispatchProxy.Create<MediaBrowser.Model.Tasks.ITaskManager, LibraryScanCancellationTests.ManagerProxy>();
        var handler = (LibraryScanCancellationTests.ManagerProxy)(object)tasks;
        if (fullScan) handler.Workers = [LibraryScanCancellationTests.Worker("RefreshLibrary", MediaBrowser.Model.Tasks.TaskState.Running)];
        using var native = new NativeLibraryRefresher(_library, _files,
            (_, _, _) => Task.FromCanceled(new CancellationToken(true)), tasks);
        var monitor = DispatchProxy.Create<ILibraryMonitor, OriginalLanguageFlowTests.CallProxy>();
        var providers = DispatchProxy.Create<IProviderManager, OriginalLanguageFlowTests.CallProxy>();
        var fixer = new FixLibraryRefresher(_library, providers, _files, NullLogger<FixLibraryRefresher>.Instance, monitor, native);
        var targets = new FixLibraryRefresher.Targets();
        targets.Paths.Add(path);
        var before = Api.Diagnostics.RecentAll().Where(entry => entry.Source == "Fix.NativeIndex").Sum(entry => entry.Count);
        await fixer.QueueAsync(targets, CancellationToken.None);
        var after = Api.Diagnostics.RecentAll().Where(entry => entry.Source == "Fix.NativeIndex").Sum(entry => entry.Count);
        Assert.Equal(fullScan ? before : before + 1, after);
    }

    [Fact]
    public async Task ScanStartingDuringIndexingDefersMetadataAndReleasesTheRefreshGate()
    {
        var path = Path.Combine(_root, "movie.mkv");
        File.WriteAllText(path, "owned");
        _items[path] = new Video { Id = Guid.NewGuid(), Path = path };
        var tasks = DispatchProxy.Create<MediaBrowser.Model.Tasks.ITaskManager, LibraryScanCancellationTests.ManagerProxy>();
        var handler = (LibraryScanCancellationTests.ManagerProxy)(object)tasks;
        using var native = new NativeLibraryRefresher(_library, _files, (item, _, _) =>
        {
            _events.Add("metadata:" + item.Path);
            return Task.CompletedTask;
        }, tasks);
        _folder.Index = () => handler.Executing!(tasks, new Jellyfin.Data.Events.GenericEventArgs<MediaBrowser.Model.Tasks.IScheduledTaskWorker>(
            LibraryScanCancellationTests.Worker("RefreshLibrary", MediaBrowser.Model.Tasks.TaskState.Running)));
        await Assert.ThrowsAsync<NativeLibraryRefresher.LibraryScanInProgressException>(() => native.RefreshAsync([path], [], CancellationToken.None));
        Assert.DoesNotContain("metadata:" + path, _events);
        _folder.Index = null;
        await native.RefreshAsync([path], [], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("metadata:" + path, _events);
    }

    public void Dispose()
    {
        _refresh.Dispose();
        Directory.Delete(_root, true);
    }

    private RecordingFolder Folder(string path)
    {
        var folder = new RecordingFolder { Id = Guid.NewGuid(), Path = path, Events = _events };
        _items[path] = folder;
        return folder;
    }

    private sealed class RecordingFolder : Folder
    {
        internal required List<string> Events { get; init; }
        internal Action? Index { get; set; }

        protected override Task ValidateChildrenInternal(IProgress<double> progress, bool recursive, bool refreshChildMetadata, bool allowRemoveRoot,
            MetadataRefreshOptions refreshOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            Assert.False(recursive);
            Assert.False(refreshChildMetadata);
            Assert.False(allowRemoveRoot);
            Events.Add("index:" + Path);
            Index?.Invoke();
            return Task.CompletedTask;
        }
    }
}
