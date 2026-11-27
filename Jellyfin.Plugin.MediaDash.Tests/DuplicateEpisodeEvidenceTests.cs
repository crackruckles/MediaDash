using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Metadata;
using Jellyfin.Plugin.MediaDash.Scanners;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Issue #3 field reports, re-checked with what each filename says and the TVDb episode list.
public sealed class DuplicateEpisodeEvidenceTests
{
    private static readonly IReadOnlyList<TvdbEpisode> Firefly =
    [
        new(0, 1, "Serenity", 119),
        new(1, 1, "Serenity", 86),
        new(1, 2, "The Train Job", 43),
        new(1, 3, "Bushwhacked", 43),
        new(1, 7, "Jaynestown", 43),
        new(1, 14, "Objects in Space", 43)
    ];

    private static readonly IReadOnlyList<TvdbEpisode> Futurama =
    [
        new(0, 5, "Bender's Big Score", 88),
        new(0, 6, "The Beast with a Billion Backs", 89),
        new(0, 7, "Bender's Game", 88),
        new(0, 8, "Into the Wild Green Yonder", 89)
    ];

    private static PluginConfiguration Config() => new()
    {
        DuplicateAutoFixConfidence = 0.80,
        DuplicateExactHashEnabled = true,
        DuplicateTitleJaccardVeto = 0.40,
        DuplicateRuntimeVetoPct = 15
    };

    private static EpisodeEvidence.Verdict Compare(string a, string b, int season, int episode, IReadOnlyList<TvdbEpisode>? guide = null, string show = "Show")
        => EpisodeEvidence.Compare(EpisodeEvidence.Read("/tv/" + a, guide, show), EpisodeEvidence.Read("/tv/" + b, guide, show), season, episode);

    [Theory]
    [InlineData("Doctor Who S13E01 Part 1.mkv", "Doctor Who S13E01 Part 2.mkv")]
    [InlineData("Doctor Who S13E01 (1).mkv", "Doctor Who S13E01 (2).mkv")]
    [InlineData("Doctor Who S13E01 Part One.mkv", "Doctor Who S13E01 Part Two.mkv")]
    [InlineData("Doctor Who S13E01.mkv", "Doctor Who S13E01 Pt.2.mkv")]
    public void DifferentParts_AreNeverCopies(string a, string b)
    {
        var verdict = Compare(a, b, 13, 1);
        Assert.True(verdict.Veto);
        Assert.Equal("differentParts", verdict.Reason);
    }

    [Fact]
    public void SamePart_OfOneEpisode_IsACopy()
    {
        var verdict = Compare("Doctor Who S13E01 Part 2 720p.mkv", "Doctor.Who.S13E01.Part.2.1080p.mkv", 13, 1);
        Assert.False(verdict.Veto);
        Assert.True(verdict.Confirmed);
    }

    [Fact]
    public void DoubleEpisode_IsNotACopyOfTheSingleEpisode()
    {
        var verdict = Compare("Show S01E01-E02 1080p.mkv", "Show S01E01 720p.mkv", 1, 1);
        Assert.True(verdict.Veto);
        Assert.Equal("differentEpisodeNumbers", verdict.Reason);
    }

    [Theory]
    [InlineData("Show S01E01E02.mkv", 1, 1, 2)]
    [InlineData("Show S01E01-02.mkv", 1, 1, 2)]
    [InlineData("Show 1x05.mkv", 1, 5, 5)]
    [InlineData("Show Season 2 Episode 7.mkv", 2, 7, 7)]
    [InlineData("Show S01E01 - 720p.mkv", 1, 1, 1)]
    [InlineData("Show S01E01-1080p.mkv", 1, 1, 1)]
    public void ReadsEpisodeNumbers(string name, int season, int first, int last)
    {
        var claim = EpisodeEvidence.Read("/tv/" + name, null, "Show");
        Assert.Equal(season, claim.Season);
        Assert.Equal(first, claim.FirstEpisode);
        Assert.Equal(last, claim.LastEpisode);
    }

    [Fact]
    public void LiteralNames_JellyfinFiledOnOneNumber_AreDifferentEpisodes()
    {
        // Jellyfin filed both under S01E07; their names say S01E02 and S01E03.
        var verdict = Compare("Firefly Season 1 Episode 2.mkv", "Firefly Season 1 Episode 3.mkv", 1, 7);
        Assert.True(verdict.Veto);
    }

