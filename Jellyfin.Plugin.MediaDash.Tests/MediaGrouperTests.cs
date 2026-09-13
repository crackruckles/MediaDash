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

    // Regression from a user-reported case: Jellyfin's metadata match failed on `spooks S01E06.mkv`
    // and it populated `Episode.SeriesName` with the raw filename ("spooks S01E06") instead of
    // returning it empty. The old scanner trusted SeriesName verbatim and suggested grouping
    // under `spooks S01E06/` — one folder per episode. The defense-in-depth sanitizer in
    // ResolveSafeSeriesName now runs the extractor over the resolved name whenever it still
    // carries an episode marker.
    [Theory]
    [InlineData("spooks S01E06", @"C:\tv\spooks S01E06.mkv", @"C:\tv", "spooks")]
    [InlineData("The Office S03E14", @"C:\tv\The Office S03E14.mkv", @"C:\tv", "The Office")]
    [InlineData("Some Show - 2x05", @"C:\tv\Some Show - 2x05 - Title.mkv", @"C:\tv", "Some Show")]
    [InlineData("My.Show.S01E08.1080p", @"C:\tv\My.Show.S01E08.1080p.mkv", @"C:\tv", "My.Show")]
    public void ResolveSafeSeriesName_StripsSxxExxWhenJellyfinLeaksItIntoSeriesName(
        string leakedSeriesName,
        string episodePath,
        string tvRoot,
        string expected)
    {
        // No PremiereDate available; the on-disk fallback layer sees a bare tv root with the file
        // directly under it (no year-suffixed folder), so the code path reaches the sanitizer.
        var resolved = MediaGrouperScanner.ResolveSafeSeriesName(
            leakedSeriesName,
            seriesPremiereYear: null,
            episodeFilePath: episodePath,
            tvRoot: tvRoot);
        Assert.Equal(expected, resolved);
    }

    // Second half of the spooks-in-Movies-library regression: even if Jellyfin identifies the file
    // as a Movie (because it sits in the Movies library folder), the Media Grouper must refuse to
    // emit a "group this movie" issue when the name still carries a TV episode marker. Otherwise
    // the user sees "Group under spooks S01E06" and, if they approve, ends up with one folder per
    // episode inside their movies library.
    [Theory]
    [InlineData("spooks S01E06", "spooks S01E06", true)]
    [InlineData("The Office S03E14", "The Office S03E14", true)]
    [InlineData("Some Show - 2x05", "Some Show - 2x05 - Title", true)]
    [InlineData("My.Show.S01E08.1080p", "My.Show.S01E08.1080p", true)]
    // Jellyfin-identified movie names never carry SxxExx by legitimate means — no false positives.
    [InlineData("Blade Runner", "Blade Runner (1982)", false)]
    [InlineData("Interstellar", "Interstellar (2014)", false)]
    [InlineData("Kill Bill Vol. 1", "Kill Bill Vol. 1", false)]
    [InlineData("2012", "2012", false)]
    // Marker in EITHER the canonical name OR the filename triggers the guard.
    [InlineData("Some Movie", "Actually A TV Episode S01E01", true)]
    [InlineData("spooks S01E06", "spooks S01E06 [1080p]", true)]
    public void LooksLikeTvEpisodeMisfiled(string canonical, string filenameNoExt, bool expected)
    {
        Assert.Equal(expected, MediaGrouperScanner.LooksLikeTvEpisodeMisfiled(canonical, filenameNoExt));
    }

    [Fact]
    public void ResolveSafeSeriesName_LeavesCleanSeriesNameAlone()
    {
        // No SxxExx marker → sanitizer is a no-op → SeriesName passes through unchanged.
        var resolved = MediaGrouperScanner.ResolveSafeSeriesName(
            "Breaking Bad",
            seriesPremiereYear: null,
            episodeFilePath: @"C:\tv\Breaking Bad\S01E01.mkv",
            tvRoot: @"C:\tv");
        Assert.Equal("Breaking Bad", resolved);
    }
}
