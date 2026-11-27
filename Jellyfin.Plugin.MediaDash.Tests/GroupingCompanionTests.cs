using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("Diagnostics")]
public sealed class GroupingCompanionTests : IDisposable
{
    private readonly Plugin? _previous = Plugin.Instance;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-group-companions-" + Guid.NewGuid().ToString("N"));
    private readonly PluginConfiguration _config = new() { DryRun = false, UngroupedFixMode = FixMode.ManualApprove, MisplacedFixMode = FixMode.ManualApprove };
    private readonly List<MediaBrowser.Controller.Entities.BaseItem> _items = [];
    private readonly ILibraryManager _manager;
    private readonly MediaMoveService _service;
    private readonly MediaDashDb _db;
    private readonly LibraryGuard _guard;
    private readonly ILibraryMonitor _monitor;
    private readonly string _source;
    private readonly string _target;

    public GroupingCompanionTests()
    {
        Directory.CreateDirectory(_root);
        _source = Write("Movie.mkv", "video");
        _target = Path.Combine(_root, "Movie", "Movie.mkv");
        _db = new MediaDashDb(Path.Combine(_root, "test.db"));
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, _config);
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, plugin);
        var manager = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)manager).Call = (method, args) => method.Name switch
        {
            "GetVirtualFolders" => new List<VirtualFolderInfo> { new() { Locations = [_root] } },
            "GetItemList" => _items,
            "GetItemById" => _items.Find(item => args![0] is Guid id && item.Id == id),
            _ => null
        };
        _manager = manager;
        _guard = new LibraryGuard(manager);
        _monitor = DispatchProxy.Create<ILibraryMonitor, OriginalLanguageFlowTests.CallProxy>();
        _service = NewService();
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private MediaMoveService NewService() => new(_db, _guard, new ProtectionService(_db.Protection, _manager), _manager, NullLogger<MediaMoveService>.Instance);

    private Task<FixResult> Group(string? source = null, string? target = null, string action = "MoveFile") =>
        new MediaGrouperFixer(_service).FixAsync(
            MediaMoveTestApproval.Create(_db, _config, _items, IssueType.Ungrouped, source ?? _source, target ?? _target, action: action), null, CancellationToken.None);

    [Fact]
    public async Task ShortTitleDoesNotStealALongerTitlesCompanions()
    {
        Write("Movie-Other.mkv", "other video");
        var artwork = Write("Movie-Other-poster.jpg", "other artwork");
        Assert.True((await Group()).Success);
        Assert.Equal("other artwork", File.ReadAllText(artwork));
        Assert.False(File.Exists(Path.Combine(_root, "Movie", "Movie-Other-poster.jpg")));
    }

    [Fact]
    public async Task ProtectedCompanionBlocksTheWholeGroup()
    {
        var subtitle = Write("Movie.en.srt", "protected subtitle");
        _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = subtitle, ScannersMask = "*", FixersMask = "*" });
        var result = await Group();
        Assert.False(result.Success);
        Assert.Equal("video", File.ReadAllText(_source));
        Assert.Equal("protected subtitle", File.ReadAllText(subtitle));
        Assert.False(File.Exists(_target));
    }

    [Fact]
    public async Task LooseVideoMovesMatchingSubtitlesMetadataAndArtwork()
    {
        var names = new[] { "Movie.en.srt", "Movie.da.forced.idx", "Movie.da.forced.sub", "Movie.nfo", "Movie-poster.jpg", "Movie.bif" };
        foreach (var name in names) Write(name, name);
        Write("Other.en.srt", "other subtitle");
        Write("Movie.txt", "personal note");
        Write("poster.jpg", "shared artwork");

        Assert.True((await Group()).Success);

        Assert.Equal("video", File.ReadAllText(_target));
        foreach (var name in names)
        {
            Assert.False(File.Exists(Path.Combine(_root, name)));
            Assert.Equal(name, File.ReadAllText(Path.Combine(_root, "Movie", name)));
        }
        Assert.Equal("other subtitle", File.ReadAllText(Path.Combine(_root, "Other.en.srt")));
        Assert.Equal("personal note", File.ReadAllText(Path.Combine(_root, "Movie.txt")));
        Assert.Equal("shared artwork", File.ReadAllText(Path.Combine(_root, "poster.jpg")));
    }

    [Fact]
    public async Task DryRunLeavesVideoAndCompanionsInPlace()
    {
        _config.DryRun = true;
        var subtitle = Write("Movie.en.srt", "subtitle");
        Assert.True((await Group()).Success);
        Assert.True(File.Exists(_source));
        Assert.Equal("subtitle", File.ReadAllText(subtitle));
        Assert.False(Directory.Exists(Path.GetDirectoryName(_target)));
    }

    [Fact]
    public async Task CompanionCollisionPreservesBothVersions()
    {
        var subtitle = Write("Movie.en.srt", "original subtitle");
        var destination = Write(Path.Combine("Movie", "Movie.en.srt"), "existing subtitle");
        var result = await Group();
        Assert.False(result.Success);
        Assert.Equal(FixReason.DestinationCollision, result.Reason);
        Assert.Equal("original subtitle", File.ReadAllText(subtitle));
        Assert.Equal("existing subtitle", File.ReadAllText(destination));
        Assert.True(File.Exists(_source));
        Assert.False(File.Exists(_target));
        Assert.Contains(Jellyfin.Plugin.MediaDash.Api.Diagnostics.Recent(), d =>
            d.Source == "MediaMove.Refused" && d.Message.Contains(Path.GetFileName(subtitle), StringComparison.Ordinal));
    }

    [Fact]
    public async Task LateCompanionCollisionReportsRecoveryInsteadOfCompletion()
    {
        var subtitle = Write("Movie.en.srt", "source subtitle");
        _service.Checkpoint = (point, index) =>
        {
            if (point == "BeforeEntry" && index == 1)
                Write(Path.Combine("Movie", "Movie.en.srt"), "late destination subtitle");
        };
        var result = await Group();
        Assert.False(result.Success);
        Assert.Equal(FixOutcome.Refused, result.Outcome);
        Assert.Equal(FixReason.MoveNeedsReview, result.Reason);
        Assert.Equal("video", File.ReadAllText(_source));
        Assert.Equal("NeedsReview", _db.Moves.Read(result.MoveOperationId!)!.State);
        Assert.Equal("source subtitle", File.ReadAllText(subtitle));
        Assert.Equal("late destination subtitle", File.ReadAllText(Path.Combine(_root, "Movie", "Movie.en.srt")));
        Assert.Equal(0, result.BytesFreed);
    }

    [Fact]
    public async Task CompanionMatchingFollowsFilesystemCaseRules()
    {
        var subtitle = Write("movie.en.srt", "subtitle");
        Assert.True((await Group()).Success);
        Assert.Equal(!OperatingSystem.IsWindows(), File.Exists(subtitle));
        Assert.Equal(OperatingSystem.IsWindows(), File.Exists(Path.Combine(_root, "Movie", "movie.en.srt")));
    }

    [Fact]
    public async Task PrimaryCollisionLeavesAllSourcesUntouched()
    {
        var subtitle = Write("Movie.en.srt", "subtitle");
        Write(Path.Combine("Movie", "Movie.mkv"), "existing video");
        Assert.False((await Group()).Success);
        Assert.Equal("video", File.ReadAllText(_source));
        Assert.Equal("subtitle", File.ReadAllText(subtitle));
        Assert.Equal("existing video", File.ReadAllText(_target));
    }

    [Fact]
    public async Task FolderMoveKeepsGenericArtworkAndMetadataTogether()
    {
        Write(Path.Combine("Show", "Season 01", "E01.mkv"), "episode");
        Write(Path.Combine("Show", "tvshow.nfo"), "metadata");
        Write(Path.Combine("Show", "poster.jpg"), "artwork");
        Assert.True((await Group(Path.Combine(_root, "Show"), Path.Combine(_root, "Grouped", "Show"), "MoveFolder")).Success);
        Assert.Equal("metadata", File.ReadAllText(Path.Combine(_root, "Grouped", "Show", "tvshow.nfo")));
        Assert.Equal("artwork", File.ReadAllText(Path.Combine(_root, "Grouped", "Show", "poster.jpg")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Show")));
    }

    [Fact]
    public async Task OutsideLibraryDestinationPreservesAllSources()
    {
        var subtitle = Write("Movie.en.srt", "subtitle");
        var target = Path.Combine(Path.GetDirectoryName(_root)!, "outside-" + Guid.NewGuid().ToString("N"), "Movie.mkv");
        Assert.False((await Group(target: target)).Success);
        Assert.Equal("video", File.ReadAllText(_source));
        Assert.Equal("subtitle", File.ReadAllText(subtitle));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task SorterCompanionCollisionLeavesTheWholeMoveUntouched()
    {
        var subtitle = Write("Movie.en.srt", "subtitle");
        var target = Path.Combine(_root, "Sorted", "Renamed.mkv");
        var occupied = Write(Path.Combine("Sorted", "Renamed.en.srt"), "existing subtitle");
        var result = await new MediaSorterFixer(_service).FixAsync(
            MediaMoveTestApproval.Create(_db, _config, _items, IssueType.Misplaced, _source, target), null, default);
        Assert.False(result.Success);
        Assert.Equal("video", File.ReadAllText(_source));
        Assert.False(File.Exists(target));
        Assert.Equal("subtitle", File.ReadAllText(subtitle));
        Assert.Equal("existing subtitle", File.ReadAllText(occupied));
    }

    [Fact]
    public async Task SorterStillMovesAndRenamesCompanions()
    {
        var subtitle = Write("Movie.en.srt", "subtitle");
        var target = Path.Combine(_root, "Sorted", "Renamed.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var result = await new MediaSorterFixer(_service).FixAsync(
            MediaMoveTestApproval.Create(_db, _config, _items, IssueType.Misplaced, _source, target),
            null, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(subtitle));
        Assert.Equal("subtitle", File.ReadAllText(Path.Combine(_root, "Sorted", "Renamed.en.srt")));
    }

    public void Dispose()
    {
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, _previous);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
