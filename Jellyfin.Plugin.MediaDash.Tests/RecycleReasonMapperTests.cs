using System;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Data;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// The Reason chip and RestoreHint the user sees are user-facing safety copy: MUST cover every
/// IssueType, MUST be plain-language, and MUST always be non-empty (an empty chip is worse than
/// no chip — it makes the user question whether the row is broken).
/// </summary>
public class RecycleReasonMapperTests
{
    [Fact]
    public void EveryIssueType_ResolvesToANonEmptyReason()
    {
        var types = Enum.GetValues<IssueType>();
        foreach (var type in types)
        {
            var reason = RecycleReasonMapper.ReasonFor(type);
            Assert.False(string.IsNullOrWhiteSpace(reason), $"IssueType.{type} produced an empty reason string.");
        }
    }

    [Fact]
    public void HistoryProvenance_IncludesOriginalPathInTheHint_WhenKnown()
    {
        var hint = RecycleReasonMapper.RestoreHintFor(RecycleProvenance.History, "/lib/movies/Movie.mkv");
        Assert.Contains("/lib/movies/Movie.mkv", hint);
        Assert.Contains("Restore", hint);
        Assert.Contains("restored", hint);
    }

    [Fact]
    public void HistoryProvenance_HasGenericFallback_WhenOriginalPathIsNull()
    {
        var hint = RecycleReasonMapper.RestoreHintFor(RecycleProvenance.History, null);
        Assert.False(string.IsNullOrWhiteSpace(hint));
        // Must still tell the user the original-location semantics + suffix-on-collision behaviour.
        Assert.Contains("Restore", hint);
        Assert.Contains("restored", hint);
    }

    [Fact]
    public void ManifestProvenance_ExplainsThatOriginCameFromTheBinSidecar()
    {
        var hint = RecycleReasonMapper.RestoreHintFor(RecycleProvenance.Manifest, "/lib/foo/bar.mp3");
        Assert.Contains("/lib/foo/bar.mp3", hint);
        Assert.Contains("Restore", hint);
        Assert.Contains("manifest", hint);
    }

    [Fact]
    public void OrphanProvenance_DirectsUserToTheFilesTab()
    {
        var hint = RecycleReasonMapper.RestoreHintFor(RecycleProvenance.Orphan, null);
        Assert.Contains("Files tab", hint);
        Assert.Contains("Recycle bin", hint);
        // Must NOT promise a Restore button — orphaned rows can only be manually moved.
        Assert.DoesNotContain("Click Restore", hint);
    }

    [Theory]
    [InlineData(IssueType.Duplicate)]
    [InlineData(IssueType.SubtitleLanguage)]
    [InlineData(IssueType.AudioLanguage)]
    [InlineData(IssueType.OrphanedDebris)]
    [InlineData(IssueType.EmbeddedCoverArt)]
    public void ReasonStringsAreShortEnoughToFitInAChip(IssueType type)
    {
        var reason = RecycleReasonMapper.ReasonFor(type);
        // Chip max width is around 45 chars comfortably; anything longer wraps ugly.
        Assert.InRange(reason.Length, 1, 45);
    }

    [Fact]
    public void ReasonsAreDistinct_ForDifferentTypesThatShareABroadCategory()
    {
        // Field report: "Fix removed the file" was the old blanket string. Users needed to know
        // WHY. Assert reasons differ for pairs that might otherwise blur into "MediaDash removed it".
        Assert.NotEqual(RecycleReasonMapper.ReasonFor(IssueType.SubtitleLanguage), RecycleReasonMapper.ReasonFor(IssueType.AudioLanguage));
        Assert.NotEqual(RecycleReasonMapper.ReasonFor(IssueType.Duplicate), RecycleReasonMapper.ReasonFor(IssueType.Playability));
        Assert.NotEqual(RecycleReasonMapper.ReasonFor(IssueType.HeavyTranscode), RecycleReasonMapper.ReasonFor(IssueType.FailedTranscode));
    }

    [Theory]
    [InlineData(RecycleProvenance.History, "/lib/movies/Movie.mkv")]
    [InlineData(RecycleProvenance.History, null)]
    [InlineData(RecycleProvenance.Manifest, "/lib/movies/Movie.mkv")]
    [InlineData(RecycleProvenance.Manifest, null)]
    [InlineData(RecycleProvenance.Orphan, null)]
    public void RestoreHint_NeverContainsAngleBracketsOrAsciiDoubleQuotes(RecycleProvenance provenance, string? path)
    {
        // Regression guard for the "tooltip cuts off mid-sentence" bug. The hint renders into an
        // HTML title attribute — an inline " closes the attribute and everything after is
        // interpreted as the next attribute or ignored. Similarly, "<foo>" reads as an HTML tag
        // once the attribute has closed. Ban both.
        var hint = RecycleReasonMapper.RestoreHintFor(provenance, path);
        Assert.DoesNotContain("<", hint);
        Assert.DoesNotContain(">", hint);
        Assert.DoesNotContain("\"", hint);
    }
}
