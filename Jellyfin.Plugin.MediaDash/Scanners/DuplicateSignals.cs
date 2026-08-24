using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>Confidence tier for a duplicate pair. See the rework spec §2.</summary>
internal enum ConfidenceTier
{
    /// <summary>Byte-identical (size + full hash match). Confidence 1.00.</summary>
    Exact,

    /// <summary>Grouped by a shared provider ID (Tmdb/Imdb/Tvdb/Isbn/MusicBrainzTrack). Base 0.90.</summary>
    Identified,

    /// <summary>Grouped by a fallback key (movie-name/episode/book-name/audio-name). Base 0.70.</summary>
    Heuristic
}

/// <summary>
/// Deterministic, unit-testable signals used by the DuplicateScanner confidence ladder.
/// See docs/field-reports (2026-08-22 duplicate rework spec) for the model.
/// </summary>
internal static partial class DuplicateSignals
{
    /// <summary>
    /// Filename tokens that describe how a file was encoded / muxed — resolution, source, codec,
    /// audio, HDR labels, and release tags. Stripped before computing title-token Jaccard so that
    /// two files of the same movie in different quality don't split into disjoint token sets.
    /// Do NOT include edition words (extended, directors, unrated, remastered) — those legitimately
    /// distinguish content and are handled by the edition system.
    /// </summary>
    internal static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        // Resolution
        "2160p", "1080p", "720p", "480p", "576p", "4k", "uhd", "hd", "sd",
        // Source — includes the split-hyphen halves ("web-dl" → tokens "web" + "dl") so the
        // net effect of stripping matches the spec's compound-form noise entries like "webdl".
        "bluray", "blu", "br", "bdrip", "brrip", "webdl", "web", "webrip", "wp", "hdtv", "dvdrip", "dvd", "rip", "dl", "remux", "hdrip", "bdremux",
        // Codec
        "x264", "x265", "h264", "h265", "hevc", "avc", "av1", "xvid", "divx", "vp9", "mpeg2", "mpeg4",
        // Audio (channel counts as numeric tokens because the tokenizer strips punctuation:
        // "5.1" → "5" "1" → "51" when joined? Actually tokenizer splits on non-alnum then keeps
        // alnum runs, so "5.1" produces tokens "5" and "1". We include the concatenated form for
        // filenames that write it without a separator like "51ch"; treat individual "5"/"1" as
        // digits that survive naturally — the year filter (§3.1 step 3) leaves them.
        "aac", "ac3", "eac3", "dd", "ddp", "dts", "dtshd", "truehd", "atmos", "flac", "mp3", "opus", "51", "71", "20",
        // HDR
        "hdr", "hdr10", "hdr10plus", "dv", "dovi", "hlg", "sdr", "10bit", "8bit",
        // Release tags
        "proper", "repack", "internal", "limited"
    };

    // Whitelist of media / subtitle / book / comic extensions this plugin cares about. Anything
    // else (a "-GROUP" suffix that just happens to sit after a dot) is left in the stem so its
    // tokens still contribute to the Jaccard calculation. Built from the canonical MediaFormats
    // and SubtitleFormats sets so an extension added there flows through automatically.
    private static readonly HashSet<string> KnownExtensions = BuildKnownExtensions();

    private static HashSet<string> BuildKnownExtensions()
    {
        var set = new HashSet<string>(MediaFormats.All, StringComparer.OrdinalIgnoreCase);
        set.UnionWith(SubtitleFormats.Extensions);
        return set;
    }

    /// <summary>
    /// Computes Jaccard similarity between the title tokens of two filenames (extensions dropped,
    /// noise tokens and year-like tokens stripped). Returns <see cref="double.NaN"/> when either
    /// side has zero title tokens — callers treat NaN as "cannot judge" and skip the title veto.
    /// Do NOT collapse NaN to 0.0 (that would veto every unnamed-after-strip pair).
    /// </summary>
    /// <param name="stemA">First filename (with or without extension — extension is stripped).</param>
    /// <param name="stemB">Second filename.</param>
    /// <returns>Jaccard in [0,1] or NaN when the signal is unavailable.</returns>
    internal static double TitleTokenJaccard(string? stemA, string? stemB)
    {
        var a = TitleTokens(stemA);
        var b = TitleTokens(stemB);
        if (a.Count == 0 || b.Count == 0)
        {
            return double.NaN;
        }

        var union = new HashSet<string>(a, StringComparer.Ordinal);
        union.UnionWith(b);
        if (union.Count == 0)
        {
            return 0.0;
        }

        var intersect = new HashSet<string>(a, StringComparer.Ordinal);
        intersect.IntersectWith(b);
        return (double)intersect.Count / union.Count;
    }

    /// <summary>
    /// Returns <c>|a-b| / max(a,b)</c> for two positive runtime tick counts, or <c>null</c> when
    /// either input is null or non-positive.
    /// </summary>
    /// <param name="ticksA">First runtime in <see cref="TimeSpan"/> ticks.</param>
    /// <param name="ticksB">Second runtime in ticks.</param>
    /// <returns>Relative delta in [0,∞), or null when incomparable.</returns>
    internal static double? RuntimeDeltaFraction(long? ticksA, long? ticksB)
    {
        if (ticksA is not > 0 || ticksB is not > 0)
        {
            return null;
        }

        var max = Math.Max(ticksA.Value, ticksB.Value);
        var min = Math.Min(ticksA.Value, ticksB.Value);
        return (double)(max - min) / max;
    }

    /// <summary>
    /// Classifies a group key produced by <c>DuplicateScanner.GetGroupKey</c> into a confidence
    /// tier. Provider-ID keys yield <see cref="ConfidenceTier.Identified"/>; name/episode fallback
    /// keys yield <see cref="ConfidenceTier.Heuristic"/>. <see cref="ConfidenceTier.Exact"/> is
    /// only assigned per-pair by the hash check, not per-group.
    /// </summary>
    /// <param name="groupKey">The group key string (e.g. <c>"movie:tmdb:12345"</c>, <c>"episode:…"</c>).</param>
    /// <returns>The tier.</returns>
    internal static ConfidenceTier TierForKey(string groupKey)
    {
        if (string.IsNullOrEmpty(groupKey))
        {
            return ConfidenceTier.Heuristic;
        }

        // Provider-ID keys embed the provider name after the kind prefix, e.g. "movie:tmdb:...".
        return groupKey.Contains(":tmdb:", StringComparison.Ordinal)
            || groupKey.Contains(":imdb:", StringComparison.Ordinal)
            || groupKey.Contains(":tvdb:", StringComparison.Ordinal)
            || groupKey.Contains(":isbn:", StringComparison.Ordinal)
            || groupKey.Contains(":musicbrainztrack:", StringComparison.Ordinal)
            ? ConfidenceTier.Identified
            : ConfidenceTier.Heuristic;
    }

    private static List<string> TitleTokens(string? filename)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(filename))
        {
            return result;
        }

        // Path.GetFileNameWithoutExtension chops on the LAST dot regardless of what follows it,
        // so "Inception.2010.1080p.BluRay.x264-YIFY" becomes "Inception.2010.1080p.BluRay" (losing
        // both the codec and the release-group tokens). Strip only recognized media/subtitle
        // extensions instead — release names full of dots keep every token intact.
        var stem = StripKnownExtension(Path.GetFileName(filename)).ToLowerInvariant();
        foreach (Match m in TokenSplitRegex().Matches(stem))
        {
            var token = m.Value;
            if (token.Length == 0 || NoiseTokens.Contains(token) || YearRegex().IsMatch(token))
            {
                continue;
            }

            result.Add(token);
        }

        return result;
    }

    private static string StripKnownExtension(string filename)
    {
        var ext = Path.GetExtension(filename);
        return KnownExtensions.Contains(ext) ? filename[..^ext.Length] : filename;
    }

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex TokenSplitRegex();

    [GeneratedRegex(@"^(19|20)\d{2}$")]
    private static partial Regex YearRegex();
}
