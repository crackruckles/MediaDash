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

    [Fact]
    public void Classify_AudioItem_ReturnsMusicKind()
    {
        var kind = MediaSorterScanner.ClassifyByBaseItemKind(Jellyfin.Data.Enums.BaseItemKind.Audio);
        Assert.Equal(MediaSorterScanner.MediaKind.Music, kind);
    }

    [Fact]
    public void Classify_AudioBook_ReturnsAudioBookKind()
    {
        var kind = MediaSorterScanner.ClassifyByBaseItemKind(Jellyfin.Data.Enums.BaseItemKind.AudioBook);
        Assert.Equal(MediaSorterScanner.MediaKind.AudioBook, kind);
    }

    [Fact]
    public void Classify_Book_ReturnsBookKind()
    {
        var kind = MediaSorterScanner.ClassifyByBaseItemKind(Jellyfin.Data.Enums.BaseItemKind.Book);
        Assert.Equal(MediaSorterScanner.MediaKind.Book, kind);
    }

    [Fact]
    public void Classify_UnrecognisedKind_ReturnsNull()
    {
        // Sanity check: an unknown kind (e.g. Photo) returns null, doesn't crash the classifier.
        var kind = MediaSorterScanner.ClassifyByBaseItemKind(Jellyfin.Data.Enums.BaseItemKind.Photo);
        Assert.Null(kind);
    }
}
