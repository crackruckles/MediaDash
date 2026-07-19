using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Language code matching that copes with ISO 639-2 bibliographic/terminological variants (fre/fra, ger/deu).
/// </summary>
public static class LanguageHelper
{
    private static readonly Dictionary<string, string> BibliographicToTerminological = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alb"] = "sqi",
        ["arm"] = "hye",
        ["baq"] = "eus",
        ["bur"] = "mya",
        ["chi"] = "zho",
        ["cze"] = "ces",
        ["dut"] = "nld",
        ["fre"] = "fra",
        ["geo"] = "kat",
        ["ger"] = "deu",
        ["gre"] = "ell",
        ["ice"] = "isl",
        ["mac"] = "mkd",
        ["mao"] = "mri",
        ["may"] = "msa",
        ["per"] = "fas",
        ["rum"] = "ron",
        ["slo"] = "slk",
        ["tib"] = "bod",
        ["wel"] = "cym"
    };

    /// <summary>
    /// Normalizes a language tag to a lowercase ISO 639-2/T code. Null, empty and unknown map to "und".
    /// </summary>
    /// <param name="language">The raw language tag from the media file.</param>
    /// <returns>The normalized code.</returns>
    public static string Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "und";
        }

        var lang = language.Trim().ToLowerInvariant();
        return BibliographicToTerminological.TryGetValue(lang, out var terminological) ? terminological : lang;
    }

    /// <summary>
    /// Checks whether a track language is in the allowed list. Undetermined ("und", missing) tracks are always allowed,
    /// because deleting a track whose language is unknown is not safe.
    /// </summary>
    /// <param name="language">The raw language tag from the media file.</param>
    /// <param name="allowed">The allowed ISO 639-2 codes.</param>
    /// <returns>True when the track should be kept.</returns>
    public static bool IsAllowed(string? language, IReadOnlyList<string> allowed)
    {
        var normalized = Normalize(language);
        if (normalized == "und")
        {
            return true;
        }

        foreach (var entry in allowed)
        {
            if (string.Equals(Normalize(entry), normalized, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
