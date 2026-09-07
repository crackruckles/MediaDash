using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class LanguageHelperTests
{
    [Theory]
    [InlineData(null, "und")]
    [InlineData("", "und")]
    [InlineData("  ", "und")]
    [InlineData("eng", "eng")]
    [InlineData("ENG", "eng")]
    [InlineData("fre", "fra")]
    [InlineData("ger", "deu")]
    [InlineData("chi", "zho")]
    [InlineData("dut", "nld")]
    [InlineData("es-MX", "spa")]
    [InlineData("ES_mx", "spa")]
    [InlineData("en-US", "eng")]
    public void Normalize_MapsBibliographicVariantsAndCase(string? input, string expected)
    {
        Assert.Equal(expected, LanguageHelper.Normalize(input));
    }

    [Fact]
    public void IsAllowed_UndeterminedIsAlwaysAllowed()
    {
        Assert.True(LanguageHelper.IsAllowed(null, ["eng"]));
        Assert.True(LanguageHelper.IsAllowed("und", ["eng"]));
        Assert.True(LanguageHelper.IsAllowed("", ["eng"]));
    }

    [Fact]
    public void IsAllowed_MatchesAcrossIsoVariants()
    {
        // Track tagged bibliographic, config uses terminological — and vice versa.
        Assert.True(LanguageHelper.IsAllowed("fre", ["fra"]));
        Assert.True(LanguageHelper.IsAllowed("fra", ["fre"]));
        Assert.True(LanguageHelper.IsAllowed("deu", ["ger"]));
    }

    [Fact]
    public void IsAllowed_RejectsLanguagesOutsideList()
    {
        Assert.False(LanguageHelper.IsAllowed("fra", ["eng"]));
        Assert.False(LanguageHelper.IsAllowed("jpn", ["eng", "spa"]));
    }

    [Theory]
    // Norwegian macrolanguage: a user with "nor" allowed should keep Bokmål (nob) and Nynorsk (nno)
    // tracks — most Norwegian media is tagged with the specific variant. Symmetric: a user with
    // "nob" or "nno" allowed should also keep generic "nor" tracks.
    [InlineData("nob", new[] { "nor" })]
    [InlineData("nno", new[] { "nor" })]
    [InlineData("nor", new[] { "nob" })]
    [InlineData("nor", new[] { "nno" })]
    [InlineData("nob", new[] { "nno" })]
    public void IsAllowed_TreatsNorwegianVariantsAsEquivalent(string track, string[] allowed)
    {
        Assert.True(LanguageHelper.IsAllowed(track, allowed));
    }

    [Fact]
    public void IsAllowed_NorwegianDoesNotBleedIntoUnrelatedLanguages()
    {
        // Sanity: equivalence group is scoped. "nor" must not match unrelated codes.
        Assert.False(LanguageHelper.IsAllowed("swe", ["nor"]));
        Assert.False(LanguageHelper.IsAllowed("dan", ["nob"]));
    }
}
