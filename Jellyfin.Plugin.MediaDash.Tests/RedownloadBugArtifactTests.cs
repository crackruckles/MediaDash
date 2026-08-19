using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Data;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class RedownloadBugArtifactTests
{
    // The pre-0.9.9 SubtitleLanguage path-collision bug wrote history rows crediting almost the whole
    // video size as BytesFreed. If *arr later backfilled the deleted file, RedownloadDetector would
    // flag it as a redownload — misleading. The heuristic here catches the shape so the UI can label it.

    [Fact]
    public void FullFileFreedOnSubtitleFixIsFlagged()
    {
        // 4 GB video, "freed" 4 GB — impossible for a legit -c copy subtitle strip.
        Assert.True(RedownloadDetector.IsLikelySubtitleBugArtifact(
            IssueType.SubtitleLanguage,
            bytesFreed: 4_000_000_000,
            originalSize: 4_000_000_000));
    }

    [Fact]
    public void MostOfFileFreedOnSubtitleFixIsFlagged()
    {
        // 80% freed — still well above the 70% threshold.
        Assert.True(RedownloadDetector.IsLikelySubtitleBugArtifact(
            IssueType.SubtitleLanguage,
            bytesFreed: 8_000_000_000,
            originalSize: 10_000_000_000));
    }

    [Fact]
    public void SmallSubtitleStripDeltaIsNotFlagged()
    {
        // Realistic PGS strip: 500 MB freed from a 30 GB Blu-ray remux — ~1.7% of the file.
        Assert.False(RedownloadDetector.IsLikelySubtitleBugArtifact(
            IssueType.SubtitleLanguage,
            bytesFreed: 500_000_000,
            originalSize: 30_000_000_000));
    }

    [Fact]
    public void JustUnderThresholdIsNotFlagged()
    {
        // 69% — deliberately below the 70% line so the heuristic can't creep on unusual-but-legit strips.
        Assert.False(RedownloadDetector.IsLikelySubtitleBugArtifact(
            IssueType.SubtitleLanguage,
            bytesFreed: 690,
            originalSize: 1000));
    }

    [Fact]
    public void QualityReencodeCanShrinkALotAndIsNotFlagged()
    {
        // Quality fixes legitimately re-encode aggressively — 8 Mbps → 2 Mbps drops the file to 25%,
        // freeing 75%. Scoping the artefact heuristic to SubtitleLanguage keeps this legit case clean.
        Assert.False(RedownloadDetector.IsLikelySubtitleBugArtifact(
            IssueType.Quality,
            bytesFreed: 7_500_000_000,
            originalSize: 10_000_000_000));
    }

    [Fact]
    public void AudioLanguageStripIsNotFlagged()
    {
        // Same TrackFixer path, but the "external audio" bug doesn't exist — scope stays subtitle-only.
        Assert.False(RedownloadDetector.IsLikelySubtitleBugArtifact(
            IssueType.AudioLanguage,
            bytesFreed: 4_000_000_000,
            originalSize: 4_000_000_000));
    }

    [Fact]
    public void ZeroOriginalSizeIsNotFlagged()
    {
        // Guards the * 0.7 multiplication from producing a degenerate "flag everything with any BytesFreed".
        Assert.False(RedownloadDetector.IsLikelySubtitleBugArtifact(
            IssueType.SubtitleLanguage,
            bytesFreed: 1_000_000,
            originalSize: 0));
    }
}
