using Jellyfin.Plugin.MediaDash.Api;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class RedownloadGrowthHeuristicTests
{
    // MB → bytes helper. Mirrors the units the banner UI displays.
    private static long Mb(long mb) => mb * 1024L * 1024L;

    // The false positives from the 2026-08-17 user bug report were all Blu-ray remux subtitle strips:
    // the "shrink" was 0.5 % - 5 % of a 50 GB file, but the detector treated any file >= 90 % of
    // original as a redownload, so every successful track strip on a big remux got flagged.
    // These tests are the regression net.

    [Fact]
    public void WillyWonkaCase_ShrunkFileStillInPlace_DoesNotWarn()
    {
        // From the screenshot: original 54225 MB → shrunk saved 276 MB → current 53949 MB (= shrunk).
        Assert.False(RedownloadDetector.HasGrownBackAboveShrunkSize(
            currentSize: Mb(53949),
            originalSize: Mb(54225),
            bytesFreed: Mb(276)));
    }

    [Fact]
    public void XMenCase_ShrunkFileStillInPlace_DoesNotWarn()
    {
        // Original 53640 MB → shrunk saved 2887 MB → current 50753 MB.
        Assert.False(RedownloadDetector.HasGrownBackAboveShrunkSize(
            currentSize: Mb(50753),
            originalSize: Mb(53640),
            bytesFreed: Mb(2887)));
    }

    [Fact]
    public void MissionImpossibleCase_ShrunkFileStillInPlace_DoesNotWarn()
    {
        // Original 51456 MB → shrunk saved 2598 MB → current 48858 MB.
        Assert.False(RedownloadDetector.HasGrownBackAboveShrunkSize(
            currentSize: Mb(48858),
            originalSize: Mb(51456),
            bytesFreed: Mb(2598)));
    }

    [Fact]
    public void QualityReencodeUndone_WarnsCorrectly()
    {
        // Original 5000 MB → aggressive re-encode saved 3000 MB → *arr replaced back at 5000 MB.
        // shrunkSize = 2000 MB, current = 5000 MB, 2.5x growth → warn.
        Assert.True(RedownloadDetector.HasGrownBackAboveShrunkSize(
            currentSize: Mb(5000),
            originalSize: Mb(5000),
            bytesFreed: Mb(3000)));
    }

    [Fact]
    public void QualityReencodeIntact_DoesNotWarn()
    {
        // Same fix, but no *arr — the 2000 MB shrunk file is still there.
        Assert.False(RedownloadDetector.HasGrownBackAboveShrunkSize(
            currentSize: Mb(2000),
            originalSize: Mb(5000),
            bytesFreed: Mb(3000)));
    }

    [Fact]
    public void SmallFluctuationBelowFloor_DoesNotWarn()
    {
        // 20 MB growth on a 100 MB shrunk file — under the 50 MB absolute floor.
        Assert.False(RedownloadDetector.HasGrownBackAboveShrunkSize(
            currentSize: Mb(120),
            originalSize: Mb(150),
            bytesFreed: Mb(50)));
    }

    [Fact]
    public void SubtitleBugArtifactInflatedBytesFreed_StillWarnsSoLikelyArtifactTagCanFire()
    {
        // BytesFreed >= originalSize → shrunkSize clamps to 0. Any real file at the path counts as
        // "grown back", so the warning fires and the LikelySubtitleBugArtifact label is what tells
        // the user it's the pre-0.9.9 bug. This preserves the previously-built artefact recovery flow.
        Assert.True(RedownloadDetector.HasGrownBackAboveShrunkSize(
            currentSize: Mb(4000),
            originalSize: Mb(4000),
            bytesFreed: Mb(4000)));
    }

    [Fact]
    public void GrowthJustAboveFivePercent_Warns()
    {
        // 2000 MB shrunk, 2101 MB current → 5.05% growth, above the 50 MB floor. Warn.
        Assert.True(RedownloadDetector.HasGrownBackAboveShrunkSize(
            currentSize: Mb(2101),
            originalSize: Mb(4000),
            bytesFreed: Mb(2000)));
    }
}
