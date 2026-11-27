using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;

namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>
/// Pure grouping-plus-ordering function that turns a list of eligible queued issues into a
/// deterministic sequence of fix operations. Runs no filesystem, DB or ffmpeg work; every
/// input is passed explicitly by the caller. Extracted from the legacy inline
/// <c>BuildTranscodeCompanions</c> + combined-pairs blocks in <see cref="FixTask"/>.
///
/// Invariants:
/// <list type="bullet">
///   <item>Every unique input issue belongs to exactly one plan — companions are never emitted as
///     their own plan and no issue is silently lost.</item>
///   <item>Deterministic across runs for the same (input, rank, size, comparer) inputs.</item>
///   <item>Path comparison is caller-supplied. Callers on case-insensitive volumes
///     (Windows / HFS+ default / most APFS) pass <see cref="StringComparer.OrdinalIgnoreCase"/>;
///     case-sensitive volumes pass <see cref="StringComparer.Ordinal"/>. The builder never
///     lowercases a path — symlink/canonical resolution is not a string operation.</item>
///   <item>When multiple transcode-family issues share a path, the first one in execution
///     order claims any <c>AudioLanguage</c>/<c>SubtitleLanguage</c> companions; the rest become
///     standalone <see cref="PlannedFixKind.Single"/> plans. ponytail: preserves pre-refactor
///     behaviour — concurrent double-mutation on the same path is a scanner de-dup problem,
///     not a planner problem.</item>
/// </list>
/// </summary>
internal static class FixPlanBuilder
{
    /// <summary>
    /// Produces the plan sequence for the current queue.
    /// </summary>
    /// <param name="eligibleIssues">Pre-filtered queue (library-scoped, policy-passing, type-authorised). The caller owns filtering.</param>
    /// <param name="fixerRank">Ordering rank for an issue type. Lower runs first.</param>
    /// <param name="issueSize">Size used for the secondary tie-break. Callers typically stat the filesystem here — the builder invokes the delegate but does no I/O itself.</param>
    /// <param name="pathComparer">Comparer used to group issues on the same physical path.</param>
    /// <returns>The plan sequence in execution order.</returns>
    public static IReadOnlyList<PlannedFix> Build(
        IReadOnlyList<Issue> eligibleIssues,
        Func<IssueType, int> fixerRank,
        Func<Issue, long> issueSize,
        StringComparer pathComparer)
    {
        if (eligibleIssues.Count == 0)
        {
            return Array.Empty<PlannedFix>();
        }

        // Dedup defensively — a malformed input list with the same Id twice must not lose the
        // issue nor emit two plans for it. First occurrence wins so ordering upstream is honored.
        var deduped = eligibleIssues
            .GroupBy(i => i.Id)
            .Select(g => g.First())
            .ToList();

        // Keep sources in place for repairs, then change filenames/formats, then move folders.
        // Deterministic order within each phase: rank, size, then id as the stable final tie-break so two
        // issues at the same rank+size never flip position across runs even if the DB returns
        // rows in a different order or LINQ's stable-sort semantics change.
        var ordered = deduped
            .OrderBy(ExecutionPhase)
            .ThenBy(i => fixerRank(i.Type))
            .ThenBy(issueSize)
            .ThenBy(i => i.Id)
            .ToList();

        var transcodeCompanions = BuildTranscodeCompanionMap(ordered, pathComparer);
        var transcodeCompanionIds = new HashSet<long>(
            transcodeCompanions.SelectMany(kv => kv.Value.Select(i => i.Id)));
        var combinedPairs = BuildCombinedPairs(ordered, transcodeCompanionIds, pathComparer);
        var combinedPartnerIds = new HashSet<long>(combinedPairs.Values.Select(v => v.Id));

        var plans = new List<PlannedFix>(ordered.Count);
        foreach (var issue in ordered)
        {
            if (transcodeCompanionIds.Contains(issue.Id) || combinedPartnerIds.Contains(issue.Id))
            {
                continue;
            }

            if (transcodeCompanions.TryGetValue(issue.Id, out var companions))
            {
                plans.Add(new PlannedFix(PlannedFixKind.TranscodeWithTracks, issue, companions));
                continue;
            }

            if (combinedPairs.TryGetValue(issue.Id, out var partner))
            {
                plans.Add(new PlannedFix(PlannedFixKind.TrackPair, issue, new[] { partner }));
                continue;
            }

            plans.Add(new PlannedFix(PlannedFixKind.Single, issue, Array.Empty<Issue>()));
        }

        return plans;
    }

