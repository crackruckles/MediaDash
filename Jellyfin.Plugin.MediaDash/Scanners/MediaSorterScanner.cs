using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
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
    private readonly MediaMovePlanner _planner;
    private readonly ILogger<MediaSorterScanner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaSorterScanner"/> class.
    /// </summary>
    /// <param name="guard">The library path guard, used to short-circuit the scan when the user's target folders sit outside every Jellyfin library.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="planner">Complete move planning.</param>
    public MediaSorterScanner(LibraryGuard guard, ILogger<MediaSorterScanner> logger, MediaMovePlanner planner)
    {
        _guard = guard;
        _planner = planner;
        _logger = logger;
    }

    /// <summary>Kind of media the sorter recognizes.</summary>
    internal enum MediaKind
    {
        /// <summary>A movie.</summary>
        Movie,

        /// <summary>A TV episode.</summary>
        Tv,

        /// <summary>Anime (movie-length OVA or episodic series). Detected via Jellyfin's "Anime" genre tag.</summary>
        Anime,

        /// <summary>An audio track (music).</summary>
        Music,

        /// <summary>An audiobook.</summary>
        AudioBook,

        /// <summary>An e-book or digital book.</summary>
        Book,

        /// <summary>A comic or manga.</summary>
        Comic,

        /// <summary>A picture / photo.</summary>
        Picture
    }

    /// <inheritdoc />
    public IssueType Type => IssueType.Misplaced;

    private static Configuration.PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <inheritdoc />
    public Task<IReadOnlyList<Issue>> ScanAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var moviesTarget = NormalizeDir(Config.MoviesTargetPath);
        var tvTarget = NormalizeDir(Config.TvTargetPath);
        var animeTarget = NormalizeDir(Config.AnimeTargetPath);
        var musicTarget = NormalizeDir(Config.MusicTargetPath);
        var booksTarget = NormalizeDir(Config.BooksTargetPath);
        var comicsTarget = NormalizeDir(Config.ComicsTargetPath);
        var picturesTarget = NormalizeDir(Config.PicturesTargetPath);

        // Nothing to sort when the user hasn't told us where the two core piles live. Music / books
        // / comics / pictures are opt-in per-kind — configured missing → scanner just doesn't flag
        // that kind. The Movies + TV pair is still required because those are the two libraries
        // Jellyfin creates on first-run by default, so their absence signals "sorter not set up yet".
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

        // The four opt-in kinds fail soft: bad config surfaces on Errors tab and disables just that
        // kind for this run, rather than stopping the sorter for Movies + TV too.
        if (animeTarget is not null && ValidateTarget("Anime", Config.AnimeTargetPath, animeTarget) is string animeErr)
        {
            Api.Diagnostics.Record("MediaSorter.BadTarget", animeErr);
            animeTarget = null;
        }

        if (musicTarget is not null && ValidateTarget("Music", Config.MusicTargetPath, musicTarget) is string musicErr)
        {
            Api.Diagnostics.Record("MediaSorter.BadTarget", musicErr);
            musicTarget = null;
        }

        if (booksTarget is not null && ValidateTarget("Books", Config.BooksTargetPath, booksTarget) is string booksErr)
        {
            Api.Diagnostics.Record("MediaSorter.BadTarget", booksErr);
            booksTarget = null;
        }

        if (comicsTarget is not null && ValidateTarget("Comics", Config.ComicsTargetPath, comicsTarget) is string comicsErr)
        {
            Api.Diagnostics.Record("MediaSorter.BadTarget", comicsErr);
            comicsTarget = null;
        }

        if (picturesTarget is not null && ValidateTarget("Pictures", Config.PicturesTargetPath, picturesTarget) is string picsErr)
        {
            Api.Diagnostics.Record("MediaSorter.BadTarget", picsErr);
            picturesTarget = null;
        }

        return _planner.ScanAsync(Type, items, Config, progress, cancellationToken);
    }

    /// <summary>
    /// Maps a Jellyfin BaseItemKind to a MediaSorter MediaKind. Returns null for kinds
    /// the sorter does not handle (photos, TV series/season shells, etc.).
    /// </summary>
    /// <param name="kind">The Jellyfin base item kind.</param>
    /// <returns>The MediaKind, or null when unclassifiable.</returns>
    internal static MediaKind? ClassifyByBaseItemKind(BaseItemKind kind)
    {
        return kind switch
        {
            BaseItemKind.Movie => MediaKind.Movie,
            BaseItemKind.Episode => MediaKind.Tv,
            BaseItemKind.Audio => MediaKind.Music,
            BaseItemKind.AudioBook => MediaKind.AudioBook,
            BaseItemKind.Book => MediaKind.Book,
            BaseItemKind.Photo => MediaKind.Picture,
            _ => null
        };
    }

    /// <summary>
    /// A film Jellyfin filed as an episode of a same-named show. Production 2026-11-22: "Sinners 2025
    /// 1080p WEBRip…" sat inside "Sinners (2016)" as an episode of unknown season, and once a season was
    /// entered it was renamed "Saints &amp; Sinners S01E2025" (the film's year read as an episode number).
    /// </summary>
    /// <param name="episode">The episode item.</param>
    /// <param name="path">Its file.</param>
    /// <param name="formerPaths">Paths earlier moves recorded for this file, newest first.</param>
    /// <returns>True when it should be treated as a movie.</returns>
    internal static bool LooksLikeMovieFiledAsEpisode(Episode episode, string path, IReadOnlyList<string>? formerPaths = null)
        => FilmIdentity(episode, path, formerPaths) is not null;

    /// <summary>
    /// The "Title (Year)" of a film filed as an episode, or null when the item reads as a real episode.
    /// It has no usable episode number (none, or one that is really a year), no SxxEyy in any name it
    /// has carried, a feature-length runtime when known, and a "Title Year" name whose year is not the
    /// show's.
    /// </summary>
    /// <param name="episode">The episode item.</param>
    /// <param name="path">Its file.</param>
    /// <param name="formerPaths">Paths earlier moves recorded for this file, newest first.</param>
    /// <returns>The canonical film name, or null.</returns>
    internal static string? FilmIdentity(Episode episode, string path, IReadOnlyList<string>? formerPaths = null)
    {
        if (string.IsNullOrWhiteSpace(path)
            || (episode.IndexNumber is int number && number is < 1900 or > 2100)
            || (episode.IndexNumber is null && episode.ParentIndexNumber is not null)
            || (episode.RunTimeTicks is long ticks && ticks > 0 && TimeSpan.FromTicks(ticks) < TimeSpan.FromMinutes(60)))
        {
            return null;
        }

        var names = new List<string>();
        foreach (var candidate in (formerPaths ?? []).Prepend(path))
        {
            names.Add(Path.GetFileNameWithoutExtension(candidate));
            names.Add(Path.GetFileName(Path.GetDirectoryName(candidate) ?? string.Empty));
        }

        names = names.Where(name => !string.IsNullOrWhiteSpace(name) && !SeasonLikeFolder().IsMatch(name) && !YearAsEpisode().IsMatch(name)).ToList();
        if (names.Any(Fixers.MoveProvenance.HasEpisodeMarker))
        {
            return null;
        }

        int? seriesYear = null;
        try
        {
            var series = episode.Series;
            seriesYear = series?.ProductionYear ?? series?.PremiereDate?.Year;
        }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException)
        {
        }

        for (var folder = Path.GetDirectoryName(path); seriesYear is null && !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
        {
            var match = CanonicalFolder().Match(Path.GetFileName(folder));
            if (match.Success)
            {
                seriesYear = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }

        if (seriesYear is not int showYear)
        {
            return null;
        }

        foreach (var name in names)
        {
            var (title, year) = Metadata.MediaIdentityService.CleanTitle(Metadata.MediaIdentityService.StripSite(name));
            if (year is int filmYear && title.Length > 0 && filmYear <= DateTime.UtcNow.Year + 1)
            {
                return Math.Abs(showYear - filmYear) > 1
                    ? Fixers.RenameTemplate.Scrub(title + " (" + filmYear.ToString(CultureInfo.InvariantCulture) + ")")
                    : null;
            }
        }

        return null;
    }

    [GeneratedRegex(@"^(?:season|series|specials)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonLikeFolder();

    [GeneratedRegex(@"[Ss]\d{1,2}[Ee](?:19|20)\d{2}(?!\d)")]
    private static partial Regex YearAsEpisode();

    [GeneratedRegex(@"^.+ \((\d{4})\)$")]
    private static partial Regex CanonicalFolder();

    /// <summary>
    /// Returns the classification of an item under the configured source. Public/internal for tests.
    /// </summary>
    /// <param name="item">The library item.</param>
    /// <param name="path">The file path being considered.</param>
    /// <param name="source">Which source to use.</param>
    /// <param name="animeEnabled">True when an Anime target path is configured; controls whether the anime kind is returned. When false, anime collapses back to Movie/Tv so existing behaviour is preserved.</param>
    /// <param name="comicsEnabled">True when a separate comic target is configured; comic Book items use that enabled destination.</param>
    /// <param name="formerPaths">Paths earlier moves recorded for this file.</param>
    /// <returns>The kind, or null if unidentifiable.</returns>
    internal static MediaKind? ClassifyItem(BaseItem item, string path, MediaSortSource source, bool animeEnabled = false, bool comicsEnabled = false, IReadOnlyList<string>? formerPaths = null)
    {
        if (source == MediaSortSource.JellyfinMetadata)
        {
            var baseKind = item switch
            {
                Movie => (MediaKind?)MediaKind.Movie,
                Episode episode when LooksLikeMovieFiledAsEpisode(episode, path, formerPaths) => MediaKind.Movie,
                Episode => (MediaKind?)MediaKind.Tv,
                _ => ClassifyByBaseItemKind(item.GetBaseItemKind())
            };

            if (baseKind is null)
            {
                return null;
            }

            if (comicsEnabled && baseKind == MediaKind.Book && MediaFormats.Comics.Contains(Path.GetExtension(path)))
            {
                return MediaKind.Comic;
            }

            // Anime override only applies to video content (Movie/Tv). Books/Music are never "anime".
            if (animeEnabled && HasAnimeGenre(item) && (baseKind == MediaKind.Movie || baseKind == MediaKind.Tv))
            {
                return MediaKind.Anime;
            }

            return baseKind;
        }

        return ClassifyFilename(Path.GetFileName(path) ?? string.Empty);
    }

    /// <summary>Checks whether the item's genres include "Anime" (case-insensitive). Public/internal for tests.</summary>
    /// <param name="item">The library item.</param>
    /// <returns>True when the anime tag is present.</returns>
    internal static bool HasAnimeGenre(BaseItem item)
    {
        var genres = item.Genres;
        if (genres is null || genres.Length == 0)
        {
            return false;
        }

        foreach (var g in genres)
        {
            if (string.Equals(g, "Anime", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

        // MD-009: scans must not mutate library contents. Removed the write-probe (CreateFile +
        // Delete) that used to run on every scan of every configured target — it violated the
        // global dry-run/no-local-change guarantee and could leave a `.mediadash-write-probe-*`
        // orphan behind if the process crashed between the two calls. The real write happens in
        // MediaSorterFixer during a fix run, which reports permission failures with a specific
        // per-file error. Users with a broken target now see the file-move failure at fix time
        // instead of a scan-time probe leak.
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
