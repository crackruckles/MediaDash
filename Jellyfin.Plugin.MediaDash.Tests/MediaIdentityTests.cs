using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Metadata;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class MediaIdentityTests
{
    [Theory]
    [InlineData("<tvshow><id>477406</id></tvshow>", 477406)]
    [InlineData("<tvshow><tvdbid>477406</tvdbid></tvshow>", 477406)]
    [InlineData("<tvshow><uniqueid type=\"tvdb\">477406</uniqueid></tvshow>", 477406)]
    [InlineData("<tvshow><id>477406</id><uniqueid type=\"tvdb\">123</uniqueid></tvshow>", null)]
    [InlineData("<movie><id>477406</id></movie>", null)]
    [InlineData("<tvshow><uniqueid type=\"tmdb\">477406</uniqueid></tvshow>", null)]
    [InlineData("<!DOCTYPE tvshow [<!ENTITY id '477406'>]><tvshow><id>&id;</id></tvshow>", null)]
    [InlineData("<tvshow><id>477406", null)]
    [InlineData("", null)]
    public void SeriesNfoIdentityRequiresOneValidTvdbId(string xml, int? expected)
    {
        var root = Directory.CreateTempSubdirectory("md-nfo-id-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "tvshow.nfo"), xml);
            Assert.Equal(expected, MediaIdentityService.ReadSeriesNfoTvdbId(root.FullName));
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData("Outlander - Blood of My Blood", "Outlander Blood of My Blood (2025)", "Outlander: Blood of My Blood", 2025)]
    [InlineData("The Mentalist", "The Mentalist (2008)", "The Mentalist", 2008)]
    [InlineData("Kusuriya no Hitorigoto - 53 (WEB 1080p) [Anarchy]", "The Apothecary Diaries (2023)", "The Apothecary Diaries", 2023)]
    public async Task NfoIdentityMergesAlternateFoldersWithoutApi(string release, string canonical, string title, int year)
    {
        var root = Directory.CreateTempSubdirectory("md-nfo-master-");
        try
        {
            var db = new MediaDashDb(Path.Combine(root.FullName, "test.db"));
            var tv = Directory.CreateDirectory(Path.Combine(root.FullName, "TV")).FullName;
            var master = new Series { Id = Guid.NewGuid(), Name = title, ProductionYear = year,
                Path = Directory.CreateDirectory(Path.Combine(tv, canonical)).FullName };
            master.ProviderIds["Tvdb"] = "477406";
            var other = new Series { Id = Guid.NewGuid(), Name = release,
                Path = Directory.CreateDirectory(Path.Combine(tv, release)).FullName };
            File.WriteAllText(Path.Combine(other.Path, "tvshow.nfo"), "<tvshow><uniqueid type=\"tvdb\">477406</uniqueid></tvshow>");
            File.WriteAllText(Path.Combine(other.Path, "episode.mkv"), "unmoved episode");
            var service = new MediaIdentityService(db.Identities, null, NullLogger.Instance, () => [master, other]);
            var masters = await service.ResolveSeriesMastersAsync([], new PluginConfiguration { TvTargetPath = tv }, default);
            Assert.Equal(master.Path, masters[other.Id]);
            Assert.Equal(477406, db.Identities.Get(other.Id)!.TvdbId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(true);
        }
    }

    [Theory]
    [InlineData("www.UIndex.org    -    The Mentalist S04E03 1080p AMZN WEB-DL DDP5 1 H 264-ViSUM", "The Mentalist", null)]
    [InlineData("Sinners 2025 1080p", "Sinners", 2025)]
    [InlineData("The Mentalist (2008)", "The Mentalist", 2008)]
    [InlineData("Scrubs.2026.S02E07.720p.HDTV.x264-SYNCOPY", "Scrubs", 2026)]
    [InlineData("[SubsPlease] Frieren - 12 (1080p)", "Frieren - 12", null)]
    [InlineData("Last Week Tonight with John Oliver S13E28 Midterms 2026 1080P", "Last Week Tonight with John Oliver", null)]
    [InlineData("1923 (2022)", "1923", 2022)]
    public void CleanTitleStripsReleaseNoise(string raw, string title, int? year)
    {
        var (cleaned, parsed) = MediaIdentityService.CleanTitle(raw);
        Assert.Equal(title, cleaned);
        Assert.Equal(year, parsed);
    }

    [Fact]
    public void PickMatchRequiresAnExactTitleAndRejectsAmbiguity()
    {
        var hits = new[]
        {
            new TvdbSearchHit(82459, "The Mentalist", 2008, []),
            new TvdbSearchHit(469029, "The Senti Mentals", null, []),
        };
        Assert.Equal(82459, MediaIdentityService.PickMatch(hits, "The Mentalist", null));
        Assert.Equal(82459, MediaIdentityService.PickMatch(hits, "the mentalist", 2008));
        Assert.Null(MediaIdentityService.PickMatch(hits, "The Mentalist", 1995));
        Assert.Null(MediaIdentityService.PickMatch(hits, "Mentalist", null));

        var office = new[] { new TvdbSearchHit(73244, "The Office (US)", 2005, ["The Office"]), new TvdbSearchHit(78107, "The Office (UK)", 2001, ["The Office"]) };
        Assert.Null(MediaIdentityService.PickMatch(office, "The Office", null));
        Assert.Equal(73244, MediaIdentityService.PickMatch(office, "The Office", 2005));
    }

    [Fact]
    public async Task ClientSpacesRequestsUnderTheRateLimitAndSignsInOnce()
    {
        var handler = new FakeTvdb();
        handler.Search["The Mentalist"] = """[{"tvdb_id":"82459","name":"The Mentalist","year":"2008"}]""";
        using var client = new TvdbClient(() => new HttpClient(handler, false), NullLogger.Instance, TimeSpan.FromMilliseconds(60));
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 6; i++)
        {
            var hits = await client.SearchAsync("key", "The Mentalist", "series", CancellationToken.None);
            Assert.Equal(82459, Assert.Single(hits).Id);
        }

        // 1 login + 6 searches = 7 request starts, at least 6 gaps of 60 ms.
        Assert.Equal(7, client.RequestsSent);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(6 * 60 - 15), watch.Elapsed.ToString());
        Assert.Equal(1, handler.Logins);
    }

    [Fact]
    public async Task ClientSignsInAgainWhenTheTokenExpires()
    {
        var handler = new FakeTvdb { RejectFirstSearch = true };
        handler.Remote["tt1196946"] = """[{"series":{"id":82459,"name":"The Mentalist"}}]""";
        using var client = new TvdbClient(() => new HttpClient(handler, false), NullLogger.Instance, TimeSpan.Zero);
        Assert.Equal(82459, await client.RemoteIdAsync("key", "tt1196946", "series", CancellationToken.None));
        Assert.Null(await client.RemoteIdAsync("key", "tt1196946", "movie", CancellationToken.None));
        Assert.Equal(2, handler.Logins);
    }

    [Fact]
    public async Task ClientReportsARejectedKey()
    {
        var handler = new FakeTvdb { RejectLogin = true };
        using var client = new TvdbClient(() => new HttpClient(handler, false), NullLogger.Instance, TimeSpan.Zero);
        await Assert.ThrowsAsync<TvdbAuthenticationException>(() => client.SearchAsync("bad", "x", "series", CancellationToken.None));
    }

    [Fact]
    public async Task MastersMergeSameTitleFoldersButNotConflictingYears()
    {
        var root = Directory.CreateTempSubdirectory("md-identity-");
        try
        {
            var tv = Path.Combine(root.FullName, "TV");
            var db = new MediaDashDb(Path.Combine(root.FullName, "test.db"));
            var handler = new FakeTvdb();
            handler.Search["The Mentalist"] = """[{"tvdb_id":"82459","name":"The Mentalist","year":"2008"}]""";
            using var client = new TvdbClient(() => new HttpClient(handler, false), NullLogger.Instance, TimeSpan.Zero);
            var service = new MediaIdentityService(db.Identities, client, NullLogger.Instance);
            Series Show(string folder, string name, string? tvdb, int? year)
            {
                Directory.CreateDirectory(Path.Combine(tv, folder));
                var series = new Series { Id = Guid.NewGuid(), Name = name, Path = Path.Combine(tv, folder), ProductionYear = year };
                if (tvdb is not null)
                {
                    series.ProviderIds["Tvdb"] = tvdb;
                }

                return series;
            }

            var master = Show("The Mentalist (2008)", "The Mentalist", "82459", 2008);
            var plain = Show("The Mentalist", "The Mentalist", "82459", 2008);
            var junk = Show("www.UIndex.org    -    The Mentalist S04E03 1080p", "www UIndex org    -    The Mentalist", null, null);
            var sinners = Show("Sinners (2016)", "Sinners", "308095", 2016);
            var sinnersMovie = Show("Sinners 2025 1080p", "Sinners", "308095", 2016);
            BaseItem[] items = [master, plain, junk, sinners, sinnersMovie];
            var config = new PluginConfiguration { TvTargetPath = tv, TvdbApiKey = "key" };

            var masters = await service.ResolveSeriesMastersAsync(items, config, CancellationToken.None);
            Assert.Equal(master.Path, masters[plain.Id]);
            Assert.Equal(master.Path, masters[junk.Id]);
            Assert.Equal(master.Path, masters[master.Id]);
            Assert.Equal(sinners.Path, masters[sinners.Id]);
            Assert.False(masters.ContainsKey(sinnersMovie.Id));
            Assert.Equal(82459, db.Identities.Get(junk.Id)!.TvdbId);
            Assert.Equal("library-match", db.Identities.Get(junk.Id)!.TvdbSource);
            Assert.Equal("jellyfin", db.Identities.Get(plain.Id)!.TvdbSource);
            Assert.Equal(master.Path, db.Identities.GetMaster("series", 82459));

            // Second scan answers from the cache: no new search requests.
            var searches = handler.Searches;
            await service.ResolveSeriesMastersAsync(items, config, CancellationToken.None);
            Assert.Equal(searches, handler.Searches);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SeriesAreReadFromJellyfinWhenTheScanListsOnlyEpisodes()
    {
        var root = Directory.CreateTempSubdirectory("md-identity-episodes-");
        try
        {
            var tv = Path.Combine(root.FullName, "TV");
            var db = new MediaDashDb(Path.Combine(root.FullName, "test.db"));
            var master = new Series { Id = Guid.NewGuid(), Name = "MobLand", Path = Directory.CreateDirectory(Path.Combine(tv, "MobLand (2025)")).FullName, ProductionYear = 2025 };
            master.ProviderIds["Tvdb"] = "446831";
            var plain = new Series { Id = Guid.NewGuid(), Name = "MobLand", Path = Directory.CreateDirectory(Path.Combine(tv, "MobLand")).FullName, ProductionYear = 2025 };
            plain.ProviderIds["Tvdb"] = "446831";
            var service = new MediaIdentityService(db.Identities, null, NullLogger.Instance, () => [master, plain]);
            BaseItem[] items =
            [
                new Episode { Id = Guid.NewGuid(), SeriesId = master.Id, Name = "One", Path = Path.Combine(master.Path, "S01E01.mkv") },
                new Episode { Id = Guid.NewGuid(), SeriesId = plain.Id, Name = "Two", Path = Path.Combine(plain.Path, "S02E01.mkv") },
            ];

            var masters = await service.ResolveSeriesMastersAsync(items, new PluginConfiguration { TvTargetPath = tv }, CancellationToken.None);
            Assert.Equal(master.Path, masters[plain.Id]);
            Assert.Equal(446831, db.Identities.Get(plain.Id)!.TvdbId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void LeftoverFoldersOfAGroupedSeriesAreFlaggedOnlyWhenNoMediaRemains()
    {
        var root = Directory.CreateTempSubdirectory("md-leftover-");
        try
        {
            var tv = Path.Combine(root.FullName, "TV");
            string Folder(string name, params string[] files)
            {
                var path = Directory.CreateDirectory(Path.Combine(tv, name)).FullName;
                foreach (var file in files)
                {
                    File.WriteAllText(Path.Combine(path, file), "x");
                }

                return path;
            }

            var master = Folder("MobLand (2025)", "tvshow.nfo");
            var artOnly = Folder("MobLand S02E04 Blank Curtain 1080p WEB H264-CAKES", "tvshow.nfo", "folder.jpg", "backdrop.jpg", "RARBG.txt");
            var withVideo = Folder("MobLand S02E05 1080p WEB H264-MeGusta", "folder.jpg", "MobLand.S02E05.mkv");
            var otherYear = Folder("Sinners 2025 1080p", "folder.jpg");
            var sinners = Folder("Sinners (2016)", "tvshow.nfo");
            var noMaster = Folder("Gone S01E01 1080p", "folder.jpg");
            GroupedLeftover[] candidates =
            [
                new(artOnly, 446831, master, 2025),
                new(withVideo, 446831, master, 2025),
                new(otherYear, 308095, sinners, 2016),
                new(noMaster, 1, Path.Combine(tv, "Gone (2020)"), 2020),
            ];
            var issues = new List<Jellyfin.Plugin.MediaDash.Data.Issue>();
            Scanners.OrphanCleanupScanner.DetectGroupedLeftovers(candidates, [tv], issues, CancellationToken.None);
            var issue = Assert.Single(issues);
            Assert.Equal(artOnly, issue.Path);
            Assert.Equal(Scanners.OrphanCleanupScanner.KindGroupedLeftover, Fixers.OrphanCleanupFixer.ReadKind(issue.DetailsJson));
            Assert.Contains("MobLand (2025)", issue.SuggestedFix, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("The Mentalist (2008)", true)]
    [InlineData("The Office (US)", true)]
    [InlineData("Miraculous - Tales of Ladybug & Cat Noir", true)]
    [InlineData("www.UIndex.org    -    War.2026.S01E07.1080p.WEB.h264-ETHEL", false)]
    [InlineData("Miraculous Tales of Ladybug and Cat Noir S06E18 Heartfixer 1080p DSNP WEB-DL", false)]
    [InlineData("Shoresy.S01.1080p.HULU.WEBRip.DDP5.1.x264-SMURF[eztv.re]", false)]
    public void OnlyPlainTitledFoldersCanBeMasters(string folder, bool clean)
        => Assert.Equal(clean, MediaIdentityService.IsCleanFolderName(folder));

    [Fact]
    public async Task UnidentifiedFoldersTakeTheLibrarysIdBeforeAskingTvdbAndReleaseFoldersNeverBecomeMasters()
    {
        var root = Directory.CreateTempSubdirectory("md-identity-library-");
        try
        {
            var tv = Path.Combine(root.FullName, "TV");
            var db = new MediaDashDb(Path.Combine(root.FullName, "test.db"));
            var handler = new FakeTvdb();
            handler.Search["War"] = """[{"tvdb_id":"467092","name":"War","year":"2026"}]""";
            using var client = new TvdbClient(() => new HttpClient(handler, false), NullLogger.Instance, TimeSpan.Zero);
            Series Show(string folder, string name, string? tvdb, int? year)
            {
                var series = new Series { Id = Guid.NewGuid(), Name = name, Path = Directory.CreateDirectory(Path.Combine(tv, folder)).FullName, ProductionYear = year };
                if (tvdb is not null)
                {
                    series.ProviderIds["Tvdb"] = tvdb;
                }

                return series;
            }

            var war = Show("WAR (2026)", "WAR", "477406", 2026);
            var warRelease = Show("War 2026 S01E01 Its Done 1080p AMZN WEB-DL", "War 2026", "477406", 2026);
            var unidentified = Show("www.UIndex.org    -    War.2026.S01E07.1080p.WEB.h264-ETHEL", "www UIndex org    -    War 2026", null, null);
            var chibiA = Show("Miraculous Chibi S01E01 1080p WEB", "Miraculous Chibi", "350873", 2018);
            var chibiB = Show("Miraculous Chibi S01E02 1080p WEB", "Miraculous Chibi", "350873", 2018);
            Series[] all = [war, warRelease, unidentified, chibiA, chibiB];
            db.Identities.SetMaster("series", 350873, chibiA.Path, "Miraculous Chibi", 2018);
            var service = new MediaIdentityService(db.Identities, client, NullLogger.Instance, () => all);

            var masters = await service.ResolveSeriesMastersAsync([], new PluginConfiguration { TvTargetPath = tv }, CancellationToken.None);
            Assert.Equal(477406, db.Identities.Get(unidentified.Id)!.TvdbId);
            Assert.Equal("library-match", db.Identities.Get(unidentified.Id)!.TvdbSource);
            Assert.Equal(0, handler.Searches);
            Assert.Equal(war.Path, masters[unidentified.Id]);
            Assert.False(masters.ContainsKey(chibiA.Id));
            Assert.Null(db.Identities.GetMaster("series", 350873));
            Assert.DoesNotContain(db.Identities.GetGroupedLeftovers(), leftover => leftover.TvdbId == 350873);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AnEmptySettingUsesTheBuiltInProjectKey()
    {
        var root = Directory.CreateTempSubdirectory("md-identity-key-");
        try
        {
            var db = new MediaDashDb(Path.Combine(root.FullName, "test.db"));
            var handler = new FakeTvdb();
            using var client = new TvdbClient(() => new HttpClient(handler, false), NullLogger.Instance, TimeSpan.Zero);
            var service = new MediaIdentityService(db.Identities, client, NullLogger.Instance);
            var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "TV", "Unknown Show S01E01 1080p")).FullName;
            BaseItem[] items = [new Series { Id = Guid.NewGuid(), Name = "Unknown Show", Path = folder }];
            await service.ResolveSeriesMastersAsync(items, new PluginConfiguration { TvTargetPath = Path.Combine(root.FullName, "TV") }, CancellationToken.None);
            Assert.Equal(1, handler.Logins);
            Assert.Contains(TvdbClient.ProjectApiKey, handler.LoginBody, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    internal sealed class FakeTvdb : HttpMessageHandler
    {
        public Dictionary<string, string> Search { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Remote { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool RejectLogin { get; init; }

        public bool RejectFirstSearch { get; set; }

        public int Logins { get; private set; }

        public string? LoginBody { get; private set; }

        public int Searches { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/login", StringComparison.Ordinal))
            {
                Logins++;
                LoginBody = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
                return Task.FromResult(RejectLogin ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Json("""{"data":{"token":"t"}}"""));
            }

            if (RejectFirstSearch)
            {
                RejectFirstSearch = false;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            if (uri.AbsolutePath.Contains("/search/remoteid/", StringComparison.Ordinal))
            {
                var id = Uri.UnescapeDataString(uri.AbsolutePath[(uri.AbsolutePath.LastIndexOf('/') + 1)..]);
                return Task.FromResult(Remote.TryGetValue(id, out var body) ? Json("{\"data\":" + body + "}") : new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            Searches++;
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query)["query"] ?? string.Empty;
            return Task.FromResult(Json("{\"data\":" + (Search.TryGetValue(query, out var hits) ? hits : "[]") + "}"));
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
