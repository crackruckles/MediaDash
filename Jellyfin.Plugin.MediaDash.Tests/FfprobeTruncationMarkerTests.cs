using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class FfprobeTruncationMarkerTests
{
    // Verbatim ffmpeg stderr on a truncated MKV. Reproduced against a real fixture:
    //   ffmpeg -xerror -v error -i truncated.mkv -t 30 -f null -
    // exits 0 but emits this line, hiding the truncation from the shallow decode check.
    private const string TruncatedMkvStderr = "[in#0/matroska,webm @ 000001ebdb7230c0] File ended prematurely";

    [Fact]
    public void FileEndedPrematurely_IsTruncation()
    {
        Assert.True(FfprobeService.HasTruncationMarker(TruncatedMkvStderr));
    }

    [Fact]
    public void TruncatingPacket_IsTruncation()
    {
        Assert.True(FfprobeService.HasTruncationMarker("[matroska @ 0x1234] Truncating packet of size 4096"));
    }

    [Fact]
    public void EmptyStderr_IsNotTruncation()
    {
        Assert.False(FfprobeService.HasTruncationMarker(string.Empty));
        Assert.False(FfprobeService.HasTruncationMarker(null));
    }

    [Fact]
    public void BenignHevcParserChatter_IsNotTruncation()
    {
        // Real world: HEVC SEI parser routinely logs warnings that don't mean the file is broken.
        // Must not false-positive on these.
        var stderr = "[hevc @ 0x7f] Invalid NAL unit size (12345 > 456)\n[hevc @ 0x7f] SEI parsing failed";
        Assert.False(FfprobeService.HasTruncationMarker(stderr));
    }

    // --- ParseLastTimeSeconds ---

    [Fact]
    public void ParseLastTime_TypicalStatsLine()
    {
        // Real -stats output from the truncated fixture run:
        //   frame= 190 fps=0.0 q=-0.0 Lsize=N/A time=00:00:07.84 bitrate=N/A speed=205x
        var stderr = "frame=  190 fps=0.0 q=-0.0 Lsize=N/A time=00:00:07.84 bitrate=N/A speed= 205x elapsed=0:00:00.03";
        Assert.Equal(7.84, FfprobeService.ParseLastTimeSeconds(stderr) ?? -1, 2);
    }

    [Fact]
    public void ParseLastTime_MultipleLines_TakesTheLast()
    {
        // -stats emits many progress lines during a long decode; the LAST one is where ffmpeg stopped.
        var stderr = "frame=100 time=00:00:04.16\nframe=200 time=00:00:08.33\nframe=250 time=00:00:10.42";
        Assert.Equal(10.42, FfprobeService.ParseLastTimeSeconds(stderr) ?? -1, 2);
    }

    [Fact]
    public void ParseLastTime_HoursAndFractionalSeconds()
    {
        Assert.Equal(3723.5, FfprobeService.ParseLastTimeSeconds("time=01:02:03.5") ?? -1, 2);
    }

    [Fact]
    public void ParseLastTime_NoTimeInStderr_ReturnsNull()
    {
        Assert.Null(FfprobeService.ParseLastTimeSeconds("[hevc] some warning"));
        Assert.Null(FfprobeService.ParseLastTimeSeconds(string.Empty));
        Assert.Null(FfprobeService.ParseLastTimeSeconds(null));
    }
}