    [Fact]
    public void TvdbTitles_TellApartEpisodesWithoutNumbers()
    {
        var verdict = Compare("Firefly - The Train Job.mkv", "Firefly - Bushwhacked.mkv", 1, 7, Firefly, "Firefly");
        Assert.True(verdict.Veto);
    }

    [Fact]
    public void TvdbTitle_NamingAnotherEpisode_RemovesTheFileFromTheSlot()
    {
        var claim = EpisodeEvidence.Read("/tv/Firefly - Objects in Space.mkv", Firefly, "Firefly");
        Assert.False(claim.Names(1, 7));
        Assert.True(claim.Names(1, 14));
    }

    [Fact]
    public void TvdbTitle_ConfirmsCopiesWithoutNumbers()
    {
        var verdict = Compare("Firefly - Jaynestown 720p.mkv", "Firefly.Jaynestown.1080p.BluRay.mkv", 1, 7, Firefly, "Firefly");
        Assert.False(verdict.Veto);
        Assert.True(verdict.Confirmed);
    }

    [Fact]
    public void TiedTitles_StayAmbiguousInsteadOfGuessing()
    {
        // "Serenity" is both the pilot and the film special; either is fine for the pilot slot.
        var claim = EpisodeEvidence.Read("/tv/Firefly - Serenity.mkv", Firefly, "Firefly");
        Assert.True(claim.Names(1, 1));
        Assert.True(claim.Names(0, 1));
    }

    [Fact]
    public void FuturamaFilms_FiledAsOneSpecial_AreVetoedByTitle()
    {
        var verdict = Compare(
            "Movie.3.Futurama.Benders.Game.2008.1080p.BluRay.DTS.x264-PerfectionHD.mkv",
            "Movie.2.Futurama.The.Beast.With.a.Billion.Backs.2008.1080p.WEB-DL.DD5.x264-DON.mkv",
            0,
            7,
            Futurama,
            "Futurama");
        Assert.True(verdict.Veto);
    }

    [Fact]
    public void Specials_WithoutAnyConfirmation_AreNotOffered()
    {
        var verdict = Compare("Futurama Special A.mkv", "Futurama Special B.mkv", 0, 3);
        Assert.True(verdict.Veto);
        Assert.Equal("unconfirmedSpecial", verdict.Reason);
    }

    [Fact]
    public void ShowTitleAlone_IsNotEpisodeEvidence()
    {
        var guide = new List<TvdbEpisode> { new(1, 1, "Futurama", 22) };
        var claim = EpisodeEvidence.Read("/tv/Futurama 1080p.mkv", guide, "Futurama");
        Assert.Empty(claim.TitleMatches);
    }

    [Theory]
    [InlineData("Rick and Morty S09E05 1080p.mkv", "Rick.and.Morty.2013.S09E05.720p.mkv", true)]
    [InlineData("The Haunting of Hill House S01E10.mkv", "The Haunting S01E10.mkv", false)]
    [InlineData("www.UIndex.org - Rick and Morty S09E05.mkv", "Rick and Morty S09E05.mkv", true)]
    [InlineData("S09E05.mkv", "S09E05.mkv", false)]
    public void ShowNamesAreComparedAcrossFolders(string a, string b, bool same)
        => Assert.Equal(same, EpisodeEvidence.SameShowName("/tv/x/" + a, "/tv/y/" + b));

    [Fact]
    public void EpisodeVeto_StopsAPairThatWouldOtherwiseScore()
    {
        var keeper = new DuplicateScanner.Candidate { Path = "/tv/Doctor Who/Doctor Who S13E01 Part 1.mkv", Size = 1 };
        var loser = new DuplicateScanner.Candidate { Path = "/tv/Doctor Who/Doctor Who S13E01 Part 2.mkv", Size = 1 };
        var verdict = EpisodeEvidence.Compare(EpisodeEvidence.Read(keeper.Path, null, "Doctor Who"), EpisodeEvidence.Read(loser.Path, null, "Doctor Who"), 13, 1);

        var (_, withoutCheck, _) = DuplicateScanner.ScorePair(keeper, loser, ConfidenceTier.Identified, true, false, Config());
        var (confidence, vetoed, signals) = DuplicateScanner.ScorePair(keeper, loser, ConfidenceTier.Identified, true, false, Config(), verdict);

        Assert.False(withoutCheck);
        Assert.True(vetoed);
        Assert.Equal(0.0, confidence);
        Assert.Equal("differentParts", signals["vetoReason"]);
    }

