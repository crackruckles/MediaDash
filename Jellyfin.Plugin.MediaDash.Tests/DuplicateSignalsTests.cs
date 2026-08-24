using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Field-report spec §3 + §8. Confidence signals must be deterministic and unit-testable —
// scoring downstream (§4) trusts these exactly.
public sealed class DuplicateSignalsTests
{
    // ── TitleTokenJaccard ─────────────────────────────────────────────────────

    [Fact]
    public void TitleJaccard_SameTitleDifferentQuality_IsOne()
    {
        // Both stems reduce to just the title token "inception" after noise + year strip.
        // Release-group suffixes are handled in a separate test — the two names here have none.
        var j = DuplicateSignals.TitleTokenJaccard(
            "Inception.2010.1080p.BluRay.x264",
            "Inception.2010.2160p.WEB-DL.HDR.x265");
        Assert.Equal(1.0, j);
    }

    [Fact]
    public void TitleJaccard_ReleaseGroupNamesAreNotStripped_AndReduceJaccard()
    {
        // Documented limitation: the noise set can't enumerate every scene/p2p group name
        // (YIFY, RARBG, DON, PerfectionHD…). If two rips of the same movie carry different
        // group tags, those tokens land in the disjoint set. Real-world rips of the *same*
        // title still Jaccard well above the 0.40 veto because the title token(s) dominate;
        // this test just pins down the shape so future noise-set changes don't accidentally
        // suppress the group tokens (or over-suppress and strip legitimate title words).
        var j = DuplicateSignals.TitleTokenJaccard(
            "Inception.2010.1080p.BluRay.x264-YIFY",
            "Inception.2010.2160p.WEB-DL.x265-RARBG");
        // Union: { inception, yify, rarbg }. Intersect: { inception }. Jaccard = 1/3.
        Assert.Equal(1.0 / 3.0, j, precision: 6);
    }

    [Fact]
    public void TitleJaccard_DifferentTitlesSameYear_IsLowAndVetoes()
    {
        // The GitHub issue #3 regression: two different Futurama specials collided on episode key.
        // Their filename title tokens barely overlap; Jaccard must fall below the 0.40 veto.
        var j = DuplicateSignals.TitleTokenJaccard(
            "Movie.3.Futurama.Benders.Game.2008.1080p.BluRay.DTS.x264-PerfectionHD",
            "Movie.2.Futurama.The.Beast.With.a.Billion.Backs.2008.1080p.WEB-DL.DD5.x264-DON");
        Assert.InRange(j, 0.0, 0.399);
    }

    [Fact]
    public void TitleJaccard_EitherSideEmptyAfterStrip_IsNaN()
    {
        // A stem that reduces to zero title tokens (all noise) can't be judged. Caller must skip
        // the title veto instead of vetoing every unnamed-after-strip pair.
        var j = DuplicateSignals.TitleTokenJaccard("1080p.BluRay.x264", "Inception.2010");
        Assert.True(double.IsNaN(j));
    }

    [Fact]
    public void TitleJaccard_YearsAndNoiseAreStripped_EditionWordsAreKept()
    {
        // "extended" / "directors" survive the strip because they legitimately differentiate content.
        var j = DuplicateSignals.TitleTokenJaccard(
            "Blade.Runner.1982.Directors.Cut.1080p.BluRay.x264",
            "Blade.Runner.1982.Extended.Cut.1080p.BluRay.x264");
        // Shared: blade, runner, cut. Differing: directors vs extended. Jaccard = 3 / 5.
        Assert.Equal(3.0 / 5.0, j, precision: 6);
    }

    [Fact]
    public void TitleJaccard_UnrelatedTitles_IsZero()
    {
        var j = DuplicateSignals.TitleTokenJaccard(
            "Inception.2010.1080p.BluRay.x264",
            "Interstellar.2014.1080p.BluRay.x264");
        Assert.Equal(0.0, j);
    }

    // ── RuntimeDeltaFraction ─────────────────────────────────────────────────

    [Fact]
    public void RuntimeDelta_NullOrZero_IsNull()
    {
        Assert.Null(DuplicateSignals.RuntimeDeltaFraction(null, 100_000_000L));
        Assert.Null(DuplicateSignals.RuntimeDeltaFraction(100_000_000L, null));
        Assert.Null(DuplicateSignals.RuntimeDeltaFraction(0L, 100_000_000L));
        Assert.Null(DuplicateSignals.RuntimeDeltaFraction(-1L, 100_000_000L));
    }

    [Fact]
    public void RuntimeDelta_IdenticalRuntimes_IsZero()
    {
        var d = DuplicateSignals.RuntimeDeltaFraction(1_000_000L, 1_000_000L);
        Assert.Equal(0.0, d);
    }

    [Fact]
    public void RuntimeDelta_TenPercentApart_IsTenPercent()
    {
        // 900 vs 1000 → delta 100 / max 1000 = 0.10.
        var d = DuplicateSignals.RuntimeDeltaFraction(900L, 1000L);
        Assert.Equal(0.10, d!.Value, precision: 6);
    }

    // ── TierForKey ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("movie:tmdb:12345")]
    [InlineData("movie:imdb:tt0133093")]
    [InlineData("movie:tvdb:9876")]
    [InlineData("book:isbn:9780451524935")]
    [InlineData("audio:musicbrainztrack:abcd")]
    public void TierForKey_ProviderIdKeys_AreIdentified(string key)
    {
        // Enum type is internal; xUnit needs the test method public, so tier expectation stays
        // in the method body rather than in [InlineData].
        Assert.Equal(ConfidenceTier.Identified, DuplicateSignals.TierForKey(key));
    }

    [Theory]
    [InlineData("movie:name:inception:2010:something")]
    [InlineData("episode:abcd1234:s1:e1")]
    [InlineData("book:name:dune:something")]
    [InlineData("audio:name:artist:album:track:120:something")]
    [InlineData("")]
    public void TierForKey_FallbackKeys_AreHeuristic(string key)
    {
        Assert.Equal(ConfidenceTier.Heuristic, DuplicateSignals.TierForKey(key));
    }
}
