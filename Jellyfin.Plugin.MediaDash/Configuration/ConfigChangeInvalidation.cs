using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.MediaDash.Data;

namespace Jellyfin.Plugin.MediaDash.Configuration;

/// <summary>
/// Diffs two <see cref="PluginConfiguration"/> snapshots and maps changed fields to the
/// <see cref="IssueType"/>s whose Detected rows are now stale — the reader of the field.
/// Pure static; no DI, no side effects.
/// </summary>
/// <remarks>
/// Purpose is issue #64: user adds a language to <c>AllowedAudioLanguages</c>, existing rows
/// (with pre-change <c>SuggestedFix</c> and <c>DetailsJson</c>) still say the language will be
/// removed. On config save we invalidate the affected types so the next scan repopulates them.
/// </remarks>
public static class ConfigChangeInvalidation
{
    // Wildcard sentinel: a field in this set forces a full rescan (all scanners) rather than
    // a per-type subset. EnabledLibraries changes the item set every scanner sees.
    private static readonly HashSet<string> FullRescanFields = new(StringComparer.Ordinal)
    {
        nameof(PluginConfiguration.EnabledLibraries)
    };

    // Field → IssueType(s) invalidated when the field changes. Grown incrementally — a field
    // not listed here is treated as UI-only (no rescan). Wrong to include: fields that affect
    // FIX behaviour only (disposal, retention, dry-run) or pure presentation.
    private static readonly Dictionary<string, IssueType[]> FieldToTypes = new(StringComparer.Ordinal)
    {
        // Language pickers (#64).
        [nameof(PluginConfiguration.AllowedAudioLanguages)] = [IssueType.AudioLanguage],
        [nameof(PluginConfiguration.AllowedSubtitleLanguages)] = [IssueType.SubtitleLanguage, IssueType.MissingSubtitles],
        [nameof(PluginConfiguration.SubtitleHearingImpairedMode)] = [IssueType.SubtitleLanguage, IssueType.MissingSubtitles],

        // Quality thresholds.
        [nameof(PluginConfiguration.MaxResolutionHeight)] = [IssueType.Quality],
        [nameof(PluginConfiguration.MaxBitrateMbpsAt1080p)] = [IssueType.Quality],
        [nameof(PluginConfiguration.PreferredCodec)] = [IssueType.Quality],
        [nameof(PluginConfiguration.QualityTolerancePercent)] = [IssueType.Quality],
        [nameof(PluginConfiguration.SkipHdrContent)] = [IssueType.Quality],
        [nameof(PluginConfiguration.QualityScanAudiobooks)] = [IssueType.Quality],
        [nameof(PluginConfiguration.MinScanFileSizeMb)] = [IssueType.Quality],
        [nameof(PluginConfiguration.ReencodeFileTypes)] = [IssueType.Quality],

        // Playability.
        [nameof(PluginConfiguration.ThoroughPlayabilityCheck)] = [IssueType.Playability],

        // Duplicate confidence / keeper policy.
        [nameof(PluginConfiguration.DuplicateMinAgeDays)] = [IssueType.Duplicate],
        [nameof(PluginConfiguration.TreatEditionsAsDuplicates)] = [IssueType.Duplicate],
        [nameof(PluginConfiguration.KeeperPolicyOrder)] = [IssueType.Duplicate],
        [nameof(PluginConfiguration.CodecPreferenceOrder)] = [IssueType.Duplicate],
        [nameof(PluginConfiguration.DuplicateExactHashEnabled)] = [IssueType.Duplicate],
        [nameof(PluginConfiguration.DuplicateAutoFixConfidence)] = [IssueType.Duplicate],
        [nameof(PluginConfiguration.DuplicateTitleJaccardVeto)] = [IssueType.Duplicate],
        [nameof(PluginConfiguration.DuplicateRuntimeVetoPct)] = [IssueType.Duplicate],

        // Stale.
        [nameof(PluginConfiguration.StaleThresholdDays)] = [IssueType.Stale],
        [nameof(PluginConfiguration.StaleExcludedLibraryIds)] = [IssueType.Stale],
        [nameof(PluginConfiguration.StaleExcludedGenres)] = [IssueType.Stale],

        // Media sorter / grouper share the target-path family.
        [nameof(PluginConfiguration.MoviesTargetPath)] = [IssueType.Misplaced, IssueType.Ungrouped],
        [nameof(PluginConfiguration.TvTargetPath)] = [IssueType.Misplaced, IssueType.Ungrouped],
        [nameof(PluginConfiguration.TvdbApiKey)] = [IssueType.Ungrouped],
        [nameof(PluginConfiguration.AnimeTargetPath)] = [IssueType.Misplaced, IssueType.Ungrouped],
        [nameof(PluginConfiguration.MusicTargetPath)] = [IssueType.Misplaced],
        [nameof(PluginConfiguration.BooksTargetPath)] = [IssueType.Misplaced],
        [nameof(PluginConfiguration.ComicsTargetPath)] = [IssueType.Misplaced],
        [nameof(PluginConfiguration.PicturesTargetPath)] = [IssueType.Misplaced],
        [nameof(PluginConfiguration.MediaSortSource)] = [IssueType.Misplaced],

        // Embedded cover art.
        [nameof(PluginConfiguration.EmbeddedCoverStripFromAudio)] = [IssueType.EmbeddedCoverArt],
        [nameof(PluginConfiguration.EmbeddedCoverFilename)] = [IssueType.EmbeddedCoverArt],

        // Transcode log lookback.
        [nameof(PluginConfiguration.HeavyTranscodeLookbackDays)] = [IssueType.HeavyTranscode, IssueType.FailedTranscode],

        // Trickplay.
        [nameof(PluginConfiguration.TrickplayMinSizeMb)] = [IssueType.LargeTrickplay],
        [nameof(PluginConfiguration.TrickplayWebPQuality)] = [IssueType.LargeTrickplay],

        // Subtitle-font pass — force-font rewrite ceiling changes what's considered fixable.
        [nameof(PluginConfiguration.SubtitleForceFont)] = [IssueType.SubtitleFonts],

        // Orphan cleanup toggles.
        [nameof(PluginConfiguration.OrphanScanEmptyFolders)] = [IssueType.OrphanedDebris],
        [nameof(PluginConfiguration.OrphanScanSubtitles)] = [IssueType.OrphanedDebris],
        [nameof(PluginConfiguration.OrphanScanTrickplay)] = [IssueType.OrphanedDebris],
        [nameof(PluginConfiguration.OrphanScanMetadata)] = [IssueType.OrphanedDebris],

        // FixMode toggles: Off→On starts a scanner; On→Off leaves stale rows a user can no
        // longer act on. Both directions want a refresh.
        [nameof(PluginConfiguration.DuplicateFixMode)] = [IssueType.Duplicate],
        [nameof(PluginConfiguration.TranscodeFixMode)] = [IssueType.Quality],
        [nameof(PluginConfiguration.SubtitleFixMode)] = [IssueType.SubtitleLanguage],
        [nameof(PluginConfiguration.AudioFixMode)] = [IssueType.AudioLanguage],
        [nameof(PluginConfiguration.PlayabilityFixMode)] = [IssueType.Playability],
        [nameof(PluginConfiguration.MisplacedFixMode)] = [IssueType.Misplaced],
        [nameof(PluginConfiguration.MissingSubtitlesFixMode)] = [IssueType.MissingSubtitles],
        [nameof(PluginConfiguration.StaleFixMode)] = [IssueType.Stale],
        [nameof(PluginConfiguration.SuspiciousFileFixMode)] = [IssueType.MalwareRisk],
        [nameof(PluginConfiguration.TrickplayFixMode)] = [IssueType.LargeTrickplay],
        [nameof(PluginConfiguration.SubtitleFontFixMode)] = [IssueType.SubtitleFonts],
        [nameof(PluginConfiguration.OrphanCleanupFixMode)] = [IssueType.OrphanedDebris],
        [nameof(PluginConfiguration.NfoFixMode)] = [IssueType.CorruptNfo],
        [nameof(PluginConfiguration.HeavyTranscodeFixMode)] = [IssueType.HeavyTranscode],
        [nameof(PluginConfiguration.FailedTranscodeFixMode)] = [IssueType.FailedTranscode],
        [nameof(PluginConfiguration.EmbeddedCoverFixMode)] = [IssueType.EmbeddedCoverArt],
        [nameof(PluginConfiguration.UngroupedFixMode)] = [IssueType.Ungrouped],
        [nameof(PluginConfiguration.CorruptArtworkFixMode)] = [IssueType.CorruptArtwork]
    };

