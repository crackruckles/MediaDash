using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class FfprobeTruncationMarkerTests
{
    [Theory]
    [InlineData(99, 1, false)]
    [InlineData(98, 2, false)]
    [InlineData(97, 3, true)]
    [InlineData(4, 457, true)]
    public void DecodeErrorRateIsBoundedForEachStream(int decoded, int errors, bool excessive)
    {
        var log = "[in#0/matroska @ 0x1]   Input stream #0:0 (video): 100000 packets read (1000000 bytes); 100000 frames decoded;\n"
            + $"[in#0/matroska @ 0x1]   Input stream #0:1 (audio): 100 packets read (1000 bytes); {decoded} frames decoded; {errors} decode errors (100 samples);\n";
        Assert.Equal(excessive, FfprobeService.HasExcessiveDecodeErrors(log));
        Assert.Equal(excessive, FfprobeService.HasExcessiveDecodeErrors(log.Replace("]   Input", "] [verbose]   Input")));
        Assert.Null(FfprobeService.HasExcessiveDecodeErrors("title: 0 frames decoded; 100 decode errors"));
    }

    [Fact]
    public void DecodeDiagnosticsIgnoreMetadataAndRetainActualErrors()
    {
        const string error = "[aist#0:0/mp3 @ 0x1] [dec:mp3float @ 0x2] [error] Header missing";
        var log = "[info] title : File ended prematurely Permission denied [error] false alarm\n" + error + "\n[info] Input file: Permission denied.mp3";
        Assert.Equal(error, FfprobeService.DecodeDiagnostics(log));
    }

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
    public void TruncatingPacket_IsNotTruncation()
    {
        // Reversed 2026-08-31: field reports (Reddit thread — 4 independent users) confirmed
        // "Truncating packet" fires on benign container quirks (MKV cluster boundaries, MPEG-TS
        // packet alignment, MP4 mdat overlap) on files that play fine in Jellyfin. Not a reliable
        // truncation signal on its own. Real truncation still trips exit code, "File ended
        // prematurely", and the bitrate-vs-size heuristic in PlayabilityScanner.
        Assert.False(FfprobeService.HasTruncationMarker("[matroska @ 0x1234] Truncating packet of size 4096"));
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

    [Fact]
    public void ParseLastTimeIgnoresVerboseMetadataAndPaths()
    {
        var log = "[info] size=N/A time=00:00:03.00 bitrate=N/A\r\n"
            + "[in#0/mp3 @ 0x1] [verbose] Input file #0 (time=00:09:00.mp3):\n"
            + "[info] title : time=00:08:00";
        Assert.Equal(3, FfprobeService.ParseLastTimeSeconds(log));
    }
}
