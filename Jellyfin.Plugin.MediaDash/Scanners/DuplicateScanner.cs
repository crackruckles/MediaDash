using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using MediaBrowser.Controller.Entities;
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

    private static string? GetGroupKey(BaseItem item)
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

            var name = NormalizeName(movie.Name);
            return name.Length == 0
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"movie:name:{name}:{movie.ProductionYear ?? 0}");
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

    private static string GetEdition(string path)
    {
        var match = EditionRegex().Match(Path.GetFileNameWithoutExtension(path));
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private async Task<List<Issue>> RankGroupAsync(string groupKey, List<(BaseItem Item, string Path)> group, CancellationToken cancellationToken)
    {
        var candidates = new List<Candidate>();
        foreach (var (item, path) in group)
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                continue;
            }

            var probe = await _ffprobe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
            var video = probe?.Streams?.FirstOrDefault(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase));
            if (video is null)
            {
                // Unreadable copies are the playability scanner's business; don't rank them here.
                continue;
            }

            candidates.Add(new Candidate
            {
                Item = item,
                Path = path,
                Size = fileInfo.Length,
                Pixels = (long)(video.Width ?? 0) * (video.Height ?? 0),
                Codec = video.CodecName ?? string.Empty,
                Bitrate = long.TryParse(video.BitRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : 0,
                Resolution = $"{video.Width}x{video.Height}"
            });
        }

        if (candidates.Count < 2)
        {
            return [];
        }

        var ranked = Rank(candidates);
        var keeper = ranked[0];
        var issues = new List<Issue>();
        foreach (var loser in ranked.Skip(1))
        {
            issues.Add(new Issue
            {
                Type = IssueType.Duplicate,
                ItemId = loser.Item.Id,
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

    private static List<Candidate> Rank(List<Candidate> candidates)
    {
        var codecOrder = Config.CodecPreferenceOrder;
        IOrderedEnumerable<Candidate>? ordered = null;
        foreach (var criterion in Config.KeeperPolicyOrder)
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

    private sealed class Candidate
    {
        public required BaseItem Item { get; init; }

        public required string Path { get; init; }

        public long Size { get; init; }

        public long Pixels { get; init; }

        public string Codec { get; init; } = string.Empty;

        public long Bitrate { get; init; }

        public string Resolution { get; init; } = string.Empty;
    }
}
