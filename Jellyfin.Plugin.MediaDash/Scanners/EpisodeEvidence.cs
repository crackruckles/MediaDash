using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MediaDash.Metadata;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// What a filename says about which episode it holds, checked against the TVDb episode list when
/// one is available. The duplicate scanner uses it so two files are only offered as copies of one
/// episode when nothing in their names says otherwise.
/// </summary>
/// <remarks>
/// Field reports this answers (issue #3): classic Doctor Who parts ("S13E01 Part 2" beside
/// "S13E01 Part 1"), shows Jellyfin numbered wrongly from literal names (Firefly, The Six Million
/// Dollar Man: every file landed on one episode number), and two Futurama films filed as specials
/// with the same special number.
/// </remarks>
internal static partial class EpisodeEvidence
{
    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6,
        ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["i"] = 1, ["ii"] = 2, ["iii"] = 3, ["iv"] = 4
    };

    /// <summary>Reads what a filename claims about its episode.</summary>
    /// <param name="path">The file.</param>
    /// <param name="guide">TVDb episodes of the series, when known.</param>
    /// <param name="showName">Series name, so a title equal to the show name is not used as evidence.</param>
    /// <returns>The claim.</returns>
    internal static Claim Read(string path, IReadOnlyList<TvdbEpisode>? guide, string? showName)
    {
        var stem = MediaIdentityService.StripSite(System.IO.Path.GetFileNameWithoutExtension(path) ?? string.Empty);
        int? season = null, first = null, last = null;
        var marker = EpisodeRange().Match(stem);
        if (marker.Success)
        {
            season = int.Parse(marker.Groups["s"].Success ? marker.Groups["s"].Value : marker.Groups["s2"].Value, CultureInfo.InvariantCulture);
            first = int.Parse(marker.Groups["e"].Success ? marker.Groups["e"].Value : marker.Groups["e2"].Value, CultureInfo.InvariantCulture);
            last = marker.Groups["last"].Success && int.Parse(marker.Groups["last"].Value, CultureInfo.InvariantCulture) is var end && end > first ? end : first;
        }
        else if (LiteralEpisode().Match(stem) is { Success: true } literal)
        {
            season = int.Parse(literal.Groups["s"].Value, CultureInfo.InvariantCulture);
            first = last = int.Parse(literal.Groups["e"].Value, CultureInfo.InvariantCulture);
        }

        int? part = null;
        if (PartMarker().Match(stem) is { Success: true } partMatch)
        {
            var value = partMatch.Groups["n"].Value;
            part = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number
                : NumberWords.TryGetValue(value, out var word) ? word : null;
        }

        return new Claim(season, first, last, part, guide is null ? [] : MatchTitles(stem, guide, showName));
    }

    /// <summary>Decides whether two files can be copies of the episode Jellyfin filed them under.</summary>
    /// <param name="a">The first file's claim.</param>
    /// <param name="b">The second file's claim.</param>
    /// <param name="season">Season Jellyfin filed both under.</param>
    /// <param name="episode">Episode number Jellyfin filed both under.</param>
    /// <returns>Whether the pair must not be offered, why, and whether both names confirm the episode.</returns>
    internal static Verdict Compare(Claim a, Claim b, int season, int episode)
    {
        if (a.Part != b.Part)
        {
            return new Verdict(true, "differentParts", false);
        }

        if (a.FirstEpisode is not null && b.FirstEpisode is not null
            && (a.Season != b.Season || a.FirstEpisode != b.FirstEpisode || a.LastEpisode != b.LastEpisode))
        {
            return new Verdict(true, "differentEpisodeNumbers", false);
        }

        if (a.Names(season, episode) == false || b.Names(season, episode) == false)
        {
            return new Verdict(true, "nameIsAnotherEpisode", false);
        }

        if (a.TitleMatches.Count > 0 && b.TitleMatches.Count > 0 && !a.TitleMatches.Intersect(b.TitleMatches).Any())
        {
            return new Verdict(true, "differentEpisodeTitles", false);
        }

        var confirmed = a.Names(season, episode) == true && b.Names(season, episode) == true;

        // Specials are numbered loosely by every provider; two files only share a special when
        // their names say which one they are.
        if (season == 0 && !confirmed)
        {
            return new Verdict(true, "unconfirmedSpecial", false);
        }

        return new Verdict(false, confirmed ? "confirmed" : null, confirmed);
    }

    /// <summary>True when both filenames carry the same show name before their episode marker.</summary>
    /// <param name="pathA">First file.</param>
    /// <param name="pathB">Second file.</param>
    /// <returns>Whether the show names agree; false when either name has none.</returns>
    internal static bool SameShowName(string pathA, string pathB)
    {
        var a = ShowName(pathA);
        return a.Length > 0 && a == ShowName(pathB);
    }

    private static string ShowName(string path)
    {
        var stem = MediaIdentityService.StripSite(System.IO.Path.GetFileNameWithoutExtension(path) ?? string.Empty);
        var marker = EpisodeRange().Match(stem);
        if (!marker.Success)
        {
            marker = LiteralEpisode().Match(stem);
        }

        if (!marker.Success || marker.Index == 0)
        {
            return string.Empty;
        }

        return string.Concat(Tokens(stem[..marker.Index]).Where(token => !YearToken().IsMatch(token)));
    }

    private static List<(int Season, int Episode)> MatchTitles(string stem, IReadOnlyList<TvdbEpisode> guide, string? showName)
    {
        var tokens = Tokens(stem);
        var show = string.Join(' ', Tokens(showName ?? string.Empty));
        var best = 0;
        var matches = new List<(int Season, int Episode)>();
        foreach (var candidate in guide)
        {
            var title = Tokens(candidate.Name);
            var joined = string.Join(' ', title);
            if (title.Count == 0 || joined.Length < 4 || joined == show || GenericTitle().IsMatch(joined)
                || (title.Count == 1 && (title[0].Length < 5 || DuplicateSignals.NoiseTokens.Contains(title[0])))
                || title.Count < best || !ContainsRun(tokens, title))
            {
                continue;
            }

            if (title.Count > best)
            {
                best = title.Count;
                matches.Clear();
            }

            if (!matches.Contains((candidate.Season, candidate.Number)))
            {
                matches.Add((candidate.Season, candidate.Number));
            }
        }

        return matches;
    }

    private static bool ContainsRun(List<string> haystack, List<string> needle)
    {
        for (var start = 0; start + needle.Count <= haystack.Count; start++)
        {
            var hit = true;
            for (var i = 0; i < needle.Count && hit; i++)
            {
                hit = haystack[start + i] == needle[i];
            }

            if (hit)
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> Tokens(string text)
        => NonWord().Split(text.ToLowerInvariant().Replace("&", " and ", StringComparison.Ordinal).Replace("'", string.Empty, StringComparison.Ordinal).Replace("\u2019", string.Empty, StringComparison.Ordinal))
            .Where(token => token.Length > 0).ToList();

    // S01E02, S01E02E03, S01E02-E03, S01E02-03, 1x02.
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:[Ss](?<s>\d{1,2})[ ._-]?[Ee](?<e>\d{1,3})(?:[ ._-]?(?:[Ee]|-[Ee]?)(?<last>\d{1,3}))?|(?<s2>\d{1,2})x(?<e2>\d{2,3}))(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeRange();

    // "Season 1 Episode 2", "Season.01.Ep.02".
    [GeneratedRegex(@"(?i)\bseason[ ._-]*(?<s>\d{1,2})[ ._-]*(?:episode|ep)[ ._-]*(?<e>\d{1,3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralEpisode();

    // "Part 2", "Pt.2", "Part Two", "CD2", "Disc 2", or a trailing "(2)".
    [GeneratedRegex(@"(?i)(?:\b(?:part|pt|cd|disc|disk)[ ._-]?(?<n>\d{1,2}|one|two|three|four|five|six|seven|eight|nine|ten|i{1,3}|iv)\b|\((?<n>\d{1,2})\)\s*$)", RegexOptions.CultureInvariant)]
    private static partial Regex PartMarker();

    [GeneratedRegex(@"^(?:episode|chapter|part|ep|tba|tbd|untitled|unknown)?\s*\d*$", RegexOptions.CultureInvariant)]
    private static partial Regex GenericTitle();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonWord();

    [GeneratedRegex(@"^(?:19|20)\d\d$", RegexOptions.CultureInvariant)]
    private static partial Regex YearToken();

    /// <summary>What one filename says about its episode.</summary>
    /// <param name="Season">Season from an episode marker or a literal "Season X Episode Y".</param>
    /// <param name="FirstEpisode">First episode the name covers.</param>
    /// <param name="LastEpisode">Last episode the name covers (same as first for one episode).</param>
    /// <param name="Part">Part number ("Part 2", "(2)"), when the name has one.</param>
    /// <param name="TitleMatches">TVDb episodes whose title appears in the name (several when titles tie).</param>
    internal sealed record Claim(int? Season, int? FirstEpisode, int? LastEpisode, int? Part, IReadOnlyList<(int Season, int Episode)> TitleMatches)
    {
        /// <summary>Whether the name says it is this episode: true, false (it names another), or null (it does not say).</summary>
        /// <param name="season">Season.</param>
        /// <param name="episode">Episode number.</param>
        /// <returns>The answer.</returns>
        internal bool? Names(int season, int episode)
        {
            if (FirstEpisode is not null)
            {
                return Season == season && FirstEpisode <= episode && episode <= LastEpisode;
            }

            return TitleMatches.Count == 0 ? null : TitleMatches.Contains((season, episode));
        }
    }

    /// <summary>The outcome of comparing two names.</summary>
    /// <param name="Veto">True when the names show the files are not the same episode.</param>
    /// <param name="Reason">Short reason code for the signals record.</param>
    /// <param name="Confirmed">True when both names say they are the episode they were filed under.</param>
    internal sealed record Verdict(bool Veto, string? Reason, bool Confirmed);
}
