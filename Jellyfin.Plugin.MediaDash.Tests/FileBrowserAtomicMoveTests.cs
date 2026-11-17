using System;
using System.IO;
using System.Threading;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>The compatibility helper permits strict same-filesystem renames only.</summary>
public sealed class FileBrowserAtomicMoveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-atomic-compat-" + Guid.NewGuid().ToString("N"));

    public FileBrowserAtomicMoveTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameFilesystemMoveRetainsTheCompleteObjectIdentityAndContents(bool directory)
    {
        var source = Path.Combine(_root, "source");
        var target = Path.Combine(_root, "target");
        if (directory)
        {
            Directory.CreateDirectory(Path.Combine(source, "empty"));
            File.WriteAllText(Path.Combine(source, "notes.txt"), "owned notes");
        }
        else File.WriteAllText(source, "owned bytes");
        Assert.True(PhysicalFileIdentity.TryRead(source, out var before));

        FileBrowserController.CrossDeviceMove(source, target, directory);

        Assert.True(PhysicalFileIdentity.TryRead(target, out var after));
        Assert.Equal(before, after);
        Assert.False(File.Exists(source) || Directory.Exists(source));
        Assert.Equal(directory ? "owned notes" : "owned bytes", File.ReadAllText(directory ? Path.Combine(target, "notes.txt") : target));
        if (directory) Assert.True(Directory.Exists(Path.Combine(target, "empty")));
        Assert.Single(Directory.GetFileSystemEntries(_root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OccupiedDestinationPreservesBothOriginalObjects(bool directory)
    {
        var source = Path.Combine(_root, "source");
        var target = Path.Combine(_root, "target");
        if (directory)
        {
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(target);
        }

        var original = directory ? Path.Combine(source, "payload") : source;
        var occupied = directory ? Path.Combine(target, "payload") : target;
        File.WriteAllText(original, "original bytes");
        File.WriteAllText(occupied, "unrelated bytes");

        Assert.Throws<IOException>(() => FileBrowserController.CrossDeviceMove(source, target, directory));

        Assert.Equal("original bytes", File.ReadAllText(original));
        Assert.Equal("unrelated bytes", File.ReadAllText(occupied));
        Assert.Equal(2, Directory.GetFileSystemEntries(_root).Length);
    }

    [Fact]
    public void CancellationBeforeRenameLeavesOnlyTheOriginal()
    {
        var source = Path.Combine(_root, "source");
        var target = Path.Combine(_root, "target");
        File.WriteAllText(source, "original bytes");
        using var cancellation = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() => FileBrowserController.CrossDeviceMove(source, target, false,
            (_, _) => cancellation.Cancel(), cancellation.Token));

        Assert.Equal("original bytes", File.ReadAllText(source));
        Assert.Single(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void ANewImportAtTheOldPathAfterRenameIsPreserved()
    {
        var source = Path.Combine(_root, "source");
        var target = Path.Combine(_root, "target");
        File.WriteAllText(source, "original bytes");

        FileBrowserController.CrossDeviceMove(source, target, false, (step, _) =>
        {
            if (step == "Published") File.WriteAllText(source, "new import");
        });

        Assert.Equal("original bytes", File.ReadAllText(target));
        Assert.Equal("new import", File.ReadAllText(source));
        Assert.Equal(2, Directory.GetFileSystemEntries(_root).Length);
    }

    [Fact]
    public void NewAlternateStreamAfterRenameRemainsAccessible()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = Path.Combine(_root, "source");
        var target = Path.Combine(_root, "target");
        File.WriteAllText(source, "original bytes");

        FileBrowserController.CrossDeviceMove(source, target, false, (step, _) =>
        {
            if (step == "Published") File.WriteAllText(target + ":new-notes", "new notes");
        });

        Assert.Equal("original bytes", File.ReadAllText(target));
        Assert.Equal("new notes", File.ReadAllText(target + ":new-notes"));
        Assert.False(File.Exists(source));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
