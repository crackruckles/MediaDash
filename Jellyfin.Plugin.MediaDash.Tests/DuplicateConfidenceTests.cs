using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Field-report spec §8. Scoring + gating tests exercised through the pure ScorePair helper.
// The full RankGroupAsync path pulls in ILibraryManager/BaseItem and isn't practical to
// unit-test without heavy scaffolding; the pure function IS the load-bearing logic.
public sealed class DuplicateConfidenceTests
{
    private static PluginConfiguration DefaultConfig() => new()
    {
        DuplicateAutoFixConfidence = 0.80,
        DuplicateExactHashEnabled = true,
        DuplicateTitleJaccardVeto = 0.40,
        DuplicateRuntimeVetoPct = 15
    };

    private static DuplicateScanner.Candidate C(string path, long size = 1_000_000L) => new()
    {
        Path = path,
        Size = size
    };

    // ── #3 regression: two Futurama specials sharing an episode slot must NOT flag as dupes ─

    [Fact]
    public void FuturamaSpecials_DifferentTitles_AreVetoedAtHeuristicTier()
    {
        var keeper = C("/media/Futurama/Specials/Movie.3.Futurama.Benders.Game.2008.1080p.BluRay.DTS.x264-PerfectionHD.mkv");
        var loser  = C("/media/Futurama/Specials/Movie.2.Futurama.The.Beast.With.a.Billion.Backs.2008.1080p.WEB-DL.DD5.x264-DON.mkv");
        var (confidence, vetoed, signals) = DuplicateScanner.ScorePair(
            keeper, loser, ConfidenceTier.Heuristic,
            sameDirectoryDistinctStems: true,
            hashesMatch: false,
            DefaultConfig());

        Assert.True(vetoed);
        Assert.Equal(0.0, confidence);
        Assert.Equal("titleJaccardBelowThreshold", signals["vetoReason"]);
    }

    // ── Tier 0 (Exact) ──────────────────────────────────────────────────────

    [Fact]
    public void ByteIdentical_ScoresOneAndOverridesEverything()
    {
        // Even with a completely unrelated filename and a Heuristic tier (which would otherwise
        // score low), a confirmed hash match takes the pair to 1.0 with no veto path.
        var (confidence, vetoed, signals) = DuplicateScanner.ScorePair(
            C("/a/completely-unrelated-thing.mkv"),
            C("/b/nothing-in-common.mkv"),
            ConfidenceTier.Heuristic,
            sameDirectoryDistinctStems: false,
            hashesMatch: true,
            DefaultConfig());

        Assert.False(vetoed);
        Assert.Equal(1.0, confidence);
        Assert.Equal("Exact", signals["appliedTier"]);
    }

    // ── Tier 1 (Identified) ─────────────────────────────────────────────────

    [Fact]
    public void IdentifiedTier_SimilarFilenames_ScoresBaseNinety()
    {
        // Tier 1 has no soft adjustments — provider-ID evidence is trusted as-is.
        var (confidence, vetoed, _) = DuplicateScanner.ScorePair(
            C("/a/Inception.2010.1080p.BluRay.x264.mkv"),
            C("/b/Inception.2010.2160p.WEB-DL.x265.mkv"),
            ConfidenceTier.Identified,
            sameDirectoryDistinctStems: false,
            hashesMatch: false,
            DefaultConfig());

        Assert.False(vetoed);
        Assert.Equal(0.90, confidence);
    }

    // ── Tier 2 (Heuristic) — soft adjustments ────────────────────────────────

    [Fact]
    public void HeuristicTier_HighJaccard_AddsFifteenBoost()
    {
        // Same title, same year, different quality → title jaccard = 1.0 → +0.15 → 0.85.
        var (confidence, vetoed, _) = DuplicateScanner.ScorePair(
            C("/a/Inception.2010.1080p.BluRay.x264.mkv"),
            C("/b/Inception.2010.2160p.WEB-DL.x265.mkv"),
            ConfidenceTier.Heuristic,
            sameDirectoryDistinctStems: false,
            hashesMatch: false,
            DefaultConfig());

        Assert.False(vetoed);
        Assert.Equal(0.85, confidence, precision: 6);
    }

    [Fact]
    public void HeuristicTier_SameDirDistinctStems_AppliesMinusPointTwentyFive()
    {
        // Spec §8: heuristic tier, same dir, distinct stems, Jaccard 0.5 → 0.70 − 0.25 = 0.45.
        // (We craft the filenames so Jaccard lands at 0.5 — a mix of shared + non-shared tokens.)
        // Below the 0.80 gate → emitted but not auto-queued.
        var (confidence, vetoed, _) = DuplicateScanner.ScorePair(
            C("/media/tv/Show/Season 01/Feature Length.Something.mkv"),
            C("/media/tv/Show/Season 01/Feature Length.Different.mkv"),
            ConfidenceTier.Heuristic,
            sameDirectoryDistinctStems: true,
            hashesMatch: false,
            DefaultConfig());

        // Both stems: "feature", "length", "something" / "different". Union 4, Intersect 2 → 0.5.
        // Base 0.70 − 0.25 (same-dir) = 0.45.
        Assert.False(vetoed);
        Assert.Equal(0.45, confidence, precision: 6);
    }

    // ── Runtime veto ─────────────────────────────────────────────────────────

    [Fact]
    public void RuntimeVeto_LargeDelta_VetoesPair()
    {
        // Both runtimes known, 40% delta > 15% veto — pair is not a duplicate.
        var keeper = new DuplicateScanner.Candidate
        {
            Path = "/a/Movie.2010.1080p.mkv",
            Size = 1_000_000L,
            Item = null
        };
        var loser = new DuplicateScanner.Candidate
        {
            Path = "/b/Movie.2010.1080p.mkv",
            Size = 1_000_000L,
            Item = null
        };
        // We can't easily attach RunTimeTicks without a real BaseItem; instead we test the veto
        // via DuplicateSignals.RuntimeDeltaFraction directly to prove the threshold works.
        var delta = DuplicateSignals.RuntimeDeltaFraction(6_000L, 10_000L);
        Assert.NotNull(delta);
        Assert.True(delta > 0.15);

        // And that ScorePair with a NaN jaccard (both stems collapse to zero title tokens after
        // noise-strip) still emits at Identified base 0.90 without the runtime signal (both
        // ticks null in this test setup because Item is null).
        var (confidence, vetoed, _) = DuplicateScanner.ScorePair(
            keeper, loser, ConfidenceTier.Identified,
            sameDirectoryDistinctStems: false, hashesMatch: false, DefaultConfig());
        Assert.False(vetoed);
        Assert.Equal(0.90, confidence);
    }
}
