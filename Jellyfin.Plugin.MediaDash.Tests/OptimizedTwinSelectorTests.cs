using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class OptimizedTwinSelectorTests
{
    // Shape used by RecycleBin.ListContents — mirrored here so the test doesn't need a real bin on disk.
    private static (string, string, long, DateTime, string?) E(string fileName, string binPath, long size, DateTime recycledAtUtc)
        => (fileName, binPath, size, recycledAtUtc, null);

    private static readonly DateTime FixedAt = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PicksTheSmallerSiblingWithMatchingBasenameInWindow()
    {
        var entries = new List<(string, string, long, DateTime, string?)>
        {
            E("Film.mkv", "/bin/20260601-120000-000/Film.mkv", 4_000_000_000, FixedAt),
            E("Film.mkv", "/bin/20260601-120001-500/Film.mkv", 3_900_000_000, FixedAt.AddSeconds(1.5)),
        };

        var twin = RecycleBin.SelectOptimizedTwin(entries, "/bin/20260601-120000-000/Film.mkv", 4_000_000_000, FixedAt);

        Assert.NotNull(twin);
        Assert.Equal("/bin/20260601-120001-500/Film.mkv", twin!.Value.BinPath);
        Assert.Equal(3_900_000_000, twin.Value.SizeBytes);
    }

    [Fact]
    public void RefusesToPickWhenMultipleSmallerSiblingsMatch()
    {
        // Two matching-smaller siblings in the window — ambiguous, return null so the user picks.
        var entries = new List<(string, string, long, DateTime, string?)>
        {
            E("Film.mkv", "/bin/20260601-120000-000/Film.mkv", 4_000_000_000, FixedAt),
            E("Film.mkv", "/bin/20260601-120001-500/Film.mkv", 3_900_000_000, FixedAt.AddSeconds(1.5)),
            E("Film.mkv", "/bin/20260601-120002-000/Film.mkv", 3_800_000_000, FixedAt.AddSeconds(2)),
        };

        var twin = RecycleBin.SelectOptimizedTwin(entries, "/bin/20260601-120000-000/Film.mkv", 4_000_000_000, FixedAt);

        Assert.Null(twin);
    }

    [Fact]
    public void SkipsSiblingsOutsideTheFiveMinuteWindow()
    {
        var entries = new List<(string, string, long, DateTime, string?)>
        {
            E("Film.mkv", "/bin/20260601-120000-000/Film.mkv", 4_000_000_000, FixedAt),
            E("Film.mkv", "/bin/20260601-130000-000/Film.mkv", 3_900_000_000, FixedAt.AddHours(1)),
        };

        var twin = RecycleBin.SelectOptimizedTwin(entries, "/bin/20260601-120000-000/Film.mkv", 4_000_000_000, FixedAt);

        Assert.Null(twin);
    }

    [Fact]
    public void SkipsSiblingsThatAreEqualOrLargerThanOriginal()
    {
        // A same-size or bigger sibling is not "the optimized" copy.
        var entries = new List<(string, string, long, DateTime, string?)>
        {
            E("Film.mkv", "/bin/20260601-120000-000/Film.mkv", 4_000_000_000, FixedAt),
            E("Film.mkv", "/bin/20260601-120001-500/Film.mkv", 4_000_000_000, FixedAt.AddSeconds(1.5)),
            E("Film.mkv", "/bin/20260601-120002-000/Film.mkv", 4_100_000_000, FixedAt.AddSeconds(2)),
        };

        var twin = RecycleBin.SelectOptimizedTwin(entries, "/bin/20260601-120000-000/Film.mkv", 4_000_000_000, FixedAt);

        Assert.Null(twin);
    }

    [Fact]
    public void SkipsSiblingsWithDifferentBasenames()
    {
        var entries = new List<(string, string, long, DateTime, string?)>
        {
            E("Film.mkv", "/bin/20260601-120000-000/Film.mkv", 4_000_000_000, FixedAt),
            E("Other.mkv", "/bin/20260601-120001-500/Other.mkv", 3_000_000_000, FixedAt.AddSeconds(1.5)),
        };

        var twin = RecycleBin.SelectOptimizedTwin(entries, "/bin/20260601-120000-000/Film.mkv", 4_000_000_000, FixedAt);

        Assert.Null(twin);
    }

    [Fact]
    public void SkipsTheOriginalItself()
    {
        // The original's own entry must not be selected — case-insensitive path compare.
        var entries = new List<(string, string, long, DateTime, string?)>
        {
            E("Film.mkv", "/BIN/20260601-120000-000/Film.mkv", 4_000_000_000, FixedAt),
        };

        var twin = RecycleBin.SelectOptimizedTwin(entries, "/bin/20260601-120000-000/Film.mkv", 4_000_000_000, FixedAt);

        Assert.Null(twin);
    }

    // Helper mirroring E() but with manifest OriginalPath populated — used by the tie-break tests below.
    private static (string, string, long, DateTime, string?) EM(string fileName, string binPath, long size, DateTime recycledAtUtc, string originalPath)
        => (fileName, binPath, size, recycledAtUtc, originalPath);

    [Fact]
    public void DisambiguatesTwoCandidatesUsingManifestOriginalPath()
    {
        // Two same-basename, same-window, both-smaller candidates. Without sourceOriginalPath the
        // selector would refuse. With it, the one whose manifest matches wins.
        var sourceOriginalPath = "/library/Movies/Film (2020)/Film.mkv";
        var entries = new List<(string, string, long, DateTime, string?)>
        {
            E("Film.mkv", "/bin/20260601-120000-000/Film.mkv", 4_000_000_000, FixedAt),
            EM("Film.mkv", "/bin/20260601-120001-500/Film.mkv", 3_900_000_000, FixedAt.AddSeconds(1.5), sourceOriginalPath),
            EM("Film.mkv", "/bin/20260601-120002-000/Film.mkv", 3_800_000_000, FixedAt.AddSeconds(2), "/library/Movies/OtherFilm (2020)/Film.mkv"),
        };

        var twin = RecycleBin.SelectOptimizedTwin(
            entries,
            "/bin/20260601-120000-000/Film.mkv",
            4_000_000_000,
            FixedAt,
            sourceOriginalPath);

        Assert.NotNull(twin);
        Assert.Equal("/bin/20260601-120001-500/Film.mkv", twin!.Value.BinPath);
    }

    [Fact]
    public void ManifestTieBreakRefusesWhenTwoCandidatesShareTheSameOriginalPath()
    {
        // Two candidates both manifest-matched to the source — still ambiguous, refuse.
        var sourceOriginalPath = "/library/Movies/Film (2020)/Film.mkv";
        var entries = new List<(string, string, long, DateTime, string?)>
        {
            E("Film.mkv", "/bin/20260601-120000-000/Film.mkv", 4_000_000_000, FixedAt),
            EM("Film.mkv", "/bin/20260601-120001-500/Film.mkv", 3_900_000_000, FixedAt.AddSeconds(1.5), sourceOriginalPath),
            EM("Film.mkv", "/bin/20260601-120002-000/Film.mkv", 3_800_000_000, FixedAt.AddSeconds(2), sourceOriginalPath),
        };

        var twin = RecycleBin.SelectOptimizedTwin(
            entries,
            "/bin/20260601-120000-000/Film.mkv",
            4_000_000_000,
            FixedAt,
            sourceOriginalPath);

        Assert.Null(twin);
    }

    [Fact]
    public void ManifestTieBreakIgnoredWhenCallerPassesNullSourcePath()
    {
        // Legacy caller with no sourceOriginalPath — even if candidates carry manifests, we fall
        // back to the old ambiguity rule to preserve back-compat with pre-manifest call sites.
        var entries = new List<(string, string, long, DateTime, string?)>
        {
            E("Film.mkv", "/bin/20260601-120000-000/Film.mkv", 4_000_000_000, FixedAt),
            EM("Film.mkv", "/bin/20260601-120001-500/Film.mkv", 3_900_000_000, FixedAt.AddSeconds(1.5), "/library/A/Film.mkv"),
            EM("Film.mkv", "/bin/20260601-120002-000/Film.mkv", 3_800_000_000, FixedAt.AddSeconds(2), "/library/B/Film.mkv"),
        };

        var twin = RecycleBin.SelectOptimizedTwin(
            entries,
            "/bin/20260601-120000-000/Film.mkv",
            4_000_000_000,
            FixedAt,
            sourceOriginalPath: null);

        Assert.Null(twin);
    }

    [Fact]
    public void ManifestTieBreakSkipsCandidatesWithNullManifest()
    {
        // Mixed candidates: one manifest-matched, one pre-manifest legacy (null OriginalPath).
        // The manifest-matched one wins even though both otherwise qualify.
        var sourceOriginalPath = "/library/Movies/Film (2020)/Film.mkv";
        var entries = new List<(string, string, long, DateTime, string?)>
        {
            E("Film.mkv", "/bin/20260601-120000-000/Film.mkv", 4_000_000_000, FixedAt),
            E("Film.mkv", "/bin/20260601-120001-500/Film.mkv", 3_900_000_000, FixedAt.AddSeconds(1.5)),
            EM("Film.mkv", "/bin/20260601-120002-000/Film.mkv", 3_800_000_000, FixedAt.AddSeconds(2), sourceOriginalPath),
        };

        var twin = RecycleBin.SelectOptimizedTwin(
            entries,
            "/bin/20260601-120000-000/Film.mkv",
            4_000_000_000,
            FixedAt,
            sourceOriginalPath);

        Assert.NotNull(twin);
        Assert.Equal("/bin/20260601-120002-000/Film.mkv", twin!.Value.BinPath);
    }
}