    private static int ExecutionPhase(Issue issue) => issue.Type switch
    {
        IssueType.Misplaced or IssueType.Ungrouped or IssueType.ManualMove => 2,
        IssueType.Quality or IssueType.HeavyTranscode or IssueType.FailedTranscode or IssueType.FormatCompatibility => 1,
        IssueType.Playability when IssueDetailsReader.TryReadPlayability(issue.DetailsJson, out var details)
            && details?.Reason == "container-extension-mismatch" => 1,
        _ => 0
    };

    /// <summary>
    /// Groups every queued transcode-family issue with any <c>AudioLanguage</c>/<c>SubtitleLanguage</c>
    /// issue on the same path. Preserves the pre-refactor behaviour: when a path has multiple
    /// transcode-family issues (rare but possible: <c>Quality</c> + <c>FailedTranscode</c> co-detected),
    /// the first one in natural order wins — later transcodes become standalone plans.
    /// Kept internal so <see cref="FixTask.BuildTranscodeCompanions"/> can forward without
    /// re-implementing the grouping.
    /// </summary>
    /// <param name="queue">The queue (may be ordered or not — grouping is order-independent for the map, but the winning transcode is chosen by <paramref name="queue"/>'s natural order).</param>
    /// <param name="pathComparer">Path grouping policy.</param>
    /// <returns>Map of transcode issue id → companion audio/subtitle issues on the same path.</returns>
    internal static Dictionary<long, List<Issue>> BuildTranscodeCompanionMap(
        IReadOnlyList<Issue> queue,
        StringComparer pathComparer)
    {
        var result = new Dictionary<long, List<Issue>>();
        var pathGroups = queue
            .Where(i => i.Type is IssueType.Quality
                                  or IssueType.HeavyTranscode
                                  or IssueType.FailedTranscode
                                  or IssueType.AudioLanguage
                                  or IssueType.SubtitleLanguage)
            .GroupBy(i => i.Path, pathComparer);
        foreach (var pathGroup in pathGroups)
        {
            var transcode = pathGroup.FirstOrDefault(i => i.Type is IssueType.Quality
                                                                   or IssueType.HeavyTranscode
                                                                   or IssueType.FailedTranscode);
            if (transcode is null)
            {
                continue;
            }

            var companions = pathGroup
                .Where(i => i.Type is IssueType.AudioLanguage or IssueType.SubtitleLanguage)
                .ToList();
            if (companions.Count == 0)
            {
                continue;
            }

            result[transcode.Id] = companions;
        }

        return result;
    }

    private static Dictionary<long, Issue> BuildCombinedPairs(
        IReadOnlyList<Issue> queue,
        HashSet<long> transcodeCompanionIds,
        StringComparer pathComparer)
    {
        var pairs = new Dictionary<long, Issue>();
        var trackPairs = queue
            .Where(i => (i.Type == IssueType.AudioLanguage || i.Type == IssueType.SubtitleLanguage)
                        && !transcodeCompanionIds.Contains(i.Id))
            .GroupBy(i => i.Path, pathComparer);
        foreach (var pathGroup in trackPairs)
        {
            var audio = pathGroup.FirstOrDefault(i => i.Type == IssueType.AudioLanguage);
            var subtitle = pathGroup.FirstOrDefault(i => i.Type == IssueType.SubtitleLanguage);
            if (audio is not null && subtitle is not null)
            {
                pairs[audio.Id] = subtitle;
            }
        }

        return pairs;
    }
}
