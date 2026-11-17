using System;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Contract tests for <see cref="FixResultPolicy.GetQueueDisposition"/>. Refused operations
/// preserve their approval for review; actual no-change outcomes can complete a stale finding.
/// </summary>
/// <remarks>
/// The enums are internal (see <see cref="FixOutcome"/> / <see cref="FixReason"/> /
/// <see cref="QueueDisposition"/>) so <see cref="Xunit.InlineDataAttribute"/> can't
/// carry them directly through xUnit's public-signature discovery. Rows are passed
/// as strings and <see cref="Enum.Parse{T}(string)"/>'d inside the theory — the
/// InternalsVisibleTo attribute on the plugin assembly gives the test binary access.
/// </remarks>
public sealed class FixResultPolicyTests
{
    [Theory]
    // Successful mutation and preview both dispose the queue row via the success handler;
    // policy still has to answer if a producer bug ever routes them here. Never resurrect
    // the retry loop for something the producer said succeeded.
    [InlineData("Applied", "None", "CompleteLegacy")]
    [InlineData("Applied", "Unknown", "CompleteLegacy")]
    [InlineData("Preview", "None", "CompleteLegacy")]

    // NoChange — every current IsStaleFailure phrase of this shape is terminal for the queue.
    [InlineData("NoChange", "AlreadyHealthy", "CompleteLegacy")]
    [InlineData("NoChange", "NothingToRemove", "CompleteLegacy")]
    [InlineData("NoChange", "ExistingCover", "CompleteLegacy")]

    // Deferred — provider quota MUST stay queued (subtitle-quota reset semantics).
    [InlineData("Deferred", "ProviderQuota", "KeepQueued")]

    // Refused — preserve the approval and failed-attempt explanation for review.
    [InlineData("Refused", "OutsideLibrary", "KeepQueued")]
    [InlineData("Refused", "DrmProtected", "KeepQueued")]
    [InlineData("Refused", "DestinationCollision", "KeepQueued")]
    [InlineData("Refused", "UnsupportedFormat", "KeepQueued")]
    [InlineData("Refused", "IncompatibleContainer", "KeepQueued")]
    [InlineData("Refused", "InvalidCombinedRequest", "KeepQueued")]
    [InlineData("Refused", "TypeDisabled", "KeepQueued")]
    [InlineData("Refused", "LibraryDisabled", "KeepQueued")]
    // Protected reserves the Never-modify roadmap intent; keep queued in 01-A so no current
    // behavior claims it (Never-modify is a separately-migrated persisted feature).
    [InlineData("Refused", "Protected", "KeepQueued")]

    // Failed — the two terminal shapes today are SourceMissing / SourceChanged (FileNotFound,
    // DirectoryNotFound, IsStaleFailure "no longer exists"). Everything else must keep the
    // issue queued so the next scheduled run retries.
    [InlineData("Failed", "SourceMissing", "CompleteLegacy")]
    [InlineData("Failed", "SourceChanged", "CompleteLegacy")]
    // Roadmap i-110-minimum-savings: Quality re-encode that missed savings threshold must NOT
    // resurrect the retry loop — an identical encode on the next run produces an identical output.
    [InlineData("Failed", "InsufficientSavings", "CompleteLegacy")]
    [InlineData("Failed", "AccessDenied", "KeepQueued")]
    [InlineData("Failed", "InsufficientSpace", "KeepQueued")]
    [InlineData("Failed", "FileLocked", "KeepQueued")]
    [InlineData("Failed", "VerificationFailed", "KeepQueued")]
    [InlineData("Failed", "ProcessFailed", "KeepQueued")]
    [InlineData("Failed", "ProbeUnavailable", "KeepQueued")]
    [InlineData("Failed", "RecoveryRequired", "KeepQueued")]
    [InlineData("Failed", "Cancelled", "KeepQueued")]
    [InlineData("Failed", "Unknown", "KeepQueued")]
    [InlineData("Failed", "None", "KeepQueued")]

    // Unspecified — producer hasn't been migrated (01-B in progress). Never terminal.
    [InlineData("Unspecified", "None", "KeepQueued")]
    [InlineData("Unspecified", "SourceMissing", "KeepQueued")]
    [InlineData("Unspecified", "OutsideLibrary", "KeepQueued")]
    public void GetQueueDisposition_MapsAllInventoriedPairs(string outcome, string reason, string expected)
    {
        var actual = FixResultPolicy.GetQueueDisposition(
            Enum.Parse<FixOutcome>(outcome),
            Enum.Parse<FixReason>(reason));
        Assert.Equal(Enum.Parse<QueueDisposition>(expected), actual);
    }

