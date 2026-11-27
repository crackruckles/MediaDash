using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Pure-planner invariants for <c>FixPlanBuilder.Build</c>. Every input issue must land in
/// exactly one plan (single, track pair, or transcode-with-tracks) or be explicitly deferred —
/// none silently lost. Transcode-companion routing beats combined-pair grouping for a shared
/// path. Ordering is deterministic across runs given the same rank, size and comparer inputs.
/// Uses <c>InternalsVisibleTo</c> — <see cref="PlannedFix"/> and <see cref="PlannedFixKind"/>
/// stay internal.
/// </summary>
public sealed class FixPlanBuilderTests
{
    // Legacy ranks also exercise saved orders that put movers early. Two audio issues at the same rank + size still get the id
    // tie-break — that's the invariant the builder is responsible for, not the caller.
    private static int Rank(IssueType type) => type switch
    {
        IssueType.MalwareRisk => 0,
        IssueType.Playability => 1,
        IssueType.Duplicate => 2,
        IssueType.OrphanedDebris => 3,
        IssueType.Misplaced => 4,
        IssueType.Ungrouped => 5,
        IssueType.MissingSubtitles => 6,
        IssueType.SubtitleLanguage => 7,
        IssueType.AudioLanguage => 8,
        IssueType.Quality => 9,
        IssueType.HeavyTranscode => 10,
        IssueType.FailedTranscode => 11,
        IssueType.SubtitleFonts => 12,
        IssueType.EmbeddedCoverArt => 13,
        IssueType.CorruptNfo => 14,
        IssueType.CorruptArtwork => 15,
        IssueType.Stale => 16,
        IssueType.LargeTrickplay => 17,
        _ => int.MaxValue,
    };

    private static long ZeroSize(Issue _) => 0;

    private static Issue NewIssue(long id, IssueType type, string path) => new()
    {
        Id = id,
        Type = type,
        Path = path,
        Status = IssueStatus.Queued,
        DetectedAtUtc = DateTime.UtcNow,
    };

    [Fact]
    public void EmptyQueueReturnsEmptyPlan()
    {
        var plans = FixPlanBuilder.Build(Array.Empty<Issue>(), Rank, ZeroSize, StringComparer.OrdinalIgnoreCase);

        Assert.Empty(plans);
    }

    [Fact]
    public void SingleIssueBecomesSinglePlan()
    {
        var only = NewIssue(1, IssueType.Playability, "/lib/movie.mkv");

        var plans = FixPlanBuilder.Build(new[] { only }, Rank, ZeroSize, StringComparer.OrdinalIgnoreCase);

        var plan = Assert.Single(plans);
        Assert.Equal(PlannedFixKind.Single, plan.Kind);
        Assert.Same(only, plan.Primary);
        Assert.Empty(plan.Companions);
    }

    [Fact]
    public void AudioSubtitlePairOnSamePathBecomesTrackPair()
    {
        var audio = NewIssue(2, IssueType.AudioLanguage, "/lib/tv/E.mkv");
        var subtitle = NewIssue(1, IssueType.SubtitleLanguage, "/lib/tv/E.mkv");

        var plans = FixPlanBuilder.Build(new[] { audio, subtitle }, Rank, ZeroSize, StringComparer.OrdinalIgnoreCase);

        var plan = Assert.Single(plans);
        Assert.Equal(PlannedFixKind.TrackPair, plan.Kind);
        Assert.Same(audio, plan.Primary);
        Assert.Same(subtitle, Assert.Single(plan.Companions));
    }

    [Fact]
    public void TranscodeWithAudioAndSubtitleBecomesTranscodeWithTracks()
    {
        var transcode = NewIssue(1, IssueType.Quality, "/lib/tv/E.mkv");
        var audio = NewIssue(2, IssueType.AudioLanguage, "/lib/tv/E.mkv");
        var subtitle = NewIssue(3, IssueType.SubtitleLanguage, "/lib/tv/E.mkv");

        var plans = FixPlanBuilder.Build(new[] { transcode, audio, subtitle }, Rank, ZeroSize, StringComparer.OrdinalIgnoreCase);

        var plan = Assert.Single(plans);
        Assert.Equal(PlannedFixKind.TranscodeWithTracks, plan.Kind);
        Assert.Same(transcode, plan.Primary);
        Assert.Equal(2, plan.Companions.Count);
        Assert.Contains(audio, plan.Companions);
        Assert.Contains(subtitle, plan.Companions);
    }

