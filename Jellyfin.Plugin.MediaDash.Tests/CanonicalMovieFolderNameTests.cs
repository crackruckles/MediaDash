using Jellyfin.Plugin.MediaDash.Fixers;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class CanonicalMovieFolderNameTests
{
    [Theory]
    [InlineData("Format HEVC Main 10 (2024)", "Format HEVC Main 10 (2024)", 2024, "Format HEVC Main 10 (2024)")]
    [InlineData("Brackets & Co [2022] $money", "Brackets & Co [2022] $money", 2022, "Brackets & Co [2022] $money")]
    [InlineData("Some.Movie.2019.1080p.WEB-DL.x264-GRP", "Some Movie", 2019, "Some Movie (2019)")]
    [InlineData("www.UIndex.org - Some Movie (2019)", "Some Movie (2019)", 2019, "Some Movie (2019)")]
    [InlineData("Super Troopers 2 (2018)", "Super Troopers 2", 2018, "Super Troopers 2 (2018)")]
    public void KeepsDeliberateNamesAndNeverDoublesTheYear(string current, string name, int year, string expected)
        => Assert.Equal(expected, MediaMovePlanner.CanonicalMovieFolderName(new Movie { Name = name, ProductionYear = year }, current));
}
