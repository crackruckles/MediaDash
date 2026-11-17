using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Detects Movies and Episodes that aren't filed under a per-title (or per-franchise) parent folder
/// inside their library root, and emits move-to-group issues. Uses Jellyfin's identified metadata
/// (<see cref="Episode.SeriesName"/>, <see cref="BaseItem.Name"/>) so a badly-named file/folder still
/// lands under the canonical title; falls back to the filename only when Jellyfin hasn't identified.
/// TV: any episode not already under <c>TvRoot/&lt;SeriesName&gt;/…</c> is queued into it.
/// Movies: loose files always get their own folder; folders whose scrubbed Jellyfin names share a
/// franchise stem (e.g. "Scary Movie", "Scary Movie 2") are queued into a shared folder. Solo movies
/// already inside a folder are left alone.
/// </summary>
public sealed partial class MediaGrouperScanner : IScanner
{
    // Matches "Series Name (YYYY)" folder-suffix convention — the same shape Jellyfin's own
    // documented layout recommends and the shape users create on disk to keep reboots apart.
    // Trailing whitespace tolerated so " Doctor Who (2005) " still matches. Trailing bracketed
    // provider tags also tolerated (R08 audit 2026-09-19): users pin the correct TVDb/TMDb/IMDb
    // ID on reboot folders like "Doctor Who (1963) [tvdbid-76107]" so Jellyfin can't collapse it
    // into the 2005 reboot entity — the on-disk-year rule must still recognise these as
    // year-suffixed folders instead of falling through to Jellyfin's wrong metadata year.
    private static readonly Regex SeriesYearSuffixRegex = new(
        @"^(.+?)\s*\((\d{4})\)(?:\s*(?:\[[^\]]+\]|\{[^}]+\}|\([^\)]+\)))*\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly LibraryGuard _guard;
    private readonly MediaMovePlanner _planner;
    private readonly ILogger<MediaGrouperScanner> _logger;

    /// <summary>Initializes a new instance of the <see cref="MediaGrouperScanner"/> class.</summary>
    /// <param name="guard">The library path guard.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="planner">Complete move planning.</param>
    public MediaGrouperScanner(LibraryGuard guard, ILogger<MediaGrouperScanner> logger, MediaMovePlanner planner)
    {
        _guard = guard;
        _planner = planner;
        _logger = logger;
    }

    private enum MovieContainerKind
    {
        Loose,
        InFolder
    }

    /// <inheritdoc />
    public IssueType Type => IssueType.Ungrouped;

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <inheritdoc />
    public Task<IReadOnlyList<Issue>> ScanAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken cancellationToken)
        => _planner.ScanAsync(Type, items, Config, progress, cancellationToken);

    private static void EmitMovieGroupIssues(List<MovieCandidate> candidates, string moviesRoot, List<Issue> issues, HashSet<string> seenSources)
    {
        var byStem = candidates.GroupBy(c => c.StrippedStem, StringComparer.OrdinalIgnoreCase);
        foreach (var rawGroup in byStem)
        {
            // Bug-pattern audit round 3 (2026-09-19) — remake split. A stripped-stem group where
            // nobody carried a sequel marker AND at least two members have different production
            // years is REMAKES (The Thing 1982 + 2011, Fright Night 1985 + 2011, Suspiria 1977 +
            // 2018), not a franchise. Split into per-year subgroups so each remake gets its own
            // solo folder; members with no ProductionYear metadata bucket together (safer than
            // guessing). Franchise sequels (at least one member with ":" / "Part" / trailing
            // digit / roman numeral) collapse as before — that's what the whole scanner is for.
            IEnumerable<IReadOnlyList<MovieCandidate>> effectiveGroups;
            var hadAnyMarker = rawGroup.Any(m => m.HadSequelMarker);
            if (!hadAnyMarker && rawGroup.Select(m => m.ProductionYear).Distinct().Count() > 1)
            {
                effectiveGroups = rawGroup
                    .GroupBy(m => m.ProductionYear)
                    .Select(g => (IReadOnlyList<MovieCandidate>)g.ToArray());
            }
            else
            {
                effectiveGroups = new[] { (IReadOnlyList<MovieCandidate>)rawGroup.ToArray() };
            }

            foreach (var effective in effectiveGroups)
            {
                EmitOneGroup(effective, moviesRoot, issues, seenSources);
            }
        }
    }

