using System;
using System.IO;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class OwnFolderFilmTests : IDisposable
{
    private readonly string _tv = Directory.CreateTempSubdirectory("md-own-folder-film-").FullName;

    public void Dispose() => Directory.Delete(_tv, recursive: true);

    private string Video(string relative)
    {
        var path = Path.Combine(_tv, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "video");
        return path;
    }

    [Fact]
    public void AFilmAloneInAFolderOfItsOwnNameIsAFilmWhateverItsRuntime()
    {
        var path = Video(Path.Combine("Movie In Tv (2018)", "Movie In Tv (2018).mkv"));
        var episode = new Episode { Name = "Movie In Tv (2018)", RunTimeTicks = TimeSpan.FromSeconds(8).Ticks };
        Assert.Equal("Movie In Tv (2018)", MediaSorterScanner.FilmIdentity(episode, path));
        Assert.True(MediaSorterScanner.LooksLikeMovieFiledAsEpisode(episode, path));
    }

    [Fact]
    public void AnEpisodeNumberThatIsTheFilmsYearStillCounts()
    {
        var path = Video(Path.Combine("Movie In Tv (2018)", "Movie In Tv (2018).mkv"));
        Assert.Equal("Movie In Tv (2018)", MediaSorterScanner.OwnFolderFilm(new Episode { IndexNumber = 2018 }, path));
    }

    [Theory]
    [InlineData("Show (2020)/Show S01E01.mkv", null, null)]
    [InlineData("Pilot/Pilot.mkv", null, null)]
    [InlineData("Movie In Tv (2018)/Movie In Tv (2018).mkv", 1, null)]
    [InlineData("Movie In Tv (2018)/Movie In Tv (2018).mkv", null, 3)]
    public void RealEpisodesAndLookAlikesAreNotFilms(string relative, int? season, int? number)
    {
        var path = Video(relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.Null(MediaSorterScanner.OwnFolderFilm(new Episode { ParentIndexNumber = season, IndexNumber = number }, path));
    }

    [Fact]
    public void AFolderWithAnotherVideoIsAShowNotAFilm()
    {
        var path = Video(Path.Combine("Movie In Tv (2018)", "Movie In Tv (2018).mkv"));
        Video(Path.Combine("Movie In Tv (2018)", "Extras", "Behind the scenes.mkv"));
        Assert.Null(MediaSorterScanner.OwnFolderFilm(new Episode(), path));
    }
}
