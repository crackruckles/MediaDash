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

    // Jellyfin's per-item sidecar naming convention. Any file whose basename (without extension)
    // matches one of these is a poster / theme song / theme video / logo etc, NOT library content.
    // Kept as a HashSet<> so lookup is O(1) even for the ~15 names.
    private static readonly HashSet<string> SidecarFilenames = new(StringComparer.OrdinalIgnoreCase)
    {
        "theme", "themevideo",
        "poster", "folder", "backdrop", "banner", "logo", "clearart", "clearlogo",
        "disc", "thumb", "landscape", "characterart", "fanart"
    };

    // Folder names Jellyfin scans for extras/theme media. Files inside these subtrees are
    // trailers, deleted scenes, behind-the-scenes clips, theme songs — never real library items.
    private static readonly HashSet<string> SidecarFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "extras", "trailers", "theme-music", "theme music", "themes",
        "behind the scenes", "behindthescenes",
        "deleted scenes", "deletedscenes",
        "interviews", "scenes", "shorts", "featurettes", "others", "sample", "samples"
    };

    private readonly FfprobeService _ffprobe;
    private readonly FileHasher _hasher;
    private readonly ILogger<DuplicateScanner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DuplicateScanner"/> class.
    /// </summary>
    /// <param name="ffprobe">The probe service, used to rank copies by quality.</param>
    /// <param name="hasher">SHA-256 hasher for Tier-0 byte-identical confirmation. Only invoked within a formed group of same-size candidates.</param>
    /// <param name="logger">The logger.</param>
    public DuplicateScanner(FfprobeService ffprobe, FileHasher hasher, ILogger<DuplicateScanner> logger)
    {
        _ffprobe = ffprobe;
        _hasher = hasher;
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
            if (key is null || string.IsNullOrEmpty(item.Path))
            {
                continue;
            }

            // Only the item's primary path. MediaFileHelper.GetFilePaths also yields every
            // LocalAlternateVersions[] path — those are user-declared merged versions (or a
            // scraper auto-merged them), i.e. intentional by definition. Feeding them here made
            // DuplicateScanner flag them against each other and pick a "keeper" for each merged
            // Movie/Episode item, which is exactly the "all episodes of Firefly / Doctor Who
            // Classic / Six Million Dollar Man look like duplicates of one keeper" symptom in the
            // 2026-08-21 field report (A4). True cross-item duplicates (two distinct Jellyfin
            // items whose paths happen to collide, or two items that scraped to the same
            // provider ID) still surface via the group key.
            var path = item.Path;
            if (IsSidecarPath(path))
            {
                continue;
            }

            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }

            list.Add((item, path));
        }

        // Same-item collapse: a group whose entries all share one BaseItem.Id is not a duplication
        // finding — the user's library has one item there. Belt-and-braces alongside the
        // "only primary path" change above.
        var duplicateGroups = groups
            .Where(g => g.Value.Select(v => v.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Where(g => g.Value.Select(v => v.Item.Id).Distinct().Count() > 1)
            .ToList();

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

            var fileNorm = NormalizeName(Path.GetFileName(movie.Path) ?? string.Empty);
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
            var bookFileNorm = NormalizeName(Path.GetFileName(book.Path) ?? string.Empty);
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

    internal static bool IsSidecarPath(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (SidecarFilenames.Contains(stem))
        {
            return true;
        }

        // Jellyfin's per-item extras convention is the compact form ("MovieName-trailer.mp4",
        // "MovieName-behindthescenes.mp4") but users routinely write the spaced form too
        // ("Movie (2009) - Behind the scenes.m2ts", "Show - Deleted Scenes.mkv" — real 2026-08-07
        // bug report). A single regex catches BOTH: any of the extras keywords at end-of-stem,
        // preceded by a separator run that includes at least one dash/dot/underscore. Requiring
        // that separator keeps a title like "Deleted Scenes: The Movie" from tripping (its final
        // word wouldn't be an extras keyword). Multi-song themes ("theme-1.mp3") stay covered
        // by the prefix check below.
        if (SidecarSuffixRegex().IsMatch(stem))
        {
            return true;
        }

        if (stem.StartsWith("theme-", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("themevideo-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Any directory in the path matching an extras folder name → sidecar. Walks up the path
        // string once; cheap. Handles both Windows and POSIX separators.
        var dir = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(dir))
        {
            var name = Path.GetFileName(dir);
            if (SidecarFolders.Contains(name))
            {
                return true;
            }

            var parent = Path.GetDirectoryName(dir);
            if (string.Equals(parent, dir, StringComparison.Ordinal))
            {
                break;
            }

            dir = parent;
        }

        return false;
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

        // Explicit {edition-X} marker (Jellyfin/Plex convention) wins when present.
        var match = EditionRegex().Match(stem);
        if (match.Success)
        {
            return "edition:" + match.Groups[1].Value.Trim().ToLowerInvariant();
        }

        // Fall through: the normalised filename IS the edition key. Any variation between two
        // TMDb-matched files — quality tag, source, group name, cut name, anything — means the
        // user filed them under different names, so treat them as different editions and don't
        // flag as duplicates. Byte-identical copies (Sonarr/Radarr places the same filename
        // in two library folders) still normalise to the same string and still get flagged.
        // Replaces the older regex-whitelist approach, which was fragile — users kept reporting
        // new suffix combinations (2026-08-07 bug report) and the list grew unbounded.
        return NormalizeName(stem);
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
        var tier = DuplicateSignals.TierForKey(groupKey);
        var issues = new List<Issue>();
        foreach (var loser in ranked.Skip(1))
        {
            // Exact byte-match check is opt-in and only worth doing when sizes match — different
            // sizes cannot be byte-identical, and hashing them wastes IO. FileHasher caches by
            // (path, size, mtime) so a re-scan pays no disk cost on unchanged files.
            var hashesMatch = false;
            if (Config.DuplicateExactHashEnabled && keeper.Size == loser.Size && keeper.Size > 0)
            {
                var keeperHash = await _hasher.HashAsync(keeper.Path, cancellationToken).ConfigureAwait(false);
                var loserHash = await _hasher.HashAsync(loser.Path, cancellationToken).ConfigureAwait(false);
                hashesMatch = keeperHash is not null && loserHash is not null
                    && string.Equals(keeperHash, loserHash, StringComparison.Ordinal);
            }

            var sameDirectoryDistinctStems = SameDirectoryDistinctStems(keeper.Path, loser.Path);
            var (confidence, vetoed, signals) = ScorePair(keeper, loser, tier, sameDirectoryDistinctStems, hashesMatch, Config);
            if (vetoed)
            {
                _logger.LogDebug("Duplicate pair vetoed for '{Loser}' vs keeper '{Keeper}' (groupKey {GroupKey}, tier {Tier})", loser.Path, keeper.Path, groupKey, tier);
                continue;
            }

            var detailsJson = JsonSerializer.Serialize(new
            {
                groupKey,
                keeperPath = keeper.Path,
                keeper = new { keeper.Resolution, keeper.Codec, keeper.Size, keeper.Bitrate },
                thisCopy = new { loser.Resolution, loser.Codec, loser.Size, loser.Bitrate },
                confidence,
                signals
            });

            issues.Add(new Issue
            {
                Type = IssueType.Duplicate,
                ItemId = loser.Item!.Id,
                Path = loser.Path,
                Status = IssueStatus.Detected,
                DetectedAtUtc = DateTime.UtcNow,
                SizeSavings = loser.Size,
                Confidence = confidence,
                SuggestedFix = string.Format(
                    CultureInfo.InvariantCulture,
                    "Safe to delete — a better copy exists ({0}, {1}, confidence {2:F2}).",
                    keeper.Resolution,
                    keeper.Codec.ToUpperInvariant(),
                    confidence),
                DetailsJson = detailsJson
            });
        }

        return issues;
    }

    /// <summary>
    /// Scores a keeper↔loser candidate pair per the duplicate confidence ladder. Pure/deterministic —
    /// all IO (hash comparison, filesystem stems) is done by the caller and passed in.
    /// See docs/field-reports (2026-08-22 duplicate rework spec §2, §4).
    /// </summary>
    /// <param name="keeper">The candidate the ranker chose to keep.</param>
    /// <param name="loser">The candidate under evaluation.</param>
    /// <param name="tier">Tier derived from the group key (via <see cref="DuplicateSignals.TierForKey"/>).</param>
    /// <param name="sameDirectoryDistinctStems">True when both files sit in the same folder with different filename stems — the #3 shape of intentional distinct files sharing a bad metadata slot.</param>
    /// <param name="hashesMatch">True when caller already confirmed byte-identical (same size + same SHA-256).</param>
    /// <param name="cfg">Plugin config providing the veto thresholds.</param>
    /// <returns>Confidence in [0,1], whether the pair was vetoed (skip emission entirely), and a signals dict for DetailsJson.</returns>
    internal static (double Confidence, bool Vetoed, IReadOnlyDictionary<string, object?> Signals) ScorePair(
        Candidate keeper,
        Candidate loser,
        ConfidenceTier tier,
        bool sameDirectoryDistinctStems,
        bool hashesMatch,
        Configuration.PluginConfiguration cfg)
    {
        var jaccard = DuplicateSignals.TitleTokenJaccard(
            System.IO.Path.GetFileName(keeper.Path),
            System.IO.Path.GetFileName(loser.Path));
        var runtimeDelta = DuplicateSignals.RuntimeDeltaFraction(keeper.Item?.RunTimeTicks, loser.Item?.RunTimeTicks);

        var signals = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tier"] = tier.ToString(),
            ["titleJaccard"] = double.IsNaN(jaccard) ? null : jaccard,
            ["runtimeDelta"] = runtimeDelta,
            ["sameDirectoryDistinctStems"] = sameDirectoryDistinctStems,
            ["hashesMatch"] = hashesMatch
        };

        // Tier 0: byte-identical wins outright — no veto path can override.
        if (hashesMatch)
        {
            signals["appliedTier"] = ConfidenceTier.Exact.ToString();
            return (1.0, Vetoed: false, signals);
        }

        // Vetoes apply to Tier 1 (Identified) and Tier 2 (Heuristic). Title-token veto is
        // skipped when the signal is NaN — the caller literally cannot judge it, so we fall
        // back to the runtime veto alone. Do NOT collapse NaN to 0.0 here (would veto
        // everything unnamed after noise-stripping).
        if (!double.IsNaN(jaccard) && jaccard < cfg.DuplicateTitleJaccardVeto)
        {
            signals["vetoReason"] = "titleJaccardBelowThreshold";
            return (0.0, Vetoed: true, signals);
        }

        if (runtimeDelta is double delta && delta > (cfg.DuplicateRuntimeVetoPct / 100.0))
        {
            signals["vetoReason"] = "runtimeDeltaExceedsThreshold";
            return (0.0, Vetoed: true, signals);
        }

        var confidence = tier == ConfidenceTier.Identified ? 0.90 : 0.70;

        // Soft adjustments only for the Heuristic tier — Identified matches already carry
        // provider-ID evidence and don't need to be inflated further.
        if (tier == ConfidenceTier.Heuristic)
        {
            if (!double.IsNaN(jaccard) && jaccard >= 0.80)
            {
                confidence += 0.15;
            }

            if (runtimeDelta is double d && d <= 0.05)
            {
                confidence += 0.10;
            }

            if (sameDirectoryDistinctStems)
            {
                confidence -= 0.25;
            }
        }

        confidence = Math.Clamp(confidence, 0.0, 1.0);
        signals["appliedTier"] = tier.ToString();
        return (confidence, Vetoed: false, signals);
    }

    private static bool SameDirectoryDistinctStems(string keeperPath, string loserPath)
    {
        var kDir = Path.GetDirectoryName(keeperPath);
        var lDir = Path.GetDirectoryName(loserPath);
        if (string.IsNullOrEmpty(kDir) || string.IsNullOrEmpty(lDir))
        {
            return false;
        }

        if (!string.Equals(kDir, lDir, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var kStem = Path.GetFileNameWithoutExtension(keeperPath);
        var lStem = Path.GetFileNameWithoutExtension(loserPath);
        return !string.Equals(kStem, lStem, StringComparison.OrdinalIgnoreCase);
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

    // Separator run must include at least one [-._] so a plain-space title ending in a keyword
    // ("The Trailer") is safe. Extras keywords are the Jellyfin-documented ones plus common spaced
    // variants: behind[-_ ]?the[-_ ]?scenes, deleted[-_ ]?scenes?, trailer, clip, featurette, etc.
    [GeneratedRegex(@"(?:\s+[-._]|[-._])[\s._-]*(behind[\s._-]?the[\s._-]?scenes|deleted[\s._-]?scenes?|featurettes?|interviews?|clips?|shorts?|trailers?|samples?|scenes?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SidecarSuffixRegex();

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
