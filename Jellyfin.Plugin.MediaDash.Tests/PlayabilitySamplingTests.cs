using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class PlayabilitySamplingTests
{
    [Theory]
    [InlineData("N/A")]
    [InlineData("0")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void InvalidContainerDurationUsesLongestPlayableStream(string containerDuration)
    {
        var probe = new FfprobeData
        {
            Format = new FfprobeFormat { Duration = containerDuration },
            Streams = new[]
            {
                new FfprobeStreamInfo { CodecType = "subtitle", Duration = "9000" },
                new FfprobeStreamInfo { CodecType = "video", Duration = "NaN" },
                new FfprobeStreamInfo { CodecType = "audio", Duration = "60" },
                new FfprobeStreamInfo { CodecType = "audio", Duration = "120" }
            }
        };
        Assert.True(probe.TryGetDuration(out var duration));
        Assert.Equal(120, duration);
    }

    [Fact]
    public void ShortAudio_SamplesWholeFile()
    {
        Assert.True(FfprobeService.ShouldSampleWholeFile(30));
        Assert.True(FfprobeService.ShouldSampleWholeFile(59.9));
    }

    [Fact]
    public void LongerContent_UsesRegionalSampling()
    {
        Assert.False(FfprobeService.ShouldSampleWholeFile(60));
        Assert.False(FfprobeService.ShouldSampleWholeFile(3600));
    }

    [Fact]
    public void NonPositiveDuration_UsesRegionalSampling()
    {
        Assert.False(FfprobeService.ShouldSampleWholeFile(0));
        Assert.False(FfprobeService.ShouldSampleWholeFile(-1));
    }
}
