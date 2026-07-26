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
}
