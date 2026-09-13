using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class PlayabilityFixerDetailReasonTests
{
    [Fact]
    public void ReadsDetailFromScannerJson()
    {
        var json = "{\"reason\":\"no-video\",\"detail\":\"The file contains no video stream.\"}";
        Assert.Equal("The file contains no video stream", PlayabilityFixer.TryGetDetail(json));
    }

    [Fact]
    public void MissingDetailKeyReturnsNull()
    {
        Assert.Null(PlayabilityFixer.TryGetDetail("{\"reason\":\"unreadable\"}"));
    }

    [Fact]
    public void EmptyOrMalformedJsonReturnsNull()
    {
        Assert.Null(PlayabilityFixer.TryGetDetail(""));
        Assert.Null(PlayabilityFixer.TryGetDetail("{}"));
        Assert.Null(PlayabilityFixer.TryGetDetail("not json"));
        Assert.Null(PlayabilityFixer.TryGetDetail("{\"detail\":\"   \"}"));
    }

    [Fact]
    public void NonStringDetailReturnsNull()
    {
        Assert.Null(PlayabilityFixer.TryGetDetail("{\"detail\":123}"));
        Assert.Null(PlayabilityFixer.TryGetDetail("{\"detail\":null}"));
    }
}
