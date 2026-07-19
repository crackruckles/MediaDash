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
}
