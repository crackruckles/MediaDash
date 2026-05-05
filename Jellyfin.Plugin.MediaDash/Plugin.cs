using System;
using System.Collections.Generic;
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
        // The dashboard HTML is embedded as a resource in the DLL.
        // Jellyfin serves it at /web/configurationpage?name=mediadash-dashboard
        yield return new PluginPageInfo
        {
            Name = "mediadash-dashboard",
            EmbeddedResourcePath =
                $"{GetType().Namespace}.Web.dashboard.html",
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "bar_chart",
            DisplayName = "MediaDash",
        };

        // Configuration page served at Plugins → MediaDash → Settings
        yield return new PluginPageInfo
        {
            Name = "mediadash-config",
            EmbeddedResourcePath =
                $"{GetType().Namespace}.Web.config.html",
        };
    }
}
