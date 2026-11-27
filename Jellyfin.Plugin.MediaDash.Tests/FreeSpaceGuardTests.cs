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
    public void DescribeShortfall_ReturnsNullWhenSpaceGatePasses()
    {
        // Happy path: 1 KB file × 2 fits on any host running the test suite, so the guard reports
        // "no shortfall" via null. Callers key off this to decide whether to emit the error row.
        Assert.Null(FreeSpaceGuard.DescribeShortfall(_tempFile, multiplier: 2));
    }

    [Fact]
    public void DescribeShortfall_UnmeasurablePathDoesNotClaimMeasuredShortage()
    {
        var message = FreeSpaceGuard.DescribeShortfall("\0invalid-path", multiplier: 2);
        Assert.NotNull(message);
        Assert.Contains("couldn't determine free space", message);
        Assert.DoesNotContain("Not enough free", message);
    }

    [Fact]
    public void DescribeShortfall_MeasuredShortageReportsTheOutputDirectory()
    {
        using (var source = File.OpenWrite(_tempFile))
        {
            source.SetLength(1024 * 1024);
        }

        Assert.False(FreeSpaceGuard.HasEnoughFreeSpace(_tempFile, int.MaxValue));
        var message = FreeSpaceGuard.DescribeShortfall(_tempFile, int.MaxValue);
        Assert.NotNull(message);
        Assert.Contains("Not enough free disk space", message);
        Assert.Contains(Path.GetDirectoryName(_tempFile)!, message);
        Assert.Contains("available", message);
    }

    [Fact]
    public void DescribeShortfall_FailClosedBranchStaysBucketCompatible()
    {
        // Missing file + unresolvable path both return a non-null string. The callers prefix
        // "Not enough free disk space to …" + this fragment, so the BucketReason regex must still
        // bucket the composite as "Not enough free disk space". We only check the fragment is
        // non-empty — the prefix is what the bucketing matches on.
        var missing = Path.Combine(Path.GetTempPath(), "mediadash-space-missing-" + Guid.NewGuid().ToString("N") + ".bin");
        Assert.False(string.IsNullOrEmpty(FreeSpaceGuard.DescribeShortfall(missing, multiplier: 2)));
        Assert.False(string.IsNullOrEmpty(FreeSpaceGuard.DescribeShortfall("\0invalid-path", multiplier: 2)));
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
