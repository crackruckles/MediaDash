using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.MediaDash.I18n;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class I18nCatalogTests
{
    [Fact]
    public void EnglishIsShipped()
    {
        Assert.Contains("en", I18nCatalog.ListLocales());
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("de-AT", "de")]
    [InlineData("es_ES", "es")]
    [InlineData("xx", "en")]
    [InlineData("", "en")]
    [InlineData(null, "en")]
    public void ResolveLocaleFallsBackDownTheBcp47Chain(string? request, string expected)
    {
        Assert.Equal(expected, I18nCatalog.ResolveLocale(request));
    }

    [Fact]
    public void EveryShippedLocaleHasTheSameHtmlKeysAsEnglish()
    {
        var enKeys = LoadHtmlKeys("en");
        Assert.NotEmpty(enKeys);

        foreach (var locale in I18nCatalog.ListLocales().Where(l => l != "en"))
        {
            var keys = LoadHtmlKeys(locale);
            var missing = enKeys.Except(keys).ToList();
            var extra = keys.Except(enKeys).ToList();
            Assert.True(missing.Count == 0 && extra.Count == 0,
                $"Locale '{locale}' has key-shape drift. Missing: [{string.Join(", ", missing)}], extra: [{string.Join(", ", extra)}]");
        }
    }

    [Fact]
    public void GetHtmlReturnsTranslationWhenAvailable()
    {
        var translated = I18nCatalog.GetHtml("de", "tab.settings", "Settings");
        Assert.Equal("Einstellungen", translated);
    }

    [Fact]
    public void GetHtmlFallsBackToSuppliedDefaultWhenKeyMissing()
    {
        Assert.Equal("literal", I18nCatalog.GetHtml("de", "no.such.key", "literal"));
    }

    private static HashSet<string> LoadHtmlKeys(string locale)
    {
        using var stream = I18nCatalog.OpenBestMatch(locale);
        using var doc = JsonDocument.Parse(stream);
        Assert.True(doc.RootElement.TryGetProperty("html", out var html), $"'{locale}' is missing the html block.");
        return html.EnumerateObject().Select(p => p.Name).ToHashSet();
    }
}
