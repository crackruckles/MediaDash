using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Fixers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Metadata;

/// <content>TVDb resolution and master-folder selection.</content>
public sealed partial class MediaIdentityService
{
    // Returns the id, where it came from, and the API key to keep using (null once TVDb has
    // failed this scan, so one outage doesn't cost a timeout per title).
    private async Task<(int? Id, string Source, string? ApiKey)> ResolveAsync(BaseItem item, string kind, string? folderName, string? apiKey, CancellationToken cancellationToken)
    {
        if (ProviderInt(item, "Tvdb") is int known)
        {
            return (known, "jellyfin", apiKey);
        }

        if (kind == SeriesKind && ReadSeriesNfoTvdbId(item.Path) is int nfoId)
        {
            return (nfoId, "nfo", apiKey);
        }

        if (apiKey is null)
        {
            return (null, string.Empty, null);
        }

        try
        {
            if (Provider(item, "Imdb") is { } imdb)
            {
                var key = "remote:" + kind + ":" + imdb.ToLowerInvariant();
                if (!_store.TryGetLookup(key, NegativeCacheAge, out var cached))
                {
                    cached = await _tvdb!.RemoteIdAsync(apiKey, imdb, kind, cancellationToken).ConfigureAwait(false);
                    _store.SaveLookup(key, cached);
                }

                if (cached is int byImdb)
                {
                    return (byImdb, "tvdb-remoteid", apiKey);
                }
            }

            // Name search only for series: unidentified movies are left to Jellyfin.
            if (kind != SeriesKind)
            {
                return (null, string.Empty, apiKey);
            }

            foreach (var candidate in new[] { item.Name, folderName }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
            {
                var (title, year) = CleanTitle(candidate!);
                year ??= item.ProductionYear;
                if (Normalize(title).Length < 2)
                {
                    continue;
                }

                var key = "search:" + kind + ":" + Normalize(title) + "|" + year;
                if (!_store.TryGetLookup(key, NegativeCacheAge, out var cached))
                {
                    var hits = await _tvdb!.SearchAsync(apiKey, title, kind, cancellationToken).ConfigureAwait(false);
                    cached = PickMatch(hits, title, year);
                    _store.SaveLookup(key, cached);
                }

                if (cached is int byName)
                {
                    return (byName, "tvdb-search", apiKey);
                }
            }

            return (null, string.Empty, apiKey);
        }
        catch (TvdbAuthenticationException ex)
        {
            _logger.LogWarning("TVDb lookups skipped for this scan: {Message}", ex.Message);
            return (null, string.Empty, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning("TVDb lookups skipped for this scan: TVDb is unavailable ({Message}).", ex.Message);
            return (null, string.Empty, null);
        }
    }

    private Dictionary<Guid, string> PickMasters(IReadOnlyList<Series> series, IReadOnlyList<BaseItem> items, Dictionary<Guid, (int? Id, string Source)> seriesIds, PluginConfiguration config)
    {
        var roots = new[] { config.TvTargetPath, config.AnimeTargetPath }
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
            .ToArray();
        var episodeCounts = items.OfType<Episode>().GroupBy(episode => episode.SeriesId).ToDictionary(group => group.Key, group => group.Count());
        var result = new Dictionary<Guid, string>();
        var groups = series
            .Where(show => seriesIds.TryGetValue(show.Id, out var value) && value.Id is not null && Directory.Exists(show.Path))
            .Select(show => (Show: show, Folder: Path.TrimEndingDirectorySeparator(Path.GetFullPath(show.Path)), Root: RootOf(show.Path, roots)))
            .Where(entry => entry.Root is not null)
            .GroupBy(entry => (entry.Root!, seriesIds[entry.Show.Id].Id!.Value));
        foreach (var group in groups)
        {
            var (root, tvdbId) = group.Key;
            var folders = group.Select(entry => entry.Folder).Distinct(StringComparer.Ordinal).ToList();
            var stored = _store.GetMaster(SeriesKind, tvdbId);
            string? master = stored is not null && Directory.Exists(stored) && RootOf(stored, roots) is { } storedRoot && PathEquals(storedRoot, root)
                && IsCleanFolderName(Path.GetFileName(stored)) ? stored : null;
            if (master is null && stored is not null)
            {
                _store.RemoveMaster(SeriesKind, tvdbId);
            }

            var eligible = group.Where(entry => IsCleanFolderName(Path.GetFileName(entry.Folder))).ToList();
            if (master is null && (folders.Count < 2 || eligible.Count == 0))
            {
                // No plain-titled folder to merge into: leave the group to name-based grouping.
                continue;
            }

            // Prefer the identified series' canonical "Name (Year)" folder, then any year-suffixed
            // folder, then the folder holding the most episodes.
            var identified = group.FirstOrDefault(entry => seriesIds[entry.Show.Id].Source == "jellyfin").Show ?? group.First().Show;
            var canonical = RenameTemplate.Scrub(CleanTitle(identified.Name ?? string.Empty).Title + (YearOf(identified) is int year ? " (" + year + ")" : string.Empty));
            master ??= folders.FirstOrDefault(folder => string.Equals(Path.GetFileName(folder), canonical, StringComparison.OrdinalIgnoreCase))
                ?? eligible.OrderByDescending(entry => FolderYear(Path.GetFileName(entry.Folder)) is not null && LooksCanonical(Path.GetFileName(entry.Folder)))
                    .ThenByDescending(entry => episodeCounts.GetValueOrDefault(entry.Show.Id))
                    .ThenBy(entry => entry.Folder.Length)
                    .ThenBy(entry => entry.Folder, StringComparer.Ordinal)
                    .First().Folder;
            var masterYear = FolderYear(Path.GetFileName(master)) ?? YearOf(identified);
            _store.SetMaster(SeriesKind, tvdbId, master, identified.Name ?? Path.GetFileName(master), masterYear);
            foreach (var entry in group)
            {
                var folderYear = FolderYear(Path.GetFileName(entry.Folder));
                if (folderYear is int claimed && masterYear is int expected && Math.Abs(claimed - expected) > 1)
                {
                    _logger.LogInformation("TVDb grouping: '{Folder}' names year {Claimed} but its master '{Master}' is {Expected}; left for review.", entry.Folder, claimed, master, expected);
                    continue;
                }

                result[entry.Show.Id] = master;
            }
        }

        return result;
    }

    // "Name (2008)" with nothing after the year: a folder someone named on purpose.
    private static bool LooksCanonical(string folderName)
        => folderName.EndsWith(')') && !ReleaseMarker().IsMatch(folderName);
}