    /// <summary>
    /// Produces a shallow clone of <paramref name="source"/> with arrays deep-copied so later
    /// mutation of <paramref name="source"/> can't leak into a held snapshot. Everything else
    /// (primitives, strings, enums) is a value copy; the config carries no reference types
    /// beyond arrays and strings, so this is enough for the diff use case.
    /// </summary>
    /// <param name="source">The configuration to snapshot.</param>
    /// <returns>A freshly-allocated <see cref="PluginConfiguration"/> holding the same values.</returns>
    public static PluginConfiguration Clone(PluginConfiguration source)
    {
        var target = new PluginConfiguration();
        foreach (var prop in typeof(PluginConfiguration).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var v = prop.GetValue(source);
            if (v is Array arr)
            {
                v = arr.Clone();
            }

            prop.SetValue(target, v);
        }

        return target;
    }

    /// <summary>
    /// Returns the property names whose values differ between the two snapshots. Uses reflection over
    /// public instance properties; string comparison is ordinal; arrays / lists are compared element-wise.
    /// </summary>
    /// <param name="before">The previous snapshot.</param>
    /// <param name="after">The new snapshot.</param>
    /// <returns>The set of changed property names.</returns>
    public static IReadOnlyList<string> ChangedFields(PluginConfiguration before, PluginConfiguration after)
    {
        var changed = new List<string>();
        foreach (var prop in typeof(PluginConfiguration).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var a = prop.GetValue(before);
            var b = prop.GetValue(after);
            if (!ValueEquals(a, b))
            {
                changed.Add(prop.Name);
            }
        }

        return changed;
    }