    [Fact]
    public void TranscodeAloneBecomesSingle()
    {
        var transcode = NewIssue(1, IssueType.Quality, "/lib/movies/M.mp4");

        var plans = FixPlanBuilder.Build(new[] { transcode }, Rank, ZeroSize, StringComparer.OrdinalIgnoreCase);

        var plan = Assert.Single(plans);
        Assert.Equal(PlannedFixKind.Single, plan.Kind);
        Assert.Same(transcode, plan.Primary);
    }

    [Fact]
    public void AudioAndSubtitleOnDifferentFilesEachBecomeSingle()
    {
        var audio = NewIssue(1, IssueType.AudioLanguage, "/lib/A.mkv");
        var subtitle = NewIssue(2, IssueType.SubtitleLanguage, "/lib/B.mkv");

        var plans = FixPlanBuilder.Build(new[] { audio, subtitle }, Rank, ZeroSize, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(2, plans.Count);
        Assert.All(plans, p => Assert.Equal(PlannedFixKind.Single, p.Kind));
    }

    [Fact]
    public void MultipleTranscodesOnSamePathFirstClaimsCompanionsRestBecomeSingle()
    {
        // Preserves pre-refactor behaviour: BuildTranscodeCompanionMap picks the FIRST
        // transcode-family issue per path (natural order of the queue); later transcodes
        // don't get companions and thus emit their own Single plans. Double-mutation on the
        // same path is a scanner de-dup concern, not a planner concern.
        var first = NewIssue(1, IssueType.Quality, "/lib/A.mkv");
        var second = NewIssue(2, IssueType.HeavyTranscode, "/lib/A.mkv");
        var audio = NewIssue(3, IssueType.AudioLanguage, "/lib/A.mkv");

        var plans = FixPlanBuilder.Build(new[] { first, second, audio }, Rank, ZeroSize, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(2, plans.Count);
        var claimant = plans.Single(p => p.Kind == PlannedFixKind.TranscodeWithTracks);
        Assert.Same(first, claimant.Primary);
        Assert.Same(audio, Assert.Single(claimant.Companions));
        var loner = plans.Single(p => p.Kind == PlannedFixKind.Single);
        Assert.Same(second, loner.Primary);
    }

    [Fact]
    public void CaseInsensitiveComparerMergesCaseDistinctPaths()
    {
        // Windows / HFS+ default / most APFS. Same file across casing => one plan.
        var transcode = NewIssue(1, IssueType.Quality, @"C:\Media\TV\Show.mkv");
        var audio = NewIssue(2, IssueType.AudioLanguage, @"c:\media\tv\show.mkv");

        var plans = FixPlanBuilder.Build(new[] { transcode, audio }, Rank, ZeroSize, StringComparer.OrdinalIgnoreCase);

        var plan = Assert.Single(plans);
        Assert.Equal(PlannedFixKind.TranscodeWithTracks, plan.Kind);
        Assert.Same(audio, Assert.Single(plan.Companions));
    }

    [Fact]
    public void CaseSensitiveComparerDoesNotMergeCaseDistinctPaths()
    {
        // Linux ext4/xfs/btrfs and case-sensitive APFS volumes: /A/foo.mkv and /A/FOO.mkv
        // are two different files. The pre-refactor OrdinalIgnoreCase grouping would have
        // silently merged them and mis-claimed one as a companion of the other.
        var upperTranscode = NewIssue(1, IssueType.Quality, "/lib/tv/FOO.mkv");
        var lowerAudio = NewIssue(2, IssueType.AudioLanguage, "/lib/tv/foo.mkv");

        var plans = FixPlanBuilder.Build(new[] { upperTranscode, lowerAudio }, Rank, ZeroSize, StringComparer.Ordinal);

        Assert.Equal(2, plans.Count);
        Assert.All(plans, p => Assert.Equal(PlannedFixKind.Single, p.Kind));
        Assert.Contains(plans, p => ReferenceEquals(p.Primary, upperTranscode));
        Assert.Contains(plans, p => ReferenceEquals(p.Primary, lowerAudio));
    }

    [Fact]
    public void EveryInputIssueBelongsToExactlyOnePlan()
    {
        // Cross-invariant check the plan checklist calls out: no silent losses, no double-
        // assignment. Mix of Singles, TrackPair, TranscodeWithTracks over unrelated files.
        var issues = new[]
        {
            NewIssue(1, IssueType.Playability, "/lib/A.mkv"),
            NewIssue(2, IssueType.Quality, "/lib/B.mkv"),
            NewIssue(3, IssueType.AudioLanguage, "/lib/B.mkv"),
            NewIssue(4, IssueType.SubtitleLanguage, "/lib/B.mkv"),
            NewIssue(5, IssueType.AudioLanguage, "/lib/C.mkv"),
            NewIssue(6, IssueType.SubtitleLanguage, "/lib/C.mkv"),
            NewIssue(7, IssueType.Duplicate, "/lib/D.mkv"),
        };

        var plans = FixPlanBuilder.Build(issues, Rank, ZeroSize, StringComparer.OrdinalIgnoreCase);

        var assignedIds = plans
            .SelectMany(p => new[] { p.Primary.Id }.Concat(p.Companions.Select(c => c.Id)))
            .ToArray();
        Assert.Equal(assignedIds.Length, assignedIds.Distinct().Count());
        Assert.Equal(issues.Select(i => i.Id).OrderBy(id => id), assignedIds.OrderBy(id => id));
    }

    [Fact]
    public void OrderingRespectsRankThenSizeThenId()
    {
        // Two Duplicates same rank; one bigger. Two Playabilities same rank + size; ordered by id.
        var largeDup = NewIssue(20, IssueType.Duplicate, "/lib/big.mkv");
        var smallDup = NewIssue(10, IssueType.Duplicate, "/lib/small.mkv");
        var playHiId = NewIssue(30, IssueType.Playability, "/lib/broken1.mkv");
        var playLoId = NewIssue(2, IssueType.Playability, "/lib/broken2.mkv");
        var sizes = new Dictionary<long, long>
        {
            [largeDup.Id] = 5_000_000_000,
            [smallDup.Id] = 100_000,
            [playHiId.Id] = 0,
            [playLoId.Id] = 0,
        };

        // Feed in a scrambled order; the builder must sort into (Playability lo-id, Playability
        // hi-id, small Duplicate, large Duplicate).
        var plans = FixPlanBuilder.Build(
            new[] { largeDup, playHiId, smallDup, playLoId },
            Rank,
            i => sizes[i.Id],
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal(4, plans.Count);
        Assert.Same(playLoId, plans[0].Primary);
        Assert.Same(playHiId, plans[1].Primary);
        Assert.Same(smallDup, plans[2].Primary);
        Assert.Same(largeDup, plans[3].Primary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PathChangingFixesFollowRepairsAndFolderMovesRunLastEvenWithCustomRanks(bool reversed)
    {
        var repairs = new[] { IssueType.MalwareRisk, IssueType.Playability, IssueType.Duplicate,
            IssueType.OrphanedDebris, IssueType.MissingSubtitles, IssueType.SubtitleLanguage,
            IssueType.AudioLanguage, IssueType.SubtitleFonts, IssueType.EmbeddedCoverArt,
            IssueType.CorruptNfo, IssueType.CorruptArtwork, IssueType.Stale, IssueType.LargeTrickplay }
            .Select((type, index) => NewIssue(index + 1, type, "/lib/repair-" + index)).ToArray();
        var rewrites = new[] { IssueType.Quality, IssueType.HeavyTranscode, IssueType.FailedTranscode, IssueType.FormatCompatibility }
            .Select((type, index) => NewIssue(index + 100, type, "/lib/rewrite-" + index)).ToList();
        var rename = NewIssue(200, IssueType.Playability, "/lib/wrong-extension.avi");
        rename.DetailsJson = "{\"reason\":\"container-extension-mismatch\",\"rename_to_ext\":\"mkv\"}";
        rewrites.Add(rename);
        var moves = new[] { IssueType.Misplaced, IssueType.Ungrouped, IssueType.ManualMove }
            .Select((type, index) => NewIssue(index + 300, type, "/lib/move-" + index)).ToArray();
        var plans = FixPlanBuilder.Build(moves.Concat(rewrites).Concat(repairs).ToArray(),
            type => reversed ? -(int)type : Rank(type), ZeroSize, StringComparer.Ordinal);

        Assert.Equal(repairs.Select(issue => issue.Id).Order(), plans.Take(repairs.Length).Select(plan => plan.Primary.Id).Order());
        Assert.Equal(rewrites.Select(issue => issue.Id).Order(), plans.Skip(repairs.Length).Take(rewrites.Count).Select(plan => plan.Primary.Id).Order());
        Assert.Equal(moves.Select(issue => issue.Id).Order(), plans.TakeLast(moves.Length).Select(plan => plan.Primary.Id).Order());
    }

    [Fact]
    public void DeferredTranscodeStillClaimsItsEarlierRankedTrackCompanions()
    {
        var transcode = NewIssue(1, IssueType.Quality, "/lib/film.mkv");
        var audio = NewIssue(2, IssueType.AudioLanguage, transcode.Path);
        var subtitle = NewIssue(3, IssueType.SubtitleLanguage, transcode.Path);
        var sidecar = NewIssue(4, IssueType.SubtitleFonts, "/lib/film.en.ass");
        var move = NewIssue(5, IssueType.Ungrouped, transcode.Path);
        var plans = FixPlanBuilder.Build(new[] { move, transcode, audio, subtitle, sidecar }, _ => 0, ZeroSize, StringComparer.Ordinal);
        Assert.Equal(new long[] { 4, 1, 5 }, plans.Select(plan => plan.Primary.Id));
        Assert.Equal(PlannedFixKind.TranscodeWithTracks, plans[1].Kind);
        Assert.Equal(new long[] { 2, 3 }, plans[1].Companions.Select(issue => issue.Id));
    }

    [Fact]
    public void OrderingIsDeterministicAcrossPermutations()
    {
        // Property-flavored regression: shuffle the input under a fixed seed a few times and
        // assert the plan sequence is identical every run. No fuzzing package needed; a small
        // handful of permutations is enough to catch a nondeterministic sort or a hash-order
        // dependency in the grouping.
        var canonical = new[]
        {
            NewIssue(1, IssueType.Playability, "/lib/A.mkv"),
            NewIssue(2, IssueType.Quality, "/lib/B.mkv"),
            NewIssue(3, IssueType.AudioLanguage, "/lib/B.mkv"),
            NewIssue(4, IssueType.SubtitleLanguage, "/lib/B.mkv"),
            NewIssue(5, IssueType.Duplicate, "/lib/C.mkv"),
            NewIssue(6, IssueType.AudioLanguage, "/lib/D.mkv"),
            NewIssue(7, IssueType.SubtitleLanguage, "/lib/D.mkv"),
            NewIssue(8, IssueType.LargeTrickplay, "/lib/E.mkv"),
        };
        var reference = FixPlanBuilder.Build(canonical, Rank, ZeroSize, StringComparer.OrdinalIgnoreCase);
        var referenceSequence = reference.Select(p => (p.Kind, p.Primary.Id)).ToArray();

        var rng = new Random(42);
        for (var trial = 0; trial < 8; trial++)
        {
            var shuffled = canonical.OrderBy(_ => rng.Next()).ToArray();
            var plans = FixPlanBuilder.Build(shuffled, Rank, ZeroSize, StringComparer.OrdinalIgnoreCase);
            var sequence = plans.Select(p => (p.Kind, p.Primary.Id)).ToArray();
            Assert.Equal(referenceSequence, sequence);
        }
    }

    [Fact]
    public void UnrelatedIssueTypesStayIndependentSingles()
    {
        // A Duplicate + LargeTrickplay on the same file must NOT get any companion routing —
        // the transcode-companion and track-pair rules are scoped strictly to their listed
        // types. This mirrors the pre-refactor FixTaskTranscodeCompanionTests.UnrelatedIssueTypesAreIgnored.
        var duplicate = NewIssue(1, IssueType.Duplicate, "/lib/A.mkv");
        var trickplay = NewIssue(2, IssueType.LargeTrickplay, "/lib/A.mkv");

        var plans = FixPlanBuilder.Build(new[] { duplicate, trickplay }, Rank, ZeroSize, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(2, plans.Count);
        Assert.All(plans, p => Assert.Equal(PlannedFixKind.Single, p.Kind));
    }

    [Fact]
    public void DuplicateInputIdsAreDedupedNotDoubleAssigned()
    {
        // Malformed input: same DB row appearing twice in the input list (should not happen in
        // production, but the builder must not lose an id nor emit two plans for it). First
        // occurrence wins so the ordering upstream is honored.
        var only = NewIssue(1, IssueType.Playability, "/lib/A.mkv");
        var duplicate = NewIssue(1, IssueType.Playability, "/lib/A.mkv");

        var plans = FixPlanBuilder.Build(new[] { only, duplicate }, Rank, ZeroSize, StringComparer.OrdinalIgnoreCase);

        var plan = Assert.Single(plans);
        Assert.Same(only, plan.Primary);
    }
}
