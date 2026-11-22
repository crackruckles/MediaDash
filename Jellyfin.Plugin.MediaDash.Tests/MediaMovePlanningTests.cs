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

    [Theory]
    [InlineData("Outlander - Blood of My Blood", "Outlander Blood of My Blood (2025)", "Outlander: Blood of My Blood", 2025)]
    [InlineData("The Mentalist", "The Mentalist (2008)", "The Mentalist", 2008)]
    public async Task NfoIdentifiedEpisodesJoinTheCanonicalSeries(string sourceName, string targetName, string title, int year)
    {
        var master = new Series { Id = Guid.NewGuid(), Name = title, ProductionYear = year,
            Path = Directory.CreateDirectory(Path.Combine(Tv, targetName)).FullName };
        master.ProviderIds["Tvdb"] = "477406";
        var source = new Series { Id = Guid.NewGuid(), Name = sourceName,
            Path = Directory.CreateDirectory(Path.Combine(Tv, sourceName)).FullName };
        Write(Path.Combine(source.Path, "tvshow.nfo"), "<tvshow><id>477406</id></tvshow>");
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = source.Id, SeriesName = sourceName, ParentIndexNumber = 1,
            IndexNumber = 3, Path = Write(Path.Combine(source.Path, "episode.mkv")) };
        _items.AddRange([master, source, episode]);
        var manager = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)manager).Call = (method, args) => method.Name == "GetItemById"
            ? _items.FirstOrDefault(item => item.Id == (Guid)args![0]!) : null;
        var identities = new Metadata.MediaIdentityService(_db.Identities, null, NullLogger.Instance, () => [master, source]);
        var planner = new MediaMovePlanner(_guard, new ProtectionService(_db.Protection, manager), manager, _db.Moves, identities);
        var issues = await planner.ScanAsync(IssueType.Ungrouped, _items, _config, new Progress<double>(), default);
        var plan = MediaMovePlan.ReadIssue(Assert.Single(issues))!;
        Assert.Empty(plan.BlockingReasons);
        // "episode.mkv" carries no number of its own, so it is named for the episode Jellyfin identified.
        Assert.Equal(Path.Combine(master.Path, "Season 01", sourceName + " S01E03.mkv"), plan.Entries[0].Destination);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupingIncludesAnimeWithADedicatedDestination(bool movie)
    {
        _config.AnimeTargetPath = Tv;
        var source = Write(movie ? Path.Combine(Tv, "Anime Film.mkv")
            : Path.Combine(Tv, "Kusuriya no Hitorigoto - 51 (WEB 1080p) [Anarchy]", "episode.mkv"));
        BaseItem item = movie
            ? new Movie { Id = Guid.NewGuid(), Name = "Anime Film", Path = source, Genres = ["Anime"] }
            : new Episode { Id = Guid.NewGuid(), SeriesName = "Kusuriya no Hitorigoto", ParentIndexNumber = 2, IndexNumber = 27, Path = source, Genres = ["Anime"] };
        _items.Add(item);

        var plan = MediaMovePlan.ReadIssue(Assert.Single(await Group()))!;
        Assert.Empty(plan.BlockingReasons);
        // An episode's release folder is never nested inside the series folder: the episode goes to
        // its season folder (production 2026-11-21: "Show/<release folder>/" is not a Jellyfin season).
        Assert.Equal(movie ? Path.Combine(Tv, "Anime Film", "Anime Film.mkv")
            : Path.Combine(Tv, "Kusuriya no Hitorigoto", "Season 02", "Kusuriya no Hitorigoto S02E27.mkv"), plan.Entries[0].Destination);
    }

    // ---- Production report 2026-11-21: grouping must produce Jellyfin's naming layout. ----

    private (Series Series, Episode[] Episodes) Show(string folder, string name, int year, params (int Season, int Number, string RelativePath)[] episodes)
    {
        var series = new Series { Id = Guid.NewGuid(), Name = name, PremiereDate = new DateTime(year, 1, 1), Path = Directory.CreateDirectory(Path.Combine(Tv, folder)).FullName };
        var items = episodes.Select(episode => new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, SeriesName = name, ParentIndexNumber = episode.Season,
            IndexNumber = episode.Number, Path = Write(Path.Combine(series.Path, episode.RelativePath)) }).ToArray();
        _items.Add(series);
        _items.AddRange(items);
        return (series, items);
    }

    [Fact]
    public async Task EpisodesNestedInAReleaseFolderInsideTheirSeriesAreRegroupedIntoSeasons()
    {
        var release = "www.UIndex.org    -    Rick and Morty S06E04 1080p 10bit WEBRip 6CH x265 HEVC-PSA";
        var (_, episodes) = Show("Rick and Morty (2013)", "Rick and Morty", 2013,
            (6, 4, Path.Combine(release, "Rick and Morty S06E04.mkv")),
            (6, 5, Path.Combine("Season 06", "Rick and Morty S06E05.mkv")));
        Write(Path.Combine(Tv, "Rick and Morty (2013)", release, "Torrent Downloaded From UIndex.org.txt"), "notes");

        var plan = MediaMovePlan.ReadIssue(Assert.Single(await Group()))!;
        Assert.Empty(plan.BlockingReasons);
        Assert.Equal(episodes[0].Path, plan.Entries[0].Source);
        Assert.Equal(Path.Combine(Tv, "Rick and Morty (2013)", "Season 06", "Rick and Morty S06E04.mkv"), plan.Entries[0].Destination);
        Assert.DoesNotContain(plan.Entries, entry => entry.IsDirectory);
    }

    [Fact]
    public async Task RegroupedEpisodesJoinAnExistingUnpaddedSeasonFolder()
    {
        // Rick and Morty (2013) already has "Season 9"; a regrouped S09 episode must not create "Season 09".
        Show("Rick and Morty (2013)", "Rick and Morty", 2013,
            (9, 10, Path.Combine("Season 9", "Rick and Morty S09E10.mkv")),
            (9, 5, Path.Combine("www.UIndex.org    -    Rick.and.Morty.S09E05.MULTI.1080p.WEB.H264-HiggsBoson", "rick.and.morty.s09e05.mkv")),
            (8, 10, Path.Combine("www.UIndex.org    -    Rick and Morty S08E10 Hot Rick 1080p AMZN WEB-DL DDP5 1 H 264-NTb", "Rick and Morty S08E10.mkv")));
        Directory.CreateDirectory(Path.Combine(Tv, "Rick and Morty (2013)", "Season 8"));

        var destinations = (await Group()).Select(issue => MediaMovePlan.ReadIssue(issue)!)
            .Select(plan => { Assert.Empty(plan.BlockingReasons); return plan.Entries[0].Destination; }).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal([Path.Combine(Tv, "Rick and Morty (2013)", "Season 8", "Rick and Morty S08E10.mkv"),
            Path.Combine(Tv, "Rick and Morty (2013)", "Season 9", "rick.and.morty.s09e05.mkv")], destinations);
    }

    [Fact]
    public async Task AnEpisodeWhoseNameAnEarlierMoveRemovedIsIdentifiedFromMoveHistoryAndRenamed()
    {
        // Production 2026-11-22: legacy grouping moved ".../<release S09E05>/<inner release>/info" to
        // "Rick and Morty (2013)/info" and orphan cleanup recycled the release folder; "info.mkv" was
        // left with no season or episode anywhere on disk.
        var show = Path.Combine(Tv, "Rick and Morty (2013)");
        var release = "Rick.and.Morty.S09E05.Jer.Bud.1080p.AMZN.WEB-DL.DDP5.1.H.264-FLUX";
        var series = new Series { Id = Guid.NewGuid(), Name = "Rick and Morty", PremiereDate = new DateTime(2013, 1, 1), Path = Directory.CreateDirectory(show).FullName };
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, SeriesName = "Rick and Morty", Path = Write(Path.Combine(show, "info", "info.mkv")) };
        Write(Path.Combine(show, "info", "info.nfo"), "<episodedetails><title>info</title></episodedetails>");
        Write(Path.Combine(show, "info", "info-thumb.jpg"), "thumb");
        Write(Path.Combine(show, "info", "info.trickplay", "320 - 10x10", "0.jpg"), "tiles");
        Write(Path.Combine(show, "info", "season.nfo"), "<season />");
        Write(Path.Combine(show, "Season 9", "Rick and Morty S09E10.mkv"), "other episode");
        _items.AddRange([series, episode]);
        _db.History.AddHistory(new HistoryEntry { Type = IssueType.Ungrouped, Path = "legacy", FixedAtUtc = DateTime.UtcNow, Success = true,
            Action = "grouped " + Path.Combine(Tv, "Rick and Morty", "www.UIndex.org    -    Rick and Morty S09E05 Jer Bud 1080p AMZN WEB-DL DDP5 1 H 264-FLUX", release, "info")
                + " \u2192 " + Path.Combine(show, "info") });
        var manager = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)manager).Call = (method, args) => method.Name == "GetItemById"
            ? _items.FirstOrDefault(item => item.Id == (Guid)args![0]!) : null;
        var planner = new MediaMovePlanner(_guard, new ProtectionService(_db.Protection, manager), manager, _db.Moves, history: _db.History);

        var plan = MediaMovePlan.ReadIssue(Assert.Single(await planner.ScanAsync(IssueType.Ungrouped, _items, _config, new Progress<double>(), default)))!;
        Assert.Empty(plan.BlockingReasons);
        var season9 = Path.Combine(show, "Season 9");
        var moved = plan.Entries.ToDictionary(entry => Path.GetFileName(entry.Source), entry => entry.Destination);
        Assert.Equal(Path.Combine(season9, release + ".mkv"), moved["info.mkv"]);
        // Jellyfin's NFO from while the episode was unidentified carries the wrong title; it stays behind.
        Assert.False(moved.ContainsKey("info.nfo"));
        Assert.Equal(Path.Combine(season9, release + "-thumb.jpg"), moved["info-thumb.jpg"]);
        Assert.Equal(Path.Combine(season9, release + ".trickplay"), moved["info.trickplay"]);
        Assert.False(moved.ContainsKey("season.nfo"));

        // Without the move record the episode stays unidentified and is never moved on a guess.
        var blind = new MediaMovePlanner(_guard, new ProtectionService(_db.Protection, manager), manager, _db.Moves);
        Assert.Contains(MediaMovePlan.ReadIssue(Assert.Single(await blind.ScanAsync(IssueType.Ungrouped, _items, _config, new Progress<double>(), default)))!.BlockingReasons,
            reason => reason.Contains("season is unknown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEpisodeNumberedOnlyByItsReleaseFolderKeepsItsNumberWhenMovedOut()
    {
        Show("Show (2020)", "Show", 2020, (1, 2, Path.Combine("Show.S01E02.1080p.WEB-GRP", "video.mkv")));
        Write(Path.Combine(Tv, "Show (2020)", "Show.S01E02.1080p.WEB-GRP", "video.en.srt"), "subtitles");
        var plan = MediaMovePlan.ReadIssue(Assert.Single(await Group()))!;
        Assert.Empty(plan.BlockingReasons);
        var moved = plan.Entries.ToDictionary(entry => Path.GetFileName(entry.Source), entry => entry.Destination);
        Assert.Equal(Path.Combine(Tv, "Show (2020)", "Season 01", "Show.S01E02.1080p.WEB-GRP.mkv"), moved["video.mkv"]);
        Assert.Equal(Path.Combine(Tv, "Show (2020)", "Season 01", "Show.S01E02.1080p.WEB-GRP.en.srt"), moved["video.en.srt"]);
    }

    [Fact]
    public void MoveHistoryMapsAPathBackThroughChainedMoves()
    {
        var root = Path.GetPathRoot(Tv)!;
        string P(params string[] parts) => Path.Combine([root, .. parts]);
        var moves = MoveProvenance.ParseGrouped([
            "grouped " + P("tv", "A", "S01E01 release", "x") + " \u2192 " + P("tv", "B", "x"),
            "grouped " + P("tv", "B") + " \u2192 " + P("tv", "C"),
            "moved something else",
        ]);
        Assert.Equal(2, moves.Count);
        Assert.Equal([P("tv", "B", "x", "f.mkv"), P("tv", "A", "S01E01 release", "x", "f.mkv")], MoveProvenance.FormerPaths(P("tv", "C", "x", "f.mkv"), moves));
        Assert.Equal((1, 1, "S01E01 release"), MoveProvenance.FindEpisodeIdentity(P("tv", "C", "x", "f.mkv"), [P("tv")], moves));
        Assert.False(MoveProvenance.HasEpisodeMarker("H.264 1920x1080 x265"));
    }

    [Fact]
    public async Task AnEpisodeWithoutAJellyfinSeasonUsesTheSeasonInItsFilename()
    {
        // Production 2026-11-22: blocked as "The season is unknown" though the name says S04E01.
        var series = new Series { Id = Guid.NewGuid(), Name = "Yellowstone", PremiereDate = new DateTime(2018, 1, 1),
            Path = Directory.CreateDirectory(Path.Combine(Tv, "Yellowstone (2018)")).FullName };
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, SeriesName = "Yellowstone",
            Path = Write(Path.Combine(series.Path, "Yellowstone 2018 S04E01 Half The Money 1080p REPACK AMZN WEB-DL AAC5 1 H 26.mkv")) };
        _items.AddRange([series, episode]);

        var plan = MediaMovePlan.ReadIssue(Assert.Single(await Group()))!;
        Assert.Empty(plan.BlockingReasons);
        Assert.Equal(Path.Combine(series.Path, "Season 04", Path.GetFileName(episode.Path)), plan.Entries[0].Destination);
    }

    [Fact]
    public async Task ASeasonTheUserEntersPlacesAnEpisodeNothingElseCouldPlace()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Show", PremiereDate = new DateTime(2020, 1, 1),
            Path = Directory.CreateDirectory(Path.Combine(Tv, "Show (2020)")).FullName };
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, SeriesName = "Show", IndexNumber = 4,
            Path = Write(Path.Combine(series.Path, "Unlabelled Episode.mkv")) };
        _items.AddRange([series, episode]);
        var manager = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)manager).Call = (method, args) => method.Name == "GetItemById"
            ? _items.FirstOrDefault(item => item.Id == (Guid)args![0]!) : null;
        var planner = new MediaMovePlanner(_guard, new ProtectionService(_db.Protection, manager), manager, _db.Moves, state: _db);

        var blocked = MediaMovePlan.ReadIssue(Assert.Single(await planner.ScanAsync(IssueType.Ungrouped, _items, _config, new Progress<double>(), default)))!;
        Assert.Contains(blocked.BlockingReasons, reason => reason.StartsWith("The season is unknown", StringComparison.Ordinal));

        _db.SetState(MediaMovePlanner.SeasonOverrideKey(episode.Id), "3");
        var planned = await planner.PlanItemAsync(IssueType.Ungrouped, _items, _config, episode.Id, default);
        var plan = MediaMovePlan.ReadIssue(Assert.IsType<Issue>(planned))!;
        Assert.Empty(plan.BlockingReasons);
        Assert.Equal(Path.Combine(series.Path, "Season 03"), Path.GetDirectoryName(plan.Entries[0].Destination));
        Assert.Equal(episode.Id, plan.ItemId);
    }

    [Fact]
    public async Task EpisodesLooseInTheirSeriesFolderMoveIntoSeasonFolders()
    {
        Show("The New Pope (2020)", "The New Pope", 2020,
            (1, 1, "The New Pope 2020 S01E01 1080p WEB-DL.mkv"), (1, 2, "The New Pope 2020 S01E02 1080p WEB-DL.mkv"));

        var issues = await Group();
        Assert.Equal(2, issues.Count);
        Assert.All(issues, issue =>
        {
            var plan = MediaMovePlan.ReadIssue(issue)!;
            Assert.Empty(plan.BlockingReasons);
            Assert.Equal(Path.Combine(Tv, "The New Pope (2020)", "Season 01"), Path.GetDirectoryName(plan.Entries[0].Destination));
        });
    }

    [Fact]
    public async Task ASeriesFolderWithLooseEpisodesIsRestructuredRatherThanRenamedWhole()
    {
        // "Avatar - Seven Havens/<episodes>" was renamed whole to "Avatar Seven Havens (2026)/<episodes>",
        // carrying the loose layout along.
        var (series, _) = Show("Avatar - Seven Havens", "Avatar Seven Havens", 2026,
            (1, 1, "Avatar.Seven.Havens.S01E01.1080p.WEBRip.x265-NeoNoir.mkv"),
            (1, 2, "Avatar.Seven.Havens.S01E02.1080p.WEBRip.x265-NeoNoir.mkv"));

        var issues = await Group();
        Assert.Equal(2, issues.Count);
        Assert.All(issues, issue =>
        {
            var plan = MediaMovePlan.ReadIssue(issue)!;
            Assert.Empty(plan.BlockingReasons);
            Assert.NotEqual(series.Path, plan.Entries[0].Source);
            Assert.Equal(Path.Combine(Tv, "Avatar Seven Havens (2026)", "Season 01"), Path.GetDirectoryName(plan.Entries[0].Destination));
        });
    }

    [Theory]
    [InlineData("Season 01")]
    [InlineData("Season 1")]
    public async Task EpisodesAlreadyInTheirSeasonFolderAreLeftAlone(string season)
    {
        Show("Show (2005)", "Show", 2005, (1, 1, Path.Combine(season, "Show S01E01.mkv")), (1, 2, Path.Combine(season, "Show S01E02.mkv")));
        Assert.Empty(await Group());
    }

    [Fact]
    public async Task AnEpisodeInTheWrongSeasonFolderMovesToItsOwnSeason()
    {
        Show("Show (2005)", "Show", 2005, (2, 1, Path.Combine("Season 01", "Show S02E01.mkv")));
        var plan = MediaMovePlan.ReadIssue(Assert.Single(await Group()))!;
        Assert.Equal(Path.Combine(Tv, "Show (2005)", "Season 02", "Show S02E01.mkv"), plan.Entries[0].Destination);
    }

    private Movie Feature(string name, int year, string relativePath)
    {
        var item = new Movie { Id = Guid.NewGuid(), Name = name, ProductionYear = year, Path = Write(Path.Combine(Movies, relativePath)) };
        _items.Add(item);
        return item;
    }

    [Fact]
    public async Task MovieFoldersWrappedInAFranchiseFolderAreUnwrapped()
    {
        Feature("Super Troopers", 2001, Path.Combine("Super Troopers", "Super Troopers (2001)", "Super Troopers (2001).mkv"));
        Feature("Super Troopers 2", 2018, Path.Combine("Super Troopers", "Super Troopers 2 (2018)", "Super Troopers 2 (2018).mkv"));

        var issues = await Group();
        Assert.Equal(2, issues.Count);
        var destinations = issues.Select(issue => MediaMovePlan.ReadIssue(issue)!).Select(plan =>
        {
            Assert.Empty(plan.BlockingReasons);
            Assert.True(plan.Entries[0].IsDirectory);
            return plan.Entries[0].Destination;
        }).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal([Path.Combine(Movies, "Super Troopers (2001)"), Path.Combine(Movies, "Super Troopers 2 (2018)")], destinations);
    }

    [Fact]
    public async Task AWrapperWithoutAYearIsReplacedByTitleAndYear()
    {
        Feature("28 Days Later", 2002, Path.Combine("28 Days Later", "28.Days.Later.2002.1080p.BluRay-GRP", "28.Days.Later.2002.1080p.BluRay-GRP.mkv"));
        var plan = MediaMovePlan.ReadIssue(Assert.Single(await Group()))!;
        Assert.Equal(Path.Combine(Movies, "28 Days Later (2002)"), plan.Entries[0].Destination);
    }

    [Fact]
    public async Task AReleaseFolderInsideTheMoviesOwnFolderMovesItsVideoUp()
    {
        var film = Feature("Sorry, Baby", 2025, Path.Combine("Sorry, Baby (2025)", "Sorry.Baby.2025.Hybrid.2160p.WEB-DL-AOC", "Sorry.Baby.2025.2160p-AOC.mkv"));
        var plan = MediaMovePlan.ReadIssue(Assert.Single(await Group()))!;
        Assert.Empty(plan.BlockingReasons);
        Assert.Equal(film.Path, plan.Entries[0].Source);
        Assert.Equal(Path.Combine(Movies, "Sorry, Baby (2025)", "Sorry.Baby.2025.2160p-AOC.mkv"), plan.Entries[0].Destination);
    }

    [Fact]
    public async Task AReleaseNamedMovieFolderIsRenamedToTitleAndYear()
    {
        Feature("Wrath of Man", 2021, Path.Combine("Wrath.of.Man.2021.1080p.Remux.AVC.DTS-HD.MA.5.1-playBD", "Wrath.of.Man.2021.1080p.Remux.mkv"));
        var plan = MediaMovePlan.ReadIssue(Assert.Single(await Group()))!;
        Assert.Equal(Path.Combine(Movies, "Wrath of Man (2021)"), plan.Entries[0].Destination);
    }

    [Theory]
    [InlineData("Film (2020)")]
    [InlineData("Film (2020) [tmdbid-123]")]
    public async Task ACanonicalMovieFolderIsLeftAlone(string folder)
    {
        Feature("Film", 2020, Path.Combine(folder, "Film (2020).mkv"));
        Assert.Empty(await Group());
    }

    [Fact]
    public async Task GroupingReportsActivityWithoutReadingALargeFile()
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
            // Identity-only planning never reads content, so each file reports complete at once.
            Assert.Contains(updates, update => update.Path == film.Path && update.Percent < 90 && update.Label?.Contains("100% of file", StringComparison.Ordinal) == true);
            Assert.DoesNotContain(updates, update => update.Label?.Contains("33% of file", StringComparison.Ordinal) == true);
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

    // Production 2026-11-22: "Sinners 2025" was filed as an episode of "Sinners (2016)" and later renamed
    // "Saints & Sinners S01E2025" into Season 01. Misplaced files moves it to Movies under the film's name
    // and leaves the season's own files behind.
    [Fact]
    public async Task FilmFiledAsAnEpisodeIsSortedToMovies()
    {
        var showFolder = Path.Combine(Tv, "Sinners (2016)");
        var series = new Series { Id = Guid.NewGuid(), Name = "Saints & Sinners", Path = showFolder, ProductionYear = 2016 };
        var file = Write(Path.Combine(showFolder, "www.UIndex.org    -    Sinners 2025 1080p WEBRip DDP Atmos 5 1 10bit H 265-iVy", "Sinners 2025 1080p.mkv"));
        Write(Path.Combine(Path.GetDirectoryName(file)!, "tvshow.nfo"), "release metadata");
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, SeriesName = "Saints & Sinners", Path = file, RunTimeTicks = TimeSpan.FromMinutes(137).Ticks };
        _items.AddRange([series, episode]);

        var issue = Assert.Single(await Sort());
        var plan = Assert.IsType<MediaMovePlan>(MediaMovePlan.ReadIssue(issue));
        Assert.Empty(plan.BlockingReasons);
        var entry = Assert.Single(plan.Entries);
        Assert.Equal(Path.Combine(Movies, "Sinners (2025)", "Sinners (2025).mkv"), entry.Destination);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupingIntoExistingSeriesPlansIndependentEpisodeBundles(bool collision)
    {
        var target = Path.Combine(Tv, "MobLand (2025)", "Season 02");
        Write(Path.Combine(target, "MobLand.S02E01.mkv"), "existing episode");
        Write(Path.Combine(Tv, "MobLand (2025)", "tvshow.nfo"), "existing metadata");
        for (var number = 2; number <= 3; number++)
        {
            var source = Path.Combine(Tv, "MobLand release " + number);
            var series = new Series { Id = Guid.NewGuid(), Name = "MobLand", Path = source, PremiereDate = new DateTime(2025, 1, 1) };
            var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, SeriesName = "MobLand", ParentIndexNumber = 2,
                IndexNumber = number, Path = Write(Path.Combine(source, "MobLand.S02E0" + number + ".mkv")) };
            Write(Path.ChangeExtension(episode.Path, ".en.srt"), "subtitles");
            Write(Path.Combine(source, "tvshow.nfo"), "source metadata");
            _items.AddRange([series, episode]);
        }

        if (collision) Write(Path.Combine(target, "MobLand.S02E02.en.srt"), "existing subtitle");
        var issues = await Group();
        Assert.Equal(2, issues.Count);
        foreach (var issue in issues)
        {
            var plan = Assert.IsType<MediaMovePlan>(MediaMovePlan.ReadIssue(issue));
            Assert.True(plan.HasValidDefinition());
            Assert.Equal(2, plan.Entries.Length);
            Assert.All(plan.Entries, entry =>
            {
                Assert.False(entry.IsDirectory);
                Assert.Equal(target, Path.GetDirectoryName(entry.Destination));
            });
            Assert.DoesNotContain(plan.BlockingReasons, reason => reason.Contains("overlaps", StringComparison.Ordinal));
            if (collision && issue.Path.EndsWith("02.mkv", StringComparison.Ordinal))
                Assert.Contains(plan.BlockingReasons, reason => reason.Contains("already occupied", StringComparison.Ordinal));
            else
                Assert.Empty(plan.BlockingReasons);
        }
    }

    private static JsonElement Details(Issue issue) => JsonDocument.Parse(issue.DetailsJson).RootElement.Clone();

    [Fact]
    public async Task SameNamedSeriesFromDifferentYearsKeepTheirOwnMissingArtwork()
    {
        foreach (var year in new[] { 2020, 2025 })
        {
            Write(Path.Combine(Tv, "MobLand (" + year + ")", "tvshow.nfo"), "destination metadata");
            var folder = Path.Combine(Tv, "MobLand release " + year);
            var series = new Series { Id = Guid.NewGuid(), Name = "MobLand", Path = folder, PremiereDate = new DateTime(year, 1, 1) };
            var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, SeriesName = "MobLand", ParentIndexNumber = 1,
                Path = Write(Path.Combine(folder, "MobLand.S01E01.mkv")) };
            Write(Path.Combine(folder, "logo.png"), "art " + year);
            _items.AddRange([series, episode]);
        }

        var findings = await Group();
        Assert.Equal(2, findings.Count);
        foreach (var issue in findings)
        {
            var plan = MediaMovePlan.ReadIssue(issue)!;
            Assert.Empty(plan.BlockingReasons);
            Assert.Equal(2, plan.Entries.Length);
            Assert.EndsWith("logo.png", plan.Entries[1].Destination, StringComparison.Ordinal);
            Assert.Contains(issue.Path.Contains("2020", StringComparison.Ordinal) ? "2020" : "2025", plan.Entries[1].Destination, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task JoiningExistingSeriesStillBlocksTwoProposalsForTheSameFile()
    {
        Directory.CreateDirectory(Path.Combine(Tv, "MobLand (2025)"));
        for (var index = 0; index < 2; index++)
        {
            var source = Path.Combine(Tv, "MobLand release " + index);
            var series = new Series { Id = Guid.NewGuid(), Name = "MobLand", Path = source, PremiereDate = new DateTime(2025, 1, 1) };
            _items.AddRange([series, new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, SeriesName = "MobLand", ParentIndexNumber = 2,
                Path = Write(Path.Combine(source, "MobLand.S02E02.mkv")) }]);
        }

        var issues = await Group();
        Assert.Equal(2, issues.Count);
        Assert.All(issues, issue => Assert.Contains(MediaMovePlan.ReadIssue(issue)!.BlockingReasons,
            reason => reason.Contains("overlaps", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JoiningExistingSeriesStillBlocksConflictingProviderIdentities(bool destinationConflict)
    {
        var target = Path.Combine(Tv, "MobLand (2025)");
        Directory.CreateDirectory(target);
        for (var index = 0; index < 2; index++)
        {
            var source = destinationConflict && index == 1 ? target : Path.Combine(Tv, "MobLand release " + index);
            var series = new Series { Id = Guid.NewGuid(), Name = "MobLand", Path = source, PremiereDate = new DateTime(2025, 1, 1) };
            series.ProviderIds["Tvdb"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _items.Add(series);
            if (destinationConflict && index == 1) continue;
            _items.Add(new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, SeriesName = "MobLand", ParentIndexNumber = 2,
                Path = Write(Path.Combine(source, "MobLand.S02E0" + index + ".mkv")) });
        }

        var issues = await Group();
        Assert.NotEmpty(issues);
        Assert.All(issues, issue => Assert.True(issue.HasBlockingWarnings, issue.DetailsJson));
    }

    [Fact]
    public async Task GroupingVisitsASeriesPayloadOnceForAllItsEpisodes()
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
                if (Plugin.CurrentActivityLabel?.Contains("; 100% of file", StringComparison.Ordinal) == true)
                {
                    readPaths.Add(Plugin.CurrentActivity);
                }
            }));
            var plan = MediaMovePlan.ReadIssue(Assert.Single(issues))!;
            Assert.Empty(plan.BlockingReasons);
            // A progress tick can repeat the last file's label, so count distinct files visited.
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

    [Fact]
    public async Task AnNfoJellyfinRewritesForAMovedSiblingDoesNotRefuseTheRemainingEpisodes()
    {
        // Production 2026-11-22: episodes left "The Office (2005)/The Office (US)/" one by one and Jellyfin,
        // still indexing the old paths, wrote "<moved episode>.nfo" back into that folder. Every remaining
        // episode was then refused as "changed after approval".
        _config.UngroupedFixMode = FixMode.ManualApprove;
        _config.DryRun = false;
        var (_, episodes) = Show("The Office (2005)", "The Office", 2005,
            (2, 1, Path.Combine("The Office (US)", "The.Office.US.S02E01.mkv")),
            (2, 2, Path.Combine("The Office (US)", "The.Office.US.S02E02.mkv")));
        foreach (var episode in episodes)
            Write(Path.ChangeExtension(episode.Path, ".nfo"), "<episodedetails />");
        Directory.CreateDirectory(Path.Combine(Tv, "The Office (2005)", "Season 02"));
        var found = await Group();
        Assert.Equal(2, found.Count);
        _db.Issues.ReplaceDetectedIssues(IssueType.Ungrouped, found);
        _db.Issues.QueueDetectedIssues(IssueType.Ungrouped);
        var queued = _db.Issues.GetIssues(IssueType.Ungrouped).OrderBy(issue => issue.Path, StringComparer.Ordinal).ToArray();

        var first = await MoveService().ExecuteAsync(queued[0], null, CancellationToken.None);
        Assert.True(first.Success, first.Message);
        Assert.False(File.Exists(episodes[0].Path));
        Write(Path.ChangeExtension(episodes[0].Path, ".nfo"), "<episodedetails>rewritten at the old path</episodedetails>");

        var second = await MoveService().ExecuteAsync(queued[1], null, CancellationToken.None);
        Assert.True(second.Success, second.Message);
        Assert.True(File.Exists(Path.Combine(Tv, "The Office (2005)", "Season 02", "The.Office.US.S02E02.mkv")));
        Assert.True(File.Exists(Path.ChangeExtension(episodes[0].Path, ".nfo")), "An unowned file stays where it is.");
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
        Assert.Equal(Path.Combine(Movies, "The Hobbit Part 1 (2026)", "The Hobbit Part 1.mkv"), Details(issues.Single(i => i.ItemId == one.Id)).GetProperty("target").GetString());
        Assert.Equal(Path.Combine(Movies, "The Hobbit Part 2 (2026)", "The Hobbit Part 2.mkv"), Details(issues.Single(i => i.ItemId == two.Id)).GetProperty("target").GetString());
    }

    [Fact]
    public async Task AlreadyContainedCollectionFilmsDoNotGainFranchiseNesting()
    {
        Film("The Hobbit Part 1", Path.Combine(Movies, "The Hobbit Part 1", "The Hobbit Part 1.mkv"), "11");
        Film("The Hobbit Part 2", Path.Combine(Movies, "The Hobbit Part 2", "The Hobbit Part 2.mkv"), "22");

        // Each keeps its own folder directly in the library; only the missing year is added.
        var destinations = (await Group()).Select(issue => MediaMovePlan.ReadIssue(issue)!.Entries[0].Destination).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal([Path.Combine(Movies, "The Hobbit Part 1 (2026)"), Path.Combine(Movies, "The Hobbit Part 2 (2026)")], destinations);

        Directory.Move(Path.Combine(Movies, "The Hobbit Part 1"), Path.Combine(Movies, "The Hobbit Part 1 (2026)"));
        Directory.Move(Path.Combine(Movies, "The Hobbit Part 2"), Path.Combine(Movies, "The Hobbit Part 2 (2026)"));
        foreach (var film in _items.OfType<Movie>())
            film.Path = Path.Combine(Movies, film.Name + " (2026)", film.Name + ".mkv");
        Assert.Empty(await Group());
    }

    [Fact]
    public async Task ScannedApprovalIncludesEveryCompanionAndIdentityEvidence()
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
        // Scan-time plans carry identity evidence (file ID, length, modified time, metadata), not
        // content hashes: the scan must not read every payload byte.
        Assert.All(entries, entry =>
        {
            var root = entry.GetProperty("inventory")[0];
            Assert.Equal(string.Empty, root.GetProperty("sha256").GetString());
            Assert.NotEqual(0UL, root.GetProperty("fileId").GetUInt64());
            Assert.True(root.GetProperty("lastWriteUtcTicks").GetInt64() > 0);
        });
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
    public async Task ReplacedPayloadWithPreservedSizeAndTimestampChangesTheApprovalFingerprint()
    {
        // Plans bind file identity, not content: a same-size, backdated in-place edit is not seen
        // (moves are renames, so those bytes move intact), but a different file put in its place is.
        var film = Film("Film", Path.Combine(Movies, "Film.mkv"));
        var first = Details(Assert.Single(await Group()));
        Assert.True(first.TryGetProperty("movePlan", out var plan), "Approval must include payload identity evidence.");
        var fingerprint = plan.GetProperty("fingerprint").GetString();
        var modified = File.GetLastWriteTimeUtc(film.Path);
        var replacement = film.Path + ".replacement";
        File.WriteAllBytes(replacement, new byte[new FileInfo(film.Path).Length]);
        File.SetLastWriteTimeUtc(replacement, modified);
        File.Delete(film.Path);
        File.Move(replacement, film.Path);
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
        Film("Contained", Path.Combine(Movies, "Contained (2026)", "Contained.mkv"));
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
