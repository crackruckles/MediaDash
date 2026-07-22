using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Builds the canonical filename for a re-encoded file. Movies:
/// <c>Name (Year) - {height}p.{ext}</c>. TV: <c>SeriesName - S{ss:00}E{ee:00} - {height}p.{ext}</c>.
/// </summary>
public static partial class RenameTemplate
{
    /// <summary>
    /// Returns the canonical filename (no directory) for the item, or null when the item lacks
    /// the metadata needed to build a safe name (missing year on a movie, missing season/episode on TV).
    /// </summary>
    /// <param name="item">The library item (Movie or Episode).</param>
    /// <param name="height">The output video height in pixels.</param>
    /// <param name="extension">The target extension (with or without the leading dot).</param>
    /// <returns>The canonical filename, or null.</returns>
    public static string? Build(BaseItem item, int height, string extension)
    {
        var ext = string.IsNullOrEmpty(extension)
            ? "mkv"
            : extension.TrimStart('.').ToLowerInvariant();
        var res = height > 0 ? $"{height.ToString(CultureInfo.InvariantCulture)}p" : "video";

        if (item is Movie movie)
        {
            if (string.IsNullOrWhiteSpace(movie.Name) || movie.ProductionYear is not int year)
            {
                return null;
            }

            var name = Scrub(movie.Name);
            return $"{name} ({year.ToString(CultureInfo.InvariantCulture)}) - {res}.{ext}";
        }

        if (item is Episode episode)
        {
            var series = episode.SeriesName;
            var season = episode.ParentIndexNumber;
            var number = episode.IndexNumber;
            if (string.IsNullOrWhiteSpace(series) || season is null || number is null)
            {
                return null;
            }

            var name = Scrub(series);
            return $"{name} - S{season.Value.ToString("00", CultureInfo.InvariantCulture)}E{number.Value.ToString("00", CultureInfo.InvariantCulture)} - {res}.{ext}";
        }

        return null;
    }

    /// <summary>
    /// Strips characters that Windows/Linux/macOS don't allow (or that break shell quoting)
    /// and collapses whitespace. Exposed for tests.
    /// </summary>
    /// <param name="raw">The raw title.</param>
    /// <returns>The scrubbed title.</returns>
    public static string Scrub(string raw)
    {
        // <>:"/\|?* are forbidden on Windows; the others (control chars, leading/trailing dot/space) break tools.
        var cleaned = InvalidCharsRegex().Replace(raw, string.Empty);
        cleaned = WhitespaceRegex().Replace(cleaned, " ").Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "Untitled" : cleaned;
    }

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1F]", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidCharsRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
