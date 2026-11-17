using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Analytics;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("Diagnostics")]
public sealed class FixLibraryRefreshTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-library-refresh-" + Guid.NewGuid().ToString("N"));
    private readonly Plugin? _previousPlugin = Plugin.Instance;
    private readonly RunState? _previousState = Plugin.AttachedRunState;
    private readonly Api.FixRunSummary? _previousSummary = Plugin.LastFixRun;
    private readonly PluginConfiguration _config = new() { DryRun = false, PauseDuringPlayback = false, RecycleBinPauseFixesAtGb = 0 };
    private readonly List<(Guid Id, MetadataRefreshOptions Options)> _refreshes = [];
    private readonly List<string> _notified = [];
    private readonly ILibraryMonitor _monitor;
    private readonly List<VirtualFolderInfo> _libraries = [];
    private readonly List<BaseItem> _libraryFolders = [];
    private readonly Dictionary<Guid, BaseItem> _items = [];
    private readonly Dictionary<Guid, Folder[]> _collections = [];
    private readonly MediaDashDb _db;
    private readonly ILibraryManager _manager;
    private readonly FixLibraryRefresher _refresher;
    private readonly Guid _movies = Guid.NewGuid();
    private readonly Guid _shows = Guid.NewGuid();
    private readonly RunState _state = new();
    private int _attempts;
    private Func<Issue, CancellationToken, FixResult> _fix = (_, _) => new FixResult { Success = true, Outcome = FixOutcome.Applied };

    public FixLibraryRefreshTests()
    {
        Directory.CreateDirectory(_root);
        _db = new MediaDashDb(Path.Combine(_root, "test.db"));
        foreach (var property in typeof(PluginConfiguration).GetProperties().Where(p => p.PropertyType == typeof(FixMode)))
            property.SetValue(_config, FixMode.ManualApprove);
        _config.RecycleBinPath = Path.Combine(_root, "recycle");
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, _config);
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, plugin);
        AddLibrary(_movies, "movies");
        AddLibrary(_shows, "shows");
        AddLibrary(Guid.NewGuid(), "untouched");
        _manager = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)_manager).Call = (method, args) => method.Name switch
        {
            "GetVirtualFolders" => _libraries,
            "GetItemById" => _items.GetValueOrDefault((Guid)args![0]!),
            "FindByPath" => _items.Values.FirstOrDefault(item => item.Path == (string)args![0]!),
            "GetCollectionFolders" => _collections.GetValueOrDefault(((BaseItem)args![0]!).Id) ?? [],
            "GetItemList" => _libraryFolders,
            _ => null
        };
        var providers = DispatchProxy.Create<IProviderManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)providers).Call = (_, args) =>
        {
            _refreshes.Add(((Guid)args![0]!, (MetadataRefreshOptions)args[1]!));
            return null;
        };
        _monitor = DispatchProxy.Create<ILibraryMonitor, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)_monitor).Call = (_, args) => { _notified.Add((string)args![0]!); return null; };
        _refresher = new FixLibraryRefresher(_manager, providers,
            DispatchProxy.Create<IFileSystem, OriginalLanguageFlowTests.CallProxy>(), NullLogger<FixLibraryRefresher>.Instance, _monitor);
    }

    public static IEnumerable<object[]> FixerTypes => Enum.GetValues<IssueType>()
        .Where(t => t is not IssueType.Stale and not IssueType.ManualDelete).Select(t => new object[] { t });

    [Fact]
    public void ChangedPathBatchRetainsDeletedSourceAndPreviouslyUnindexedDestination()
    {
        var source = Path.Combine(_root, "movies", "old.mkv");
        var destination = Path.Combine(_root, "shows", "new", "new.mkv");
        File.WriteAllText(source, "original");
        var issue = new Issue { Type = IssueType.Misplaced, Path = source,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { targetPath = destination }) };
        var attempt = _refresher.Capture(issue);
        File.Delete(source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "original");
        var batch = new FixLibraryRefresher.Targets();
        _refresher.Complete(attempt, new FixResult { Outcome = FixOutcome.Applied }, batch);
        Assert.Contains(source, batch.Paths);
        Assert.Contains(destination, batch.Paths);
        Assert.Contains(_movies, batch.Items);
        Assert.Contains(_shows, batch.Items);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnchangedOrDryRunAttemptDoesNotContributeChangedPaths(bool dryRun)
    {
        var issue = new Issue { Path = Path.Combine(_root, "movies", "unchanged.mkv") };
        var attempt = _refresher.Capture(issue);
        var batch = new FixLibraryRefresher.Targets();
        _refresher.Complete(attempt, new FixResult { WasDryRun = dryRun, Outcome = dryRun ? FixOutcome.Applied : FixOutcome.NoChange }, batch);
        Assert.Empty(batch.Paths);
        Assert.Empty(batch.Items);
    }

    [Fact]
    public async Task TimestampPreservingRepairRefreshesOnlyItsItem()
    {
        var id = Guid.NewGuid();
        var path = Path.Combine(_root, "movies", "one.mkv");
        File.WriteAllText(path, "original");
        _items[id] = new Video { Id = id, Path = path };
        AddIssue(IssueType.AudioLanguage, "movies", "one", itemId: id);
        var timestamp = File.GetLastWriteTimeUtc(path);
        _fix = (_, _) =>
        {
            File.WriteAllText(path, "repaired");
            File.SetLastWriteTimeUtc(path, timestamp);
            return new FixResult { Success = true, Outcome = FixOutcome.Applied };
        };
        await Run();
        var refresh = Assert.Single(_refreshes);
        Assert.Equal(id, refresh.Id);
        Assert.True(refresh.Options.ForceSave);
        Assert.Equal(MetadataRefreshMode.FullRefresh, refresh.Options.MetadataRefreshMode);
    }

    [Theory]
    [InlineData((int)FixOutcome.Refused)]
    [InlineData((int)FixOutcome.NoChange)]
    [InlineData((int)FixOutcome.Failed)]
    public async Task UnchangedAttemptsDoNotRefresh(int outcome)
    {
        AddIssue(IssueType.AudioLanguage, "movies", "one");
        _fix = (_, _) => new FixResult { Outcome = (FixOutcome)outcome };
        await Run();
        Assert.Empty(_refreshes);
        Assert.Empty(_notified);
    }

    [Fact]
    public async Task MultipleRepairsOfOneItemRefreshItOnlyOnce()
    {
        var id = Guid.NewGuid();
        AddIssue(IssueType.AudioLanguage, "movies", "one", itemId: id);
        AddIssue(IssueType.MissingSubtitles, "movies", "one", itemId: id);
        await Run();
        Assert.Equal(2, _attempts);
        Assert.Equal(id, Assert.Single(_refreshes).Id);
        Assert.Empty(_notified);
    }

    [Fact]
    public async Task CancelledUnchangedAttemptDoesNotRefresh()
    {
        AddIssue(IssueType.AudioLanguage, "movies", "one");
        using var cts = new CancellationTokenSource();
        _fix = (_, _) => { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); return null!; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(cts.Token));
        Assert.Empty(_refreshes);
        Assert.Empty(_notified);
    }

    [Fact]
    public async Task DeletedItemRefreshesItsExistingParent()
    {
        AddIssue(IssueType.Duplicate, "movies", "one");
        _fix = (issue, _) =>
        {
            File.Delete(issue.Path);
            return new FixResult { Success = true, Outcome = FixOutcome.Applied };
        };
        await Run();
        Assert.Empty(_refreshes);
        Assert.Equal(new[] { Path.Combine(_root, "movies") }, _notified);
    }

    [Theory]
    [MemberData(nameof(FixerTypes))]
    public async Task EveryFixerRefreshesItsChangedItemsOnlyAfterTheRun(IssueType type)
    {
        AddIssue(type, "movies", "one");
        AddIssue(type, "movies", "two");
        _fix = (_, _) =>
        {
            Assert.Empty(_refreshes);
            return new FixResult { Success = true, Outcome = FixOutcome.Applied };
        };
        await Run();
        Assert.Equal(2, _attempts);
        Assert.Equal(2, _refreshes.Count);
        Assert.All(_refreshes, refresh =>
        {
            Assert.NotEqual(_movies, refresh.Id);
            Assert.Equal(MetadataRefreshMode.FullRefresh, refresh.Options.MetadataRefreshMode);
            Assert.Equal(MetadataRefreshMode.Default, refresh.Options.ImageRefreshMode);
            Assert.False(refresh.Options.ReplaceAllMetadata);
            Assert.False(refresh.Options.ReplaceAllImages);
            Assert.True(refresh.Options.ForceSave);
        });
        Assert.Empty(_notified);
    }

    [Theory]
    [InlineData("targetPath", IssueType.Misplaced)]
    [InlineData("target", IssueType.Ungrouped)]
    public async Task MovesRefreshSourceAndDestinationOnce(string key, IssueType type)
    {
        var target = Path.Combine(_root, "shows", "moved.mkv");
        AddIssue(type, "movies", "move", System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { [key] = target }));
        await Run();
        Assert.Contains(_refreshes, refresh => _items[refresh.Id].Path == Path.Combine(_root, "movies", "move.mkv"));
        Assert.DoesNotContain(_refreshes, refresh => refresh.Id == _shows);
        Assert.Equal(new[] { Path.Combine(_root, "shows") }, _notified);
    }

    [Fact]
    public void MetadataOutsideLibraryRootsUsesTheOwningItem()
    {
        var itemId = Guid.NewGuid();
        _items[itemId] = new Video { Id = itemId, Path = Path.Combine(_root, "movies", "movie.mkv") };
        File.WriteAllText(_items[itemId].Path, "movie");
        _collections[itemId] = [new CollectionFolder { Id = _movies }];
        var libraries = new FixLibraryRefresher.Targets();
        _refresher.Collect(new Issue { Type = IssueType.CorruptArtwork, Path = Path.Combine(_root, "metadata", "poster.jpg"), ItemId = itemId }, libraries);
        _refresher.Queue(libraries);
        Assert.Equal(itemId, Assert.Single(_refreshes).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DryRunsAndBlockedIssuesDoNotRefresh(bool blocked)
    {
        _config.DryRun = !blocked;
        if (blocked) _config.AudioFixMode = FixMode.Off;
        AddIssue(IssueType.AudioLanguage, "movies", "one");
        await Run();
        Assert.Empty(_refreshes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationStillRefreshesAChangedItem(bool cancel)
    {
        AddIssue(IssueType.AudioLanguage, "movies", "one");
        using var cts = new CancellationTokenSource();
        _fix = (issue, _) =>
        {
            File.WriteAllText(issue.Path, "committed partial change");
            if (cancel) { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); }
            throw new IOException("failure after a partial change");
        };
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(cts.Token));
        else await Run();
        Assert.Equal(Path.Combine(_root, "movies", "one.mkv"), _items[Assert.Single(_refreshes).Id].Path);
        Assert.False(_state.ReadSnapshot(RunRole.Fix).IsRunning);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("broken json")]
    [InlineData("{\"targetPath\":42}")]
    public void BadDetailsDoNotLoseTheSourceLibrary(string details)
    {
        var libraries = new FixLibraryRefresher.Targets();
        _refresher.Collect(new Issue { Type = IssueType.Misplaced, Path = Path.Combine(_root, "movies", "gone.mkv"), DetailsJson = details }, libraries);
        Assert.Equal(_movies, Assert.Single(libraries.Items));
    }

    [Fact]
    public void SiblingPrefixIsNotInsideTheLibrary()
    {
        var libraries = new FixLibraryRefresher.Targets();
        _refresher.Collect(new Issue { Path = Path.Combine(_root, "movies-extra", "movie.mkv") }, libraries);
        Assert.Empty(libraries.Items);
    }

    [Fact]
    public async Task Jellyfin12LibraryWithoutVirtualFolderIdStillRefreshes()
    {
        _libraries[0].ItemId = null;
        AddIssue(IssueType.AudioLanguage, "movies", "one");
        await Run();
        Assert.Equal(Path.Combine(_root, "movies", "one.mkv"), _items[Assert.Single(_refreshes).Id].Path);
    }

    [Fact]
    public async Task EndOfRunSidecarCleanupAlsoRefreshesTheAffectedLibrary()
    {
        var orphan = Path.Combine(_root, "shows", "movie.mediadash.tmp.mkv");
        File.WriteAllText(orphan, "abandoned encode");
        AddIssue(IssueType.AudioLanguage, "movies", "one");
        await Run();
        Assert.False(File.Exists(orphan));
        Assert.Single(_refreshes);
        Assert.DoesNotContain(_refreshes, refresh => refresh.Id == _shows);
        Assert.DoesNotContain(_refreshes, refresh => refresh.Id == _movies);
        Assert.Equal(new[] { Path.Combine(_root, "shows") }, _notified);
    }

    [Fact]
    public async Task NextRunOnlyRefreshesItsOwnLibraries()
    {
        AddIssue(IssueType.AudioLanguage, "movies", "one");
        await Run();
        _refreshes.Clear();
        AddIssue(IssueType.AudioLanguage, "shows", "two");
        await Run();
        Assert.Equal(Path.Combine(_root, "shows", "two.mkv"), _items[Assert.Single(_refreshes).Id].Path);
    }

    [Fact]
    public void QueueFailureDoesNotPreventOtherItemsFromRefreshing()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        AddIssue(IssueType.AudioLanguage, "movies", "one", itemId: first);
        AddIssue(IssueType.AudioLanguage, "shows", "two", itemId: second);
        var attempted = new List<Guid>();
        var providers = DispatchProxy.Create<IProviderManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)providers).Call = (_, args) =>
        {
            attempted.Add((Guid)args![0]!);
            if (attempted.Count == 1) throw new IOException("queue unavailable");
            return null;
        };
        new FixLibraryRefresher(_manager, providers, DispatchProxy.Create<IFileSystem, OriginalLanguageFlowTests.CallProxy>(),
            NullLogger<FixLibraryRefresher>.Instance, _monitor).Queue(new[] { first, second });
        Assert.Equal(new[] { first, second }, attempted);
        Assert.Empty(_notified);
    }

    [Fact]
    public void LibraryLookupFailureDoesNotBlockTheRepairOrLoseItsKnownOwner()
    {
        var id = Guid.NewGuid();
        AddIssue(IssueType.AudioLanguage, "movies", "one", itemId: id);
        var original = ((OriginalLanguageFlowTests.CallProxy)(object)_manager).Call;
        ((OriginalLanguageFlowTests.CallProxy)(object)_manager).Call = (method, args) =>
            method.Name == "GetVirtualFolders" ? throw new IOException("library lookup unavailable") : original!(method, args);
        var issue = Assert.Single(_db.Issues.GetIssues(IssueType.AudioLanguage));
        var attempt = _refresher.Capture(issue);
        var targets = new FixLibraryRefresher.Targets();
        _refresher.Complete(attempt, new FixResult { Success = true, Outcome = FixOutcome.Applied }, targets);
        _refresher.Queue(targets);
        Assert.Equal(id, Assert.Single(_refreshes).Id);
    }

    private void AddLibrary(Guid id, string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        _libraries.Add(new VirtualFolderInfo { Name = name, ItemId = id.ToString("N"), Locations = [path] });
        var folder = new Folder { Id = id, Name = name, Path = path };
        _libraryFolders.Add(folder);
        _items[id] = folder;
    }

    private void AddIssue(IssueType type, string library, string name, string details = "{}", Guid itemId = default)
    {
        var path = Path.Combine(_root, library, name + ".mkv");
        File.WriteAllText(path, "original");
        if (itemId == Guid.Empty) itemId = Guid.NewGuid();
        _items[itemId] = new Video { Id = itemId, Path = path };
        if (type == IssueType.CorruptArtwork)
        {
            var artwork = Path.Combine(_root, "metadata", name + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(artwork)!);
            File.WriteAllText(artwork, "artwork");
            _items[itemId] = new Video { Id = itemId, Path = path, ImageInfos = [new ItemImageInfo { Path = artwork, Type = ImageType.Primary }] };
            _collections[itemId] = [new CollectionFolder { Id = library == "movies" ? _movies : _shows }];
            path = artwork;
        }

        _db.Issues.ReplaceDetectedIssues(type, new[]
        {
            new Issue { Type = type, ItemId = itemId, Path = path, DetailsJson = details, DetectedAtUtc = DateTime.UtcNow }
        });
        _db.Issues.QueueDetectedIssues(type);
    }

    private Task Run(CancellationToken cancellationToken = default)
    {
        var config = new PluginConfigurationProvider(() => _config);
        var paths = DispatchProxy.Create<IApplicationPaths, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)paths).Call = (_, _) => _root;
        var serverPaths = DispatchProxy.Create<IServerApplicationPaths, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)serverPaths).Call = (method, _) => method.Name == "get_InternalMetadataPath" ? Path.Combine(_root, "metadata") : _root;
        var executor = new FixExecutor(_db.Issues, [new StubFixer((issue, ct) => { _attempts++; return _fix(issue, ct); })],
            config, TimeProvider.System, new ProtectionService(_db.Protection, _manager), NullLogger<FixExecutor>.Instance);
        _state.ArmBypassIdleCheck(RunRole.Fix);
        return new FixTask(_db.Issues, new RecycleBin(paths, NullLogger<RecycleBin>.Instance, _db.History), null!, null!,
            new AnalyticsReporter(_db.History, _db.Issues, null!, NullLogger<AnalyticsReporter>.Instance),
            new LibraryGuard(_manager, serverPaths), _state, config, executor, new FixResultRecorder(_db, NullLogger<FixResultRecorder>.Instance),
            NullLogger<FixTask>.Instance, _refresher).ExecuteAsync(new Progress<double>(), cancellationToken);
    }

    public void Dispose()
    {
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, _previousPlugin);
        Plugin.AttachedRunState = _previousState;
        Plugin.LastFixRunBacking = _previousSummary;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }

    private sealed class StubFixer(Func<Issue, CancellationToken, FixResult> fix) : IFixer
    {
        public bool CanFix(IssueType type) => true;
        public Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
            => Task.FromResult(fix(issue, cancellationToken));
    }
}
