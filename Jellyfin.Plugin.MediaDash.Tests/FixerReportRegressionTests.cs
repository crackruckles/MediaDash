using System;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class FixerReportRegressionTests
{
    [Fact]
    public void AssSubtitleFile_NonUtf8_RefusesLossyRewrite()
    {
        var bytes = Encoding.ASCII.GetBytes("[Events]\nDialogue: ").Concat(new byte[] { 0xC4, 0xE3, 0xBA, 0xC3 }).ToArray();
        Assert.Throws<NotSupportedException>(() => AssSubtitleFile.ParseBytes(bytes));
    }

    [Fact]
    public void RealtimeThrottle_StreamCopy_DoesNotThrottle()
    {
        string[] args = { "-i", "input.mkv", "-map", "0", "-c", "copy", "output.mkv" };
        Assert.DoesNotContain("-re", FfmpegExecutor.InjectRealtimeThrottle(args));
    }

    [Fact]
    public void RealtimeThrottle_MixedCopyAndEncode_Throttles()
    {
        string[] args = { "-i", "input.mkv", "-c", "copy", "-c:a", "aac", "output.mkv" };
        Assert.Contains("-re", FfmpegExecutor.InjectRealtimeThrottle(args));
    }

    [Theory]
    [InlineData("120", "10", false)]
    [InlineData(null, "120", false)]
    [InlineData("120", null, false)]
    [InlineData("120", "119", true)]
    public void CoverStrip_RequiresMatchingKnownDuration(string? source, string? output, bool accepted)
    {
        var before = new Jellyfin.Plugin.MediaDash.Probing.FfprobeData { Format = new() { Duration = source } };
        var after = new Jellyfin.Plugin.MediaDash.Probing.FfprobeData { Format = new() { Duration = output } };
        Assert.Equal(accepted, EmbeddedCoverArtFixer.StripDurationMatches(before, after));
    }

    [Theory]
    [InlineData("wmv", "asf")]
    [InlineData("mka", "matroska")]
    [InlineData("divx", "avi")]
    [InlineData("ogm", "ogg")]
    [InlineData("m2t", "mpegts")]
    [InlineData("aif", "aiff")]
    [InlineData("aifc", "aiff")]
    public void Muxer_AliasesResolve(string extension, string muxer)
    {
        Assert.Equal(muxer, TranscodeFixer.ContainerToFfmpegFormat(extension));
    }
}