    [Fact]
    public void ByteIdenticalFiles_StillMatchWhateverTheirNamesSay()
    {
        var keeper = new DuplicateScanner.Candidate { Path = "/tv/Show/Show S01E01.mkv", Size = 1 };
        var loser = new DuplicateScanner.Candidate { Path = "/tv/Show/Show S01E01 (1).mkv", Size = 1 };
        var verdict = EpisodeEvidence.Compare(EpisodeEvidence.Read(keeper.Path, null, "Show"), EpisodeEvidence.Read(loser.Path, null, "Show"), 1, 1);
        var (confidence, vetoed, _) = DuplicateScanner.ScorePair(keeper, loser, ConfidenceTier.Identified, true, hashesMatch: true, Config(), verdict);
        Assert.False(vetoed);
        Assert.Equal(1.0, confidence);
    }

    [Fact]
    public void ConfirmedEpisode_RaisesConfidence()
    {
        var keeper = new DuplicateScanner.Candidate { Path = "/tv/Show/Show S01E01 1080p.mkv", Size = 1 };
        var loser = new DuplicateScanner.Candidate { Path = "/tv/Show/Show.S01E01.720p.mkv", Size = 1 };
        var verdict = EpisodeEvidence.Compare(EpisodeEvidence.Read(keeper.Path, null, "Show"), EpisodeEvidence.Read(loser.Path, null, "Show"), 1, 1);
        var (confidence, vetoed, _) = DuplicateScanner.ScorePair(keeper, loser, ConfidenceTier.Identified, true, false, Config(), verdict);
        Assert.False(vetoed);
        Assert.Equal(0.95, confidence);
    }

    [Theory]
    [InlineData("episode:tvdb:78874:s1:e7", 78874)]
    [InlineData("episode:0f1e:s1:e7", null)]
    [InlineData("movie:tmdb:1:2000", null)]
    public void ReadsTheSeriesIdFromTheGroupKey(string key, int? expected)
        => Assert.Equal(expected, DuplicateScanner.SeriesTvdbId(key));

    [Fact]
    public async Task EpisodeList_IsReadAcrossPagesInEnglish()
    {
        using var handler = new EpisodePages(englishMissing: false);
        using var client = new TvdbClient(() => new HttpClient(handler, false), NullLogger.Instance, TimeSpan.Zero);
        var episodes = await client.GetEpisodesAsync("key", 78874, CancellationToken.None);
        Assert.Equal(new[] { "Serenity", "The Train Job" }, episodes.Select(e => e.Name));
        Assert.Equal(43, episodes[1].RuntimeMinutes);
        Assert.All(handler.Paths, path => Assert.Contains("/eng", path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EpisodeList_FallsBackToOriginalTitles()
    {
        using var handler = new EpisodePages(englishMissing: true);
        using var client = new TvdbClient(() => new HttpClient(handler, false), NullLogger.Instance, TimeSpan.Zero);
        var episodes = await client.GetEpisodesAsync("key", 78874, CancellationToken.None);
        Assert.Equal(2, episodes.Count);
    }

    private sealed class EpisodePages(bool englishMissing) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/login", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("""{"data":{"token":"t"}}"""));
            }

            var english = uri.AbsolutePath.EndsWith("/eng", StringComparison.Ordinal);
            if (english && englishMissing)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            Paths.Add(uri.AbsolutePath);
            return Task.FromResult(uri.Query.Contains("page=0", StringComparison.Ordinal)
                ? Json("""{"data":{"episodes":[{"seasonNumber":1,"number":1,"name":"Serenity","runtime":86}]},"links":{"next":"https://api4.thetvdb.com/v4/series/78874/episodes/default?page=1"}}""")
                : Json("""{"data":{"episodes":[{"seasonNumber":1,"number":2,"name":"The Train Job","runtime":43}]},"links":{"next":null}}"""));
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
