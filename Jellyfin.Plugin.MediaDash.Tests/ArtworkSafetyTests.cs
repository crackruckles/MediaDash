using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Scanners;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("MediaDashDb")]
public sealed class ArtworkSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-art-safety-" + Guid.NewGuid().ToString("N"));
    private readonly Plugin? _previous = Plugin.Instance;
    private readonly RunState? _previousState = Plugin.AttachedRunState;
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly PluginConfiguration _config = new() { DryRun = false };
    private readonly MediaDashDb _db;
    private readonly Video _owner;
    private readonly ILibraryManager _library;
    private readonly IServerApplicationPaths _paths;
    private readonly ILibraryMonitor _monitor;
    private readonly RecycleBin _bin;
    private readonly string _path;
    private bool _failPersistence;
    private bool _replaceDuringPersistence;
    private bool _createDuringPersistence;
    private bool _removeParentDuringPersistence;
    private int _persistenceCount;
    private bool _replaceDuringLookup;
    private bool _protectDuringPersistence;
    private bool _addImageDuringPersistence;
    private readonly ProtectionService _protection;
    private int _lookupCount;
    private int _replaceOnLookup;

    public ArtworkSafetyTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "metadata"));
        Directory.CreateDirectory(Path.Combine(_root, "media"));
        _path = Path.Combine(_root, "metadata", "poster.png");
        File.WriteAllBytes(_path, []);
        _db = new MediaDashDb(Path.Combine(_root, "test.db"));
        _protection = new ProtectionService(_db.Protection, null);
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, _config);
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, plugin);
        _owner = new Video { Id = _ownerId, Path = Path.Combine(_root, "media", "movie.mkv"),
            ImageInfos = [new ItemImageInfo { Path = _path, Type = ImageType.Primary }] };
        _library = Proxy<ILibraryManager>((method, _) =>
        {
            if (method.Name == "GetVirtualFolders") return new List<VirtualFolderInfo> { new() { ItemId = "owned", Locations = [Path.Combine(_root, "media")] } };
            if (method.Name == "GetItemList") return new BaseItem[] { _owner };
            if (method.Name == "GetItemById")
            {
                _lookupCount++;
                if (_lookupCount == _replaceOnLookup)
                {
                    File.Move(_path, _path + ".late");
                    File.WriteAllText(_path, "healthy final replacement");
                }
                if (_replaceDuringLookup)
                {
                    _replaceDuringLookup = false;
                    File.WriteAllBytes(_path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII="));
                }
                return _owner;
            }
            if (method.Name == "UpdateItemAsync")
            {
                _persistenceCount++;
                if (_removeParentDuringPersistence)
                {
                    _removeParentDuringPersistence = false;
                    Directory.Move(Path.GetDirectoryName(_path)!, Path.GetDirectoryName(_path)! + ".offline");
                }
                if (_createDuringPersistence)
                {
                    _createDuringPersistence = false;
                    File.WriteAllText(_path, "provider replacement");
                }
                if (_failPersistence) throw new IOException("owned persistence failure");
                if (_protectDuringPersistence)
                {
                    _protectDuringPersistence = false;
                    _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = Path.GetDirectoryName(_path)!, FixersMask = "*", CreatedAtUtc = DateTime.UtcNow });
                    _protection.Invalidate();
                }
                if (_addImageDuringPersistence)
                {
                    _addImageDuringPersistence = false;
                    _owner.ImageInfos = [new ItemImageInfo { Path = _path + ".new", Type = ImageType.Backdrop }];
                }
                if (_replaceDuringPersistence)
                {
                    _replaceDuringPersistence = false;
                    File.Move(_path, _path + ".old");
                    File.WriteAllText(_path, "healthy replacement must survive");
                }
                return Task.CompletedTask;
            }
            return null;
        });
        _paths = Proxy<IServerApplicationPaths>((method, _) => method.Name switch
        {
            "get_InternalMetadataPath" => Path.Combine(_root, "metadata"),
            "get_DataPath" => _root,
            _ => null
        });
        _monitor = Proxy<ILibraryMonitor>((_, _) => null);
        _bin = new RecycleBin(_paths, NullLogger<RecycleBin>.Instance, _db.History);
    }

    private Issue Issue() => new() { Type = IssueType.CorruptArtwork, Path = _path, ItemId = _ownerId };
    private ArtworkFixer Fixer() => new(_paths, _monitor, _library, NullLogger<ArtworkFixer>.Instance, _bin, _protection);

    [Fact]
    public async Task MissingManagedArtworkClearsPersistedBindingWithoutRecoveryStorage()
    {
        File.Delete(_path);
        var fixer = new ArtworkFixer(_paths, _monitor, _library, NullLogger<ArtworkFixer>.Instance, protection: _protection);
        var scanner = new ArtworkScanner(_paths, NullLogger<ArtworkScanner>.Instance);
        var issue = Assert.Single(await scanner.ScanAsync([_owner], new Progress<double>(), default));
        var result = await fixer.FixAsync(issue, null, default);
        Assert.True(result.Success, result.Message);
        Assert.Empty(_owner.ImageInfos);
        Assert.Equal(1, _persistenceCount);
        Assert.Null(result.RecyclePath);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public async Task MissingArtworkWithUnverifiableParentRetainsBinding()
    {
        File.Delete(_path);
        Directory.Delete(Path.GetDirectoryName(_path)!);
        var result = await Fixer().FixAsync(Issue(), null, default);
        Assert.False(result.Success);
        Assert.Single(_owner.ImageInfos);
        Assert.Equal(0, _persistenceCount);
    }

    [Fact]
    public async Task MissingArtworkParentDisappearingDuringPersistenceRestoresBinding()
    {
        File.Delete(_path);
        _removeParentDuringPersistence = true;
        var result = await Fixer().FixAsync(Issue(), null, default);
        Assert.False(result.Success);
        Assert.Single(_owner.ImageInfos);
        Assert.Equal(2, _persistenceCount);
    }

    [Fact]
    public async Task MissingArtworkRecreatedDuringPersistenceRetainsBindingAndReplacement()
    {
        File.Delete(_path);
        _createDuringPersistence = true;
        var result = await Fixer().FixAsync(Issue(), null, default);
        Assert.False(result.Success);
        Assert.Equal(2, _persistenceCount);
        Assert.Equal("provider replacement", File.ReadAllText(_path));
        Assert.Single(_owner.ImageInfos);
    }

    [Fact]
    public async Task MissingArtworkProtectionAddedDuringPersistenceRestoresBinding()
    {
        File.Delete(_path);
        _protectDuringPersistence = true;
        var result = await Fixer().FixAsync(Issue(), null, default);
        Assert.False(result.Success);
        Assert.Single(_owner.ImageInfos);
        Assert.Equal(2, _persistenceCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingArtworkDryRunAndPersistenceFailureRetainBinding(bool dryRun)
    {
        File.Delete(_path);
        _config.DryRun = dryRun;
        _failPersistence = !dryRun;
        var result = await Fixer().FixAsync(Issue(), null, default);
        Assert.Single(_owner.ImageInfos);
        Assert.Equal(dryRun, result.WasDryRun);
        Assert.Equal(dryRun ? 0 : 1, _persistenceCount);
        Assert.False(File.Exists(_path));
        Assert.Null(result.RecyclePath);
    }

    [Fact]
    public async Task SelectedLibraryRescanReplacesManagedArtworkFindings()
    {
        _config.EnabledLibraries = ["owned"];
        _db.SetState("ContentAwareDefaultsChecked", "done");
        var state = new RunState();
        var manager = Proxy<ITaskManager>((method, _) => method.Name == "get_ScheduledTasks" ? Array.Empty<IScheduledTaskWorker>() : null);
        var task = new ScanTask(_library,
            [new ArtworkScanner(_paths, NullLogger<ArtworkScanner>.Instance)], _db, null!, manager,
            state, new PluginConfigurationProvider(() => _config), _protection, NullLogger<ScanTask>.Instance);
        for (var scan = 0; scan < 2; scan++)
        {
            state.ArmBypassIdleCheck(RunRole.Scan);
            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
            var issue = Assert.Single(_db.Issues.GetIssues(IssueType.CorruptArtwork, IssueStatus.Detected));
            Assert.Equal(_path, issue.Path);
        }
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public async Task MissingOwnerOrImageBindingCannotAuthorizeDisposal()
    {
        _owner.ImageInfos = [];
        var result = await Fixer().FixAsync(Issue(), null, default);
        Assert.False(result.Success);
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public async Task DisabledOwnerLibraryCannotAuthorizeDisposal()
    {
        _config.EnabledLibraries = ["different"];
        var result = await Fixer().FixAsync(Issue(), null, default);
        Assert.False(result.Success);
        Assert.True(File.Exists(_path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptionIsRecycledAndCanBeRestoredFromHistory(bool force)
    {
        var result = await Fixer().FixAsync(Issue(), null, default);
        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(_path));
        Assert.NotNull(result.RecyclePath);
        Assert.True(File.Exists(result.RecyclePath));
        Assert.Empty(_owner.ImageInfos);
        _db.Issues.ReplaceDetectedIssues(IssueType.CorruptArtwork, [Issue()]);
        var issue = Assert.Single(_db.Issues.GetIssues());
        _db.History.AddHistory(new HistoryEntry { IssueId = issue.Id, Type = issue.Type, Path = _path,
            RecyclePath = result.RecyclePath, FixedAtUtc = DateTime.UtcNow, Success = true });
        var history = Assert.Single(_db.History.GetHistory());
        var service = new RestoreService(_db, _db.History, _db.RestoreOps, _bin,
            new LibraryGuard(_library, _paths), _monitor, NullLogger<RestoreService>.Instance);
        if (force) File.WriteAllText(_path, "provider replacement");
        var restored = service.RestoreFromHistory(history.Id, force);
        Assert.True(restored.Success, restored.TechnicalDetail);
        Assert.Empty(File.ReadAllBytes(_path));
        if (force)
        {
            var swap = Assert.Single(_db.History.GetHistory(), row => row.Id != history.Id);
            var recovered = service.RestoreFromHistory(swap.Id, false);
            Assert.True(recovered.Success, recovered.TechnicalDetail);
            Assert.Equal("provider replacement", File.ReadAllText(recovered.RestoredTo!));
            Assert.Empty(File.ReadAllBytes(_path));
        }
    }

    [Fact]
    public async Task PersistenceFailurePreservesBytesAndInMemoryImageBinding()
    {
        _failPersistence = true;
        var result = await Fixer().FixAsync(Issue(), null, default);
        Assert.False(result.Success);
        Assert.True(File.Exists(_path));
        Assert.Single(_owner.ImageInfos);
    }

    [Fact]
    public async Task ProtectionAddedDuringPersistenceStopsDisposal()
    {
        _protectDuringPersistence = true;
        var result = await Fixer().FixAsync(Issue(), null, default);
        Assert.False(result.Success);
        Assert.True(File.Exists(_path));
        Assert.Single(_owner.ImageInfos);
    }

    [Fact]
    public async Task RollbackPreservesConcurrentImageUpdates()
    {
        _addImageDuringPersistence = true;
        _replaceDuringPersistence = true;
        var result = await Fixer().FixAsync(Issue(), null, default);
        Assert.False(result.Success);
        Assert.Contains(_owner.ImageInfos, image => image.Path == _path + ".new");
        Assert.Contains(_owner.ImageInfos, image => image.Path == _path);
    }

    [Fact]
    public async Task SourceReplacedDuringFinalOwnerLookupIsPreserved()
    {
        _replaceOnLookup = 4;
        var result = await Fixer().FixAsync(Issue(), null, default);
        Assert.False(result.Success);
        Assert.Equal("healthy final replacement", File.ReadAllText(_path));
        Assert.Single(_owner.ImageInfos);
    }

    [Fact]
    public async Task DisposalFailureRestoresBackdropBindingAlongsideExistingBackdrop()
    {
        _owner.ImageInfos = [new ItemImageInfo { Path = _path, Type = ImageType.Backdrop },
            new ItemImageInfo { Path = _path + ".other", Type = ImageType.Backdrop }];
        _config.RecycleBinPath = Path.Combine(_root, "blocked-bin");
        File.WriteAllText(_config.RecycleBinPath, "occupied");
        var result = await Fixer().FixAsync(Issue(), null, default);
        Assert.False(result.Success);
        Assert.True(File.Exists(_path));
        Assert.Contains(_owner.ImageInfos, image => image.Path == _path);
        Assert.Contains(_owner.ImageInfos, image => image.Path == _path + ".other");
    }

    [Fact]
    public async Task SourceReplacedDuringOwnerLookupIsPreserved()
    {
        _replaceDuringLookup = true;
        var result = await Fixer().FixAsync(Issue(), null, default);
        Assert.False(result.Success);
        Assert.True(File.Exists(_path));
        Assert.Null(ArtworkScanner.EvaluateFile(_path, null));
        Assert.Single(_owner.ImageInfos);
    }

    [Fact]
    public async Task SourceReplacedDuringPersistenceIsPreserved()
    {
        _replaceDuringPersistence = true;
        var result = await Fixer().FixAsync(Issue(), null, default);
        Assert.False(result.Success);
        Assert.Equal("healthy replacement must survive", File.ReadAllText(_path));
        Assert.Single(_owner.ImageInfos);
    }

    [Fact]
    public async Task DisposalFailurePreservesSourceAndImageBinding()
    {
        _config.RecycleBinPath = Path.Combine(_root, "blocked-bin");
        File.WriteAllText(_config.RecycleBinPath, "occupied");
        var result = await Fixer().FixAsync(Issue(), null, default);
        Assert.False(result.Success);
        Assert.True(File.Exists(_path));
        Assert.Single(_owner.ImageInfos);
    }

    [Fact]
    public void LockedEmptyArtworkIsUnverifiable()
    {
        using var locked = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Null(ArtworkScanner.EvaluateFile(_path, null));
    }

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> call) where T : class
    {
        var proxy = DispatchProxy.Create<T, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)proxy).Call = call;
        return proxy;
    }

    public void Dispose()
    {
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, _previous);
        Plugin.AttachedRunState = _previousState;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
