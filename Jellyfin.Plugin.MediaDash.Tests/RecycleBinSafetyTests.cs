using System;
using System.IO;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class RecycleBinSafetyTests
{
    [Theory]
    [InlineData("20260822-120102-003-a1b2c3d4")]
    [InlineData("20000101-000000-000-00000000")]
    public void MediaDashBatchNameIsRecognized(string directoryName)
    {
        Assert.True(RecycleBin.IsMediaDashBatchName(directoryName));
    }

    [Theory]
    [InlineData("Movies")]
    [InlineData("20260822-120102-003")]
    [InlineData("20260822-120102-003-not-hex!!")]
    [InlineData("20261322-120102-003-a1b2c3d4")]
    [InlineData("20260822-120102-003-a1b2c3d4-extra")]
    public void UnownedDirectoryIsNotRecognizedAsRecycleBatch(string directoryName)
    {
        Assert.False(RecycleBin.IsMediaDashBatchName(directoryName));
    }

    [Fact]
    public void MarkedBatchInCustomRootIsOwned()
    {
        using var directories = new TemporaryDirectories();
        var batch = directories.CreateBatch(directories.CustomRoot);
        File.WriteAllText(Path.Combine(batch, RecycleBin.OwnershipMarkerFileName), "1\n");

        Assert.True(RecycleBin.IsOwnedBatchDirectory(batch, directories.DefaultRoot));
    }

    [Fact]
    public void UnmarkedBatchInCustomRootIsNotOwned()
    {
        using var directories = new TemporaryDirectories();
        var batch = directories.CreateBatch(directories.CustomRoot);

        Assert.False(RecycleBin.IsOwnedBatchDirectory(batch, directories.DefaultRoot));
    }

    [Fact]
    public void UnmarkedLegacyBatchDirectlyUnderDedicatedDefaultRootIsOwned()
    {
        using var directories = new TemporaryDirectories();
        var batch = directories.CreateBatch(directories.DefaultRoot);

        Assert.True(RecycleBin.IsOwnedBatchDirectory(batch, directories.DefaultRoot));
    }

    [Fact]
    public void UnmarkedNestedBatchUnderDefaultRootIsNotOwned()
    {
        using var directories = new TemporaryDirectories();
        var unrelated = Directory.CreateDirectory(Path.Combine(directories.DefaultRoot, "unrelated")).FullName;
        var batch = directories.CreateBatch(unrelated);

        Assert.False(RecycleBin.IsOwnedBatchDirectory(batch, directories.DefaultRoot));
    }

    private sealed class TemporaryDirectories : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mediadash-tests-" + Guid.NewGuid().ToString("N"));

        public TemporaryDirectories()
        {
            DefaultRoot = Directory.CreateDirectory(Path.Combine(_root, "default")).FullName;
            CustomRoot = Directory.CreateDirectory(Path.Combine(_root, "custom")).FullName;
        }

        public string DefaultRoot { get; }

        public string CustomRoot { get; }

        public string CreateBatch(string root)
        {
            return Directory.CreateDirectory(Path.Combine(root, "20260822-120102-003-a1b2c3d4")).FullName;
        }

        public void Dispose()
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
