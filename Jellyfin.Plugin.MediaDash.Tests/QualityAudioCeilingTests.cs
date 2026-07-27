using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class QualityAudioCeilingTests
{
    [Theory]
    [InlineData("mp3", 320_000L, false)]
    [InlineData("mp3", 320_001L, true)]
    [InlineData("aac", 256_000L, false)]
    [InlineData("aac", 320_000L, true)]
    [InlineData("flac", 1_000_000L, false)]
    [InlineData("alac", 1_000_000L, false)]
    [InlineData("wav", 10_000_000L, false)]
    public void AudioIsOversized_MatchesExpected(string codec, long bitsPerSecond, bool expected)
    {
        Assert.Equal(expected, QualityScanner.IsAudioOversized(codec, bitsPerSecond));
    }
}
