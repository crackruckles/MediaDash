using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// End-to-end coverage for ConsolidateBetween — the batch-move engine behind the Recycle bin
/// tab's "Consolidate all" button. Uses real temp directories to exercise Directory.Move on
/// same-volume paths + the collision-skip guard.
/// </summary>
public sealed class RecycleBinConsolidateBetweenTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "mediadash-consolidate-" + Guid.NewGuid().ToString("N"));

    public RecycleBinConsolidateBetweenTests()
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
    public void RefusesWhenSourceEqualsTarget()
    {
        var (moved, skipped, bytes, warning) = RecycleBin.ConsolidateBetween(_scratch, _scratch);
        Assert.Equal(0, moved);
        Assert.Equal(0, skipped);
        Assert.Equal(0, bytes);
        Assert.NotNull(warning);
        Assert.Contains("same as", warning!);
    }

    [Fact]
    public void MovesOneBatchIntoTarget_WithFileContentsIntact()
    {
        var source = Path.Combine(_scratch, "old");
        var target = Path.Combine(_scratch, "new");
        var batch = SeedBatch(source);
        File.WriteAllText(Path.Combine(batch, "movie.mkv"), "some payload");

        var result = RecycleBin.ConsolidateBetween(source, target);
        Assert.Equal(1, result.BatchesMoved);
        Assert.Equal(0, result.BatchesSkipped);
        Assert.Null(result.Warning);

        var movedBatch = Path.Combine(target, "20260827-100000-000-a1b2c3d4");
        Assert.True(Directory.Exists(movedBatch));
        Assert.Equal("some payload", File.ReadAllText(Path.Combine(movedBatch, "movie.mkv")));
        // Source batch is gone.
        Assert.False(Directory.Exists(batch));
    }

    [Fact]
    public void CountsBytesAcrossEveryFileInEveryBatch()
    {
        var source = Path.Combine(_scratch, "old");
        var target = Path.Combine(_scratch, "new");
        var batchA = SeedBatch(source, "20260827-100000-000-a1b2c3d4");
        var batchB = SeedBatch(source, "20260827-110000-000-b2c3d4e5");
        WriteBytes(Path.Combine(batchA, "movie.mkv"), 1_000);
        WriteBytes(Path.Combine(batchA, "cover.jpg"), 200);
        WriteBytes(Path.Combine(batchB, "song.mp3"), 500);

        var result = RecycleBin.ConsolidateBetween(source, target);
        Assert.Equal(2, result.BatchesMoved);
        Assert.Equal(1_700, result.BytesMoved);
    }

    [Fact]
    public void SkipsBatchWhoseLeafAlreadyExistsAtTarget()
    {
        var source = Path.Combine(_scratch, "old");
        var target = Path.Combine(_scratch, "new");
        var sourceBatch = SeedBatch(source);
        SeedBatch(target); // collision at target
        File.WriteAllText(Path.Combine(sourceBatch, "movie.mkv"), "source-content");

        var result = RecycleBin.ConsolidateBetween(source, target);
        Assert.Equal(0, result.BatchesMoved);
        Assert.Equal(1, result.BatchesSkipped);
        // Source untouched.
        Assert.True(Directory.Exists(sourceBatch));
        Assert.Equal("source-content", File.ReadAllText(Path.Combine(sourceBatch, "movie.mkv")));
    }

    [Fact]
    public void IgnoresNonBatchShapedFoldersInSource()
    {
        var source = Path.Combine(_scratch, "old");
        var target = Path.Combine(_scratch, "new");
        Directory.CreateDirectory(Path.Combine(source, "not-a-batch"));
        Directory.CreateDirectory(Path.Combine(source, "20260827-100000-000-notahex")); // fails hex check
        var realBatch = SeedBatch(source, "20260827-100000-000-a1b2c3d4");
        File.WriteAllText(Path.Combine(realBatch, "movie.mkv"), "payload");

        var result = RecycleBin.ConsolidateBetween(source, target);
        Assert.Equal(1, result.BatchesMoved);
        Assert.Equal(0, result.BatchesSkipped);
        // Non-batch folder stays put — must never touch user folders that happen to sit alongside.
        Assert.True(Directory.Exists(Path.Combine(source, "not-a-batch")));
        Assert.True(Directory.Exists(Path.Combine(source, "20260827-100000-000-notahex")));
    }

    [Fact]
    public void CreatesTargetRoot_WhenItDoesntExist()
    {
        var source = Path.Combine(_scratch, "old");
        var target = Path.Combine(_scratch, "brand-new-target");
        var batch = SeedBatch(source);
        File.WriteAllText(Path.Combine(batch, "movie.mkv"), "content");

        Assert.False(Directory.Exists(target));
        var result = RecycleBin.ConsolidateBetween(source, target);
        Assert.Equal(1, result.BatchesMoved);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void MoveIsWholeFolder_PreservingSidecarFiles()
    {
        // A batch can hold the ownership marker + origin manifest + one or more recycled files.
        // Consolidation must move all of them together so the batch stays self-describing.
        var source = Path.Combine(_scratch, "old");
        var target = Path.Combine(_scratch, "new");
        var batch = SeedBatch(source);
        File.WriteAllText(Path.Combine(batch, RecycleBin.OwnershipMarkerFileName), string.Empty);
        File.WriteAllText(Path.Combine(batch, RecycleBin.OriginManifestFileName), "/original/path/to/file.mkv");
        File.WriteAllText(Path.Combine(batch, "file.mkv"), "content");

        RecycleBin.ConsolidateBetween(source, target);
        var movedBatch = Path.Combine(target, "20260827-100000-000-a1b2c3d4");
        Assert.True(File.Exists(Path.Combine(movedBatch, RecycleBin.OwnershipMarkerFileName)));
        Assert.True(File.Exists(Path.Combine(movedBatch, RecycleBin.OriginManifestFileName)));
        Assert.True(File.Exists(Path.Combine(movedBatch, "file.mkv")));
    }

    [Fact]
    public void MixedCase_MovesUncollided_SkipsCollided_AccumulatesBytes()
    {
        var source = Path.Combine(_scratch, "old");
        var target = Path.Combine(_scratch, "new");
        var goodBatch = SeedBatch(source, "20260827-100000-000-a1b2c3d4");
        var collidedBatch = SeedBatch(source, "20260827-110000-000-b2c3d4e5");
        SeedBatch(target, "20260827-110000-000-b2c3d4e5"); // pre-existing collision
        WriteBytes(Path.Combine(goodBatch, "movie.mkv"), 5_000);
        WriteBytes(Path.Combine(collidedBatch, "song.mp3"), 999);

        var result = RecycleBin.ConsolidateBetween(source, target);
        Assert.Equal(1, result.BatchesMoved);
        Assert.Equal(1, result.BatchesSkipped);
        Assert.Equal(5_000, result.BytesMoved);
    }

    [Fact]
    public void EmptySourceRoot_ReportsZerosSilently()
    {
        var source = Path.Combine(_scratch, "old");
        var target = Path.Combine(_scratch, "new");
        Directory.CreateDirectory(source);

        var result = RecycleBin.ConsolidateBetween(source, target);
        Assert.Equal(0, result.BatchesMoved);
        Assert.Equal(0, result.BatchesSkipped);
        Assert.Equal(0, result.BytesMoved);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void MissingSourceRoot_ReportsWarning()
    {
        var source = Path.Combine(_scratch, "does-not-exist");
        var target = Path.Combine(_scratch, "new");

        var result = RecycleBin.ConsolidateBetween(source, target);
        Assert.Equal(0, result.BatchesMoved);
        Assert.NotNull(result.Warning);
        Assert.Contains("Could not enumerate", result.Warning!);
    }

    [Fact]
    public void RecycledFileIsRestorable_AfterConsolidation_ViaManifestSidecar()
    {
        // Integration invariant: after consolidation, ReadOriginManifest against the moved batch
        // returns the same origin lines. This is the guarantee the UI depends on to restore files
        // from the migrated batches.
        var source = Path.Combine(_scratch, "old");
        var target = Path.Combine(_scratch, "new");
        var batch = SeedBatch(source);
        File.WriteAllText(Path.Combine(batch, RecycleBin.OriginManifestFileName), @"C:\media\movies\Movie.mkv");
        File.WriteAllText(Path.Combine(batch, "Movie.mkv"), "content");

        RecycleBin.ConsolidateBetween(source, target);
        var movedBatch = Path.Combine(target, "20260827-100000-000-a1b2c3d4");
        var manifestLines = RecycleBin.ReadOriginManifest(movedBatch);
        Assert.Single(manifestLines);
        Assert.Equal(@"C:\media\movies\Movie.mkv", manifestLines[0]);
    }

    private string SeedBatch(string root, string batchName = "20260827-100000-000-a1b2c3d4")
    {
        var full = Path.Combine(root, batchName);
        Directory.CreateDirectory(full);
        return full;
    }

    private static void WriteBytes(string path, long size)
    {
        File.WriteAllBytes(path, new byte[size]);
    }
}
