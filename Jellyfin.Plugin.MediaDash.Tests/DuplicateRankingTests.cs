using System.Collections.Generic;
using System.IO;
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
        // Paths built via Path.Combine so tests pass on both Windows and Linux CI (Path.GetFileName on
        // Linux treats "\" as literal, keeping the whole "C:\a\Big Buck Test..." string as the filename).
        var a = new Movie { Name = "Big Buck Test", ProductionYear = 2020, Path = Path.Combine("a", "Big Buck Test (2020).mkv") };
        var b = new Movie { Name = "Big.Buck.Test", ProductionYear = 2020, Path = Path.Combine("backup", "Big.Buck.Test.2020.mkv") };
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
            Path = Path.Combine("music", "Miles Davis - Blue in Green.flac")
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
        var book = new MediaBrowser.Controller.Entities.Book { Name = "Dune", Path = Path.Combine("books", "Dune.epub") };
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

    [Fact]
    public void GetEdition_ExplicitEditionMarkerWinsAndIsPrefixed()
    {
        // Jellyfin {edition-X} marker takes priority and gets a distinctive prefix so a bare
        // filename that normalises to the same characters can't collide with the marker key.
        Assert.Equal("edition:directors cut", DuplicateScanner.GetEdition("Movie {edition-Directors Cut}.mkv"));
    }

    [Theory]
    [InlineData("Movie -1080p.mkv", "Movie -4K.mkv")]
    [InlineData("Movie -finalcut.mkv", "Movie.mkv")]
    [InlineData("Movie -Directors Cut.mkv", "Movie -Extended.mkv")]
    [InlineData("Movie.2020.2160p.BluRay.x265.HDR.mkv", "Movie.2020.1080p.WEB-DL.mkv")]
    [InlineData("Movie -DDP5.1.mkv", "Movie -AAC.mkv")]
    [InlineData("Show S01E01 - Pilot.mkv", "Show S01E01 - Pilot [PROPER].mkv")]
    // Real 2026-08-07 report: two files in the same folder, same TMDb id, different suffixes
    // after a shared prefix. Under the old whitelist neither suffix matched a known tag so both
    // fell into the same empty-edition bucket and got flagged as dupes.
    [InlineData("StarWarsCloneWars2003 - Connecting the Dots.mkv", "StarWarsCloneWars2003 - Bridging the Saga.mkv")]
    public void GetEdition_DifferentFilenames_YieldDifferentKeys(string a, string b)
    {
        // The user has "Treat editions as duplicates" off. Any filename variation between two
        // TMDb-matched files means the user filed them under different names — treat them as
        // different editions and don't flag as duplicates. Covers every possible suffix
        // combination without a whitelist that has to grow.
        Assert.NotEqual(DuplicateScanner.GetEdition(a), DuplicateScanner.GetEdition(b));
    }

    [Theory]
    [InlineData("Movie.mkv", "Movie.mkv")]
    [InlineData("Movie.mkv", "movie.mkv")]
    [InlineData("Movie.mkv", "Movie.mp4")]
    [InlineData("Movie (2020).mkv", "Movie.2020.mkv")]
    public void GetEdition_EssentiallyIdenticalFilenames_YieldSameKey(string a, string b)
    {
        // Byte-identical or near-identical copies (case, punctuation, container swap) still
        // normalise to the same string, so they group as duplicates as expected.
        Assert.Equal(DuplicateScanner.GetEdition(a), DuplicateScanner.GetEdition(b));
    }

    [Theory]
    [InlineData("theme.mp3")]
    [InlineData("theme.flac")]
    [InlineData("Theme.MP3")]
    [InlineData("themevideo.mp4")]
    [InlineData("theme-1.mp3")]
    [InlineData("theme-2.mp3")]
    [InlineData("poster.jpg")]
    [InlineData("folder.jpg")]
    [InlineData("backdrop.jpg")]
    [InlineData("banner.png")]
    [InlineData("logo.png")]
    [InlineData("clearart.png")]
    [InlineData("Movie-trailer.mp4")]
    [InlineData("Movie-behindthescenes.mp4")]
    [InlineData("Movie-featurette.mp4")]
    // Spaced/multi-word suffix variants (2026-08-07 bug report — Crank High Voltage extras
    // named "Crank High Voltage (2009) - Behind the scenes.m2ts" were being flagged as dupes
    // of the main film because the compact-form check didn't cover them).
    [InlineData("Crank High Voltage (2009) - Behind the scenes.m2ts")]
    [InlineData("Movie (2020) - Deleted Scenes.mkv")]
    [InlineData("Movie - Deleted Scene.mkv")]
    [InlineData("Movie - Trailer.mp4")]
    [InlineData("Movie - Featurette.mp4")]
    [InlineData("Movie - Interview.mp4")]
    [InlineData("Movie.2020.-.Behind.The.Scenes.mp4")]
    [InlineData("Movie_-_Behind_The_Scenes.mp4")]
    public void IsSidecarPath_KnownSidecarFilenames_ReturnTrue(string filename)
    {
        Assert.True(DuplicateScanner.IsSidecarPath(filename));
    }

    [Theory]
    [InlineData("/mnt/media/movies/Show/extras/short.mp4")]
    [InlineData("/mnt/media/movies/Show/trailers/final.mp4")]
    [InlineData("/mnt/media/movies/Show/behind the scenes/making of.mp4")]
    [InlineData("/mnt/media/movies/Show/deleted scenes/dropped.mp4")]
    [InlineData("/mnt/media/tv/Series/theme-music/opening.mp3")]
    [InlineData("/mnt/media/movies/Show/featurettes/commentary.mp4")]
    public void IsSidecarPath_KnownSidecarFolders_ReturnTrue(string path)
    {
        // Path.GetDirectoryName respects OS-native separators. On Linux the test runner treats
        // `C:\...` as one big filename, which is why the Windows-formatted literal that used to
        // live here failed CI. Use Path.Combine when covering platform-specific separator behaviour.
        Assert.True(DuplicateScanner.IsSidecarPath(path));
    }

    [Fact]
    public void IsSidecarPath_WindowsPathOnWindows_HandlesSeparator()
    {
        var winPath = System.IO.Path.Combine("media", "movies", "Show", "extras", "clip.mp4");
        Assert.True(DuplicateScanner.IsSidecarPath(winPath));
    }

    [Theory]
    [InlineData("/mnt/media/movies/Movie (2020).mkv")]
    [InlineData("/mnt/media/tv/Show/Season 01/Show S01E01.mkv")]
    [InlineData("C:\\media\\Movie.mkv")]
    public void IsSidecarPath_RegularLibraryFiles_ReturnFalse(string path)
    {
        Assert.False(DuplicateScanner.IsSidecarPath(path));
    }
}
