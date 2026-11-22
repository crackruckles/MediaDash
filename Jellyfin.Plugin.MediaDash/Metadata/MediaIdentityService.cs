using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Metadata;

/// <summary>
/// Gives every series, episode and movie a TVDb identity row and picks one master folder per TVDb
/// series, so grouping can merge every folder of the same show however its folders are named.
/// TVDb ids come from Jellyfin first; the TVDb API (when a key is configured) is only asked about
/// titles Jellyfin could not identify, and every answer is cached.
/// </summary>
public sealed partial class MediaIdentityService
{
    internal const string SeriesKind = "series";
    internal const string EpisodeKind = "episode";
    internal const string MovieKind = "movie";
    private static readonly TimeSpan NegativeCacheAge = TimeSpan.FromDays(7);

    private readonly MediaIdentityStore _store;
    private readonly TvdbClient? _tvdb;
    private readonly ILogger _logger;
    private readonly Func<IReadOnlyList<Series>> _allSeries;

    /// <summary>Initializes a new instance of the <see cref="MediaIdentityService"/> class.</summary>
    /// <param name="db">MediaDash database.</param>
    /// <param name="tvdb">TVDb client.</param>
    /// <param name="library">Jellyfin library, used to read every series folder.</param>
    /// <param name="logger">Logger.</param>
    public MediaIdentityService(MediaDashDb db, TvdbClient tvdb, ILibraryManager library, ILogger<MediaIdentityService> logger)
        : this(db.Identities, tvdb, logger, () => library.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Series]
        }).OfType<Series>().ToList())
    {
    }

    internal MediaIdentityService(MediaIdentityStore store, TvdbClient? tvdb, ILogger logger, Func<IReadOnlyList<Series>>? allSeries = null)
    {
        _store = store;
        _tvdb = tvdb;
        _logger = logger;
        _allSeries = allSeries ?? (() => []);
    }

    /// <summary>
    /// Records identities for <paramref name="items"/> and returns, for each series that should be
    /// merged, the master folder its episodes belong in (keyed by Jellyfin series id).
    /// </summary>
    /// <param name="items">Library items from the current scan.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Series id to master folder.</returns>
    public async Task<IReadOnlyDictionary<Guid, string>> ResolveSeriesMastersAsync(IReadOnlyList<BaseItem> items, PluginConfiguration config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(config);
        var apiKey = _tvdb is null ? null : string.IsNullOrWhiteSpace(config.TvdbApiKey) ? TvdbClient.ProjectApiKey : config.TvdbApiKey.Trim();
        // Scan item lists carry episodes but not their Series containers, and a folder left behind by
        // an earlier move has no episodes at all; read every series from Jellyfin so each folder gets
        // an identity row and can be matched to its master.
        var series = items.OfType<Series>()
            .Concat(_allSeries())
            .Where(s => !string.IsNullOrWhiteSpace(s.Path))
            .DistinctBy(s => s.Id)
            .ToArray();
        var movies = items.OfType<Movie>().Where(m => !string.IsNullOrWhiteSpace(m.Path)).ToArray();
        var seriesIds = new Dictionary<Guid, (int? Id, string Source)>();
        var rows = new List<MediaIdentity>();
        var fromApi = 0;
        var processed = 0;
        // Titles Jellyfin already identified. An unidentified release folder whose cleaned title
        // matches exactly one of them takes that id before TVDb is asked: TVDb can return a
        // different same-named show than the one Jellyfin matched for the folder's siblings.
        var library = series.Where(show => ProviderInt(show, "Tvdb") is not null)
            .GroupBy(show => Normalize(CleanTitle(show.Name ?? string.Empty).Title))
            .ToDictionary(group => group.Key, group => group.Select(show => (Id: ProviderInt(show, "Tvdb")!.Value, Year: YearOf(show))).Distinct().ToList(), StringComparer.Ordinal);
        foreach (var show in series)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Plugin.CurrentActivityLabel = "Matching TVDb identities (" + (++processed) + "/" + (series.Length + movies.Length) + ")";
            var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(show.Path));
            if (ProviderInt(show, "Tvdb") is null && ReadSeriesNfoTvdbId(show.Path) is null
                && MatchLibrary(library, show.Name, folderName, show.ProductionYear) is int known)
            {
                seriesIds[show.Id] = (known, "library-match");
                rows.Add(Row(show, SeriesKind, null, known, "library-match", null));
                continue;
            }

            var (id, source, key) = await ResolveAsync(show, SeriesKind, folderName, apiKey, cancellationToken).ConfigureAwait(false);
            apiKey = key;
            fromApi += source.StartsWith("tvdb", StringComparison.Ordinal) ? 1 : 0;
            seriesIds[show.Id] = (id, source);
            rows.Add(Row(show, SeriesKind, null, id, source, null));
        }

        foreach (var movie in movies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Plugin.CurrentActivityLabel = "Matching TVDb identities (" + (++processed) + "/" + (series.Length + movies.Length) + ")";
            var (id, source, key) = await ResolveAsync(movie, MovieKind, null, apiKey, cancellationToken).ConfigureAwait(false);
            apiKey = key;
            fromApi += source.StartsWith("tvdb", StringComparison.Ordinal) ? 1 : 0;
            rows.Add(Row(movie, MovieKind, null, id, source, null));
        }

        foreach (var episode in items.OfType<Episode>().Where(e => !string.IsNullOrWhiteSpace(e.Path)))
        {
            var owner = seriesIds.TryGetValue(episode.SeriesId, out var value) ? value : (null, string.Empty);
            rows.Add(Row(episode, EpisodeKind, episode.SeriesId == Guid.Empty ? null : episode.SeriesId, owner.Item1, owner.Item2, ProviderInt(episode, "Tvdb")));
        }

        _store.Upsert(rows);
        _store.PruneMissing(path => File.Exists(path) || Directory.Exists(path));
        var masters = PickMasters(series, items, seriesIds, config);
        _logger.LogInformation(
            "TVDb identities: {Series} series ({SeriesWithId} with a TVDb id), {Movies} movies, {Api} resolved through the TVDb API; {Merged} series folders grouped into {Masters} master folders.",
            series.Length,
            seriesIds.Count(pair => pair.Value.Id is not null),
            movies.Length,
            fromApi,
            masters.Count,
            masters.Values.Distinct(StringComparer.Ordinal).Count());
        return masters;
    }

    internal static (string Title, int? Year) CleanTitle(string raw)
    {
        var text = LeadingTags().Replace(raw ?? string.Empty, string.Empty);
        text = LeadingSite().Replace(text, string.Empty).Replace('.', ' ').Replace('_', ' ');
        var cut = ReleaseMarker().Match(text);
        if (cut.Success)
        {
            text = text[..cut.Index];
        }

        int? year = null;
        var tail = TrailingYear().Match(text);
        if (tail.Success)
        {
            year = int.Parse(tail.Groups[1].Value, CultureInfo.InvariantCulture);
            text = text[..tail.Index];
        }

        text = Spaces().Replace(text, " ").Trim(' ', '-', '\u2013', '(', '[');
        return (text, year);
    }

    // Year a folder name claims (before any SxxEyy / release tags), used so a folder like
    // "Doctor Who (1963)" is never merged into a different-year master even if Jellyfin gave both
    // the same TVDb id.
    internal static int? FolderYear(string folderName) => CleanTitle(folderName).Year;

    internal static string StripSite(string name) => LeadingSite().Replace(name ?? string.Empty, string.Empty).Trim();

    internal static bool SameReleaseName(string left, string right)
        => Normalize(LeadingSite().Replace(left, string.Empty)) == Normalize(LeadingSite().Replace(right, string.Empty));

    internal static int? ReadSeriesNfoTvdbId(string folder)
    {
        try
        {
            var path = Path.Combine(folder, "tvshow.nfo");
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            MediaMoveSnapshot.ValidateAncestors(folder);
            using var stream = Scanners.MediaFileHelper.OpenSharedRead(path);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 4_000_000, ConformanceLevel = ConformanceLevel.Fragment
            });
            if (reader.MoveToContent() != XmlNodeType.Element)
            {
                return null;
            }

            var root = XElement.Load(reader.ReadSubtree());
            if (root.Name.LocalName != "tvshow")
            {
                return null;
            }

            var ids = root.Elements().Where(element => element.Name.LocalName is "tvdbid" or "id"
                || (element.Name.LocalName == "uniqueid" && string.Equals((string?)element.Attribute("type"), "tvdb", StringComparison.OrdinalIgnoreCase)))
                .Select(element => int.TryParse(element.Value, out var id) && id > 0 ? (int?)id : null)
                .Where(id => id.HasValue).Distinct().ToArray();
            return ids.Length == 1 ? ids[0] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }
    }

    internal static string Normalize(string title)
    {
        var builder = new StringBuilder(title.Length);
        foreach (var c in title.Replace("&", " and ", StringComparison.Ordinal).Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    internal static int? MatchLibrary(IReadOnlyDictionary<string, List<(int Id, int? Year)>> library, string? name, string? folderName, int? year)
    {
        foreach (var candidate in new[] { name, folderName }.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var (title, parsedYear) = CleanTitle(candidate!);
            var wanted = parsedYear ?? year;
            if (!library.TryGetValue(Normalize(title), out var entries))
            {
                continue;
            }

            var ids = entries.Where(entry => wanted is null || entry.Year is null || Math.Abs(entry.Year.Value - wanted.Value) <= 1)
                .Select(entry => entry.Id).Distinct().ToList();
            if (ids.Count == 1)
            {
                return ids[0];
            }
        }

        return null;
    }

    // A folder fit to be every copy's master: a plain title, optionally with a year or region tag,
    // never a release name ("Show S02E04 1080p WEB-…") or a site-prefixed download folder.
    internal static bool IsCleanFolderName(string folderName)
        => !ReleaseMarker().IsMatch(folderName) && !LeadingSite().IsMatch(folderName) && !LeadingTags().IsMatch(folderName);

    internal static int? PickMatch(IReadOnlyList<TvdbSearchHit> hits, string title, int? year)
    {
        var wanted = Normalize(title);
        if (wanted.Length == 0)
        {
            return null;
        }

        var matches = hits.Where(hit => Normalize(CleanTitle(hit.Name).Title) == wanted || Normalize(hit.Name) == wanted
            || hit.Aliases.Any(alias => Normalize(alias) == wanted)).ToList();
        if (year is int y)
        {
            matches = matches.Where(hit => hit.Year is null || Math.Abs(hit.Year.Value - y) <= 1).ToList();
            var exact = matches.Where(hit => hit.Year == y).Select(hit => hit.Id).Distinct().ToList();
            if (exact.Count == 1)
            {
                return exact[0];
            }
        }

        var ids = matches.Select(hit => hit.Id).Distinct().ToList();
        return ids.Count == 1 ? ids[0] : null;
    }

    private static int? ProviderInt(BaseItem item, string provider)
        => item.ProviderIds.TryGetValue(provider, out var raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

    private static string? Provider(BaseItem item, string provider)
        => item.ProviderIds.TryGetValue(provider, out var raw) && !string.IsNullOrWhiteSpace(raw) ? raw.Trim() : null;

    private static int? YearOf(BaseItem item) => item.ProductionYear ?? item.PremiereDate?.Year;

    private static MediaIdentity Row(BaseItem item, string kind, Guid? seriesId, int? tvdbId, string source, int? episodeTvdbId)
        => new(item.Id, kind, item.Path, item.Name ?? string.Empty, YearOf(item), seriesId, tvdbId, source, episodeTvdbId, Provider(item, "Imdb"), Provider(item, "Tmdb"));

    private static string? RootOf(string folder, IReadOnlyList<string> roots)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder));
        return parent is null ? null : roots.FirstOrDefault(root => PathEquals(root, parent));
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]\s*)+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingTags();

    [GeneratedRegex(@"^\s*www[\s.]+[\w-]+[\s.]+(?:org|com|net|to|me|cc|tv|io|se|info|ws)\b[\s.\-\u2013]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingSite();

    [GeneratedRegex(@"\b(?:S\d{1,2}(?:E\d{1,3})?|Season\s*\d+|\d{1,2}x\d{2}|(?:480|576|720|1080|2160)[pi]|WEB(?:-?DL|-?Rip)?|HDTV|BluRay|BDRip|BDRemux|DVDRip|x26[45]|HEVC|COMPLETE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseMarker();

    [GeneratedRegex(@"[\s(\[]((?:19|20)\d{2})[)\]]?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingYear();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();
}
