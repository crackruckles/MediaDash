using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Builds the canonical filename for a re-encoded file. Movies:
/// <c>Name (Year) - {height}p.{ext}</c>. TV: <c>SeriesName - S{ss:00}E{ee:00} - {height}p.{ext}</c>.
/// When configured, appends an <c>[tmdbid-N]</c> (movies) or <c>[tvdbid-N]</c> (episodes) tag
/// before the extension so Sonarr / Radarr-style filename metadata survives the re-encode.
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
    /// <param name="sourcePath">Path of the pre-encode file. When <paramref name="preserveExternalId"/> is true, an existing <c>[tmdbid-N]</c> / <c>[tvdbid-N]</c> in this basename is preserved verbatim (falls back to <see cref="BaseItem.ProviderIds"/> when the source has no tag).</param>
    /// <param name="preserveExternalId">When true, appends <c>[tmdbid-N]</c> (movies) or <c>[tvdbid-N]</c> (episodes) before the extension.</param>
    /// <returns>The canonical filename, or null.</returns>
    public static string? Build(BaseItem item, int height, string extension, string? sourcePath = null, bool preserveExternalId = false)
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
            var tag = preserveExternalId ? BuildIdTag("tmdbid", sourcePath, movie, MetadataProvider.Tmdb) : string.Empty;
            return $"{name} ({year.ToString(CultureInfo.InvariantCulture)}) - {res}{tag}.{ext}";
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
            var seriesItem = episode.Series ?? (BaseItem?)episode;
            var tag = preserveExternalId ? BuildIdTag("tvdbid", sourcePath, seriesItem, MetadataProvider.Tvdb) : string.Empty;
            return $"{name} - S{season.Value.ToString("00", CultureInfo.InvariantCulture)}E{number.Value.ToString("00", CultureInfo.InvariantCulture)} - {res}{tag}.{ext}";
        }

        return null;
    }

    /// <summary>
    /// Extracts a <c>[tag-N]</c> from the source basename first (whatever the user's tooling
    /// already wrote), falling back to Jellyfin's <see cref="BaseItem.ProviderIds"/>. Returns
    /// either " [tag-N]" ready to concatenate before the extension, or empty when no ID exists.
    /// Exposed internal for direct testing.
    /// </summary>
    /// <param name="tagName">The tag prefix (<c>tmdbid</c> or <c>tvdbid</c>).</param>
    /// <param name="sourcePath">Path of the pre-encode file; parsed for an existing tag.</param>
    /// <param name="item">The library item whose ProviderIds are the fallback source.</param>
    /// <param name="provider">The Jellyfin provider whose ID we're preserving.</param>
    /// <returns>Formatted tag (leading space + bracket) or empty string.</returns>
    internal static string BuildIdTag(string tagName, string? sourcePath, BaseItem? item, MetadataProvider provider)
    {
        // Source-of-truth 1: the source basename. Preserves whatever the user's tooling wrote,
        // even if Jellyfin's metadata is out of date or missing.
        if (!string.IsNullOrEmpty(sourcePath))
        {
            var basename = Path.GetFileNameWithoutExtension(sourcePath);
            if (!string.IsNullOrEmpty(basename))
            {
                var m = IdTagRegex().Match(basename);
                while (m.Success)
                {
                    if (string.Equals(m.Groups[1].Value, tagName, StringComparison.OrdinalIgnoreCase))
                    {
                        return " [" + tagName + "-" + m.Groups[2].Value + "]";
                    }

                    m = m.NextMatch();
                }
            }
        }

        // Source-of-truth 2: Jellyfin ProviderIds. Only when the source filename didn't have it.
        if (item is not null && item.ProviderIds.TryGetValue(provider.ToString(), out var pid) && !string.IsNullOrWhiteSpace(pid))
        {
            return " [" + tagName + "-" + pid + "]";
        }

        return string.Empty;
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

    // Matches Sonarr / Radarr default tag shape: [tmdbid-12345], [tvdbid-67890], [imdbid-tt1234567].
    // Group 1 = tag name, Group 2 = ID payload (digits or alphanumerics, no brackets).
    [GeneratedRegex(@"\[([a-z]+id)-([^\]]+)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdTagRegex();
}
