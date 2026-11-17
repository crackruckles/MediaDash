using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Scanners;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("Diagnostics")]
public sealed class MediaMovePlanningTests : IDisposable
{
    private readonly Plugin? _previous = Plugin.Instance;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-move-plan-" + Guid.NewGuid().ToString("N"));
    private readonly PluginConfiguration _config = new();
    private readonly LibraryGuard _guard;
    private readonly List<BaseItem> _items = [];
    private readonly MediaDashDb _db;
    private readonly MediaMovePlanner _planner;

    public MediaMovePlanningTests()
    {
        Directory.CreateDirectory(Movies);
        Directory.CreateDirectory(Tv);
        _config.MoviesTargetPath = Movies;
        _config.TvTargetPath = Tv;
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, _config);
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, plugin);
        var manager = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)manager).Call = (method, _) => method.Name switch
        {
            "GetVirtualFolders" => new List<VirtualFolderInfo> { new() { Locations = [Movies, Tv] } },
            "GetItemList" => _items,
            _ => null
        };
        _guard = new LibraryGuard(manager);
        _db = new MediaDashDb(Path.Combine(_root, "plans.db"));
        _planner = new MediaMovePlanner(_guard, new ProtectionService(_db.Protection, manager), manager, _db.Moves);
    }

    private string Movies => Path.Combine(_root, "movies");
    private string Tv => Path.Combine(_root, "tv");
    private string Write(string path, string content = "payload")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private Movie Film(string name, string file, string? provider = null)
    {
        var item = new Movie { Id = Guid.NewGuid(), Name = name, Path = Write(file), ProductionYear = 2026 };
        if (provider is not null) item.ProviderIds["Tmdb"] = provider;
        _items.Add(item);
        return item;
    }

    private Task<IReadOnlyList<Issue>> Group(IProgress<double>? progress = null) => new MediaGrouperScanner(_guard, NullLogger<MediaGrouperScanner>.Instance, _planner)
        .ScanAsync(_items, progress ?? new Progress<double>(), CancellationToken.None);

    private sealed class CallbackProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    [Fact]
    public async Task GroupingReportsActivityWhileReadingALargeFile()
    {
        var film = Film("Film", Path.Combine(Movies, "Film.mkv"));
        File.WriteAllBytes(film.Path, new byte[12 * 1024 * 1024]);
        var previous = Plugin.AttachedRunState;
        var state = new RunState();
        state.TryBegin(RunRole.Scan);
        Plugin.AttachedRunState = state;
        var updates = new List<(double Percent, string? Path, string? Label)>();
        try
        {
            var issues = await Group(new CallbackProgress(p => updates.Add((p, Plugin.CurrentActivity, Plugin.CurrentActivityLabel))));
            Assert.Single(issues);
            Assert.Contains(updates, update => update.Path == film.Path && update.Percent < 90 && update.Label?.Contains("33% of file", StringComparison.Ordinal) == true);
            Assert.Contains(updates, update => update.Label?.Contains("Checking move files", StringComparison.Ordinal) == true);
            Assert.Equal(100, updates[^1].Percent);
            Assert.True(updates.Zip(updates.Skip(1)).All(pair => pair.First.Percent <= pair.Second.Percent));
        }
        finally
        {
            Plugin.AttachedRunState = previous;
        }
    }

    private Task<IReadOnlyList<Issue>> Sort() => new MediaSorterScanner(_guard, NullLogger<MediaSorterScanner>.Instance, _planner)
        .ScanAsync(_items, new Progress<double>(), CancellationToken.None);

    private static JsonElement Details(Issue issue) => JsonDocument.Parse(issue.DetailsJson).RootElement.Clone();

    [Fact]
    public async Task GroupingReadsASeriesPayloadOnceForAllItsEpisodes()
    {
        var seriesPath = Path.Combine(Tv, "Unsorted series");
        var series = new Series { Id = Guid.NewGuid(), Name = "Show", Path = seriesPath };
        _items.Add(series);
        for (var episode = 1; episode <= 3; episode++)
        {
            _items.Add(new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, SeriesName = "Show", ParentIndexNumber = 1,
                Path = Write(Path.Combine(seriesPath, "Season 01", "Show.S01E0" + episode + ".mkv")) });
        }

        var previous = Plugin.AttachedRunState;
        var state = new RunState();
        state.TryBegin(RunRole.Scan);
        Plugin.AttachedRunState = state;
        var readPaths = new List<string?>();
        try
        {
            var issues = await Group(new CallbackProgress(_ =>
            {
                if (Plugin.CurrentActivityLabel?.Contains("; 0% of file", StringComparison.Ordinal) == true)
                {
                    readPaths.Add(Plugin.CurrentActivity);
                }
            }));
            var plan = MediaMovePlan.ReadIssue(Assert.Single(issues))!;
            Assert.Empty(plan.BlockingReasons);
            Assert.Equal(3, readPaths.Count);
            Assert.Equal(3, readPaths.Distinct().Count());
            Assert.Equal(3, Assert.Single(plan.Entries).Inventory.Count(payload => !payload.IsDirectory));
        }
        finally
        {
            Plugin.AttachedRunState = previous;
        }
    }

    private BaseItem KindFixture(string kind, bool unidentified = false)
    {
        var container = Path.Combine(Tv, "Owner");
        var name = kind switch
        {
            "book" => "Book.epub", "comic" => "Book.cbz", "photo" => "Photo.jpg", "audio" => "Track.flac",
            "audiobook" => "Book.m4b", "strm" => "Film.strm", "iso" => "Film.iso", _ => string.Empty
        };
        var file = name.Length == 0 ? container : Write(Path.Combine(container, name), kind == "strm" ? "https://example.invalid/owned-stream" : "owned primary");
        BaseItem item = kind switch
        {
            "book" or "comic" => new Book(), "photo" => new Photo(),
            "audio" => new MediaBrowser.Controller.Entities.Audio.Audio { Album = "Owner", AlbumArtists = ["Artist"] },
            "audiobook" => (BaseItem)Activator.CreateInstance(typeof(BaseItem).Assembly.GetType("MediaBrowser.Controller.Entities.AudioBook")
                ?? typeof(BaseItem).Assembly.GetType("MediaBrowser.Controller.Entities.Audio.AudioBook")!)!,
            _ => new Movie()
        };
        item.Id = Guid.NewGuid(); item.Name = "Owner"; item.Path = file;
        if (kind is "dvd" or "bluray")
        {
            var property = typeof(Video).GetProperty("VideoType")!;
            property.SetValue(item, Enum.Parse(property.PropertyType, kind == "dvd" ? "Dvd" : "BluRay", true));
            if (kind == "dvd")
            {
                Write(Path.Combine(container, "VIDEO_TS", "VIDEO_TS.IFO"), "dvd index");
                Write(Path.Combine(container, "VIDEO_TS", "VTS_01_1.VOB"), "dvd video");
            }
            else
            {
                Write(Path.Combine(container, "BDMV", "index.bdmv"), "bluray index");
                Write(Path.Combine(container, "BDMV", "STREAM", "00001.m2ts"), "bluray video");
            }
        }

        Write(Path.Combine(container, "README.md"), "user notes");
        Write(Path.Combine(container, kind == "photo" ? "Photo.xmp" : "cover.jpg"), "owned companion");
        Write(Path.Combine(container, "metadata.opf"), "book metadata");
        Directory.CreateDirectory(Path.Combine(container, "empty"));
        if (unidentified) Write(Path.Combine(container, "Unidentified" + Path.GetExtension(file)), "unidentified primary");
        _items.Add(item);
        _config.BooksTargetPath = Path.Combine(Movies, "books");
        _config.ComicsTargetPath = Path.Combine(Movies, "comics");
        _config.PicturesTargetPath = Path.Combine(Movies, "photos");
        _config.MusicTargetPath = Path.Combine(Movies, "music");
        foreach (var target in new[] { _config.BooksTargetPath, _config.ComicsTargetPath, _config.PicturesTargetPath, _config.MusicTargetPath }) Directory.CreateDirectory(target);
        return item;
    }

    [Theory]
    [InlineData("book")]
    [InlineData("comic")]
    [InlineData("photo")]
    [InlineData("audio")]
    [InlineData("audiobook")]
    [InlineData("strm")]
    [InlineData("iso")]
    [InlineData("dvd")]
    [InlineData("bluray")]
    public async Task DedicatedMediaKindsKeepTheirCompleteContainerAndUseTheEnabledTarget(string kind)
    {
        KindFixture(kind);
        var issue = Assert.Single(await Sort());
        var plan = MediaMovePlan.ReadIssue(issue)!;
        Assert.Empty(plan.BlockingReasons);
        var entry = Assert.Single(plan.Entries);
        Assert.True(entry.IsDirectory);
        var target = kind switch { "book" or "audiobook" => _config.BooksTargetPath, "comic" => _config.ComicsTargetPath, "photo" => _config.PicturesTargetPath, "audio" => _config.MusicTargetPath, _ => Movies };
        Assert.Equal(Path.Combine(target, "Owner"), entry.Destination);
        Assert.Contains(entry.Inventory, payload => payload.RelativePath == "README.md");
        Assert.Contains(entry.Inventory, payload => payload.RelativePath == "metadata.opf");
        Assert.Contains(entry.Inventory, payload => payload.RelativePath == "empty" && payload.IsDirectory);
    }

    [Theory]
    [InlineData("book")]
    [InlineData("comic")]
    [InlineData("photo")]
    [InlineData("audio")]
    [InlineData("audiobook")]
    [InlineData("strm")]
    [InlineData("iso")]
    [InlineData("dvd")]
    [InlineData("bluray")]
    public async Task CompleteMediaKindContainerCanExecuteRescanAndUndoWithEveryPayload(string kind)
    {
        var item = KindFixture(kind);
        _config.DryRun = false;
        _config.MisplacedFixMode = FixMode.ManualApprove;
        var manager = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)manager).Call = (method, args) => method.Name switch
        {
            "GetVirtualFolders" => new List<VirtualFolderInfo> { new() { Locations = [Movies, Tv] } },
            "GetItemList" => _items,
            "GetItemById" => _items.FirstOrDefault(candidate => args![0] is Guid id && candidate.Id == id),
            _ => null
        };
        var service = new MediaMoveService(_db, _guard, new ProtectionService(_db.Protection, manager), manager, NullLogger<MediaMoveService>.Instance);
        var found = Assert.Single(await Sort());
        var plan = MediaMovePlan.ReadIssue(found)!;
        Assert.Empty(plan.BlockingReasons);
        var entry = Assert.Single(plan.Entries);
        _db.Issues.ReplaceDetectedIssues(found.Type, [found]);
        _db.Issues.QueueDetectedIssues(found.Type);
        var issue = Assert.Single(_db.Issues.GetIssues(found.Type));
        var result = await service.ExecuteAsync(issue, null, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.False(Directory.Exists(entry.Source));
        MediaMoveSnapshot.VerifyInventory(entry.Destination, entry.Inventory, true, CancellationToken.None);
        var originalPath = item.Path;
        item.Path = Path.GetFullPath(Path.Combine(entry.Destination, Path.GetRelativePath(entry.Source, originalPath)));
        Assert.Empty(await Sort());
        var undone = await service.UndoAsync(result.MoveOperationId!, null, CancellationToken.None);
        Assert.True(undone.Success, undone.Message);
        MediaMoveSnapshot.VerifyInventory(entry.Source, entry.Inventory, true, CancellationToken.None);
        Assert.False(Directory.Exists(entry.Destination));
        Assert.Equal("Undone", _db.Moves.Read(result.MoveOperationId!)!.State);
    }

    [Theory]
    [InlineData("book")]
    [InlineData("comic")]
    [InlineData("photo")]
    [InlineData("audio")]
    [InlineData("audiobook")]
    public async Task UnidentifiedNonVideoPrimaryRequiresReviewRatherThanJoiningAnUnrelatedContainer(string kind)
    {
        KindFixture(kind, unidentified: true);
        var issue = Assert.Single(await Sort());
        Assert.True(issue.HasBlockingWarnings);
        Assert.Contains(MediaMovePlan.ReadIssue(issue)!.BlockingReasons, reason => reason.Contains("unidentified", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitMoveConsentExpiresWhenDryRunModeChanges(bool previewDryRun)
    {
        var source = Write(Path.Combine(Movies, "selected.opaque"), "explicit consent bytes");
        var target = Path.Combine(Tv, "selected.opaque");
        _config.DryRun = previewDryRun;
        var approved = await _planner.BuildExplicitAsync([(source, target)], _items, _config, CancellationToken.None);
        Assert.Empty(approved.BlockingReasons);

        _config.DryRun = !previewDryRun;
        var current = await _planner.BuildExplicitAsync([(source, target)], _items, _config, CancellationToken.None);
        var result = await MoveService().ExecuteExplicitAsync(approved, approved.Fingerprint, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.NotEqual(approved.Fingerprint, current.Fingerprint);
        Assert.Null(result.MoveOperationId);
        Assert.Equal("explicit consent bytes", File.ReadAllText(source));
        Assert.False(File.Exists(target));
        using var connection = _db.ConnectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM media_move_operations";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    [Theory]
    [InlineData(IssueType.Ungrouped, false)]
    [InlineData(IssueType.Ungrouped, true)]
    [InlineData(IssueType.Misplaced, false)]
    [InlineData(IssueType.Misplaced, true)]
    public async Task AutomaticMoveApprovalSurvivesDryRunModeChanges(IssueType type, bool previewDryRun)
    {
        Film("Film", type == IssueType.Ungrouped ? Path.Combine(Movies, "Film.mkv") : Path.Combine(Tv, "Film", "Film.mkv"));
        _config.UngroupedFixMode = FixMode.ManualApprove;
        _config.MisplacedFixMode = FixMode.ManualApprove;
        _config.DryRun = previewDryRun;
        var found = Assert.Single(await (type == IssueType.Ungrouped ? Group() : Sort()));
        var approved = MediaMovePlan.ReadIssue(found)!;
        Assert.Empty(approved.BlockingReasons);
        _db.Issues.ReplaceDetectedIssues(type, [found]);
        _db.Issues.QueueDetectedIssues(type);
        var queued = Assert.Single(_db.Issues.GetIssues(type));

        _config.DryRun = !previewDryRun;
        var current = MediaMovePlan.ReadIssue(Assert.Single(await (type == IssueType.Ungrouped ? Group() : Sort())))!;
        Assert.Equal(approved.Fingerprint, current.Fingerprint);
        var result = await MoveService().ExecuteAsync(queued, null, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.Equal(!previewDryRun, result.WasDryRun);
        var primary = approved.Entries[0];
        Assert.Equal(!previewDryRun, primary.IsDirectory ? Directory.Exists(primary.Source) : File.Exists(primary.Source));
        Assert.Equal(previewDryRun, primary.IsDirectory ? Directory.Exists(primary.Destination) : File.Exists(primary.Destination));
    }

    [Fact]
    public async Task ScanReplacesLegacyGroupingFindingWithCurrentMovePreview()
    {
        var path = Path.Combine(Movies, "Film.mkv");
        Film("Film", path);
        _db.Issues.ReplaceDetectedIssues(IssueType.Ungrouped,
            [new Issue { Type = IssueType.Ungrouped, Path = path, DetailsJson = "{\"action\":\"MoveFile\"}" }]);
        Assert.Null(MediaMovePlan.ReadIssue(Assert.Single(_db.Issues.GetIssues(IssueType.Ungrouped))));

        _db.Issues.ReplaceDetectedIssues(IssueType.Ungrouped, await Group());

        var current = Assert.Single(_db.Issues.GetIssues(IssueType.Ungrouped));
        var plan = MediaMovePlan.ReadIssue(current);
        Assert.NotNull(plan);
        Assert.Empty(plan.BlockingReasons);
        Assert.Equal(IssueStatus.Detected, current.Status);
        Assert.True(File.Exists(path));
    }

    private MediaMoveService MoveService()
    {
        var manager = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)manager).Call = (method, args) => method.Name switch
        {
            "GetVirtualFolders" => new List<VirtualFolderInfo> { new() { Locations = [Movies, Tv] } },
            "GetItemList" => _items,
            "GetItemById" => _items.FirstOrDefault(candidate => args![0] is Guid id && candidate.Id == id),
            _ => null
        };
        return new MediaMoveService(_db, _guard, new ProtectionService(_db.Protection, manager), manager, NullLogger<MediaMoveService>.Instance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitMovieMoveOrRenamePlansEveryOwnedCompanion(bool rename)
    {
        var movie = Film("Film", Path.Combine(Movies, "Film.mkv"));
        var subtitles = Write(Path.Combine(Movies, "Film.fr.srt"), "subtitles");
        var notes = Write(Path.Combine(Movies, "Film.nfo"), "metadata");
        var target = Path.Combine(Tv, rename ? "Renamed.mkv" : "Film.mkv");
        var plan = await _planner.BuildExplicitAsync([(movie.Path, target)], _items, _config, CancellationToken.None);
        Assert.True(plan.HasValidDefinition());
        Assert.Empty(plan.BlockingReasons);
        Assert.Equal(3, plan.Entries.Length);
        Assert.Contains(plan.Entries, entry => entry.Source == subtitles && entry.Destination == Path.Combine(Tv, rename ? "Renamed.fr.srt" : "Film.fr.srt"));
        Assert.Contains(plan.Entries, entry => entry.Source == notes && entry.Destination == Path.ChangeExtension(target, ".nfo"));
        Assert.All(plan.Entries, entry => Assert.NotEmpty(entry.Inventory));
        Assert.Equal(plan.Fingerprint, (await _planner.BuildExplicitAsync([(movie.Path, target)], _items, _config, CancellationToken.None)).Fingerprint);
        Assert.True(File.Exists(movie.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitMoveNeverSelectsAnEnabledLibraryRootEvenWithTrailingSeparator(bool trailingSeparator)
    {
        var root = trailingSeparator ? Movies + Path.DirectorySeparatorChar : Movies;
        await Assert.ThrowsAsync<IOException>(() => _planner.BuildExplicitAsync([(root, Path.Combine(Tv, "MovedLibrary"))], _items, _config, CancellationToken.None));
        Assert.True(Directory.Exists(Movies));
    }

    [Fact]
    public async Task ExplicitDirectoryMoveCollapsesConsistentChildSelectionAndIncludesUnknownFilesAndEmptyDirectories()
    {
        var folder = Path.Combine(Movies, "Album");
        var track = Write(Path.Combine(folder, "Disc 1", "01.flac"));
        Write(Path.Combine(folder, "user-notes.bin"));
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        var target = Path.Combine(Tv, "Album");
        var plan = await _planner.BuildExplicitAsync([(folder, target), (track, Path.Combine(target, "Disc 1", "01.flac"))], _items, _config, CancellationToken.None);
        Assert.Empty(plan.BlockingReasons);
        var entry = Assert.Single(plan.Entries);
        Assert.True(entry.IsDirectory);
        Assert.Contains(entry.Inventory, payload => payload.RelativePath == "user-notes.bin");
        Assert.Contains(entry.Inventory, payload => payload.RelativePath == "empty" && payload.IsDirectory);
    }

    [Fact]
    public async Task ExplicitGenericFileMovesOnlyTheSelectedFileAndDoesNotClaimUnrelatedNotes()
    {
        var selected = Write(Path.Combine(Movies, "selected.opaque"));
        Write(Path.Combine(Movies, "unrelated.nfo"));
        var plan = await _planner.BuildExplicitAsync([(selected, Path.Combine(Tv, "selected.opaque"))], _items, _config, CancellationToken.None);
        Assert.Empty(plan.BlockingReasons);
        Assert.Equal(selected, Assert.Single(plan.Entries).Source);
    }

    [Fact]
    public async Task SeparatePartFilmsKeepTheirCompleteTitleContainers()
    {
        var one = Film("The Hobbit Part 1", Path.Combine(Movies, "The Hobbit Part 1.mkv"), "11");
        var two = Film("The Hobbit Part 2", Path.Combine(Movies, "The Hobbit Part 2.mkv"), "22");
        var issues = await Group();
        Assert.Equal(2, issues.Count);
        Assert.Equal(Path.Combine(Movies, "The Hobbit Part 1", "The Hobbit Part 1.mkv"), Details(issues.Single(i => i.ItemId == one.Id)).GetProperty("target").GetString());
        Assert.Equal(Path.Combine(Movies, "The Hobbit Part 2", "The Hobbit Part 2.mkv"), Details(issues.Single(i => i.ItemId == two.Id)).GetProperty("target").GetString());
    }

    [Fact]
    public async Task AlreadyContainedCollectionFilmsDoNotGainFranchiseNesting()
    {
        Film("The Hobbit Part 1", Path.Combine(Movies, "The Hobbit Part 1", "The Hobbit Part 1.mkv"), "11");
        Film("The Hobbit Part 2", Path.Combine(Movies, "The Hobbit Part 2", "The Hobbit Part 2.mkv"), "22");
        Assert.Empty(await Group());
    }

    [Fact]
    public async Task ScannedApprovalIncludesEveryCompanionAndUncachedPayloadEvidence()
    {
        var film = Film("Film", Path.Combine(Movies, "Film.mkv"));
        Write(Path.Combine(Movies, "Film.en.forced.srt"), "subtitles");
        Write(Path.Combine(Movies, "Film-poster.png"), "artwork");
        var issue = Assert.Single(await Group());
        var details = Details(issue);
        Assert.True(details.TryGetProperty("movePlan", out var plan), "A move approval must bind the complete displayed inventory.");
        Assert.Equal(1, plan.GetProperty("version").GetInt32());
        Assert.Equal(64, plan.GetProperty("fingerprint").GetString()!.Length);
        Assert.Empty(plan.GetProperty("blockingReasons").EnumerateArray());
        var entries = plan.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(new[] { "Film-poster.png", "Film.en.forced.srt", "Film.mkv" }, entries.Select(e => Path.GetFileName(e.GetProperty("source").GetString())).Order(StringComparer.Ordinal));
        Assert.All(entries, entry => Assert.Equal(64, entry.GetProperty("inventory")[0].GetProperty("sha256").GetString()!.Length));
        Assert.Equal(film.Path, issue.Path);
        Assert.Equal(0, issue.SizeSavings);
    }

    [Fact]
    public async Task AmbiguousGenericRootArtworkRemainsVisibleAsABlocker()
    {
        Film("Film", Path.Combine(Movies, "Film.mkv"));
        Film("Other", Path.Combine(Movies, "Other.mkv"));
        Write(Path.Combine(Movies, "poster.jpg"), "shared artwork");
        var issues = await Group();
        Assert.Equal(2, issues.Count);
        Assert.All(issues, issue =>
        {
            Assert.True(Details(issue).TryGetProperty("movePlan", out var plan), "Ambiguity must remain visible without a runnable move.");
            Assert.NotEmpty(plan.GetProperty("blockingReasons").EnumerateArray());
        });
    }

    [Fact]
    public async Task SorterPlansTheWholeDedicatedContainerIncludingUnknownFilesAndEmptyDirectories()
    {
        var film = Film("Film", Path.Combine(Tv, "Film (2026)", "Film (2026).mkv"));
        Write(Path.Combine(Tv, "Film (2026)", "extras", "behind-the-scenes.mkv"));
        Write(Path.Combine(Tv, "Film (2026)", "notes.txt"));
        Directory.CreateDirectory(Path.Combine(Tv, "Film (2026)", "empty"));
        var issue = Assert.Single(await Sort());
        var details = Details(issue);
        Assert.Equal(Path.Combine(Tv, "Film (2026)"), issue.Path);
        Assert.Equal(Path.Combine(Movies, "Film (2026)"), details.GetProperty("targetPath").GetString());
        var entry = Assert.Single(details.GetProperty("movePlan").GetProperty("entries").EnumerateArray());
        var inventory = entry.GetProperty("inventory").EnumerateArray().ToArray();
        Assert.Contains(inventory, i => i.GetProperty("relativePath").GetString() == "notes.txt");
        Assert.Contains(inventory, i => i.GetProperty("relativePath").GetString() == "empty" && i.GetProperty("isDirectory").GetBoolean());
        Assert.Equal(film.Id, issue.ItemId);
    }

    [Fact]
    public async Task SameSizedEditsWithPreservedTimestampChangeTheApprovalFingerprint()
    {
        var film = Film("Film", Path.Combine(Movies, "Film.mkv"));
        var first = Details(Assert.Single(await Group()));
        Assert.True(first.TryGetProperty("movePlan", out var plan), "Approval must include uncached payload evidence.");
        var fingerprint = plan.GetProperty("fingerprint").GetString();
        var modified = File.GetLastWriteTimeUtc(film.Path);
        File.WriteAllText(film.Path, "changed");
        File.SetLastWriteTimeUtc(film.Path, modified);
        Assert.NotEqual(fingerprint, Details(Assert.Single(await Group())).GetProperty("movePlan").GetProperty("fingerprint").GetString());
    }

    [Fact]
    public async Task LongerPrimaryOwnsItsArtworkInTheDisplayedPlan()
    {
        var shortTitle = Film("Film", Path.Combine(Movies, "Film.mkv"));
        var longTitle = Film("Film-Other", Path.Combine(Movies, "Film-Other.mkv"));
        Write(Path.Combine(Movies, "Film-Other-poster.jpg"), "long title art");
        var issues = await Group();
        var shortDetails = Details(issues.Single(i => i.ItemId == shortTitle.Id));
        Assert.True(shortDetails.TryGetProperty("movePlan", out var plan));
        Assert.Single(plan.GetProperty("entries").EnumerateArray());
        Assert.Equal(2, Details(issues.Single(i => i.ItemId == longTitle.Id)).GetProperty("movePlan").GetProperty("entries").GetArrayLength());
    }

    [Theory]
    [InlineData("[tvdbid-123]")]
    [InlineData("{tvdbid-123}")]
    [InlineData("(tvdbid-123)")]
    public void YearDisambiguationPreservesEverySupportedProviderDelimiter(string tag)
    {
        var parent = "Show (2005) " + tag;
        Assert.Equal(parent, MediaGrouperScanner.ResolveSafeSeriesName("Show", 2020,
            Path.Combine(Tv, parent, "Season 02", "Show.S02E01.mkv"), Tv));
    }

    [Fact]
    public async Task MergedVersionsUseOneMatchingPrefixAndKeepBothPayloads()
    {
        var film = Film("Film", Path.Combine(Movies, "Film - Cinema.mkv"));
        film.LocalAlternateVersions = [Write(Path.Combine(Movies, "Film - Directors Cut.mkv"), "alternate")];
        Write(Path.Combine(Movies, "Film - Directors Cut.en.srt"), "alternate subtitles");
        var plan = Details(Assert.Single(await Group())).GetProperty("movePlan");
        Assert.Empty(plan.GetProperty("blockingReasons").EnumerateArray());
        var entries = plan.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(3, entries.Length);
        Assert.All(entries, entry => Assert.Equal(Path.Combine(Movies, "Film"), Path.GetDirectoryName(entry.GetProperty("destination").GetString())));
    }

    [Fact]
    public async Task DeclaredMultipartMovieIncludesEveryPartAndOneContainer()
    {
        var film = Film("Film", Path.Combine(Movies, "Film-cd1.mkv"));
        film.AdditionalParts = [Write(Path.Combine(Movies, "Film-cd2.mkv"), "second part")];
        var plan = Details(Assert.Single(await Group())).GetProperty("movePlan");
        Assert.Empty(plan.GetProperty("blockingReasons").EnumerateArray());
        var entries = plan.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        Assert.All(entries, entry => Assert.Equal(Path.Combine(Movies, "Film"), Path.GetDirectoryName(entry.GetProperty("destination").GetString())));
    }

    [Fact]
    public async Task IndependentSameYearIdentitiesCannotMergeIntoOneTargetContainer()
    {
        Film("Same title", Path.Combine(Movies, "Same title.mkv"), "100");
        Film("Same title", Path.Combine(Movies, "Same title.mp4"), "200");
        var issues = await Group();
        Assert.Equal(2, issues.Count);
        Assert.All(issues, issue => Assert.NotEmpty(Details(issue).GetProperty("movePlan").GetProperty("blockingReasons").EnumerateArray()));
    }

    [Fact]
    public async Task OneAlbumMovesItsDiscFoldersLyricsArtworkAndUnknownFilesTogether()
    {
        var first = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Id = Guid.NewGuid(), Name = "Track one", Album = "Album", AlbumArtists = ["Artist"],
            Path = Write(Path.Combine(Tv, "Album", "Disc 1", "01.flac"))
        };
        var second = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Id = Guid.NewGuid(), Name = "Track two", Album = "Album", AlbumArtists = ["Artist"],
            Path = Write(Path.Combine(Tv, "Album", "Disc 2", "02.flac"))
        };
        _items.AddRange([first, second]);
        _config.MusicTargetPath = Path.Combine(Movies, "music");
        Directory.CreateDirectory(_config.MusicTargetPath);
        Write(Path.Combine(Tv, "Album", "Disc 1", "01.lrc"), "lyrics");
        Write(Path.Combine(Tv, "Album", "cover.jpg"), "album art");
        Write(Path.Combine(Tv, "Album", "README.md"), "user notes");
        var issue = Assert.Single(await Sort());
        Assert.Equal(Path.Combine(Tv, "Album"), issue.Path);
        var plan = Details(issue).GetProperty("movePlan");
        Assert.Empty(plan.GetProperty("blockingReasons").EnumerateArray());
        var inventory = Assert.Single(plan.GetProperty("entries").EnumerateArray()).GetProperty("inventory").EnumerateArray().ToArray();
        Assert.Equal(8, inventory.Length); // root, two disc folders, two tracks, lyrics, art and notes
    }

    [Fact]
    public async Task ADeclaredSeriesContainerMovesWholeRatherThanOnlyItsFirstSeason()
    {
        var seriesPath = Path.Combine(Tv, "Unsorted series");
        var series = new Series { Id = Guid.NewGuid(), Name = "Show", Path = seriesPath, PremiereDate = new DateTime(2005, 1, 1) };
        var first = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, SeriesName = "Show", ParentIndexNumber = 1,
            Path = Write(Path.Combine(seriesPath, "Season 01", "Show.S01E01.mkv")) };
        var second = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, SeriesName = "Show", ParentIndexNumber = 2,
            PremiereDate = new DateTime(2020, 1, 1), Path = Write(Path.Combine(seriesPath, "Season 02", "Show.S02E01.mkv")) };
        _items.AddRange([series, first, second]);
        var issue = Assert.Single(await Group());
        Assert.Equal(seriesPath, issue.Path);
        Assert.Equal(Path.Combine(Tv, "Show (2005)"), Details(issue).GetProperty("target").GetString());
        Assert.Empty(Details(issue).GetProperty("movePlan").GetProperty("blockingReasons").EnumerateArray());
    }

    [Fact]
    public async Task UnidentifiedPrimaryInsideAContainerPreventsAutomaticWholeFolderMovement()
    {
        Film("Film", Path.Combine(Tv, "Film", "Film.mkv"));
        Write(Path.Combine(Tv, "Film", "Unidentified.mkv"), "unidentified primary");
        var issue = Assert.Single(await Sort());
        Assert.NotEmpty(Details(issue).GetProperty("movePlan").GetProperty("blockingReasons").EnumerateArray());
        Assert.True(issue.HasBlockingWarnings);
    }

    [Fact]
    public async Task AnOwnedIgnoreMarkerMovesInsideItsCompleteContainer()
    {
        Film("Film", Path.Combine(Tv, "Film", "Film.mkv"));
        Write(Path.Combine(Tv, "Film", ".ignore"), "owned exclusion");
        var plan = Details(Assert.Single(await Sort())).GetProperty("movePlan");
        Assert.Empty(plan.GetProperty("blockingReasons").EnumerateArray());
        Assert.Contains(Assert.Single(plan.GetProperty("entries").EnumerateArray()).GetProperty("inventory").EnumerateArray(),
            entry => entry.GetProperty("relativePath").GetString() == ".ignore");
    }

    [Fact]
    public async Task ASharedIgnoreScopeRequiresReview()
    {
        Film("Film", Path.Combine(Movies, "Film.mkv"));
        Write(Path.Combine(Movies, ".ignore"), "shared exclusion");
        var issue = Assert.Single(await Group());
        Assert.True(issue.HasBlockingWarnings);
        Assert.NotEmpty(Details(issue).GetProperty("movePlan").GetProperty("blockingReasons").EnumerateArray());
    }

    [Fact]
    public async Task AnUnchangedRescanRetainsTheExactApprovalFingerprint()
    {
        Film("Film", Path.Combine(Movies, "Film.mkv"));
        var first = Details(Assert.Single(await Group())).GetProperty("movePlan").GetProperty("fingerprint").GetString();
        Assert.Equal(first, Details(Assert.Single(await Group())).GetProperty("movePlan").GetProperty("fingerprint").GetString());
    }

    [Fact]
    public async Task MissingDeclaredMultipartCannotBeSilentlyOmittedFromApproval()
    {
        var film = Film("Film", Path.Combine(Movies, "Film cd1.mkv"));
        film.AdditionalParts = [Path.Combine(Movies, "Film cd2.mkv")];
        var issue = Assert.Single(await Group());
        Assert.True(issue.HasBlockingWarnings);
        Assert.Contains("missing", issue.DetailsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeclaredOutsideLibraryPayloadIsBlockedWithoutHashingItsContents()
    {
        var film = Film("Film", Path.Combine(Movies, "Film cd1.mkv"));
        var outside = Write(Path.Combine(_root, "private", "Film cd2.mkv"), "must not enter approval inventory");
        film.AdditionalParts = [outside];
        var issue = Assert.Single(await Group());
        Assert.True(issue.HasBlockingWarnings);
        var entries = Details(issue).GetProperty("movePlan").GetProperty("entries").EnumerateArray().ToArray();
        Assert.DoesNotContain(entries, entry => entry.GetProperty("source").GetString() == outside && entry.GetProperty("inventory").GetArrayLength() != 0);
    }

    [Fact]
    public async Task ExplicitExternalAudioAndSubtitleDeclarationsJoinTheApprovedBundle()
    {
        var film = Film("Film", Path.Combine(Movies, "Film.mkv"));
        film.AudioFiles = [Write(Path.Combine(Movies, "commentary.ac3"), "external audio")];
        film.SubtitleFiles = [Write(Path.Combine(Movies, "translation.srt"), "external subtitles")];
        var issue = Assert.Single(await Group());
        var plan = Details(issue).GetProperty("movePlan");
        Assert.Empty(plan.GetProperty("blockingReasons").EnumerateArray());
        var sources = plan.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("source").GetString()).ToArray();
        Assert.Contains(film.AudioFiles[0], sources);
        Assert.Contains(film.SubtitleFiles[0], sources);
    }

    [Fact]
    public async Task ActiveOperationClaimIsShownAsABlockingFinding()
    {
        Film("Film", Path.Combine(Movies, "Film.mkv"));
        var issue = Assert.Single(await Group());
        var plan = MediaMovePlan.ReadIssue(issue)!;
        Assert.NotNull(_db.Moves.TryBegin(plan));
        var repeated = Assert.Single(await Group());
        Assert.True(repeated.HasBlockingWarnings);
        Assert.Contains("reserved", repeated.DetailsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LargeSharedFolderDoesNotEmbedUnrelatedFilenamesInEveryApproval()
    {
        Film("Film", Path.Combine(Movies, "Film.mkv"));
        for (var index = 0; index < 500; index++)
        {
            Write(Path.Combine(Movies, "Unrelated media item " + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ".mkv"));
        }

        var issue = Assert.Single(await Group());
        Assert.False(issue.HasBlockingWarnings);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(issue.DetailsJson) < 16 * 1024,
            "One-file approval must not repeat the names of hundreds of unrelated library items.");
    }

    [Fact]
    public async Task AnIdentifiedUnrelatedMovieFolderDoesNotBlockALooseMovie()
    {
        var loose = Film("Loose", Path.Combine(Movies, "Loose.mkv"));
        Film("Contained", Path.Combine(Movies, "Contained", "Contained.mkv"));
        var issue = Assert.Single(await Group());
        Assert.Equal(loose.Id, issue.ItemId);
        Assert.False(issue.HasBlockingWarnings, issue.DetailsJson);
        Assert.Single(Details(issue).GetProperty("movePlan").GetProperty("entries").EnumerateArray());
    }

    public void Dispose()
    {
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, _previous);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}
