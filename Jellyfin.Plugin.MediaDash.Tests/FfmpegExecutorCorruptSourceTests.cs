using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class FfmpegExecutorCorruptSourceTests
{
    [Fact]
    public void DetectsUserReportedMpegDvrCorruption()
    {
        // Verbatim tail from a real user's Prodigal Son .mpg DVR recording (2026-09 report).
        // Any one of these markers should trip the classifier so the raw stderr stops surfacing
        // to the Errors tab and TranscodeFixer routes the user at the repair ladder instead.
        var stderr = "[ac3 @ 0x796c460fc180] error decoding the audio block "
            + "[mpeg2video @ 0x796c460fb380] Invalid frame dimensions 0x0. Last message repeated 8 times "
            + "[mpeg2video @ 0x796c460fb380] ignoring pic cod ext after 0 "
            + "[ac3 @ 0x796c460fba80] expacc 127 is out-of-range "
            + "[ac3 @ 0x796c460fba80] error decoding the audio block "
            + "[ac3 @ 0x796c460fba80] bandwidth code = 62 > 60";
        Assert.True(FfmpegExecutor.IsCorruptSourceError(stderr));
    }

    [Theory]
    [InlineData("[in#0/flv @ 0000021b] could not find codec parameters", true)]
    [InlineData("moov atom not found", true)]
    [InlineData("[matroska,webm @ 0x1234] EBML header parsing failed", true)]
    [InlineData("Invalid data found when processing input", true)]
    [InlineData("[mpeg2video @ 0x1] Invalid frame dimensions 0x0", true)]
    [InlineData("Header missing", true)]
    // Negatives — real transcode-side failures that MUST still surface as Ffmpeg.Error:
    [InlineData("Encoder not found", false)]
    [InlineData("No space left on device", false)]
    [InlineData("Unknown encoder 'hevc_nvenc'", false)]
    [InlineData("Bitstream filter not found", false)]
    [InlineData("", false)]
    public void MarkersMatchExpectedClassification(string stderr, bool expected)
    {
        Assert.Equal(expected, FfmpegExecutor.IsCorruptSourceError(stderr));
    }

    [Fact]
    public void NullReturnsFalse()
    {
        Assert.False(FfmpegExecutor.IsCorruptSourceError(null));
    }
}
