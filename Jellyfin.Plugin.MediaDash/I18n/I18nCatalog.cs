using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.MediaDash.I18n;

/// <summary>
/// Loads embedded i18n JSON dictionaries.
/// </summary>
public static class I18nCatalog
{
    private const string ResourcePrefix = "Jellyfin.Plugin.MediaDash.Configuration.i18n.";
    private const string DefaultLocale = "en";

    // Cache parsed html-string dictionaries per locale — task names/descriptions are read once per Jellyfin
    // task list render, and reparsing JSON on every hit would be silly for a file we ship inside the assembly.
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> HtmlCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<HashSet<string>> AvailableLocales = new(
        () => typeof(Plugin).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n => n[ResourcePrefix.Length..^".json".Length])
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Gets the codes of every locale shipped in the plugin.
    /// </summary>
    /// <returns>The locale codes, e.g. "en", "de", "zh-CN".</returns>
    public static IReadOnlyCollection<string> ListLocales() => AvailableLocales.Value;

    /// <summary>
    /// Opens the best-matching locale JSON as a stream. Falls back from "de-AT" to "de" to English so an unknown or malformed tag never returns null.
    /// </summary>
    /// <param name="locale">The requested BCP-47 tag.</param>
    /// <returns>A readable JSON stream, never null (English is guaranteed to ship).</returns>
    public static Stream OpenBestMatch(string? locale)
    {
        var match = ResolveLocale(locale);
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream(ResourcePrefix + match + ".json")
            ?? typeof(Plugin).Assembly.GetManifestResourceStream(ResourcePrefix + DefaultLocale + ".json");
        // The English JSON is embedded via .csproj, so this only ever returns null if someone breaks the build layout.
        return stream ?? throw new InvalidOperationException("i18n resources missing from the plugin assembly");
    }

    /// <summary>
    /// Resolves a BCP-47 tag to a shipped locale code. Matches on the full tag first, then the primary language subtag.
    /// </summary>
    /// <param name="locale">The requested tag.</param>
    /// <returns>The best-matching shipped locale, English if nothing else fits.</returns>
    public static string ResolveLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return DefaultLocale;
        }

        var normalised = locale.Replace('_', '-');
        var available = AvailableLocales.Value;
        if (available.Contains(normalised))
        {
            return available.First(l => l.Equals(normalised, StringComparison.OrdinalIgnoreCase));
        }

        var dash = normalised.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0)
        {
            var primary = normalised[..dash];
            if (available.Contains(primary))
            {
                return available.First(l => l.Equals(primary, StringComparison.OrdinalIgnoreCase));
            }
        }

        return DefaultLocale;
    }

    /// <summary>
    /// Reads a value from the `html` block of the resolved locale JSON. Falls back to English, then to the supplied default.
    /// Cached per locale so repeated calls (e.g. Jellyfin re-reading task Name/Description) don't reparse the JSON.
    /// </summary>
    /// <param name="locale">The requested locale.</param>
    /// <param name="key">The html key.</param>
    /// <param name="fallback">The literal to return if neither the requested locale nor English contain the key.</param>
    /// <returns>The translated string.</returns>
    public static string GetHtml(string? locale, string key, string fallback)
    {
        var resolved = ResolveLocale(locale);
        if (TryLookup(resolved, key, out var value))
        {
            return value;
        }

        if (!resolved.Equals(DefaultLocale, StringComparison.OrdinalIgnoreCase) && TryLookup(DefaultLocale, key, out value))
        {
            return value;
        }

        return fallback;
    }

    private static bool TryLookup(string locale, string key, out string value)
    {
        var dict = HtmlCache.GetOrAdd(locale, LoadHtml);
        if (dict.TryGetValue(key, out var v))
        {
            value = v;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static IReadOnlyDictionary<string, string> LoadHtml(string locale)
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(ResourcePrefix + locale + ".json");
        if (stream is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        using var doc = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty("html", out var html) || html.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in html.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                result[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
        }

        return result;
    }
}
