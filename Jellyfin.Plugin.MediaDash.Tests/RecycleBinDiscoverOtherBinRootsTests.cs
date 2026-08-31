using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// End-to-end coverage for the "other bin locations" discovery — the input that powers the
/// Recycle bin tab's Consolidate banner. Uses real temp directories so cross-drive path handling,
/// case-insensitive Windows compare, and Directory.Exists guards are all exercised.
/// </summary>
public sealed class RecycleBinDiscoverOtherBinRootsTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "mediadash-discover-" + Guid.NewGuid().ToString("N"));

    public RecycleBinDiscoverOtherBinRootsTests()
    {
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ReturnsEmpty_WhenNoHistoryPaths()
    {
        var result = RecycleBin.DiscoverOtherBinRoots(Array.Empty<string>(), _scratch);
        Assert.Empty(result);
    }

    [Fact]
    public void ReturnsEmpty_WhenAllPathsAreUnderCurrentRoot()
    {
        var currentRoot = Path.Combine(_scratch, "current");
        var batch = SeedBatch(currentRoot);
        SeedFile(batch, "movie.mkv", 100);

        var recyclePath = Path.Combine(batch, "movie.mkv");
        var result = RecycleBin.DiscoverOtherBinRoots(new[] { recyclePath }, currentRoot);
        Assert.Empty(result);
    }

    [Fact]
    public void ReturnsOneEntry_ForOneLegacyRoot()
    {
        var currentRoot = Path.Combine(_scratch, "current");
        Directory.CreateDirectory(currentRoot);

        var oldRoot = Path.Combine(_scratch, "old");
        var batch = SeedBatch(oldRoot);
        SeedFile(batch, "movie.mkv", 12345);

        var recyclePath = Path.Combine(batch, "movie.mkv");
        var result = RecycleBin.DiscoverOtherBinRoots(new[] { recyclePath }, currentRoot);
        Assert.Single(result);
        Assert.Equal(Path.TrimEndingDirectorySeparator(oldRoot), result[0].RootPath);
        Assert.Equal(1, result[0].BatchCount);
        Assert.Equal(12345, result[0].SizeBytes);
    }

    [Fact]
    public void DeduplicatesMultipleHistoryPathsPointingAtSameRoot()
    {
        var currentRoot = Path.Combine(_scratch, "current");
        Directory.CreateDirectory(currentRoot);

        var oldRoot = Path.Combine(_scratch, "old");
        var batchA = SeedBatch(oldRoot, "20260827-100000-000-a1b2c3d4");
        var batchB = SeedBatch(oldRoot, "20260827-110000-000-b2c3d4e5");
        SeedFile(batchA, "a.mkv", 100);
        SeedFile(batchB, "b.mkv", 200);

        var recyclePaths = new[]
        {
            Path.Combine(batchA, "a.mkv"),
            Path.Combine(batchB, "b.mkv"),
        };
        var result = RecycleBin.DiscoverOtherBinRoots(recyclePaths, currentRoot);
        Assert.Single(result);
        Assert.Equal(2, result[0].BatchCount);
        Assert.Equal(300, result[0].SizeBytes);
    }

    [Fact]
    public void ReturnsMultipleEntries_ForMultipleLegacyRoots()
    {
        var currentRoot = Path.Combine(_scratch, "current");
        Directory.CreateDirectory(currentRoot);

        var rootA = Path.Combine(_scratch, "oldA");
        var rootB = Path.Combine(_scratch, "oldB");
        var batchA = SeedBatch(rootA);
        var batchB = SeedBatch(rootB, "20260827-110000-000-b2c3d4e5");
        SeedFile(batchA, "a.mkv", 100);
        SeedFile(batchB, "b.mkv", 200);

        var recyclePaths = new[]
        {
            Path.Combine(batchA, "a.mkv"),
            Path.Combine(batchB, "b.mkv"),
        };
        var result = RecycleBin.DiscoverOtherBinRoots(recyclePaths, currentRoot);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.RootPath.EndsWith("oldA", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result, r => r.RootPath.EndsWith("oldB", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SkipsRootsThatNoLongerExistOnDisk()
    {
        var currentRoot = Path.Combine(_scratch, "current");
        Directory.CreateDirectory(currentRoot);

        // Reference a root that never existed — user deleted the folder manually.
        var recyclePath = Path.Combine(_scratch, "vanished", "20260827-100000-000-a1b2c3d4", "movie.mkv");
        var result = RecycleBin.DiscoverOtherBinRoots(new[] { recyclePath }, currentRoot);
        Assert.Empty(result);
    }

    [Fact]
    public void SkipsRootWhenItStillExistsButHoldsNoShapedBatches()
    {
        // Root directory is present but the batch folders have all been purged / renamed. The
        // banner should not surface a zero-batch location.
        var currentRoot = Path.Combine(_scratch, "current");
        Directory.CreateDirectory(currentRoot);
        var oldRoot = Path.Combine(_scratch, "empty");
        Directory.CreateDirectory(oldRoot);

        // Recycle path references a non-batch folder shape — DeriveBinRoot returns empty, so
        // the whole record is skipped before even hitting the measure pass.
        var recyclePath = Path.Combine(oldRoot, "not-a-batch", "movie.mkv");
        var result = RecycleBin.DiscoverOtherBinRoots(new[] { recyclePath }, currentRoot);
        Assert.Empty(result);
    }

    [Fact]
    public void CurrentRootCompareIsPathNormalized()
    {
        // Same directory, different string forms (trailing separator + doubled separators).
        // Must still filter out — otherwise the banner would offer to consolidate into itself.
        var currentRoot = Path.Combine(_scratch, "current");
        var batch = SeedBatch(currentRoot);
        SeedFile(batch, "movie.mkv", 10);

        var trailingSlashRoot = currentRoot + Path.DirectorySeparatorChar;
        var recyclePath = Path.Combine(batch, "movie.mkv");
        var result = RecycleBin.DiscoverOtherBinRoots(new[] { recyclePath }, trailingSlashRoot);
        Assert.Empty(result);
    }

    [Fact]
    public void IgnoresCorruptHistoryPathsSilently()
    {
        var currentRoot = Path.Combine(_scratch, "current");
        Directory.CreateDirectory(currentRoot);

        // Empty / whitespace / invalid path characters — should not throw, just skip.
        var oldRoot = Path.Combine(_scratch, "old");
        var batch = SeedBatch(oldRoot);
        SeedFile(batch, "real.mkv", 42);

        var recyclePaths = new List<string>
        {
            string.Empty,
            "   ",
            @"\\?\invalid",
            Path.Combine(batch, "real.mkv"),
        };
        var result = RecycleBin.DiscoverOtherBinRoots(recyclePaths, currentRoot);
        Assert.Single(result);
        Assert.Equal(42, result[0].SizeBytes);
    }

    private string SeedBatch(string root, string batchName = "20260827-100000-000-a1b2c3d4")
    {
        var full = Path.Combine(root, batchName);
        Directory.CreateDirectory(full);
        return full;
    }

    private static void SeedFile(string dir, string name, long size)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[size]);
    }
}
