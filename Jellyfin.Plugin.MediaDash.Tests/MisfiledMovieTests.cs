using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Production 2026-11-22 leftovers: a film filed as an episode (Sinners) and two releases of one film
// in different folders (Mr. Bean's Holiday) that the duplicate scanner split into separate editions.
public sealed class MisfiledMovieTests
{
    private const string Release = "/mnt/media/Complete/TV/Sinners (2016)/www.UIndex.org    -    Sinners 2025 1080p WEBRip DDP Atmos 5 1 10bit H 265-iVy";

    [Fact]
    public void FilmInsideAShowFolderIsAMovie()
    {
        var episode = new Episode { Id = Guid.NewGuid(), Path = Release + "/Sinners 2025 1080p.mkv" };
        Assert.Equal("Sinners (2025)", MediaSorterScanner.FilmIdentity(episode, episode.Path));
        Assert.Equal(MediaSorterScanner.MediaKind.Movie, MediaSorterScanner.ClassifyItem(episode, episode.Path, default));
    }

    [Fact]
    public void FilmRenamedWithItsYearAsAnEpisodeNumberIsRecoveredFromTheMoveRecord()
    {
        var path = "/mnt/media/Complete/TV/Sinners (2016)/Season 01/Saints & Sinners S01E2025.mkv";
        var episode = new Episode { Id = Guid.NewGuid(), Path = path, ParentIndexNumber = 1, IndexNumber = 2025, RunTimeTicks = TimeSpan.FromMinutes(137).Ticks };
        Assert.Equal("Sinners (2025)", MediaSorterScanner.FilmIdentity(episode, path, [Release + "/Sinners 2025 1080p.mkv"]));
    }

    [Theory]
    [InlineData("/tv/Sinners (2016)/Season 01/Sinners S01E03.mkv", 1, 3)]
    [InlineData("/tv/Sinners (2016)/Sinners.S01E03.720p-GRP/Sinners 2016 720p.mkv", null, null)]
    [InlineData("/tv/Sinners (2016)/Sinners 2016 special.mkv", null, null)]
    [InlineData("/tv/Doctor Who (2005)/Doctor Who 2023 Special.mkv", 0, null)]
    public void RealEpisodesStayEpisodes(string path, int? season, int? number)
    {
        var episode = new Episode { Id = Guid.NewGuid(), Path = path, ParentIndexNumber = season, IndexNumber = number };
        Assert.Null(MediaSorterScanner.FilmIdentity(episode, path));
    }

    [Fact]
    public void ShortItemIsNotAFilm()
    {
        var episode = new Episode { Id = Guid.NewGuid(), Path = Release + "/Sinners 2025 1080p.mkv", RunTimeTicks = TimeSpan.FromMinutes(22).Ticks };
        Assert.Null(MediaSorterScanner.FilmIdentity(episode, episode.Path));
    }

    [Fact]
    public void TwoReleasesOfOneFilmInDifferentFoldersAreOneEdition()
    {
        var keeper = new Movie { Id = Guid.NewGuid(), Path = "/movies/Mr. Bean's Holiday/Mr. Bean's Holiday (2007)/Mr Beans Holiday 2007 PROPER BluRay 1080p DD 5 1 x264-BHDStudio.mkv" };
        var other = new Movie { Id = Guid.NewGuid(), Path = "/movies/Mr. Bean's Holiday/Mr Beans Holiday 2007 1080p BluRay HEVC x265 5.1 BONE/Mr Beans Holiday 2007 1080p BluRay HEVC x265 5.1 BONE.mkv" };
        var group = new List<(BaseItem Item, string Path)> { (keeper, keeper.Path), (other, other.Path) };
        Assert.Equal(DuplicateScanner.MovieEdition(keeper.Path), DuplicateScanner.MovieEdition(other.Path));
        Assert.Single(DuplicateScanner.SplitByEdition("movie:tmdb:1268", group));
    }

    [Fact]
    public void DifferentCutsAndResolutionsOfAFilmStaySeparate()
    {
        var cinema = new Movie { Id = Guid.NewGuid(), Path = "/movies/Film (2001)/Film 2001 1080p BluRay x264-GRP.mkv" };
        var directors = new Movie { Id = Guid.NewGuid(), Path = "/movies/Film (2001)/Film 2001 Directors Cut 1080p BluRay x264-GRP.mkv" };
        var uhd = new Movie { Id = Guid.NewGuid(), Path = "/movies/Film (2001)/Film 2001 2160p UHD BluRay x265-GRP.mkv" };
        var group = new List<(BaseItem Item, string Path)> { (cinema, cinema.Path), (directors, directors.Path), (uhd, uhd.Path) };
        Assert.All(DuplicateScanner.SplitByEdition("movie:tmdb:1", group), edition => Assert.Single(edition));
    }
}
