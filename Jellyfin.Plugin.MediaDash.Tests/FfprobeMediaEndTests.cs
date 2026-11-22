using System.Collections.Generic;
using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class FfprobeMediaEndTests
{
    [Theory]
    [InlineData("01:47:12.800000000", 6432.8)]
    [InlineData("00:25:58.9", 1558.9)]
    [InlineData("0:00:10", 10)]
    public void TagDurationParsesMatroskaNanosecondFormat(string tag, double expected)
    {
        Assert.True(FfprobeData.TryParseTagDuration(tag, out var seconds));
        Assert.Equal(expected, seconds, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("N/A")]
    public void TagDurationRejectsMissingValues(string? tag)
    {
        Assert.False(FfprobeData.TryParseTagDuration(tag, out _));
    }

    [Fact]
    public void AudioVideoEndIgnoresALaterEndingSubtitleTrack()
    {
        var probe = new FfprobeData
        {
            Format = new() { Duration = "6454.7" },
            Streams =
            [
                new() { CodecType = "video", Tags = new Dictionary<string, string> { ["DURATION"] = "01:47:12.800000000" } },
                new() { CodecType = "audio", Tags = new Dictionary<string, string> { ["DURATION"] = "01:47:12.790000000" } },
                new() { CodecType = "subtitle", Tags = new Dictionary<string, string> { ["DURATION"] = "01:47:34.700000000" } },
            ],
        };

        Assert.True(probe.TryGetAudioVideoEnd(out var end));
        Assert.Equal(6432.8, end, 3);
    }

    [Fact]
    public void AudioVideoEndPrefersStreamDuration()
    {
        var probe = new FfprobeData { Streams = [new() { CodecType = "audio", Duration = "120.5" }] };
        Assert.True(probe.TryGetAudioVideoEnd(out var end));
        Assert.Equal(120.5, end, 3);
    }

    [Fact]
    public void AudioVideoEndIsUnknownWithoutDurations()
    {
        var probe = new FfprobeData { Streams = [new() { CodecType = "video" }] };
        Assert.False(probe.TryGetAudioVideoEnd(out _));
    }
}
