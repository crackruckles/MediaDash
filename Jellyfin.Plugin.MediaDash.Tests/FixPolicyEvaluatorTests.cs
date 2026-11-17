using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Rule-table coverage for <see cref="FixPolicyEvaluator"/>. Each rule from
/// <c>artifacts/refactor/02B/decision-table.md</c> gets at least one positive and one
/// negative case. Uses string-form <c>[InlineData]</c> + <c>Enum.Parse</c> so the
/// <c>FixIntent</c> / <c>FixReason</c> / <c>IssueType</c> / <c>IssueStatus</c> enums can
/// stay internal — same pattern as <see cref="FixResultPolicyTests"/>.
/// </summary>
public class FixPolicyEvaluatorTests
{
    [Theory]
    // Rule 1: outside library refuses regardless of everything else.
    [InlineData("Playability", "Automatic", "Queued", "Approved", false, false, false, "LibraryDisabled")]
    [InlineData("Duplicate", "Automatic", "Queued", "Scheduled", false, false, false, "LibraryDisabled")]

    // Rule 2: type is Off.
    [InlineData("Playability", "Off", "Queued", "Approved", true, false, false, "TypeDisabled")]
    [InlineData("AudioLanguage", "Off", "Queued", "Companion", true, false, true, "TypeDisabled")]

    // Rule 4: scheduled auto-queue + restored blocks.
    [InlineData("Quality", "Automatic", "Detected", "Scheduled", true, true, false, "RestoredPath")]

    // Manual approve bypasses restored-path gate (per plan — a user's explicit approve is stronger).
    [InlineData("Quality", "Automatic", "Queued", "Approved", true, true, false, "None")]

    // Rule 6: ManualApprove + Scheduled refuses.
    [InlineData("SubtitleLanguage", "ManualApprove", "Detected", "Scheduled", true, false, false, "ManualApprovalRequired")]

    // Rule 6 pass: ManualApprove + Approved is authorized (user explicitly approved).
    [InlineData("SubtitleLanguage", "ManualApprove", "Queued", "Approved", true, false, false, "None")]

    // Rule 7: ManualApprove + Companion + no companion refuses; with companion allows.
    [InlineData("AudioLanguage", "ManualApprove", "Detected", "Companion", true, false, false, "ManualApprovalRequired")]
    [InlineData("AudioLanguage", "ManualApprove", "Detected", "Companion", true, false, true, "None")]

    // Automatic + Companion doesn't need per-issue companion (bulk auth = bulk auth).
    [InlineData("AudioLanguage", "Automatic", "Detected", "Companion", true, false, false, "None")]

    // Rule 8: Approved intent with stale status refuses.
    [InlineData("Playability", "Automatic", "Fixed", "Approved", true, false, false, "SourceChanged")]
    [InlineData("Playability", "Automatic", "Dismissed", "Approved", true, false, false, "SourceChanged")]

    // Happy path: all rules pass.
    [InlineData("Quality", "Automatic", "Queued", "Scheduled", true, false, false, "None")]
    [InlineData("Duplicate", "Automatic", "Queued", "Approved", true, false, false, "None")]
    public void Evaluate_MatrixCoverage(
        string typeName,
        string modeName,
        string statusName,
        string intentName,
        bool withinLibrary,
        bool restoredBlocked,
        bool hasCompanion,
        string expectedReasonName)
    {
        var type = System.Enum.Parse<IssueType>(typeName);
        var status = System.Enum.Parse<IssueStatus>(statusName);
        var intent = System.Enum.Parse<FixIntent>(intentName);
        var expected = System.Enum.Parse<FixReason>(expectedReasonName);
        var config = NewConfig();
        SetMode(config, type, System.Enum.Parse<FixMode>(modeName));

        var decision = FixPolicyEvaluator.Evaluate(config, type, status, intent, withinLibrary, restoredBlocked, hasCompanion);

        if (expected == FixReason.None)
        {
            Assert.True(decision.Allowed, "Expected Allowed but got Refused/" + decision.Reason);
            Assert.Equal(FixReason.None, decision.Reason);
        }
        else
        {
            Assert.False(decision.Allowed, "Expected Refused/" + expected + " but got Allowed");
            Assert.Equal(expected, decision.Reason);
        }
    }

