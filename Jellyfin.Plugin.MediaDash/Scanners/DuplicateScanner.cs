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
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Groups movies and episodes that are the same title and flags the lower-quality copies for removal.
/// Grouping uses provider IDs (TMDb/IMDb/TVDb) when available, falling back to normalized name and year.
/// </summary>
public sealed partial class DuplicateScanner : IScanner
{
    private static readonly string[] MovieProviders = ["Tmdb", "Imdb", "Tvdb"];

    private readonly FfprobeService _ffprobe;
    private readonly ILogger<DuplicateScanner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DuplicateScanner"/> class.
    /// </summary>
    /// <param name="ffprobe">The probe service, used to rank copies by quality.</param>
    /// <param name="logger">The logger.</param>
    public DuplicateScanner(FfprobeService ffprobe, ILogger<DuplicateScanner> logger)
    {
        _ffprobe = ffprobe;
        _logger = logger;
    }

    /// <inheritdoc />
    public IssueType Type => IssueType.Duplicate;

    private static Configuration.PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <inheritdoc />
    public async Task<IReadOnlyList<Issue>> ScanAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var groups = new Dictionary<string, List<(BaseItem Item, string Path)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var key = GetGroupKey(item);
            if (key is null)
            {
                continue;
            }

            foreach (var path in MediaFileHelper.GetFilePaths(item))
            {
                if (!groups.TryGetValue(key, out var list))
                {
                    list = [];
                    groups[key] = list;
                }

                list.Add((item, path));
            }
        }

        var duplicateGroups = groups.Where(g => g.Value.Select(v => v.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1).ToList();

        // Skip groups where any copy is younger than DuplicateMinAgeDays — Jellyfin's metadata scrape hasn't
        // stabilised yet, so a fresh import can transiently look like a duplicate of itself before provider IDs
        // land. Both copies must be past the cutoff for the pair to be worth flagging.
        if (Config.DuplicateMinAgeDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-Config.DuplicateMinAgeDays);
            duplicateGroups = duplicateGroups
                .Where(g => g.Value.All(v => v.Item.DateCreated <= cutoff))
                .ToList();
        }

        var issues = new List<Issue>();
        var processed = 0;

        foreach (var group in duplicateGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var editionGroup in SplitByEdition(group.Value))
            {
                if (editionGroup.Count < 2)
                {
                    continue;
                }

                issues.AddRange(await RankGroupAsync(group.Key, editionGroup, cancellationToken).ConfigureAwait(false));
            }

            processed++;
            progress.Report(processed * 100.0 / duplicateGroups.Count);
        }

        progress.Report(100);
        return issues;
    }

    internal static string? GetGroupKey(BaseItem item)
    {
        if (item is Episode episode)
        {
            if (episode.SeriesId.Equals(default) || episode.ParentIndexNumber is null || episode.IndexNumber is null)
            {
                return null;
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"episode:{episode.SeriesId:N}:s{episode.ParentIndexNumber}:e{episode.IndexNumber}");
        }

        if (item is Movie movie)
        {
            foreach (var provider in MovieProviders)
            {
                if (movie.ProviderIds.TryGetValue(provider, out var id) && !string.IsNullOrEmpty(id))
                {
                    return $"movie:{provider}:{id}".ToLowerInvariant();
                }
            }

            // Name-only matching needs a year AND the filename: Jellyfin often derives Movie.Name from
            // the containing folder for unidentified content (e.g. a "Season Pack" folder where every
            // .mp4 gets the same Name + year). Grouping on Name+Year alone would then flag every
            // distinct video in that folder as a duplicate of every other. Adding the normalized
            // filename makes false positives impossible for the fallback path — real duplicates come in
            // via the provider-ID branch above, which stays untouched.
            var name = NormalizeName(movie.Name);
            if (name.Length == 0 || movie.ProductionYear is null)
            {
                return null;
            }

            var fileNorm = NormalizeName(Path.GetFileName(movie.Path));
            return string.Create(CultureInfo.InvariantCulture, $"movie:name:{name}:{movie.ProductionYear}:{fileNorm}");
        }

        var kind = item.GetBaseItemKind();

        if (kind == BaseItemKind.Book)
        {
            // ponytail: kind gates entry; cast still needed for property access. If v12 moved the type, return null cleanly.
            if (item is not MediaBrowser.Controller.Entities.Book book)
            {
                return null;
            }

            if (book.ProviderIds.TryGetValue("Isbn", out var isbn) && !string.IsNullOrEmpty(isbn))
            {
                return $"book:isbn:{isbn}".ToLowerInvariant();
            }

            var titleNorm = NormalizeName(book.Name);
            if (titleNorm.Length == 0)
            {
                return null;
            }

            // Filename must also match, same guard as the Movie fallback: two files both titled "Dune"
            // (a novel and a short-story collection, say) should not be flagged as duplicates on title alone.
            var bookFileNorm = NormalizeName(Path.GetFileName(book.Path));
            return $"book:name:{titleNorm}:{bookFileNorm}";
        }

        if (kind == BaseItemKind.Audio || kind == BaseItemKind.AudioBook)
        {
            // ponytail: kind gates entry; cast still needed for property access. If v12 moved the type, return null cleanly.
            if (item is not Audio audio)
            {
                return null;
            }

            if (audio.ProviderIds.TryGetValue("MusicBrainzTrack", out var mbid) && !string.IsNullOrEmpty(mbid))
            {
                return $"audio:musicbrainztrack:{mbid}".ToLowerInvariant();
            }

            var artistNorm = NormalizeName(audio.Artists is { Count: > 0 } ? audio.Artists[0] : null);
            var albumNorm = NormalizeName(audio.Album);
            var titleNorm = NormalizeName(audio.Name);
            if (titleNorm.Length == 0)
            {
                return null;
            }

            // Require a known runtime for the fallback path. Without a MusicBrainz id, "artist+album+title"
            // alone is too loose — two tracks with the same generic title on different releases can collide.
            // Runtime is the strongest cross-file signal that survives re-encoding.
            if (audio.RunTimeTicks is not long ticks || ticks <= 0)
            {
                return null;
            }

            var seconds = (int)TimeSpan.FromTicks(ticks).TotalSeconds;
            var audioFileNorm = NormalizeName(Path.GetFileName(audio.Path));
            return string.Create(
                CultureInfo.InvariantCulture,
                $"audio:name:{artistNorm}:{albumNorm}:{titleNorm}:{seconds}:{audioFileNorm}");
        }

        return null;
    }

    private static string NormalizeName(string? name)
    {
        return name is null ? string.Empty : NonAlphanumericRegex().Replace(name.ToLowerInvariant(), string.Empty);
    }

    private static IEnumerable<List<(BaseItem Item, string Path)>> SplitByEdition(List<(BaseItem Item, string Path)> group)
    {
        var distinct = group.DistinctBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
        if (Config.TreatEditionsAsDuplicates)
        {
            yield return distinct;
            yield break;
        }

        foreach (var editionGroup in distinct.GroupBy(e => GetEdition(e.Path), StringComparer.OrdinalIgnoreCase))
        {
            yield return editionGroup.ToList();
        }
    }

    internal static string GetEdition(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var match = EditionRegex().Match(stem);
        if (match.Success)
        {
            return match.Groups[1].Value.Trim().ToLowerInvariant();
        }

        // Users often keep alt encodings/versions under trailing tags like "-1080p", "-4k",
        // "-finalcut", "- Directors Cut" instead of Jellyfin's {edition-X} convention. Treat a
        // known quality- or edition-keyword suffix as an edition marker so TreatEditionsAsDuplicates
        // =false splits them apart. Whitelist keeps the risk of splitting genuine duplicates low.
        var trailing = TrailingEditionRegex().Match(stem);
        return trailing.Success ? trailing.Groups[1].Value.Trim().ToLowerInvariant() : string.Empty;
    }

    private async Task<List<Issue>> RankGroupAsync(string groupKey, List<(BaseItem Item, string Path)> group, CancellationToken cancellationToken)
    {
        var candidates = new List<Candidate>();
        foreach (var (item, path) in group)
        {
            if (item.GetBaseItemKind() == BaseItemKind.Book)
            {
                var fileInfo = new FileInfo(path);
                if (!fileInfo.Exists)
                {
                    continue;
                }

                candidates.Add(new Candidate
                {
                    Item = item,
                    Path = path,
                    Size = fileInfo.Length,
                    Pixels = 0,
                    Codec = string.Empty,
                    Bitrate = 0,
                    Resolution = "book"
                });
                continue;
            }

            var fileInfo2 = new FileInfo(path);
            if (!fileInfo2.Exists)
            {
                continue;
            }

            var probe = await _ffprobe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
            var itemKind = item.GetBaseItemKind();
            var isAudioItem = itemKind == BaseItemKind.Audio || itemKind == BaseItemKind.AudioBook;
            var stream = isAudioItem
                ? probe?.Streams?.FirstOrDefault(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase))
                : probe?.Streams?.FirstOrDefault(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase));
            if (stream is null)
            {
                // Unreadable copies are the playability scanner's business; don't rank them here.
                continue;
            }

            candidates.Add(new Candidate
            {
                Item = item,
                Path = path,
                Size = fileInfo2.Length,
                Pixels = isAudioItem ? 0 : (long)(stream.Width ?? 0) * (stream.Height ?? 0),
                Codec = stream.CodecName ?? string.Empty,
                Bitrate = long.TryParse(stream.BitRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : 0,
                Resolution = isAudioItem
                    ? (stream.CodecName ?? "audio")
                    : $"{stream.Width}x{stream.Height}"
            });
        }

        if (candidates.Count < 2)
        {
            return [];
        }

        var ranked = Rank(candidates, Config.KeeperPolicyOrder, Config.CodecPreferenceOrder);
        var keeper = ranked[0];
        var issues = new List<Issue>();
        foreach (var loser in ranked.Skip(1))
        {
            issues.Add(new Issue
            {
                Type = IssueType.Duplicate,
                ItemId = loser.Item!.Id,
                Path = loser.Path,
                Status = IssueStatus.Detected,
                DetectedAtUtc = DateTime.UtcNow,
                SizeSavings = loser.Size,
                SuggestedFix = string.Format(
                    CultureInfo.InvariantCulture,
                    "Safe to delete — a better copy exists ({0}, {1}).",
                    keeper.Resolution,
                    keeper.Codec.ToUpperInvariant()),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    groupKey,
                    keeperPath = keeper.Path,
                    keeper = new { keeper.Resolution, keeper.Codec, keeper.Size, keeper.Bitrate },
                    thisCopy = new { loser.Resolution, loser.Codec, loser.Size, loser.Bitrate }
                })
            });
        }

        return issues;
    }

    internal static List<Candidate> Rank(List<Candidate> candidates, string[] keeperPolicyOrder, string[] codecOrder)
    {
        IOrderedEnumerable<Candidate>? ordered = null;
        foreach (var criterion in keeperPolicyOrder)
        {
            Func<Candidate, long> selector = criterion.ToUpperInvariant() switch
            {
                "RESOLUTION" => c => -c.Pixels,
                "CODEC" => c => CodecRank(c.Codec, codecOrder),
                "BITRATE" => c => -c.Bitrate,
                // Smaller file wins the final tiebreak: same quality, less space.
                "SIZE" => c => c.Size,
                _ => c => 0
            };
            ordered = ordered is null ? candidates.OrderBy(selector) : ordered.ThenBy(selector);
        }

        return (ordered ?? candidates.OrderBy(c => -c.Pixels)).ToList();
    }

    private static long CodecRank(string codec, string[] order)
    {
        var index = Array.FindIndex(order, o => string.Equals(o, codec, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? order.Length : index;
    }

    [GeneratedRegex(@"[^a-z0-9]")]
    private static partial Regex NonAlphanumericRegex();

    [GeneratedRegex(@"\{edition-([^}]+)\}", RegexOptions.IgnoreCase)]
    private static partial Regex EditionRegex();

    // Separator requires a dash somewhere (or a dot/underscore) — a plain space isn't enough, otherwise
    // titles like "The Final" would false-positive on "final". Edition keywords accept an optional
    // second word ("Cut", "Edition", "Version") separated by any of `\s._-`, so "-Directors Cut",
    // "-directors cut", "-directors_cut", "-directorscut", "-Final Cut", "-Special Edition" all match.
    [GeneratedRegex(@"(?:\s-|[-_.])[\s._-]*((?:director'?s?|final|collector'?s?|extended|theatrical|ultimate|special|super)(?:[\s._-]?(?:cut|edition|version))?|\d{3,4}p|\d+k|uhd|remux|bluray|blu-?ray|bdrip|web-?dl|web-?rip|hdrip|dvdrip|hdr(?:10\+?)?|dv|proper|repack|uncut|imax|unrated)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingEditionRegex();

    internal sealed class Candidate
    {
        public BaseItem? Item { get; init; }

        public required string Path { get; init; }

        public long Size { get; init; }

        public long Pixels { get; init; }

        public string Codec { get; init; } = string.Empty;

        public long Bitrate { get; init; }

        public string Resolution { get; init; } = string.Empty;
    }
}
