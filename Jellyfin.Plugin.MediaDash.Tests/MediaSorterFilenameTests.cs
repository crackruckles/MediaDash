using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class MediaSorterFilenameTests
{
    [Theory]
    [InlineData("The Office S03E14.mkv")]
    [InlineData("the.office.s03e14.720p.mkv")]
    [InlineData("Show Name - 2x05 - Episode Title.mkv")]
    [InlineData("Show 12x115.mkv")]
    public void FilenameHeuristic_MatchesTvPatterns(string filename)
    {
        Assert.Equal(MediaSorterScanner.MediaKind.Tv, MediaSorterScanner.ClassifyFilename(filename));
    }

    [Theory]
    [InlineData("Blade Runner (1982).mkv")]
    [InlineData("Inception.2010.1080p.mkv")]
    [InlineData("Whatever.mkv")]
    public void FilenameHeuristic_FallsBackToMovie(string filename)
    {
        Assert.Equal(MediaSorterScanner.MediaKind.Movie, MediaSorterScanner.ClassifyFilename(filename));
    }

    [Fact]
    public void FilenameHeuristic_EmptyIsNull()
    {
        Assert.Null(MediaSorterScanner.ClassifyFilename(string.Empty));
        Assert.Null(MediaSorterScanner.ClassifyFilename("   "));
    }
}
