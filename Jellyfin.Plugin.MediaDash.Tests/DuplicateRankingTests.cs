using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class DuplicateRankingTests
{
    private static readonly string[] DefaultPolicy = ["Resolution", "Codec", "Bitrate", "Size"];
    private static readonly string[] DefaultCodecs = ["av1", "hevc", "h264"];

    private static DuplicateScanner.Candidate Make(string path, long pixels, string codec, long bitrate, long size)
    {
        return new DuplicateScanner.Candidate { Path = path, Pixels = pixels, Codec = codec, Bitrate = bitrate, Size = size };
    }

    [Fact]
    public void HigherResolutionWins()
    {
        var ranked = DuplicateScanner.Rank(
            [Make("1080p", 1920 * 1080, "hevc", 8_000_000, 100), Make("4k", 3840 * 2160, "h264", 8_000_000, 400)],
            DefaultPolicy,
            DefaultCodecs);
        Assert.Equal("4k", ranked[0].Path);
    }

    [Fact]
    public void PreferredCodecBreaksResolutionTie()
    {
        var ranked = DuplicateScanner.Rank(
            [Make("h264", 1920 * 1080, "h264", 9_000_000, 100), Make("hevc", 1920 * 1080, "hevc", 5_000_000, 90)],
            DefaultPolicy,
            DefaultCodecs);
        Assert.Equal("hevc", ranked[0].Path);
    }

    [Fact]
    public void UnknownCodecRanksLast()
    {
        var ranked = DuplicateScanner.Rank(
            [Make("weird", 1920 * 1080, "wmv3", 9_000_000, 100), Make("h264", 1920 * 1080, "h264", 5_000_000, 100)],
            DefaultPolicy,
            DefaultCodecs);
        Assert.Equal("h264", ranked[0].Path);
    }

    [Fact]
    public void HigherBitrateBreaksCodecTie()
    {
        var ranked = DuplicateScanner.Rank(
            [Make("low", 1920 * 1080, "hevc", 4_000_000, 100), Make("high", 1920 * 1080, "hevc", 8_000_000, 100)],
            DefaultPolicy,
            DefaultCodecs);
        Assert.Equal("high", ranked[0].Path);
    }

    [Fact]
    public void SmallerFileWinsFinalTiebreak()
    {
        var ranked = DuplicateScanner.Rank(
            [Make("big", 1920 * 1080, "hevc", 8_000_000, 200), Make("small", 1920 * 1080, "hevc", 8_000_000, 100)],
            DefaultPolicy,
            DefaultCodecs);
        Assert.Equal("small", ranked[0].Path);
    }

    [Fact]
    public void CustomPolicyOrderIsRespected()
    {
        // Size-first policy: the smaller file is the keeper even at lower resolution.
        var ranked = DuplicateScanner.Rank(
            [Make("4k-big", 3840 * 2160, "hevc", 20_000_000, 500), Make("720p-small", 1280 * 720, "h264", 2_000_000, 50)],
            ["Size"],
            DefaultCodecs);
        Assert.Equal("720p-small", ranked[0].Path);
    }

    [Fact]
    public void GenericNamesWithoutYearOrProviderIdsNeverGroup()
    {
        // Two unrelated "1.mp4" files: no provider IDs, no year -> no group key -> never duplicates.
        var a = new Movie { Name = "1" };
        var b = new Movie { Name = "1" };
        Assert.Null(DuplicateScanner.GetGroupKey(a));
        Assert.Null(DuplicateScanner.GetGroupKey(b));
    }

    [Fact]
    public void SameNameAndYearGroupsTogether()
    {
        var a = new Movie { Name = "Big Buck Test", ProductionYear = 2020 };
        var b = new Movie { Name = "Big.Buck.Test", ProductionYear = 2020 };
        Assert.Equal(DuplicateScanner.GetGroupKey(a), DuplicateScanner.GetGroupKey(b));
        Assert.NotNull(DuplicateScanner.GetGroupKey(a));
    }

    [Fact]
    public void SameProviderIdGroupsRegardlessOfName()
    {
        var a = new Movie { Name = "Whatever" };
        a.ProviderIds["Tmdb"] = "12345";
        var b = new Movie { Name = "Something Else" };
        b.ProviderIds["Tmdb"] = "12345";
        Assert.Equal(DuplicateScanner.GetGroupKey(a), DuplicateScanner.GetGroupKey(b));
        Assert.NotNull(DuplicateScanner.GetGroupKey(a));
    }

    [Fact]
    public void LosersAreEveryoneButTheKeeper()
    {
        List<DuplicateScanner.Candidate> candidates =
        [
            Make("a", 1920 * 1080, "hevc", 8_000_000, 100),
            Make("b", 3840 * 2160, "hevc", 8_000_000, 400),
            Make("c", 1280 * 720, "h264", 2_000_000, 50)
        ];
        var ranked = DuplicateScanner.Rank(candidates, DefaultPolicy, DefaultCodecs);
        Assert.Equal("b", ranked[0].Path);
        Assert.Equal(2, ranked.Skip(1).Count());
    }
}
