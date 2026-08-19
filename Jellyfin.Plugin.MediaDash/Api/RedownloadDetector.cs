using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.MediaDash.Data;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Looks for MediaDash re-encodes that appear to have been undone by an external process
/// (typically Sonarr/Radarr replacing a "too-small" file that no longer meets its quality profile,
/// or a manual restore from the recycle bin).
/// </summary>
public static class RedownloadDetector
{
    /// <summary>
    /// Compares each recent successful re-encode against the current file at the same path.
    /// When the current file is close to the size of the original (still preserved in the recycle
    /// bin), the re-encode was undone.
    /// </summary>
    /// <param name="db">The plugin database.</param>
    /// <param name="lookback">How far back to check history. 30 days matches the default recycle-bin retention.</param>
    /// <returns>A list of suspected redownload cases, newest first, capped at 25.</returns>
    public static IReadOnlyList<RedownloadWarning> Detect(MediaDashDb db, TimeSpan lookback)
    {
        var cutoff = DateTime.UtcNow - lookback;
        var warnings = new List<RedownloadWarning>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in db.GetHistory(2000))
        {
            if (entry.FixedAtUtc < cutoff)
            {
                break;
            }

            if (!entry.Success
                || entry.WasDryRun
                || entry.Restored
                || entry.Acknowledged
                || string.IsNullOrEmpty(entry.RecyclePath)
                || (entry.Type != IssueType.Quality && entry.Type != IssueType.AudioLanguage && entry.Type != IssueType.SubtitleLanguage))
            {
                continue;
            }

            if (!seen.Add(entry.Path))
            {
                // Latest fix per path is authoritative; a later restore-and-re-fix would show up as a
                // newer history row and we already saw it.
                continue;
            }

            long currentSize;
            long originalSize;
            try
            {
                if (!File.Exists(entry.Path) || !File.Exists(entry.RecyclePath))
                {
                    continue;
                }

                currentSize = new FileInfo(entry.Path).Length;
                originalSize = new FileInfo(entry.RecyclePath).Length;
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            // Only fire when the file has grown back above where MediaDash left it. Comparing to the
            // ORIGINAL was too coarse — a legit -c copy subtitle/audio strip only shrinks a Blu-ray by
            // a few hundred MB out of 50 GB, so the shrunk file is naturally >90% of the original and
            // the detector was flagging every track-strip success as a "redownload".
            if (!HasGrownBackAboveShrunkSize(currentSize, originalSize, entry.BytesFreed))
            {
                continue;
            }

            var likelyBugArtifact = IsLikelySubtitleBugArtifact(entry.Type, entry.BytesFreed, originalSize);

            warnings.Add(new RedownloadWarning
            {
                HistoryId = entry.Id,
                Path = entry.Path,
                CurrentBytes = currentSize,
                OriginalBytes = originalSize,
                FixedAtUtc = entry.FixedAtUtc,
                RecyclePath = entry.RecyclePath,
                LikelySubtitleBugArtifact = likelyBugArtifact
            });

            if (warnings.Count >= 25)
            {
                break;
            }
        }

        return warnings;
    }

    /// <summary>
    /// True when the file currently at the path has grown at least 5 % above where MediaDash left it
    /// after the fix. shrunkSize = originalSize − bytesFreed, clamped ≥ 0 so a row with an inflated
    /// BytesFreed (see <see cref="IsLikelySubtitleBugArtifact"/>) collapses to a 0-baseline and any
    /// real file counts as "grown back". Exposed internal for direct unit testing.
    /// </summary>
    /// <param name="currentSize">Size of the file currently at the tracked path.</param>
    /// <param name="originalSize">Size of the file preserved in the recycle bin.</param>
    /// <param name="bytesFreed">BytesFreed recorded on the history row when the fix ran.</param>
    /// <returns>True when the current file has visibly grown above the shrunk size.</returns>
    internal static bool HasGrownBackAboveShrunkSize(long currentSize, long originalSize, long bytesFreed)
    {
        var shrunkSize = System.Math.Max(originalSize - bytesFreed, 0);

        // 5 % of shrunk plus a small absolute floor. Percent-only would let a legit 100 MB post-fix
        // file trigger on any 5 MB filesystem quirk; a floor keeps trivially small changes quiet.
        var growthThreshold = System.Math.Max((long)(shrunkSize * 0.05), 50L * 1024 * 1024);
        return currentSize > shrunkSize + growthThreshold;
    }

    /// <summary>
    /// True when a history row's shape matches the pre-0.9.9 SubtitleLanguage path-collision bug: a
    /// legit -c copy subtitle strip only saves a small delta because the remuxed file is nearly the
    /// same size, so BytesFreed near the whole original size means we wrongly credited the entire
    /// video file as "freed" (video path was listed as an external sidecar and the remuxed video was
    /// recycled too). Exposed internal for direct unit testing.
    /// </summary>
    /// <param name="type">The issue type of the history row.</param>
    /// <param name="bytesFreed">BytesFreed recorded on the history row.</param>
    /// <param name="originalSize">Size of the file preserved in the recycle bin.</param>
    /// <returns>True when the row looks like a bug artefact.</returns>
    internal static bool IsLikelySubtitleBugArtifact(IssueType type, long bytesFreed, long originalSize)
    {
        return type == IssueType.SubtitleLanguage
            && originalSize > 0
            && bytesFreed >= (long)(originalSize * 0.7);
    }
}