    [Fact]
    public void Evaluate_FixerDisabledListRefuses()
    {
        var config = NewConfig();
        config.PlayabilityFixMode = FixMode.Automatic;
        config.FixerDisabled = new[] { "Playability" };

        var decision = FixPolicyEvaluator.Evaluate(
            config,
            IssueType.Playability,
            IssueStatus.Queued,
            FixIntent.Approved,
            withinEnabledLibrary: true,
            autoQueueRestoredBlocked: false,
            hasApprovedCompanionOnPath: false);

        Assert.False(decision.Allowed);
        Assert.Equal(FixReason.TypeDisabled, decision.Reason);
    }

    [Fact]
    public void Evaluate_NullFixerDisabledDoesNotThrow()
    {
        // Preserves the pre-migration null-tolerance of FixTask's queue filter.
        var config = NewConfig();
        config.PlayabilityFixMode = FixMode.Automatic;
        config.FixerDisabled = null!;

        var decision = FixPolicyEvaluator.Evaluate(
            config,
            IssueType.Playability,
            IssueStatus.Queued,
            FixIntent.Approved,
            withinEnabledLibrary: true,
            autoQueueRestoredBlocked: false,
            hasApprovedCompanionOnPath: false);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void Evaluate_FixerDisabledListIsCaseSensitive()
    {
        // Pre-migration behaviour: string.Equals(..., StringComparison.Ordinal). A lowercase
        // "playability" entry does not match IssueType.Playability. Preserved deliberately —
        // the roster of allowed entries is documented by IssueType.ToString().
        var config = NewConfig();
        config.PlayabilityFixMode = FixMode.Automatic;
        config.FixerDisabled = new[] { "playability" };

        var decision = FixPolicyEvaluator.Evaluate(
            config,
            IssueType.Playability,
            IssueStatus.Queued,
            FixIntent.Approved,
            withinEnabledLibrary: true,
            autoQueueRestoredBlocked: false,
            hasApprovedCompanionOnPath: false);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void Evaluate_UnknownIssueTypeRefuses()
    {
        // Guard against a future IssueType value ending up in a persisted row on a downgraded
        // build, and against a corrupt DB row with an out-of-range integer.
        var unknownType = (IssueType)9999;
        var decision = FixPolicyEvaluator.Evaluate(
            NewConfig(),
            unknownType,
            IssueStatus.Queued,
            FixIntent.Approved,
            withinEnabledLibrary: true,
            autoQueueRestoredBlocked: false,
            hasApprovedCompanionOnPath: false);

        Assert.False(decision.Allowed);
        Assert.Equal(FixReason.Unknown, decision.Reason);
    }

    [Fact]
    public void PolicyDecision_AllowSingleton()
    {
        Assert.True(PolicyDecision.Allow.Allowed);
        Assert.Equal(FixReason.None, PolicyDecision.Allow.Reason);
    }

    [Fact]
    public void PolicyDecision_RefuseCarriesReason()
    {
        var d = PolicyDecision.Refuse(FixReason.ProviderQuota);
        Assert.False(d.Allowed);
        Assert.Equal(FixReason.ProviderQuota, d.Reason);
    }

    private static PluginConfiguration NewConfig() => new();

    private static void SetMode(PluginConfiguration config, IssueType type, FixMode mode)
    {
        switch (type)
        {
            case IssueType.Duplicate: config.DuplicateFixMode = mode; break;
            case IssueType.Quality: config.TranscodeFixMode = mode; break;
            case IssueType.SubtitleLanguage: config.SubtitleFixMode = mode; break;
            case IssueType.AudioLanguage: config.AudioFixMode = mode; break;
            case IssueType.Playability: config.PlayabilityFixMode = mode; break;
            case IssueType.Misplaced: config.MisplacedFixMode = mode; break;
            case IssueType.MissingSubtitles: config.MissingSubtitlesFixMode = mode; break;
            case IssueType.Stale: config.StaleFixMode = mode; break;
            case IssueType.MalwareRisk: config.SuspiciousFileFixMode = mode; break;
            case IssueType.LargeTrickplay: config.TrickplayFixMode = mode; break;
            case IssueType.SubtitleFonts: config.SubtitleFontFixMode = mode; break;
            case IssueType.OrphanedDebris: config.OrphanCleanupFixMode = mode; break;
            case IssueType.CorruptNfo: config.NfoFixMode = mode; break;
            case IssueType.HeavyTranscode: config.HeavyTranscodeFixMode = mode; break;
            case IssueType.FailedTranscode: config.FailedTranscodeFixMode = mode; break;
            case IssueType.EmbeddedCoverArt: config.EmbeddedCoverFixMode = mode; break;
            case IssueType.Ungrouped: config.UngroupedFixMode = mode; break;
            case IssueType.CorruptArtwork: config.CorruptArtworkFixMode = mode; break;
        }
    }
}
