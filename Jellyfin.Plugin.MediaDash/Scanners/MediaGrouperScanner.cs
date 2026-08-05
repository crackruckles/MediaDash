using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
    private readonly LibraryGuard _guard;
    private readonly ILogger<MediaGrouperScanner> _logger;

    /// <summary>Initializes a new instance of the <see cref="MediaGrouperScanner"/> class.</summary>
    /// <param name="guard">The library path guard.</param>
    /// <param name="logger">The logger.</param>
    public MediaGrouperScanner(LibraryGuard guard, ILogger<MediaGrouperScanner> logger)
    {
        _guard = guard;
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
    {
        var moviesRoot = NormalizeDir(Config.MoviesTargetPath);
        var tvRoot = NormalizeDir(Config.TvTargetPath);

        if (moviesRoot is null && tvRoot is null)
        {
            progress.Report(100);
            return Task.FromResult<IReadOnlyList<Issue>>([]);
        }

        var issues = new List<Issue>();
        var seenSources = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var movieCandidates = new List<MovieCandidate>();

        var total = items.Count;
        var processed = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (item is Episode episode && tvRoot is not null)
                {
                    var tvIssue = BuildTvIssue(episode, tvRoot);
                    if (tvIssue is not null && seenSources.Add(tvIssue.Path))
                    {
                        issues.Add(tvIssue);
                    }
                }
                else if (item is Movie movie && moviesRoot is not null)
                {
                    var candidate = BuildMovieCandidate(movie, moviesRoot);
                    if (candidate is not null)
                    {
                        movieCandidates.Add(candidate.Value);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MediaGrouper failed on {Path}; skipping", item.Path ?? string.Empty);
                Api.Diagnostics.Record(
                    "MediaGrouper.Classify",
                    "The media grouper failed while classifying '" + (item.Path ?? "?") + "': " + ex.Message + ". The item was skipped; the rest of the scan continued.");
            }

            processed++;
            if (total > 0)
            {
                progress.Report(processed * 90.0 / total);
            }
        }

        if (moviesRoot is not null)
        {
            EmitMovieGroupIssues(movieCandidates, moviesRoot, issues, seenSources);
        }

        progress.Report(100);
        return Task.FromResult<IReadOnlyList<Issue>>(issues);
    }

    private static void EmitMovieGroupIssues(List<MovieCandidate> candidates, string moviesRoot, List<Issue> issues, HashSet<string> seenSources)
    {
        var byStem = candidates.GroupBy(c => c.StrippedStem, StringComparer.OrdinalIgnoreCase);
        foreach (var group in byStem)
        {
            var members = group.ToArray();
            var isFranchise = members.Length >= 2;
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

        var seriesRaw = !string.IsNullOrWhiteSpace(episode.SeriesName)
            ? episode.SeriesName
            : ExtractShowNameFromFilename(Path.GetFileNameWithoutExtension(path)!);

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

        var solo = RenameTemplate.Scrub(canonical);
        var stem = RenameTemplate.Scrub(StripFranchiseSuffix(canonical));
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
            StrippedStem = stem.ToLowerInvariant()
        };
    }

    /// <summary>
    /// Strips trailing sequel markers ("(2004)", " 2", " III", "Part 2", ": Subtitle") so franchise
    /// siblings collapse to the same stem. Public for tests.
    /// </summary>
    /// <param name="name">The identified movie name.</param>
    /// <returns>The stem, or the trimmed input when nothing was stripped.</returns>
    public static string StripFranchiseSuffix(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var s = name.Trim();
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

        return s.Length == 0 ? name.Trim() : s;
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
    }
}
