using System;
using System.IO;
using System.Threading;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class MediaMoveCleanupSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-cleanup-safety-" + Guid.NewGuid().ToString("N"));

    public MediaMoveCleanupSafetyTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedReadOnlyRecoveryCanBeDeletedOnWindows(bool alternateStream)
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = Path.Combine(_root, "recovery.mkv");
        File.WriteAllText(file, "approved payload");
        if (alternateStream) File.WriteAllText(file + ":notes", "alternate stream payload");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        var snapshot = MediaMoveSnapshot.Capture(file);

        snapshot.DeleteCaptured(file, CancellationToken.None);

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void AlternateStreamWriterAfterVerificationMustNotLoseAccessibleBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = Path.Combine(_root, "recovery.mkv");
        File.WriteAllText(file, "approved payload");
        File.WriteAllText(file + ":notes", "approved notes");
        var expected = MediaMoveMetadata.Capture(file, directory: false);
        var wrote = false;

        var failure = Record.Exception(() => MediaMoveRecoveryDeletion.DeleteWindows(file, () =>
        {
            Assert.Equal(expected, MediaMoveMetadata.Capture(file, directory: false, allowDeleteHandle: true));
            using var writer = new FileStream(file + ":notes", FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            writer.Write(System.Text.Encoding.UTF8.GetBytes("NEW NOTES"));
            wrote = true;
        }));

        Assert.IsAssignableFrom<IOException>(failure);
        Assert.True(File.Exists(file));
        Assert.Equal(wrote ? "NEW NOTESnotes" : "approved notes", File.ReadAllText(file + ":notes"));
    }

    [Fact]
    public void ChangedReadOnlyRecoveryRetainsAttributesAndBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = Path.Combine(_root, "recovery.mkv");
        File.WriteAllText(file, "approved payload");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        var snapshot = MediaMoveSnapshot.Capture(file);

        Assert.Throws<IOException>(() => snapshot.DeleteCaptured(file, CancellationToken.None, () =>
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.WriteAllText(file, "new writer bytes");
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        }));

        Assert.Equal("new writer bytes", File.ReadAllText(file));
        Assert.True((File.GetAttributes(file) & FileAttributes.ReadOnly) != 0);
    }

    [Fact]
    public void RetainedDeletionHandleExcludesWriterAndPathSubstitutionDuringVerification()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(_root, "recovery");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "payload.mkv");
        File.WriteAllText(file, "approved payload");

        MediaMoveRecoveryDeletion.DeleteWindows(file, () =>
        {
            Assert.Throws<IOException>(() => File.WriteAllText(file, "unapproved writer"));
            Assert.Throws<IOException>(() => File.Move(file, file + ".renamed"));
            Assert.Throws<IOException>(() => Directory.Move(directory, directory + ".renamed"));
        });

        Assert.False(File.Exists(file));
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void FailedDirectoryDeletionRestoresReadOnlyAttributes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(_root, "recovery");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "unapproved-child"), "preserve");
        File.SetAttributes(directory, File.GetAttributes(directory) | FileAttributes.ReadOnly);

        Assert.Throws<IOException>(() => MediaMoveRecoveryDeletion.DeleteWindows(directory, () => { }));

        Assert.True((File.GetAttributes(directory) & FileAttributes.ReadOnly) != 0);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(directory, "unapproved-child")));
        File.SetAttributes(directory, FileAttributes.Directory);
    }

    [Fact]
    public void WriterOpenedAfterCapturePreventsWindowsCleanup()
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = Path.Combine(_root, "recovery.mkv");
        File.WriteAllText(file, "approved payload");
        var snapshot = MediaMoveSnapshot.Capture(file);
        using (var writer = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.Throws<IOException>(() => snapshot.DeleteCaptured(file, CancellationToken.None));
            writer.Write(System.Text.Encoding.UTF8.GetBytes("NEW BYTES"));
        }

        Assert.StartsWith("NEW BYTES", File.ReadAllText(file), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

}
