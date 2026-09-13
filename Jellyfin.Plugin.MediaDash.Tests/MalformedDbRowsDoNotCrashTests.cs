using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Guardrail for Phase 1 (F-014, F-015, F-016 + systemic sweep). CLAUDE.md invariant #6 says the
// plugin never throws on unexpected DB row shape — this file pins that discipline. Each test
// synthesises a malformed value the DB could hold (a legacy row, a partial write, a debug
// injection) and asserts the reading code returns a safe default instead of throwing. Broken
// today → passes once Phase 1 lands.
public class MalformedDbRowsDoNotCrashTests
{
    // ─── Issue.HasBlockingWarnings (F-016) ─────────────────────────────────────
    // Called by FixTask's auto-queue rollback pass and by tests. Must never throw.

    [Theory]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    [InlineData("\"a string\"")]
    [InlineData("true")]
    [InlineData("{\"warnings\":null}")]
    [InlineData("{\"warnings\":\"not-an-array\"}")]
    [InlineData("{\"warnings\":42}")]
    [InlineData("{\"warnings\":[null,42,\"str\"]}")]
    [InlineData("{\"warnings\":[{\"blocking\":\"not-bool\"}]}")]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("{ not valid json")]
    public void HasBlockingWarnings_ReturnsFalseForMalformedInput(string detailsJson)
    {
        var issue = new Issue { DetailsJson = detailsJson };
        Assert.False(issue.HasBlockingWarnings);
    }

    [Fact]
    public void HasBlockingWarnings_TrueForRealBlockingWarning()
    {
        // Regression: making the malformed path safe must not break the happy path.
        var issue = new Issue { DetailsJson = "{\"warnings\":[{\"code\":\"x\",\"blocking\":true,\"message\":\"m\"}]}" };
        Assert.True(issue.HasBlockingWarnings);
    }

    // ─── PlayabilityFixer.TryGetReason (F-015) ─────────────────────────────────
    // Called by IsStillBrokenAsync on every Playability fix. Must never throw.

    [Theory]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    [InlineData("\"just a string\"")]
    [InlineData("{\"reason\":null}")]
    [InlineData("{\"reason\":[1,2]}")]
    [InlineData("{\"reason\":42}")]
    [InlineData("{\"reason\":true}")]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("{ malformed")]
    public void PlayabilityFixer_TryGetReason_ReturnsNullOnMalformed(string detailsJson)
    {
        var reason = PlayabilityFixer.TryGetReasonForTest(detailsJson);
        Assert.Null(reason);
    }

    [Fact]
    public void PlayabilityFixer_TryGetReason_ReturnsValueForWellFormedInput()
    {
        var reason = PlayabilityFixer.TryGetReasonForTest("{\"reason\":\"decode-error\"}");
        Assert.Equal("decode-error", reason);
    }

    // ─── IssueDto.ParseWarnings (systemic) ─────────────────────────────────────
    // Called by every /Issues API response. Must never throw — a single malformed row would
    // otherwise 500 the entire dashboard.

    [Theory]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    [InlineData("\"string\"")]
    public void IssueDto_FromIssue_ReturnsEmptyWarningsOnMalformedRoot(string detailsJson)
    {
        var issue = new Issue
        {
            Id = 1,
            Type = IssueType.AudioLanguage,
            Path = "C:/x.mkv",
            DetailsJson = detailsJson,
            Status = IssueStatus.Detected
        };
        var dto = Jellyfin.Plugin.MediaDash.Api.IssueDto.FromIssue(issue);
        Assert.Empty(dto.Warnings);
    }
}
