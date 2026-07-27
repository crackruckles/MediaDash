using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class PlayabilitySamplingTests
{
    [Fact]
    public void ShortAudio_SamplesWholeFile()
    {
        Assert.True(PlayabilityScanner.ShouldSampleWholeFile(30));
        Assert.True(PlayabilityScanner.ShouldSampleWholeFile(59.9));
    }

    [Fact]
    public void LongerContent_UsesRegionalSampling()
    {
        Assert.False(PlayabilityScanner.ShouldSampleWholeFile(60));
        Assert.False(PlayabilityScanner.ShouldSampleWholeFile(3600));
    }

    [Fact]
    public void NonPositiveDuration_UsesRegionalSampling()
    {
        Assert.False(PlayabilityScanner.ShouldSampleWholeFile(0));
        Assert.False(PlayabilityScanner.ShouldSampleWholeFile(-1));
    }
}
