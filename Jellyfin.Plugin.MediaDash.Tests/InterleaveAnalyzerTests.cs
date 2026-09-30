using System.Collections.Generic;
using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class InterleaveAnalyzerTests
{
    private const int Video = 0;
    private static readonly int[] Audio = [1, 2];

    [Fact]
    public void ShortFiles_AreNotSampled()
    {
        Assert.Empty(InterleaveAnalyzer.GetSampleStarts(0));
        Assert.Empty(InterleaveAnalyzer.GetSampleStarts(599));
    }

    [Fact]
    public void SampleStarts_SpreadEvenlyAndSkipBothEnds()
    {
        var starts = InterleaveAnalyzer.GetSampleStarts(5475.137);
        Assert.Equal(9, starts.Count);
        Assert.Equal(547, starts[0]);
        Assert.Equal(4927, starts[8]);
    }

    [Fact]
    public void ReadIntervals_UseInvariantFfprobeSyntax()
    {
        Assert.Equal("60%+5,120%+5", InterleaveAnalyzer.ToReadIntervals([60, 120]));
    }

    [Theory]
    [InlineData("0,1361.527000", 0, 1361.527)]
    [InlineData("2,0.000000,", 2, 0)]
    public void PacketLine_Parses(string line, int expectedIndex, double expectedPts)
    {
        Assert.True(InterleaveAnalyzer.TryParsePacketLine(line, out var index, out var pts));
        Assert.Equal(expectedIndex, index);
        Assert.Equal(expectedPts, pts, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1,N/A")]
    [InlineData("garbage")]
    public void PacketLine_WithoutTimestamp_IsSkipped(string? line)
    {
        Assert.False(InterleaveAnalyzer.TryParsePacketLine(line, out _, out _));
    }

    [Fact]
    public void Samples_WithAudioEverywhere_AreClean()
    {
        var starts = InterleaveAnalyzer.GetSampleStarts(3600);
        var packets = new List<(int, double)>();
        foreach (var start in starts)
        {
            packets.Add((Video, start - 4));
            packets.Add((1, start - 3.9));
            packets.Add((2, start - 3.9));
        }

        Assert.False(InterleaveAnalyzer.HasSampleWithoutAudio(packets, starts, Video, Audio));
    }

    [Fact]
    public void Sample_WithVideoButNoAudio_IsSuspicious()
    {
        var starts = InterleaveAnalyzer.GetSampleStarts(3600);
        var packets = new List<(int, double)>();
        foreach (var start in starts)
        {
            packets.Add((Video, start));
            if (start != starts[3])
            {
                packets.Add((1, start));
                packets.Add((2, start));
            }
        }

        Assert.True(InterleaveAnalyzer.HasSampleWithoutAudio(packets, starts, Video, Audio));
    }

    [Fact]
    public void Sample_MissingOnlyOneAudioTrack_IsSuspicious()
    {
        var starts = InterleaveAnalyzer.GetSampleStarts(3600);
        var packets = new List<(int, double)> { (Video, starts[0]), (1, starts[0]) };

        Assert.True(InterleaveAnalyzer.HasSampleWithoutAudio(packets, starts, Video, Audio));
    }

    [Fact]
    public void Sample_WithoutVideo_IsInconclusive()
    {
        // The seek returned nothing usable for this position; that is not evidence of a layout problem.
        var starts = InterleaveAnalyzer.GetSampleStarts(3600);
        var packets = new List<(int, double)> { (1, starts[0]) };

        Assert.False(InterleaveAnalyzer.HasSampleWithoutAudio(packets, starts, Video, Audio));
    }

    [Fact]
    public void Offset_StaysSmallWhenStreamsAlternate()
    {
        var packets = new List<(int, double)>();
        for (var t = 0.0; t < 120; t += 0.25)
        {
            packets.Add((Video, t));
            packets.Add((1, t + 0.1));
            packets.Add((2, t + 0.1));
        }

        Assert.True(InterleaveAnalyzer.MeasureMaxOffsetSeconds(packets, Video, Audio) < 1);
    }

    [Fact]
    public void Offset_MeasuresAudioStoredInOneBlockAheadOfItsVideo()
    {
        // Shape of the file that prompted this check: interleaved up to 19:01, then 29 minutes of
        // audio in one block, then the 29 minutes of video that belong to it.
        var packets = new List<(int, double)> { (Video, 1140), (1, 1140) };
        for (var t = 1141.0; t <= 2900; t += 1)
        {
            packets.Add((1, t));
        }

        for (var t = 1141.0; t <= 2900; t += 1)
        {
            packets.Add((Video, t));
        }

        var offset = InterleaveAnalyzer.MeasureMaxOffsetSeconds(packets, Video, Audio);
        Assert.Equal(1760, offset, 0);
        Assert.True(offset > InterleaveAnalyzer.MaxOffsetSeconds);
    }

    [Fact]
    public void Offset_IgnoresAudioTrackThatSimplyEndsEarly()
    {
        var packets = new List<(int, double)>();
        for (var t = 0.0; t < 600; t += 1)
        {
            packets.Add((Video, t));
            if (t < 300)
            {
                packets.Add((1, t));
            }
        }

        Assert.True(InterleaveAnalyzer.MeasureMaxOffsetSeconds(packets, Video, Audio) < 1);
    }
}
