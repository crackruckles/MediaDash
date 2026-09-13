using Jellyfin.Plugin.MediaDash.Data;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Consent gate: an Issue whose DetailsJson carries a warning with blocking:true must be
// held out of the FixTask auto-queue so the user has to press Approve knowingly. This test
// class covers Issue.HasBlockingWarnings — the getter FixTask + MediaDashDb consult. If it
// ever returns true for a row without a real blocking warning, users get one extra approval
// click. If it returns false when it should have been true, users lose data silently. Both
// modes are covered explicitly here.
public class IssueBlockingWarningsTests
{
    [Fact]
    public void EmptyDetailsJson_ReturnsFalse()
    {
        Assert.False(new Issue { DetailsJson = string.Empty }.HasBlockingWarnings);
        Assert.False(new Issue { DetailsJson = "{}" }.HasBlockingWarnings);
    }

    [Fact]
    public void MalformedJson_ReturnsFalse_NoThrow()
    {
        Assert.False(new Issue { DetailsJson = "{not valid json" }.HasBlockingWarnings);
    }

    [Fact]
    public void WarningsFieldMissing_ReturnsFalse()
    {
        Assert.False(new Issue { DetailsJson = "{\"removeIndexes\":[1,2]}" }.HasBlockingWarnings);
    }

    [Fact]
    public void WarningsArrayEmpty_ReturnsFalse()
    {
        Assert.False(new Issue { DetailsJson = "{\"warnings\":[]}" }.HasBlockingWarnings);
    }

    [Fact]
    public void WarningWithoutBlockingFlag_ReturnsFalse()
    {
        var json = "{\"warnings\":[{\"code\":\"info-only\",\"message\":\"heads up\"}]}";
        Assert.False(new Issue { DetailsJson = json }.HasBlockingWarnings);
    }

    [Fact]
    public void WarningWithBlockingFalse_ReturnsFalse()
    {
        var json = "{\"warnings\":[{\"code\":\"x\",\"blocking\":false,\"message\":\"m\"}]}";
        Assert.False(new Issue { DetailsJson = json }.HasBlockingWarnings);
    }

    [Fact]
    public void WarningWithBlockingTrue_ReturnsTrue()
    {
        var json = "{\"warnings\":[{\"code\":\"bitmap-subs-dropped\",\"blocking\":true,\"message\":\"m\"}]}";
        Assert.True(new Issue { DetailsJson = json }.HasBlockingWarnings);
    }

    [Fact]
    public void OneBlockingAmongstMany_ReturnsTrue()
    {
        var json = "{\"warnings\":[" +
            "{\"code\":\"a\",\"blocking\":false}," +
            "{\"code\":\"b\",\"blocking\":true,\"message\":\"real\"}," +
            "{\"code\":\"c\",\"blocking\":false}" +
        "]}";
        Assert.True(new Issue { DetailsJson = json }.HasBlockingWarnings);
    }

    [Fact]
    public void WarningsAlongsideOtherFields_ReturnsTrue()
    {
        // Real payload shape from AudioLanguageScanner.
        var json = "{" +
            "\"removeIndexes\":[3]," +
            "\"removeLanguages\":[\"fra\"]," +
            "\"keepLanguages\":[\"eng\"]," +
            "\"warnings\":[{\"code\":\"bitmap-subs-dropped\",\"blocking\":true,\"message\":\"drops 1 sub\"}]" +
        "}";
        Assert.True(new Issue { DetailsJson = json }.HasBlockingWarnings);
    }
}
