using Jellyfin.Plugin.MediaDash.Fixers;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class RenameTemplateTests
{
    [Theory]
    [InlineData("Blade Runner", "Blade Runner")]
    [InlineData("Blade: Runner", "Blade Runner")]
    [InlineData("Path/With\\Sep|<Chars>", "PathWithSepChars")]
    [InlineData("  Trim  ", "Trim")]
    [InlineData("Multiple   spaces", "Multiple spaces")]
    [InlineData("Trailing dots...", "Trailing dots")]
    [InlineData("", "Untitled")]
    [InlineData("///", "Untitled")]
    public void Scrub_HandlesForbiddenAndEdgeCases(string input, string expected)
    {
        Assert.Equal(expected, RenameTemplate.Scrub(input));
    }

    [Theory]
    // Sonarr / Radarr default: tag lives in the basename between the year and the quality bracket.
    [InlineData("/media/Movie Name (2020) [tmdbid-12345]/Movie Name (2020) [tmdbid-12345].mkv", "tmdbid", " [tmdbid-12345]")]
    [InlineData("/media/Movie (2020) [tmdbid-99999] [Bluray-1080p][DTS 5.1][H264].mkv", "tmdbid", " [tmdbid-99999]")]
    [InlineData("/tv/Show/Season 01/Show S01E01 [tvdbid-11111].mkv", "tvdbid", " [tvdbid-11111]")]
    // Case-insensitive tag name — some tooling writes TMDBID uppercase.
    [InlineData("/media/Movie (2020) [TMDBID-42].mkv", "tmdbid", " [tmdbid-42]")]
    public void BuildIdTag_ExtractsTagFromSourceFilename(string sourcePath, string tagName, string expected)
    {
        var provider = tagName == "tmdbid" ? MetadataProvider.Tmdb : MetadataProvider.Tvdb;
        Assert.Equal(expected, RenameTemplate.BuildIdTag(tagName, sourcePath, item: null, provider));
    }

    [Fact]
    public void BuildIdTag_ReturnsEmptyWhenSourceHasNoTagAndNoItem()
    {
        Assert.Equal(string.Empty, RenameTemplate.BuildIdTag("tmdbid", "/media/Untagged Movie (2020).mkv", item: null, MetadataProvider.Tmdb));
    }

    [Fact]
    public void BuildIdTag_IgnoresUnrelatedTags()
    {
        // A filename carrying only imdbid must not spoof a tmdbid.
        Assert.Equal(string.Empty, RenameTemplate.BuildIdTag("tmdbid", "/media/Movie (2020) [imdbid-tt1234567].mkv", item: null, MetadataProvider.Tmdb));
    }

    [Fact]
    public void BuildIdTag_HandlesNullOrEmptySource()
    {
        Assert.Equal(string.Empty, RenameTemplate.BuildIdTag("tmdbid", null, item: null, MetadataProvider.Tmdb));
        Assert.Equal(string.Empty, RenameTemplate.BuildIdTag("tmdbid", string.Empty, item: null, MetadataProvider.Tmdb));
    }
}
