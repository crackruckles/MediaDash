using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;
using AudioEntity = MediaBrowser.Controller.Entities.Audio.Audio;

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
    public void SameNameYearAndNormalizedFilenameGroupTogether()
    {
        // Two copies of the same movie in different folders. Filenames differ only in punctuation, which
        // NormalizeName strips — they collapse to the same key and get grouped as legit duplicates.
        var a = new Movie { Name = "Big Buck Test", ProductionYear = 2020, Path = @"C:\a\Big Buck Test (2020).mkv" };
        var b = new Movie { Name = "Big.Buck.Test", ProductionYear = 2020, Path = @"D:\backup\Big.Buck.Test.2020.mkv" };
        Assert.NotNull(DuplicateScanner.GetGroupKey(a));
        Assert.Equal(DuplicateScanner.GetGroupKey(a), DuplicateScanner.GetGroupKey(b));
    }

    [Fact]
    public void DifferentFilenamesUnderSameFolderDerivedNameDoNotGroup()
    {
        // Reproduces the real-world failure: Jellyfin can't identify a folder full of unrelated .mp4s and
        // derives the same Movie.Name + year for every file. Under the old fallback (name+year only) they all
        // grouped into one "duplicate" bucket. With filename in the key each stays distinct.
        var a = new Movie { Name = "Pack Folder", ProductionYear = 2023, Path = @"C:\pack\Video One.mp4" };
        var b = new Movie { Name = "Pack Folder", ProductionYear = 2023, Path = @"C:\pack\Video Two.mp4" };
        var c = new Movie { Name = "Pack Folder", ProductionYear = 2023, Path = @"C:\pack\Video Three.mp4" };
        Assert.NotEqual(DuplicateScanner.GetGroupKey(a), DuplicateScanner.GetGroupKey(b));
        Assert.NotEqual(DuplicateScanner.GetGroupKey(a), DuplicateScanner.GetGroupKey(c));
        Assert.NotEqual(DuplicateScanner.GetGroupKey(b), DuplicateScanner.GetGroupKey(c));
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

    [Fact]
    public void GetGroupKey_Audio_UsesMusicBrainzTrackId_WhenPresent()
    {
        var audio = new AudioEntity
        {
            Name = "Song",
            Album = "Album",
            Artists = new System.Collections.Generic.List<string> { "Artist" },
            ProviderIds = new System.Collections.Generic.Dictionary<string, string> { ["MusicBrainzTrack"] = "MB-123" }
        };

        var key = DuplicateScanner.GetGroupKey(audio);
        Assert.Equal("audio:musicbrainztrack:mb-123", key);
    }

    [Fact]
    public void GetGroupKey_Audio_FallsBackToArtistAlbumTitleDurationAndFilename()
    {
        var audio = new AudioEntity
        {
            Name = "Blue in Green",
            Album = "Kind of Blue",
            Artists = new System.Collections.Generic.List<string> { "Miles Davis" },
            RunTimeTicks = System.TimeSpan.FromSeconds(337).Ticks,
            Path = @"C:\music\Miles Davis - Blue in Green.flac"
        };

        var key = DuplicateScanner.GetGroupKey(audio);
        Assert.Equal("audio:name:milesdavis:kindofblue:blueingreen:337:milesdavisblueingreenflac", key);
    }

    [Fact]
    public void GetGroupKey_Audio_DifferentFilenamesDoNotGroup()
    {
        // Two tracks Jellyfin labels identically (folder-derived) but with different physical filenames
        // must not group under the fallback path.
        var a = new AudioEntity
        {
            Name = "Track", Album = "Album", Artists = new System.Collections.Generic.List<string> { "Artist" },
            RunTimeTicks = System.TimeSpan.FromSeconds(200).Ticks, Path = @"C:\a\one.mp3"
        };
        var b = new AudioEntity
        {
            Name = "Track", Album = "Album", Artists = new System.Collections.Generic.List<string> { "Artist" },
            RunTimeTicks = System.TimeSpan.FromSeconds(200).Ticks, Path = @"C:\a\two.mp3"
        };
        Assert.NotEqual(DuplicateScanner.GetGroupKey(a), DuplicateScanner.GetGroupKey(b));
    }

    [Fact]
    public void GetGroupKey_Audio_ReturnsNullWhenTitleMissing()
    {
        var audio = new AudioEntity
        {
            Album = "Album",
            Artists = new System.Collections.Generic.List<string> { "Artist" }
        };

        Assert.Null(DuplicateScanner.GetGroupKey(audio));
    }

    [Fact]
    public void GetGroupKey_Audio_ReturnsNullWhenRuntimeMissing()
    {
        // Runtime became a hard requirement for the fallback path — without it, matching is too loose.
        var audio = new AudioEntity
        {
            Name = "Track", Album = "Album",
            Artists = new System.Collections.Generic.List<string> { "Artist" }
        };
        Assert.Null(DuplicateScanner.GetGroupKey(audio));
    }

    [Fact]
    public void GetGroupKey_Book_UsesIsbn_WhenPresent()
    {
        var book = new MediaBrowser.Controller.Entities.Book
        {
            Name = "Dune",
            ProviderIds = new System.Collections.Generic.Dictionary<string, string> { ["Isbn"] = "9780441172719" }
        };

        var key = DuplicateScanner.GetGroupKey(book);
        Assert.Equal("book:isbn:9780441172719", key);
    }

    [Fact]
    public void GetGroupKey_Book_FallbackIncludesFilename()
    {
        var book = new MediaBrowser.Controller.Entities.Book { Name = "Dune", Path = @"C:\books\Dune.epub" };
        var key = DuplicateScanner.GetGroupKey(book);
        Assert.Equal("book:name:dune:duneepub", key);
    }

    [Fact]
    public void GetGroupKey_Book_DifferentFilesWithSameTitleDoNotGroup()
    {
        // Two files both titled "Dune" (novel vs short-story collection, or .epub vs .pdf) → different keys.
        var a = new MediaBrowser.Controller.Entities.Book { Name = "Dune", Path = @"C:\a\Dune.epub" };
        var b = new MediaBrowser.Controller.Entities.Book { Name = "Dune", Path = @"C:\b\Dune.pdf" };
        Assert.NotEqual(DuplicateScanner.GetGroupKey(a), DuplicateScanner.GetGroupKey(b));
    }

    [Fact]
    public void GetGroupKey_Book_ReturnsNullWhenNameMissing()
    {
        var book = new MediaBrowser.Controller.Entities.Book { Name = string.Empty };
        Assert.Null(DuplicateScanner.GetGroupKey(book));
    }
}
