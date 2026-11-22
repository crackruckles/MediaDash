using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Recovers where a file used to live from MediaDash's own move records, and the episode identity
/// carried by those former names. Earlier grouping moved a video's immediate folder out of the
/// release folder that named it ("Show.S09E05.Title-GRP/info/info.mkv" became "Show (2013)/info/info.mkv")
/// and orphan cleanup then recycled the release folder, so the move record is the only place the
/// season and episode survive.
/// </summary>
internal static partial class MoveProvenance
{
    private const string GroupedPrefix = "grouped ";
    private const string Arrow = " → ";
    private const int MaxHops = 8;

    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Reads "grouped &lt;source&gt; → &lt;destination&gt;" history actions into (source, destination) pairs.</summary>
    /// <param name="actions">History action texts.</param>
    /// <returns>Recorded folder and file moves, oldest first as given.</returns>
    internal static IReadOnlyList<(string Source, string Destination)> ParseGrouped(IEnumerable<string> actions)
    {
        var moves = new List<(string Source, string Destination)>();
        foreach (var action in actions)
        {
            if (string.IsNullOrWhiteSpace(action) || !action.StartsWith(GroupedPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var body = action[GroupedPrefix.Length..];
            var split = body.IndexOf(Arrow, StringComparison.Ordinal);
            if (split <= 0 || body.IndexOf(Arrow, split + Arrow.Length, StringComparison.Ordinal) >= 0)
            {
                continue;
            }

            var source = Path.TrimEndingDirectorySeparator(body[..split].Trim());
            var destination = Path.TrimEndingDirectorySeparator(body[(split + Arrow.Length)..].Trim());
            if (Path.IsPathFullyQualified(source) && Path.IsPathFullyQualified(destination) && !Paths.Equals(source, destination))
            {
                moves.Add((source, destination));
            }
        }

        return moves;
    }

    /// <summary>
    /// Every earlier path of <paramref name="path"/>, nearest first, found by mapping it back through
    /// recorded moves whose destination is the path or one of its folders.
    /// </summary>
    /// <param name="path">The current path.</param>
    /// <param name="moves">Recorded moves.</param>
    /// <returns>Former paths, most recent first.</returns>
    internal static IReadOnlyList<string> FormerPaths(string path, IReadOnlyList<(string Source, string Destination)> moves)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(Paths) { path };
        var current = path;
        for (var hop = 0; hop < MaxHops; hop++)
        {
            var match = moves.Where(move => LibraryGuard.IsUnder(current, move.Destination))
                .OrderByDescending(move => move.Destination.Length).Select(move => ((string Source, string Destination)?)move).FirstOrDefault();
            if (match is not { } move)
            {
                break;
            }

            var relative = Path.GetRelativePath(move.Destination, current);
            current = relative == "." ? move.Source : Path.Combine(move.Source, relative);
            if (!seen.Add(current))
            {
                break;
            }

            result.Add(current);
        }

        return result;
    }

    /// <summary>True when the name carries an SxxEyy or NxNN episode marker.</summary>
    /// <param name="name">A file stem or folder name.</param>
    /// <returns>True when Jellyfin can read an episode number from it.</returns>
    internal static bool HasEpisodeMarker(string name) => EpisodeMarker().IsMatch(name ?? string.Empty);

    /// <summary>The first SxxEyy / NxNN marker in a name.</summary>
    /// <param name="name">A file stem or folder name.</param>
    /// <returns>Season and episode, or null when the name carries no marker.</returns>
    internal static (int Season, int Episode)? ParseEpisodeMarker(string? name)
    {
        var match = EpisodeMarker().Match(name ?? string.Empty);
        if (!match.Success)
        {
            return null;
        }

        return (int.Parse(match.Groups["s"].Success ? match.Groups["s"].Value : match.Groups["s2"].Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(match.Groups["e"].Success ? match.Groups["e"].Value : match.Groups["e2"].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The nearest name carrying an episode marker: the file, then its folders up to the library root,
    /// then the same for each former path from <paramref name="moves"/>.
    /// </summary>
    /// <param name="path">The episode file.</param>
    /// <param name="roots">Library roots, where the search stops.</param>
    /// <param name="moves">Recorded moves.</param>
    /// <returns>Season, episode and the name they came from; null when no name carries them.</returns>
    internal static (int Season, int Episode, string Name)? FindEpisodeIdentity(string path, IReadOnlyList<string> roots, IReadOnlyList<(string Source, string Destination)> moves)
    {
        foreach (var candidate in FormerPaths(path, moves).Prepend(path))
        {
            for (var current = candidate; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                var trimmed = Path.TrimEndingDirectorySeparator(current);
                if (roots.Any(root => Paths.Equals(Path.TrimEndingDirectorySeparator(root), trimmed)) || Path.GetDirectoryName(trimmed) is null)
                {
                    break;
                }

                var name = Paths.Equals(trimmed, candidate) ? Path.GetFileNameWithoutExtension(trimmed) : Path.GetFileName(trimmed);
                var match = EpisodeMarker().Match(name);
                if (match.Success)
                {
                    var season = int.Parse(match.Groups["s"].Success ? match.Groups["s"].Value : match.Groups["s2"].Value, System.Globalization.CultureInfo.InvariantCulture);
                    var episode = int.Parse(match.Groups["e"].Success ? match.Groups["e"].Value : match.Groups["e2"].Value, System.Globalization.CultureInfo.InvariantCulture);
                    return (season, episode, name);
                }
            }
        }

        return null;
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:[Ss](?<s>\d{1,2})[ ._-]?[Ee](?<e>\d{1,3})|(?<s2>\d{1,2})x(?<e2>\d{2,3}))(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeMarker();
}
