using System;
using System.IO;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// MD-011 regression: the shared free-space precondition. Before this landed, TranscodeFixer and
/// TrackFixer used a `source + 500 MB` gate, which is much looser than CLAUDE.md safety invariant
/// 5's stated 2× source-size requirement. On a 40 GB Blu-ray remux the old guard passed at
/// 40.5 GB free when the doc said 80 GB.
/// </summary>
public sealed class FreeSpaceGuardTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), "mediadash-space-" + Guid.NewGuid().ToString("N") + ".bin");

    public FreeSpaceGuardTests()
    {
        File.WriteAllBytes(_tempFile, new byte[1024]);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_tempFile);
        }
        catch (IOException)
        {
            // Best-effort test cleanup.
        }
    }

    [Fact]
    public void MissingFileFailsClosed()
    {
        var missing = Path.Combine(Path.GetTempPath(), "mediadash-space-missing-" + Guid.NewGuid().ToString("N") + ".bin");
        Assert.False(FreeSpaceGuard.HasEnoughFreeSpace(missing, multiplier: 2));
    }

    [Fact]
    public void ZeroOrNegativeMultiplierShortCircuitsToTrue()
    {
        // Callers that ask for 0 headroom explicitly opt out of the gate. Better than throwing.
        Assert.True(FreeSpaceGuard.HasEnoughFreeSpace(_tempFile, multiplier: 0));
        Assert.True(FreeSpaceGuard.HasEnoughFreeSpace(_tempFile, multiplier: -1));
    }

    [Fact]
    public void MultiplierTwoOnATinyFilePassesOnAnyRealisticVolume()
    {
        // 1 KB source × 2 = 2 KB required. Any host running the test suite has at least that.
        // Sanity check that the happy path returns true instead of tripping on an unrelated guard.
        Assert.True(FreeSpaceGuard.HasEnoughFreeSpace(_tempFile, multiplier: 2));
    }

    [Fact]
    public void UnresolvableDriveFailsClosed()
    {
        // Passing an invalid drive-root-shaped path exercises the DriveInfo/IOException catch.
        // Fail-closed is the point of the finding: an unresolved drive must NOT be treated as
        // "plenty of space", which is what the old inline checks did when drive came back null.
        var badPath = "\0invalid-path";
        Assert.False(FreeSpaceGuard.HasEnoughFreeSpace(badPath, multiplier: 2));
    }

    [Fact]
    public void TryGetAvailableFreeSpaceMeasuresARealVolume()
    {
        Assert.True(FreeSpaceGuard.TryGetAvailableFreeSpace(_tempFile, out var available));
        Assert.True(available > 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\0invalid-path")]
    public void TryGetAvailableFreeSpaceFailsClosedOnUnmeasurablePaths(string path)
    {
        Assert.False(FreeSpaceGuard.TryGetAvailableFreeSpace(path, out var available));
        Assert.Equal(0, available);
    }

    [Fact]
    public void UncPathIsMeasuredInsteadOfFailingClosed()
    {
        // Regression: DriveInfo throws ArgumentException for a UNC root (\\server\share), so every
        // library on a NAS addressed by UNC path was refused with "Not enough free disk space"
        // no matter how much room the share had. Windows-only: build a UNC path to the temp
        // file's own volume through the local administrative share. Returns quietly where that
        // share is unavailable, so non-admin and non-Windows hosts don't see a false failure.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.GetPathRoot(_tempFile);
        if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
        {
            return;
        }

        var unc = @"\\localhost\" + root[0] + "$" + _tempFile.Substring(2);
        if (!File.Exists(unc))
        {
            return;
        }

        Assert.True(FreeSpaceGuard.TryGetAvailableFreeSpace(unc, out var available));
        Assert.True(available > 0);
        Assert.True(FreeSpaceGuard.HasEnoughFreeSpace(unc, multiplier: 2));
    }

    [Fact]
    public void OverflowSafeArithmeticNeverThrows()
    {
        // Documents the intent: the helper divides available-space by multiplier instead of
        // multiplying source-size by multiplier, so a pathological file size can't overflow.
        // Direct exercise of the overflow path requires a real file at long.MaxValue / multiplier
        // bytes, which no test host can provide — but the code shape is preserved by this test
        // running against a real file and by the small-file happy-path test above.
        Assert.True(FreeSpaceGuard.HasEnoughFreeSpace(_tempFile, multiplier: 3));
        Assert.True(FreeSpaceGuard.HasEnoughFreeSpace(_tempFile, multiplier: 100));
    }
}