    [Fact]
    public void GetQueueDisposition_DoesNotDependOnMessageText()
    {
        // Contract that 01-B rests on: two totally different friendly messages with the
        // same typed (outcome, reason) MUST produce the same disposition, because the
        // policy never sees the message. If this ever depended on a string, the retry
        // loop would silently come back on translation / rewording.
        var applied1 = new FixResult { Success = true, Message = "Repaired via ffmpeg -c copy", Outcome = FixOutcome.Applied };
        var applied2 = new FixResult { Success = true, Message = "Corregido con ffmpeg", Outcome = FixOutcome.Applied };
        Assert.Equal(
            FixResultPolicy.GetQueueDisposition(applied1.Outcome, applied1.Reason),
            FixResultPolicy.GetQueueDisposition(applied2.Outcome, applied2.Reason));

        var quota1 = new FixResult { Success = false, Message = "OpenSubtitles download limit reached", Outcome = FixOutcome.Deferred, Reason = FixReason.ProviderQuota };
        var quota2 = new FixResult { Success = false, Message = "OpenSubtitles ha esaurito il download quota per oggi", Outcome = FixOutcome.Deferred, Reason = FixReason.ProviderQuota };
        Assert.Equal(
            FixResultPolicy.GetQueueDisposition(quota1.Outcome, quota1.Reason),
            FixResultPolicy.GetQueueDisposition(quota2.Outcome, quota2.Reason));

        // Same friendly text, different typed reason: policy respects the reason, not the phrase.
        // (An Unknown-classified Failed must NOT impersonate AlreadyHealthy just because the
        //  message happens to contain the substring "plays fine now".)
        var wordedButUnclassified = FixResultPolicy.GetQueueDisposition(FixOutcome.Failed, FixReason.Unknown);
        var wordedAndClassified = FixResultPolicy.GetQueueDisposition(FixOutcome.NoChange, FixReason.AlreadyHealthy);
        Assert.NotEqual(wordedAndClassified, wordedButUnclassified);
    }

    [Fact]
    public void FixResult_DefaultOutcome_IsUnspecified_AndPolicyKeepsItQueued()
    {
        // A newly-constructed FixResult (e.g. any unmigrated producer in 01-B) defaults
        // to Unspecified/None. Policy MUST keep it queued so unmigrated producers can
        // never cause a silent retry-loop break.
        var raw = new FixResult { Success = false, Message = "anything" };
        Assert.Equal(FixOutcome.Unspecified, raw.Outcome);
        Assert.Equal(FixReason.None, raw.Reason);
        Assert.Equal(QueueDisposition.KeepQueued, FixResultPolicy.GetQueueDisposition(raw.Outcome, raw.Reason));
    }

    [Fact]
    public void FixResult_LegacyFailFactory_KeepsOutcomeUnspecifiedForUnmigratedProducers()
    {
        // The legacy string-only Fail overload is kept for producers not yet migrated in 01-B.
        // Policy MUST treat its default outcome as KeepQueued so an unmigrated caller can't
        // accidentally advance the queue. Migrated producers use Fail(FixReason, ...) instead.
        var failed = FixResult.Fail("some ffmpeg output");
        Assert.Equal(FixOutcome.Unspecified, failed.Outcome);
        Assert.Equal(FixReason.None, failed.Reason);
        Assert.False(failed.Success);
        Assert.Equal(QueueDisposition.KeepQueued, FixResultPolicy.GetQueueDisposition(failed.Outcome, failed.Reason));
    }

    [Fact]
    public void FixResult_DryRun_IsPreviewOutcome()
    {
        // DryRun is migrated in 01-B batch 1 because it's a lightweight producer-side signal
        // that every migrated fixer emits. Preview is a successful no-op — policy agrees the
        // queue can advance if the caller ever routed a Preview to the retry-suppression seam.
        var dry = FixResult.DryRun("moved x to y", 42);
        Assert.Equal(FixOutcome.Preview, dry.Outcome);
        Assert.Equal(FixReason.None, dry.Reason);
        Assert.True(dry.Success);
        Assert.True(dry.WasDryRun);
        Assert.Equal(QueueDisposition.CompleteLegacy, FixResultPolicy.GetQueueDisposition(dry.Outcome, dry.Reason));
    }

    [Fact]
    public void FixResult_TypedFactories_SetOutcomeAndReasonInvariantly()
    {
        var failed = FixResult.Fail(FixReason.AccessDenied, "no write perm");
        Assert.Equal(FixOutcome.Failed, failed.Outcome);
        Assert.Equal(FixReason.AccessDenied, failed.Reason);
        Assert.False(failed.Success);

        var refused = FixResult.Refused(FixReason.OutsideLibrary, "outside library");
        Assert.Equal(FixOutcome.Refused, refused.Outcome);
        Assert.Equal(FixReason.OutsideLibrary, refused.Reason);
        Assert.False(refused.Success);

        var noChange = FixResult.NoChange(FixReason.AlreadyHealthy, "plays fine now");
        Assert.Equal(FixOutcome.NoChange, noChange.Outcome);
        Assert.Equal(FixReason.AlreadyHealthy, noChange.Reason);
        Assert.False(noChange.Success);

        var deferred = FixResult.Deferred(FixReason.ProviderQuota, "opensubs out for today");
        Assert.Equal(FixOutcome.Deferred, deferred.Outcome);
        Assert.Equal(FixReason.ProviderQuota, deferred.Reason);
        Assert.False(deferred.Success);
    }
}
