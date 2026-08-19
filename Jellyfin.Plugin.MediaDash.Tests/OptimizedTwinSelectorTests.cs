using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class OptimizedTwinSelectorTests
{
    // Shape used by RecycleBin.ListContents — mirrored here so the test doesn't need a real bin on disk.
    private static (string, string, long, DateTime) E(string fileName, string binPath, long size, DateTime recycledAtUtc)
        => (fileName, binPath, size, recycledAtUtc);

    private static readonly DateTime FixedAt = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PicksTheSmallerSiblingWithMatchingBasenameInWindow()
    {
        var entries = new List<(string, string, long, DateTime)>
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
        var entries = new List<(string, string, long, DateTime)>
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
        var entries = new List<(string, string, long, DateTime)>
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
        var entries = new List<(string, string, long, DateTime)>
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
        var entries = new List<(string, string, long, DateTime)>
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
        var entries = new List<(string, string, long, DateTime)>
        {
            E("Film.mkv", "/BIN/20260601-120000-000/Film.mkv", 4_000_000_000, FixedAt),
        };

        var twin = RecycleBin.SelectOptimizedTwin(entries, "/bin/20260601-120000-000/Film.mkv", 4_000_000_000, FixedAt);

        Assert.Null(twin);
    }
}