    /// <summary>
    /// Resolves the set of <see cref="IssueType"/>s whose Detected rows should be wiped and rescanned
    /// given the changed fields. Returns <c>null</c> when a full rescan is warranted (e.g. EnabledLibraries).
    /// Returns an empty set when no relevant field changed.
    /// </summary>
    /// <param name="changedFields">The property names produced by <see cref="ChangedFields"/>.</param>
    /// <returns>The affected issue types, or null to signal "rescan everything".</returns>
    public static IReadOnlySet<IssueType>? AffectedTypes(IEnumerable<string> changedFields)
    {
        var result = new HashSet<IssueType>();
        foreach (var field in changedFields)
        {
            if (FullRescanFields.Contains(field))
            {
                return null;
            }

            if (FieldToTypes.TryGetValue(field, out var types))
            {
                foreach (var t in types)
                {
                    result.Add(t);
                }
            }
        }

        return result;
    }

    private static bool ValueEquals(object? a, object? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        if (a is string sa && b is string sb)
        {
            return string.Equals(sa, sb, StringComparison.Ordinal);
        }

        // Arrays and lists get element-wise comparison so ["eng"] == ["eng"] regardless of instance.
        if (a is System.Collections.IEnumerable ea && b is System.Collections.IEnumerable eb)
        {
            return SequenceEquals(ea, eb);
        }

        return a.Equals(b);
    }

    private static bool SequenceEquals(System.Collections.IEnumerable a, System.Collections.IEnumerable b)
    {
        var ea = a.GetEnumerator();
        var eb = b.GetEnumerator();
        while (true)
        {
            var ma = ea.MoveNext();
            var mb = eb.MoveNext();
            if (ma != mb)
            {
                return false;
            }

            if (!ma)
            {
                return true;
            }

            if (!ValueEquals(ea.Current, eb.Current))
            {
                return false;
            }
        }
    }
}
