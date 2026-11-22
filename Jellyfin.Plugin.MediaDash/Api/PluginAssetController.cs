using System.Collections.Generic;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Serves whitelisted static JS/CSS assets embedded in the plugin assembly. Introduced in
/// 06-D.1 as the "actual supported host mechanism" for auxiliary assets the config page
/// loads via <c>&lt;script src&gt;</c> / <c>&lt;link href&gt;</c> — PluginPageInfo only serves
/// the single configPage.html. Same pattern as the existing Logo/I18n endpoints, but with an
/// explicit fixed whitelist (per the refactor plan: "never expose arbitrary paths or
/// assembly resources").
/// </summary>
[ApiController]
[Route("MediaDash/Asset")]
public sealed class PluginAssetController : ControllerBase
{
    // Hardcoded name → embedded-resource-suffix map. Only names in this dictionary are
    // servable; a request for anything else 404s. Each 06-D asset that configPage.html
    // consumes via <script src> is listed here; the extracted script lives at the mapped
    // embedded-resource suffix.
    // ponytail: if this dictionary starts approaching dozens of entries, replace with a
    // folder-scan at startup filtered by extension. Not worth the abstraction while <10.
    private static readonly IReadOnlyDictionary<string, AssetDescriptor> Whitelist =
        new Dictionary<string, AssetDescriptor>(System.StringComparer.Ordinal)
        {
            // Shared runtime factories. page-lifetime.js registers createPageLifetime;
            // api-client.js registers createApi(lifetime, ApiClient); dom-helpers.js
            // registers esc() and the STR string dictionary (extraction unblocks
            // per-tab factory files that currently close over both).
            ["bootstrap.js"] = new AssetDescriptor("scripts.bootstrap.js", "application/javascript; charset=utf-8"),
            ["page-controller.js"] = new AssetDescriptor("scripts.page-controller.js", "application/javascript; charset=utf-8"),
            ["overview-tab.js"] = new AssetDescriptor("scripts.tabs.overview-tab.js", "application/javascript; charset=utf-8"),
            ["files-tab.js"] = new AssetDescriptor("scripts.tabs.files-tab.js", "application/javascript; charset=utf-8"),
            ["settings-tab.js"] = new AssetDescriptor("scripts.tabs.settings-tab.js", "application/javascript; charset=utf-8"),
            ["wizard.js"] = new AssetDescriptor("scripts.wizard.js", "application/javascript; charset=utf-8"),
            ["page-lifetime.js"] = new AssetDescriptor("scripts.page-lifetime.js", "application/javascript; charset=utf-8"),
            ["api-client.js"] = new AssetDescriptor("scripts.api-client.js", "application/javascript; charset=utf-8"),
            ["dom-helpers.js"] = new AssetDescriptor("scripts.dom-helpers.js", "application/javascript; charset=utf-8"),
            // Overview library-breakdown chart helpers (LIB_* palettes + pure HTML builders).
            // Extracted 06-FOLLOWUP-2a; consumed via alias block at top of the configPage IIFE.
            ["library-breakdown.js"] = new AssetDescriptor("scripts.library-breakdown.js", "application/javascript; charset=utf-8"),
            // Pure helpers grouped by concern. Nested scripts/tabs/ paths are embedded via
            // the csproj glob and mapped to the "scripts.tabs.foo.js" resource suffix that
            // MSBuild produces for nested folders.
            ["settings-mapper.js"] = new AssetDescriptor("scripts.settings-mapper.js", "application/javascript; charset=utf-8"),
            ["history-tab.js"] = new AssetDescriptor("scripts.tabs.history-tab.js", "application/javascript; charset=utf-8"),
            ["recycle-tab.js"] = new AssetDescriptor("scripts.tabs.recycle-tab.js", "application/javascript; charset=utf-8"),
            ["issues-tab.js"] = new AssetDescriptor("scripts.tabs.issues-tab.js", "application/javascript; charset=utf-8"),
            ["errors-tab.js"] = new AssetDescriptor("scripts.tabs.errors-tab.js", "application/javascript; charset=utf-8"),
            ["diagnostics-tab.js"] = new AssetDescriptor("scripts.tabs.diagnostics-tab.js", "application/javascript; charset=utf-8"),
            // Stylesheets. a11y.css was the smoke test (06-FOLLOWUP-3); jump.css is the
            // first topic-scoped bulk-move slice (06-FOLLOWUP-3-bulk-1, ~152 lines from
            // configPage.html covering the Phase C nav-d jump control).
            ["a11y.css"] = new AssetDescriptor("styles.a11y.css", "text/css; charset=utf-8"),
            ["jump.css"] = new AssetDescriptor("styles.jump.css", "text/css; charset=utf-8"),
            ["tokens.css"] = new AssetDescriptor("styles.tokens.css", "text/css; charset=utf-8"),
            ["layout.css"] = new AssetDescriptor("styles.layout.css", "text/css; charset=utf-8"),
            ["masthead.css"] = new AssetDescriptor("styles.masthead.css", "text/css; charset=utf-8"),
            ["tabs.css"] = new AssetDescriptor("styles.tabs.css", "text/css; charset=utf-8"),
            ["files.css"] = new AssetDescriptor("styles.files.css", "text/css; charset=utf-8"),
            ["recycle.css"] = new AssetDescriptor("styles.recycle.css", "text/css; charset=utf-8"),
            ["errors.css"] = new AssetDescriptor("styles.errors.css", "text/css; charset=utf-8"),
            ["overview.css"] = new AssetDescriptor("styles.overview.css", "text/css; charset=utf-8"),
            ["issues.css"] = new AssetDescriptor("styles.issues.css", "text/css; charset=utf-8"),
            ["settings.css"] = new AssetDescriptor("styles.settings.css", "text/css; charset=utf-8"),
            ["wizard.css"] = new AssetDescriptor("styles.wizard.css", "text/css; charset=utf-8"),
            ["history.css"] = new AssetDescriptor("styles.history.css", "text/css; charset=utf-8")
        };

