using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
/// Detects files whose location doesn't match their kind: a movie sitting under the TV target
/// folder, or a TV episode sitting under the Movies target folder. Uses Jellyfin's own metadata
/// by default; falls back to a filename-heuristic when the user opts in (for libraries where
/// Jellyfin's identifier is incomplete).
/// </summary>
public sealed partial class MediaSorterScanner : IScanner
{
    private readonly LibraryGuard _guard;
    private readonly ILogger<MediaSorterScanner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaSorterScanner"/> class.
    /// </summary>
    /// <param name="guard">The library path guard, used to short-circuit the scan when the user's target folders sit outside every Jellyfin library.</param>
    /// <param name="logger">The logger.</param>
    public MediaSorterScanner(LibraryGuard guard, ILogger<MediaSorterScanner> logger)
    {
        _guard = guard;
        _logger = logger;
    }

    /// <summary>Kind of media the sorter recognizes.</summary>
    internal enum MediaKind
    {
        /// <summary>A movie.</summary>
        Movie,

        /// <summary>A TV episode.</summary>
        Tv
    }

    /// <inheritdoc />
    public IssueType Type => IssueType.Misplaced;

    private static Configuration.PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <inheritdoc />
    public Task<IReadOnlyList<Issue>> ScanAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var moviesTarget = NormalizeDir(Config.MoviesTargetPath);
        var tvTarget = NormalizeDir(Config.TvTargetPath);

        // Nothing to sort when the user hasn't told us where the two piles live.
        if (moviesTarget is null || tvTarget is null)
        {
            progress.Report(100);
            return Task.FromResult<IReadOnlyList<Issue>>([]);
        }

        // Fail fast on bad config: if a target is misconfigured we would otherwise detect hundreds of
        // "misplaced" issues whose fix can never succeed. Surface the exact problem to the Errors tab.
        if (ValidateTarget("Movies", Config.MoviesTargetPath, moviesTarget) is string moviesErr)
        {
            Api.Diagnostics.Record("MediaSorter.BadTarget", moviesErr);
            progress.Report(100);
            return Task.FromResult<IReadOnlyList<Issue>>([]);
        }

        if (ValidateTarget("TV", Config.TvTargetPath, tvTarget) is string tvErr)
        {
            Api.Diagnostics.Record("MediaSorter.BadTarget", tvErr);
            progress.Report(100);
            return Task.FromResult<IReadOnlyList<Issue>>([]);
        }

        var issues = new List<Issue>();
        var processed = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in MediaFileHelper.GetFilePaths(item))
            {
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                var kind = ClassifyItem(item, path, Config.MediaSortSource);
                if (kind is null)
                {
                    // Unidentified: skip (surfaced as-is; user should fix metadata in Jellyfin).
                    continue;
                }

                var expectedRoot = kind == MediaKind.Movie ? moviesTarget : tvTarget;
                var wrongRoot = kind == MediaKind.Movie ? tvTarget : moviesTarget;
                var fullPath = Path.GetFullPath(path);
                if (!Fixers.LibraryGuard.IsUnder(fullPath, wrongRoot))
                {
                    // Not misplaced (may or may not be under the correct root — the sorter only
                    // fixes files that are visibly in the wrong pile).
                    continue;
                }

                var targetPath = Path.Combine(expectedRoot, Path.GetFileName(path)!);
                var savings = 0L;
                try
                {
                    savings = File.Exists(path) ? new FileInfo(path).Length : 0;
                }
                catch (IOException)
                {
                    // Broken file — Playability scanner's problem, not ours.
                }

                issues.Add(new Issue
                {
                    Type = IssueType.Misplaced,
                    ItemId = item.Id,
                    Path = path,
                    Status = IssueStatus.Detected,
                    DetectedAtUtc = DateTime.UtcNow,
                    SizeSavings = savings,
                    SuggestedFix = string.Format(
                        CultureInfo.InvariantCulture,
                        "Move to {0}",
                        expectedRoot),
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        kind = kind.ToString(),
                        targetPath,
                        expectedRoot,
                        source = Config.MediaSortSource.ToString()
                    })
                });
            }

            processed++;
            if (items.Count > 0)
            {
                progress.Report(processed * 100.0 / items.Count);
            }
        }

        progress.Report(100);
        return Task.FromResult<IReadOnlyList<Issue>>(issues);
    }

    /// <summary>
    /// Returns the classification of an item under the configured source. Public/internal for tests.
    /// </summary>
    /// <param name="item">The library item.</param>
    /// <param name="path">The file path being considered.</param>
    /// <param name="source">Which source to use.</param>
    /// <returns>The kind, or null if unidentifiable.</returns>
    internal static MediaKind? ClassifyItem(BaseItem item, string path, MediaSortSource source)
    {
        if (source == MediaSortSource.JellyfinMetadata)
        {
            return item switch
            {
                Movie => MediaKind.Movie,
                Episode => MediaKind.Tv,
                _ => null
            };
        }

        return ClassifyFilename(Path.GetFileName(path) ?? string.Empty);
    }

    /// <summary>
    /// Filename-only classifier: SxxExx or NxN pattern → TV; otherwise Movie.
    /// Deliberately generous on the TV side (false negatives — a TV file called "Show.mkv" reads as Movie —
    /// are safer than false positives). Public/internal for tests.
    /// </summary>
    /// <param name="filename">The filename (no directory).</param>
    /// <returns>The kind, or null when the filename is empty.</returns>
    internal static MediaKind? ClassifyFilename(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return null;
        }

        return SxxExxRegex().IsMatch(filename) || SeasonEpisodeRegex().IsMatch(filename)
            ? MediaKind.Tv
            : MediaKind.Movie;
    }

    /// <summary>
    /// Returns null when the target is usable, or a plain-language error message otherwise.
    /// The <paramref name="raw"/> value is what the user typed (used in messages); <paramref name="normalized"/> is the resolved full path.
    /// </summary>
    private string? ValidateTarget(string label, string raw, string normalized)
    {
        if (!Directory.Exists(normalized))
        {
            return label + " target folder does not exist: '" + raw + "'. Set it to a folder inside a Jellyfin library.";
        }

        if (!_guard.IsInsideLibrary(normalized))
        {
            return label + " target folder '" + normalized + "' is not inside any Jellyfin library. MediaDash will not move files outside your libraries — set it under one of the folders in Dashboard → Libraries.";
        }

        // Real permission check: try to create-and-delete a probe file. Property flags on Windows lie about
        // write permission (ReadOnly attribute vs. ACLs) and Linux modes vary; only an actual write is honest.
        try
        {
            var probe = Path.Combine(normalized, ".mediadash-write-probe-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (UnauthorizedAccessException)
        {
            return "Jellyfin cannot write to the " + label + " target folder '" + normalized + "'. Grant the user Jellyfin runs as (typically 'jellyfin' on Linux) read+write permission on that folder.";
        }
        catch (IOException ex)
        {
            return label + " target folder '" + normalized + "' is not writable: " + ex.Message;
        }

        return null;
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
    private static partial Regex SeasonEpisodeRegex();
}
