using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class MediaGrouperTests
{
    [Theory]
    [InlineData("Iron Man", "Iron Man")]
    [InlineData("Iron Man 2", "Iron Man")]
    [InlineData("Iron Man 3", "Iron Man")]
    [InlineData("Iron Man III", "Iron Man")]
    [InlineData("Scary Movie", "Scary Movie")]
    [InlineData("Scary Movie 2", "Scary Movie")]
    [InlineData("Interstellar (2014)", "Interstellar")]
    [InlineData("Blade Runner [1982]", "Blade Runner")]
    [InlineData("The Lord of the Rings: The Fellowship of the Ring", "The Lord of the Rings")]
    [InlineData("Kill Bill Vol. 1", "Kill Bill")]
    [InlineData("Kill Bill Vol 2", "Kill Bill")]
    [InlineData("Toy Story 3 (2010)", "Toy Story")]
    public void StripFranchiseSuffix_CollapsesSiblingsToSameStem(string input, string expected)
    {
        Assert.Equal(expected, MediaGrouperScanner.StripFranchiseSuffix(input));
    }

    [Theory]
    [InlineData("2012", "2012")]
    [InlineData("13", "13")]
    [InlineData("V", "V")]
    public void StripFranchiseSuffix_LeavesShortNumericAndSingleLetterTitles(string input, string expected)
    {
        // No preceding separator → no strip. Titles that ARE the numeral / letter stay intact.
        Assert.Equal(expected, MediaGrouperScanner.StripFranchiseSuffix(input));
    }

    [Theory]
    [InlineData("My.Show.S01E08.1080p", "My.Show")]
    [InlineData("The Office S03E14", "The Office")]
    [InlineData("Some Show - 2x05 - Episode Title", "Some Show")]
    [InlineData("show.s01e01", "show")]
    public void ExtractShowNameFromFilename_TakesTextLeftOfEpisodeMarker(string filenameNoExt, string expected)
    {
        Assert.Equal(expected, MediaGrouperScanner.ExtractShowNameFromFilename(filenameNoExt));
    }

    [Theory]
    [InlineData("Blade Runner (1982)")]
    [InlineData("just_a_movie_name")]
    public void ExtractShowNameFromFilename_ReturnsInputWhenNoMarkerFound(string filenameNoExt)
    {
        Assert.Equal(filenameNoExt, MediaGrouperScanner.ExtractShowNameFromFilename(filenameNoExt));
    }

    [Fact]
    public void ExtractShowNameFromFilename_EmptyIsEmpty()
    {
        Assert.Equal(string.Empty, MediaGrouperScanner.ExtractShowNameFromFilename(string.Empty));
        Assert.Equal(string.Empty, MediaGrouperScanner.ExtractShowNameFromFilename("   "));
    }
}
