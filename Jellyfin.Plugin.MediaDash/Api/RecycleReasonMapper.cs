using Jellyfin.Plugin.MediaDash.Data;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Maps <see cref="IssueType"/> to the plain-language reason surfaced on the Recycle bin tab.
/// Every enum value has a covering string here; new IssueType additions must add an entry or the
/// compiler nudges — <see cref="ReasonFor"/> uses an exhaustive switch expression.
/// </summary>
public static class RecycleReasonMapper
{
    /// <summary>
    /// Plain-language reason for each IssueType, phrased for a non-technical user reading the
    /// Recycle bin tab. Keep short — the UI renders as a chip.
    /// </summary>
    /// <param name="type">The issue type whose fix put this file in the bin.</param>
    /// <returns>Short reason string; never null or empty.</returns>
    public static string ReasonFor(IssueType type) => type switch
    {
        IssueType.Duplicate => "Duplicate — kept a better copy",
        IssueType.Playability => "Unplayable file removed",
        IssueType.Quality => "Re-encoded to fit quality ceiling",
        IssueType.SubtitleLanguage => "Removed unwanted subtitle language",
        IssueType.AudioLanguage => "Removed unwanted audio language",
        IssueType.Misplaced => "Moved to correct library folder",
        IssueType.MissingSubtitles => "Missing subtitles fix",
        IssueType.Stale => "Stale content cleanup",
        IssueType.CorruptArtwork => "Corrupt artwork removed",
        IssueType.MalwareRisk => "Suspicious file quarantined",
        IssueType.Ungrouped => "Regrouped with matching media",
        IssueType.LargeTrickplay => "Trickplay preview optimized",
        IssueType.SubtitleFonts => "Reduced subtitle font bloat",
        IssueType.OrphanedDebris => "Orphaned sidecar cleanup",
        IssueType.CorruptNfo => "Corrupt NFO removed",
        IssueType.HeavyTranscode => "Re-encoded for direct-play",
        IssueType.FailedTranscode => "Re-encoded (previous attempt failed)",
        IssueType.EmbeddedCoverArt => "Extracted folder cover art",
        _ => "MediaDash fix",
    };

    /// <summary>
    /// Recovery guidance for a row whose provenance and original path are known. The UI renders
    /// this verbatim so the wording lives in one place instead of scattered across templates.
    /// </summary>
    /// <param name="provenance">Where the item's metadata came from.</param>
    /// <param name="originalPath">The path the file would be restored to; null for orphans.</param>
    /// <returns>Non-empty guidance string.</returns>
    public static string RestoreHintFor(RecycleProvenance provenance, string? originalPath)
    {
        // No angle brackets or ASCII double quotes in the copy — this string ends up in an HTML
        // title attribute, and inline quotes close the attribute early on some Jellyfin builds.
        // Use en-quotes (‘ ’) and describe the suffix in prose instead of a <placeholder> pattern.
        return provenance switch
        {
            RecycleProvenance.History => string.IsNullOrEmpty(originalPath)
                ? "Click Restore to put the file back at its original location. Anything already at that path is kept — the restored copy lands beside it with a -restored suffix (for example, ‘movie-restored.mkv’)."
                : "Click Restore to put the file back at " + originalPath + ". Anything already at that path is kept — the restored copy lands beside it with a -restored suffix (for example, ‘movie-restored.mkv’).",
            RecycleProvenance.Manifest => string.IsNullOrEmpty(originalPath)
                ? "Click Restore to put the file back. The bin remembered the original path from a sidecar manifest, so restore lands the file back where it came from."
                : "Click Restore to put the file back at " + originalPath + ". The original path comes from the bin’s own manifest (this file has no matching history row).",
            _ => "This file was recycled by a MediaDash build that didn’t record where it came from. Open the Files tab, navigate into the Recycle bin shortcut, and move it back manually to the folder you want.",
        };
    }
}