    private static void EmitOneGroup(IReadOnlyList<MovieCandidate> members, string moviesRoot, List<Issue> issues, HashSet<string> seenSources)
    {
        var isFranchise = members.Count >= 2;
        {
            foreach (var member in members)
            {
                // Solo movie already in its own folder — user's rule: leave it.
                if (!isFranchise && member.Kind == MovieContainerKind.InFolder)
                {
                    continue;
                }

                var folderName = isFranchise ? member.FranchiseFolderName : member.SoloFolderName;
                if (string.IsNullOrWhiteSpace(folderName))
                {
                    continue;
                }

                var expectedRoot = Path.Combine(moviesRoot, folderName);
                if (Fixers.LibraryGuard.IsUnder(Path.GetFullPath(member.SourcePath), expectedRoot))
                {
                    continue;
                }

                if (!seenSources.Add(member.SourcePath))
                {
                    continue;
                }

                var isFolderMove = member.Kind == MovieContainerKind.InFolder;
                var sourceLeaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(member.SourcePath))!;
                var targetPath = Path.Combine(expectedRoot, sourceLeaf);

                // Target already occupied — skip. Same reasoning as BuildTvIssue.
                if (isFolderMove ? Directory.Exists(targetPath) : File.Exists(targetPath))
                {
                    continue;
                }

                issues.Add(new Issue
                {
                    Type = IssueType.Ungrouped,
                    ItemId = member.ItemId,
                    Path = member.SourcePath,
                    Status = IssueStatus.Detected,
                    DetectedAtUtc = DateTime.UtcNow,
                    SizeSavings = 0,
                    SuggestedFix = "Group under " + folderName,
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        action = isFolderMove ? "MoveFolder" : "MoveFile",
                        source = member.SourcePath,
                        target = targetPath,
                        title = folderName,
                        franchise = isFranchise
                    })
                });
            }
        }
    }

    private static Issue? BuildTvIssue(Episode episode, string tvRoot)
    {
        var path = episode.Path;
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path);
        if (!Fixers.LibraryGuard.IsUnder(fullPath, tvRoot))
        {
            return null;
        }

        // GitHub #43 (Doctor Who reboots + Silo (2023) year-strip). Jellyfin's TVDb match collapses
        // multiple reboots into one Series entity with a bare SeriesName ("Doctor Who", "Silo"), so
        // grouping on SeriesName alone destroys the year-suffixed folders users manually created on
        // disk. ResolveSafeSeriesName prefers the on-disk year suffix, falls back to PremiereDate,
        // and only lands on plain SeriesName when neither signal is present.
        var seriesRaw = ResolveSafeSeriesName(
            episode.SeriesName,
            episode.Series?.PremiereDate?.Year,
            path,
            tvRoot);

        if (string.IsNullOrWhiteSpace(seriesRaw))
        {
            return null;
        }

        var folderName = RenameTemplate.Scrub(seriesRaw);
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return null;
        }

        var expectedRoot = Path.Combine(tvRoot, folderName);
        if (Fixers.LibraryGuard.IsUnder(fullPath, expectedRoot))
        {
            return null;
        }

        var parentDir = Path.GetDirectoryName(fullPath) ?? tvRoot;
        var parentNormalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentDir));
        var parentIsRoot = string.Equals(
            parentNormalized,
            tvRoot,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        string source;
        string target;
        string action;

        if (parentIsRoot)
        {
            source = fullPath;
            target = Path.Combine(expectedRoot, Path.GetFileName(fullPath)!);
            action = "MoveFile";
        }
        else
        {
            source = parentNormalized;
            target = Path.Combine(expectedRoot, Path.GetFileName(parentNormalized)!);
            action = "MoveFolder";
        }

        // Target already occupied — the fixer would fail with "same name already exists" every fix run
        // (user report: Yellowstone (2018) folder duplicated at both root and inside canonical series folder).
        // Skip emission; the user has a manual conflict we can't safely resolve.
        var targetExists = string.Equals(action, "MoveFolder", StringComparison.Ordinal)
            ? Directory.Exists(target)
            : File.Exists(target);
        if (targetExists)
        {
            return null;
        }

        return new Issue
        {
            Type = IssueType.Ungrouped,
            ItemId = episode.Id,
            Path = source,
            Status = IssueStatus.Detected,
            DetectedAtUtc = DateTime.UtcNow,
            SizeSavings = 0,
            SuggestedFix = "Group under " + folderName,
            DetailsJson = JsonSerializer.Serialize(new
            {
                action,
                source,
                target,
                title = folderName,
                franchise = false
            })
        };
    }

    /// <summary>
    /// Resolves the folder name to group episodes under, honoring on-disk year-suffixed folders
    /// (Doctor Who reboots, Silo (2023)) that Jellyfin's TVDb match collapses into a bare
    /// SeriesName. Rule order per docs/superpowers/specs/2026-09-01-media-organiser-design.md §5.2.1:
    /// (1) on-disk parent-of-parent (Season/) or direct parent (no Season/) year-suffixed folder,
    /// (2) SeriesName + PremiereDate year, (3) plain SeriesName. Kept as a pure function so it's
    /// InlineData-testable without constructing a full Jellyfin Episode entity. Public/internal for
    /// tests; called from BuildTvIssue for the real path.
    /// GitHub #43.
    /// </summary>
    /// <param name="episodeSeriesName">Jellyfin's <c>Episode.SeriesName</c> — may be empty when metadata identification failed.</param>
    /// <param name="seriesPremiereYear">Jellyfin's <c>Series.PremiereDate</c> year — null when metadata is absent or the show has no premiere date.</param>
    /// <param name="episodeFilePath">Full path to the episode file on disk. Ancestors are walked to find a user-created year-suffix folder.</param>
    /// <param name="tvRoot">The library root path. Search never escapes above it.</param>
    /// <returns>The safe series folder name (with year suffix when disambiguation applied), or empty string when neither on-disk nor metadata signals identify the show.</returns>
    internal static string ResolveSafeSeriesName(
        string? episodeSeriesName,
        int? seriesPremiereYear,
        string episodeFilePath,
        string tvRoot)
    {
        // Rule 1: on-disk disambiguation — the year-suffixed folder the user manually made wins.
        // Preserves reboot separation ("Doctor Who (1963)" vs "(2005)" vs "(2024)") and also fixes
        // Vaygrim's case where "Silo (2023)/" was being renamed to bare "Silo/".
        var onDiskFolder = FindYearSuffixedSeriesFolder(episodeFilePath, tvRoot);
        if (onDiskFolder is not null)
        {
            return onDiskFolder;
        }

        var seriesName = !string.IsNullOrWhiteSpace(episodeSeriesName)
            ? episodeSeriesName!
            : ExtractShowNameFromFilename(Path.GetFileNameWithoutExtension(episodeFilePath) ?? string.Empty);
        if (string.IsNullOrWhiteSpace(seriesName))
        {
            return string.Empty;
        }

        // Defense-in-depth sanitizer: when Jellyfin's TVDb/TMDb match fails, some versions fall back
        // to the raw filename as `Episode.SeriesName` (e.g. "spooks S01E06"). Trusting that verbatim
        // grouped the episode under "spooks S01E06/" — one folder per episode instead of one folder
        // per show. Whenever the resolved seriesName still carries an SxxExx / NxN episode marker,
        // strip it via the same extractor the empty-SeriesName branch uses. Runs unconditionally so
        // future SeriesName-shaped-like-a-filename regressions self-heal. No-op for well-formed
        // names — the extractor returns its input unchanged when neither marker matches.
        if (SxxExxRegex().IsMatch(seriesName) || NxNRegex().IsMatch(seriesName))
        {
            var sanitized = ExtractShowNameFromFilename(seriesName);
            if (!string.IsNullOrWhiteSpace(sanitized))
            {
                seriesName = sanitized;
            }
        }

        // Rule 2: Jellyfin-metadata disambiguation. Only synthesize a year suffix when SeriesName
        // isn't already year-tagged — otherwise a "Show (2023)" name would become "Show (2023) (2023)".
        if (seriesPremiereYear is int year && !SeriesYearSuffixRegex.IsMatch(seriesName))
        {
            return seriesName + " (" + year.ToString(CultureInfo.InvariantCulture) + ")";
        }

        // Rule 3: plain SeriesName last resort.
        return seriesName;
    }

    // Looks for a "Series (YYYY)" folder among the episode file's immediate ancestors, bounded
    // by the library root. Preferred order matches the two common layouts:
    //   1. Series/Season 01/E01.mkv  — parent-of-parent is the series folder (Jellyfin canonical)
    //   2. Series/E01.mkv            — direct parent is the series folder (loose, no Season/)
    // Never returns folders equal to or above the library root: a top-level "TV (2024)" library
    // container would otherwise match and corrupt every show under it.
    private static string? FindYearSuffixedSeriesFolder(string episodeFilePath, string tvRoot)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(tvRoot));
        var normalizedFile = Path.GetFullPath(episodeFilePath);
        var directParent = Path.GetDirectoryName(normalizedFile);
        if (string.IsNullOrEmpty(directParent))
        {
            return null;
        }

        var parentOfParent = Path.GetDirectoryName(directParent);
        if (!string.IsNullOrEmpty(parentOfParent) && IsInsideLibrary(parentOfParent, normalizedRoot))
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(parentOfParent));
            if (!string.IsNullOrEmpty(name) && SeriesYearSuffixRegex.IsMatch(name))
            {
                return name;
            }
        }

        if (IsInsideLibrary(directParent, normalizedRoot))
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directParent));
            if (!string.IsNullOrEmpty(name) && SeriesYearSuffixRegex.IsMatch(name))
            {
                return name;
            }
        }

        return null;
    }

    // True when 'path' sits strictly INSIDE 'normalizedRoot' (excludes the root itself + anything above).
    private static bool IsInsideLibrary(string path, string normalizedRoot)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(normalized, normalizedRoot, cmp))
        {
            return false;
        }

        return Fixers.LibraryGuard.IsUnder(normalized, normalizedRoot);
    }

    private static MovieCandidate? BuildMovieCandidate(Movie movie, string moviesRoot)
    {
        var path = movie.Path;
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path);
        if (!Fixers.LibraryGuard.IsUnder(fullPath, moviesRoot))
        {
            return null;
        }

        var canonical = !string.IsNullOrWhiteSpace(movie.Name)
            ? movie.Name
            : Path.GetFileNameWithoutExtension(fullPath) ?? string.Empty;

        // TV episodes misfiled into a Movies library get identified by Jellyfin as Movies (whatever
        // extension the wrong library uses). If the canonical name still carries an SxxExx / NxN
        // marker after Jellyfin's identification, this file is a TV episode in the wrong pile —
        // grouping it under a movie folder ("spooks S01E06/") is nonsense. Refuse the candidate;
        // the Media Sorter scanner (a separate check) flags the misfile so the user can move it
        // to the TV library where the TV grouper handles it properly.
        if (LooksLikeTvEpisodeMisfiled(canonical, Path.GetFileNameWithoutExtension(fullPath) ?? string.Empty))
        {
            return null;
        }

        var solo = RenameTemplate.Scrub(canonical);
        var stem = RenameTemplate.Scrub(StripFranchiseSuffix(canonical, out var hadSequelMarker));
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = solo;
        }

        var parentDir = Path.GetDirectoryName(fullPath) ?? moviesRoot;
        var parentNormalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentDir));
        var parentIsRoot = string.Equals(
            parentNormalized,
            moviesRoot,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        var sourcePath = parentIsRoot ? fullPath : parentNormalized;

        return new MovieCandidate
        {
            ItemId = movie.Id,
            SourcePath = sourcePath,
            Kind = parentIsRoot ? MovieContainerKind.Loose : MovieContainerKind.InFolder,
            SoloFolderName = solo,
            FranchiseFolderName = stem,
            StrippedStem = stem.ToLowerInvariant(),
            // Bug-pattern audit round 3 (2026-09-19): franchise-remake collapse fix. See
            // MovieCandidate.HadSequelMarker doc + EmitMovieGroupIssues subgrouping.
            ProductionYear = movie.ProductionYear,
            HadSequelMarker = hadSequelMarker
        };
    }

    /// <summary>
    /// Strips trailing sequel markers ("(2004)", " 2", " III", "Part 2", ": Subtitle") so franchise
    /// siblings collapse to the same stem. Public for tests.
    /// </summary>
    /// <param name="name">The identified movie name.</param>
    /// <returns>The stem, or the trimmed input when nothing was stripped.</returns>
    public static string StripFranchiseSuffix(string name)
        => StripFranchiseSuffix(name, out _);

    /// <summary>
    /// Strip variant that reports whether any actual sequel marker was removed (colon subtitle,
    /// part, trailing digit, roman numeral). Year strip alone doesn't count — a lone year is what
    /// separates remakes, not what identifies sequels. Bug-pattern audit round 3 (2026-09-19)
    /// fix: `The Thing (1982)` and `The Thing (2011)` both stripped to "the thing" and got
    /// franchise-collapsed into one folder before this signal existed. The caller uses
    /// <paramref name="hadSequelMarker"/> to split groups back into per-year solo entries when
    /// nobody in the group had a marker AND their production years differ.
    /// </summary>
    /// <param name="name">The identified movie name.</param>
    /// <param name="hadSequelMarker">True when the stripping loop removed at least one non-year
    /// sequel token (":", "Part", trailing digit, roman numeral).</param>
    /// <returns>The stem, or the trimmed input when nothing was stripped.</returns>
    public static string StripFranchiseSuffix(string name, out bool hadSequelMarker)
    {
        hadSequelMarker = false;
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var trimmed = name.Trim();

        // Baseline: the stem after year-only stripping. Anything the full strip removes on top
        // of this baseline is a genuine sequel marker.
        var yearOnly = YearParenRegex().Replace(trimmed, string.Empty);
        yearOnly = YearBracketRegex().Replace(yearOnly, string.Empty);
        yearOnly = yearOnly.TrimEnd(' ', '.', '_', '-', ':').Trim();

        var s = trimmed;
        s = ColonSubtitleRegex().Replace(s, string.Empty);
        s = YearParenRegex().Replace(s, string.Empty);
        s = YearBracketRegex().Replace(s, string.Empty);

        // ponytail: iterate until stable so "Movie Part 2 III" collapses in one pass. Bounded by
        // string length so no infinite loop risk.
        string prev;
        do
        {
            prev = s;
            s = PartRegex().Replace(s, string.Empty);
            s = TrailingNumRegex().Replace(s, string.Empty);
            s = TrailingRomanRegex().Replace(s, string.Empty);
            s = s.TrimEnd(' ', '.', '_', '-', ':');
        }
        while (s != prev);

        var final = s.Length == 0 ? name.Trim() : s;
        hadSequelMarker = !string.Equals(yearOnly, final, StringComparison.OrdinalIgnoreCase);
        return final;
    }

    /// <summary>
    /// True when a Jellyfin-classified Movie is actually a TV episode misfiled into the Movies
    /// library (Jellyfin identifies by folder, not by filename shape). Detected by the presence of
    /// an SxxExx / NxN episode marker in either the identified canonical name or the raw filename.
    /// Used by <see cref="BuildMovieCandidate"/> to refuse the group candidate — Media Sorter
    /// handles the misfile separately. Internal for direct unit testing.
    /// </summary>
    /// <param name="canonical">The Jellyfin-identified canonical name (Movie.Name or filename fallback).</param>
    /// <param name="filenameNoExt">The filename without extension.</param>
    /// <returns>True when either candidate string carries an episode marker.</returns>
    internal static bool LooksLikeTvEpisodeMisfiled(string canonical, string filenameNoExt)
    {
        return (!string.IsNullOrEmpty(canonical) && (SxxExxRegex().IsMatch(canonical) || NxNRegex().IsMatch(canonical)))
            || (!string.IsNullOrEmpty(filenameNoExt) && (SxxExxRegex().IsMatch(filenameNoExt) || NxNRegex().IsMatch(filenameNoExt)));
    }

    /// <summary>
    /// Filename-fallback extractor: returns text left of the SxxExx / NxN marker. Used only when
    /// Jellyfin hasn't identified the item's series name. Public for tests.
    /// </summary>
    /// <param name="filenameNoExt">The filename without extension.</param>
    /// <returns>The candidate show name, or the trimmed input when no marker is present.</returns>
    public static string ExtractShowNameFromFilename(string filenameNoExt)
    {
        if (string.IsNullOrWhiteSpace(filenameNoExt))
        {
            return string.Empty;
        }

        var sMatch = SxxExxRegex().Match(filenameNoExt);
        var nMatch = NxNRegex().Match(filenameNoExt);

        int cut;
        if (sMatch.Success && nMatch.Success)
        {
            cut = Math.Min(sMatch.Index, nMatch.Index);
        }
        else if (sMatch.Success)
        {
            cut = sMatch.Index;
        }
        else if (nMatch.Success)
        {
            cut = nMatch.Index;
        }
        else
        {
            return filenameNoExt.Trim();
        }

        if (cut <= 0)
        {
            return filenameNoExt.Trim();
        }

        return filenameNoExt.Substring(0, cut).TrimEnd(' ', '.', '_', '-');
    }

    private static string? NormalizeDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"[sS]\d{1,2}[eE]\d{1,3}", RegexOptions.CultureInvariant)]
    private static partial Regex SxxExxRegex();

    [GeneratedRegex(@"\b\d{1,2}x\d{1,3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex NxNRegex();

    [GeneratedRegex(@":\s*.+$", RegexOptions.CultureInvariant)]
    private static partial Regex ColonSubtitleRegex();

    [GeneratedRegex(@"\s*\((?:19|20)\d{2}\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex YearParenRegex();

    [GeneratedRegex(@"\s*\[(?:19|20)\d{2}\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex YearBracketRegex();

    [GeneratedRegex(@"[\s._-]+(?:part|pt|vol|volume|chapter|ch)\.?\s*\d+\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PartRegex();

    [GeneratedRegex(@"[\s._-]+\d+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingNumRegex();

    [GeneratedRegex(@"[\s._-]+(?:i{1,3}|iv|v|vi{1,3}|ix|x)\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TrailingRomanRegex();

    [SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "Grouping is by StrippedStem via LINQ GroupBy; struct equality isn't used.")]
    private readonly record struct MovieCandidate
    {
        public Guid ItemId { get; init; }

        public string SourcePath { get; init; }

        public MovieContainerKind Kind { get; init; }

        public string SoloFolderName { get; init; }

        public string FranchiseFolderName { get; init; }

        public string StrippedStem { get; init; }

        /// <summary>Gets the Jellyfin-metadata production year (TMDb / TVDb / IMDb-derived). Null
        /// when the item has no year metadata — such members can't drive the remake split so they
        /// fall through as if they were a normal franchise sibling.</summary>
        public int? ProductionYear { get; init; }

        /// <summary>Gets a value indicating whether
        /// <see cref="StripFranchiseSuffix(string, out bool)"/> removed a non-year sequel token
        /// (":" subtitle, "Part", trailing digit, roman numeral). False when only the year was
        /// stripped or nothing changed. When every member of a stripped-stem group has this false
        /// AND their production years differ, the group is re-split per year: distinct-year
        /// same-title films are remakes, not franchise siblings, and must not be collapsed into
        /// one folder.</summary>
        public bool HadSequelMarker { get; init; }
    }
}