    /// <summary>Gets the whitelist keys for the manifest assertion test.</summary>
    internal static IReadOnlyCollection<string> WhitelistNames => (IReadOnlyCollection<string>)Whitelist.Keys;

    /// <summary>
    /// Returns the requested asset if it is on the whitelist and physically present in the
    /// plugin assembly. Anonymous because <c>&lt;script src&gt;</c> and <c>&lt;link href&gt;</c>
    /// cannot attach an auth header; the assets themselves are static, contain no user data,
    /// and are shipped inside the plugin DLL — same trust posture as <c>/MediaDash/Logo</c>.
    /// </summary>
    /// <param name="name">The whitelisted asset filename (e.g. <c>bootstrap.js</c>).</param>
    /// <returns>The asset stream with its declared MIME, or 404.</returns>
    [HttpGet("{name}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetAsset(string name)
    {
        if (!Whitelist.TryGetValue(name, out var descriptor))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store";

        var resourceName = string.Format(CultureInfo.InvariantCulture, "Jellyfin.Plugin.MediaDash.Configuration.{0}", descriptor.EmbeddedSuffix);
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream(resourceName);
        return stream is null ? NotFound() : File(stream, descriptor.ContentType);
    }

    /// <summary>Returns a whitelist entry for the manifest assertion test.</summary>
    /// <param name="name">The whitelisted asset filename.</param>
    /// <returns>The descriptor, or null if <paramref name="name"/> is not whitelisted.</returns>
    internal static AssetDescriptor? Lookup(string name) => Whitelist.TryGetValue(name, out var d) ? d : null;

    /// <summary>Pairs an embedded-resource suffix with its declared MIME type.</summary>
    /// <param name="EmbeddedSuffix">Suffix appended to <c>Jellyfin.Plugin.MediaDash.Configuration.</c> to form the manifest resource name.</param>
    /// <param name="ContentType">MIME type returned to the browser.</param>
    internal sealed record AssetDescriptor(string EmbeddedSuffix, string ContentType);
}
