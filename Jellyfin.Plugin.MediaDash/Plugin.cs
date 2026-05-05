using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.MediaDash.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MediaDash;

/// <summary>
/// MediaDash — a Jellyfin plugin that provides a media-processing
/// dashboard (re-encoding queue, track stripping, duplicate detection,
/// system metrics, live stream view).
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Stable GUID — must never change once published.</summary>
    public static readonly Guid StaticId = new("4a5c8f2e-1b3d-4e6f-9a2c-7d8e0f1b3c5a");

    /// <inheritdoc />
    public override string Name => "MediaDash";

    /// <inheritdoc />
    public override Guid Id => StaticId;

    /// <inheritdoc />
    public override string Description =>
        "Media processing dashboard: re-encode queue, track stripping, " +
        "duplicate detection, live stream monitor and system metrics.";

    /// <summary>Singleton accessor used by controllers.</summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>Initialises the plugin.</summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;

        // Config page — Name must match plugin Name exactly so Jellyfin
        // shows the Settings button on the plugin card.
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Web.config.html",
                ns),
        };

        // Dashboard page — accessible at /web/configurationpage?name=MediaDashDashboard
        yield return new PluginPageInfo
        {
            Name = "MediaDashDashboard",
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Web.dashboard.html",
                ns),
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "bar_chart",
            DisplayName = "MediaDash",
        };
    }
}
