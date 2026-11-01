using System.Linq;
using Jellyfin.Plugin.MediaDash.Api;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Two shares on one NAS volume both report the whole volume's space. <see cref="UncVolumeGrouping"/>
/// merges them so the Overview doesn't list the same disk twice or double-count its free space.
/// </summary>
public sealed class UncVolumeGroupingTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void SharesOnTheSameVolumeAreMerged()
    {
        // Shows and youtoobs: same volume, free space a few bytes apart because the readings were
        // taken at different moments.
        var result = UncVolumeGrouping.Group(new[]
        {
            (@"\\sunnynas\Shows", 469 * Gb, 3600 * Gb),
            (@"\\sunnynas\youtoobs", (469 * Gb) + 4096, 3600 * Gb),
        });

        var only = Assert.Single(result);
        Assert.Equal(@"\\sunnynas\Shows + \\sunnynas\youtoobs", only.Label);
        Assert.Equal(469 * Gb, only.FreeBytes);
        Assert.Equal(3600 * Gb, only.TotalBytes);
    }

    [Fact]
    public void DifferentVolumesStaySeparate()
    {
        var result = UncVolumeGrouping.Group(new[]
        {
            (@"\\sunnynas\Media", 3900 * Gb, 7000 * Gb),
            (@"\\sunnynas\Shows", 469 * Gb, 3600 * Gb),
        });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void SameTotalButClearlyDifferentFreeSpaceStaysSeparate()
    {
        var result = UncVolumeGrouping.Group(new[]
        {
            (@"\\nas\A", 100 * Gb, 1000 * Gb),
            (@"\\nas\B", 400 * Gb, 1000 * Gb),
        });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void SameNumbersOnDifferentServersStaySeparate()
    {
        var result = UncVolumeGrouping.Group(new[]
        {
            (@"\\nas1\A", 100 * Gb, 1000 * Gb),
            (@"\\NAS2\A", 100 * Gb, 1000 * Gb),
        });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ServerNameComparisonIgnoresCase()
    {
        var result = UncVolumeGrouping.Group(new[]
        {
            (@"\\SunnyNAS\Shows", 100 * Gb, 1000 * Gb),
            (@"\\sunnynas\Other", 100 * Gb, 1000 * Gb),
        });

        Assert.Single(result);
    }

    [Fact]
    public void FirstAppearanceOrderIsKeptAndThreeSharesCanMerge()
    {
        var result = UncVolumeGrouping.Group(new[]
        {
            (@"\\nas\Media", 900 * Gb, 2000 * Gb),
            (@"\\nas\Shows", 100 * Gb, 1000 * Gb),
            (@"\\nas\Kids", 100 * Gb, 1000 * Gb),
            (@"\\nas\Docs", 100 * Gb, 1000 * Gb),
        });

        Assert.Equal(2, result.Count);
        Assert.Equal(@"\\nas\Media", result[0].Label);
        Assert.Equal(@"\\nas\Shows + \\nas\Kids + \\nas\Docs", result[1].Label);
    }

    [Fact]
    public void EmptyInputGivesNoGroups()
    {
        Assert.Empty(UncVolumeGrouping.Group(Enumerable.Empty<(string Root, long FreeBytes, long TotalBytes)>()));
    }
}
