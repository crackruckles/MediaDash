using System;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Identity-only move snapshots: what the scanner records instead of hashing every payload, and
/// how execution verifies them.
/// </summary>
public sealed class MediaMoveIdentitySnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-identity-" + Guid.NewGuid().ToString("N"));

    public MediaMoveIdentitySnapshotTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void IdentityInventoryHasNoContentHashesAndVerifiesUnchangedPayload()
    {
        var folder = Folder("Show");
        var inventory = MediaMoveSnapshot.CaptureIdentity(folder).Export();

        Assert.Equal(3, inventory.Length);
        Assert.All(inventory, payload => Assert.Equal(string.Empty, payload.Sha256));
        Assert.False(MediaMoveSnapshot.HasContentHashes(inventory));
        MediaMoveSnapshot.VerifyInventory(folder, inventory, sameIdentity: true, CancellationToken.None);
    }

    [Fact]
    public void IdentityInventoryRefusesARewrittenFile()
    {
        var folder = Folder("Show");
        var inventory = MediaMoveSnapshot.CaptureIdentity(folder).Export();
        var episode = Path.Combine(folder, "S01E01.mkv");
        File.WriteAllText(episode, "different episode bytes");
        File.SetLastWriteTimeUtc(episode, DateTime.UtcNow.AddMinutes(5));

        Assert.Throws<IOException>(() => MediaMoveSnapshot.VerifyInventory(folder, inventory, sameIdentity: true, CancellationToken.None));
    }

    [Fact]
    public void IdentityInventoryRefusesAReplacedFileEvenWithTheSameSizeAndTime()
    {
        var folder = Folder("Show");
        var episode = Path.Combine(folder, "S01E01.mkv");
        var inventory = MediaMoveSnapshot.CaptureIdentity(folder).Export();
        var written = File.GetLastWriteTimeUtc(episode);
        var replacement = Path.Combine(_root, "replacement.mkv");
        File.WriteAllText(replacement, "episode one!");
        File.SetLastWriteTimeUtc(replacement, written);
        File.Delete(episode);
        File.Move(replacement, episode);

        Assert.Throws<IOException>(() => MediaMoveSnapshot.VerifyInventory(folder, inventory, sameIdentity: true, CancellationToken.None));
    }

    [Fact]
    public void HashedInventoryStillDetectsAContentChangeThatKeepsSizeAndTime()
    {
        var folder = Folder("Show");
        var inventory = MediaMoveSnapshot.Capture(folder).Export();
        Assert.True(MediaMoveSnapshot.HasContentHashes(inventory));
        var episode = Path.Combine(folder, "S01E01.mkv");
        var written = File.GetLastWriteTimeUtc(episode);
        File.WriteAllText(episode, "EPISODE ONE!");
        File.SetLastWriteTimeUtc(episode, written);

        Assert.Throws<IOException>(() => MediaMoveSnapshot.VerifyInventory(folder, inventory, sameIdentity: true, CancellationToken.None));
    }

    [Fact]
    public void IdentityCaptureDoesNotOpenFileContent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = Folder("Show");
        using var exclusive = new FileStream(Path.Combine(folder, "S01E01.mkv"), FileMode.Open, FileAccess.Read, FileShare.None);

        var inventory = MediaMoveSnapshot.CaptureIdentity(folder).Export();

        Assert.Equal(3, inventory.Length);
        Assert.Throws<IOException>(() => MediaMoveSnapshot.Capture(folder));
    }

    [Fact]
    public void RootIdentityRecordsOnlyTheRoot()
    {
        var folder = Folder("Show");
        var inventory = MediaMoveSnapshot.CaptureRootIdentity(folder).Export();

        var root = Assert.Single(inventory);
        Assert.Equal(string.Empty, root.RelativePath);
        Assert.True(root.IsDirectory);
    }

    private string Folder(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "S01E01.mkv"), "episode one!");
        File.WriteAllText(Path.Combine(folder, "S01E02.mkv"), "episode two!");
        return folder;
    }
}
