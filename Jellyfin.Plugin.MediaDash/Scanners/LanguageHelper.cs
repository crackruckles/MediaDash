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

    // Macrolanguage equivalence groups. A user who allows "nor" (Norwegian) should have their
    // "nob" (Bokmål) and "nno" (Nynorsk) tracks kept too — most Norwegian media is tagged with
    // the specific variant, so a "nor"-only allowed list would silently delete everything.
    // Every code in a group matches every other code in the same group in IsAllowed().
    // Add more groups here when users report similar false-positives (Chinese cmn/yue, etc.).
    private static readonly IReadOnlyList<HashSet<string>> EquivalenceGroups =
    [
        new(StringComparer.OrdinalIgnoreCase) { "nor", "nob", "nno" }
    ];

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

        var regionSeparator = lang.IndexOfAny(['-', '_']);
        if (regionSeparator >= 0)
        {
            lang = lang[..regionSeparator];
        }

        // ISO 639-1 (2-letter) → 639-2/T (3-letter) via CultureInfo. Without this, an "en"-tagged track
        // never matches an allowed list of ["eng"] and gets flagged for removal — including the file's
        // only English audio track when Spanish also exists ("last audio" invariant wouldn't save it).
        if (lang.Length == 2)
        {
            try
            {
                var culture = System.Globalization.CultureInfo.GetCultureInfo(lang);
                if (!string.IsNullOrEmpty(culture.ThreeLetterISOLanguageName) && culture.ThreeLetterISOLanguageName != "xx")
                {
                    lang = culture.ThreeLetterISOLanguageName.ToLowerInvariant();
                }
            }
            catch (System.Globalization.CultureNotFoundException)
            {
            }
        }

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
            var allowedNorm = Normalize(entry);
            if (string.Equals(allowedNorm, normalized, StringComparison.Ordinal))
            {
                return true;
            }

            if (SameEquivalenceGroup(allowedNorm, normalized))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameEquivalenceGroup(string a, string b)
    {
        foreach (var group in EquivalenceGroups)
        {
            if (group.Contains(a) && group.Contains(b))
            {
                return true;
            }
        }

        return false;
    }
}
