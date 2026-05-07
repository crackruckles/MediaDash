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
/// MediaDash plugin — media-processing dashboard for Jellyfin.
/// Provides re-encoding queue, track stripping, duplicate detection,
/// system metrics, and a live stream monitor.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Stable plugin GUID. Must never change after initial publication.
    /// </summary>
    public static readonly Guid PluginGuid = new("4a5c8f2e-1b3d-4e6f-9a2c-7d8e0f1b3c5a");

    /// <summary>
    /// Gets the current plugin instance. Set on construction by Jellyfin's DI container.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "MediaDash";

    /// <inheritdoc />
    public override Guid Id => PluginGuid;

    /// <inheritdoc />
    public override string Description =>
        "Media processing dashboard: re-encode queue, track stripping, " +
        "duplicate detection, live stream monitor, and system metrics.";

    /// <summary>
    /// Initialises the plugin and registers the singleton instance.
    /// </summary>
    /// <param name="applicationPaths">Instance of <see cref="IApplicationPaths"/>.</param>
    /// <param name="xmlSerializer">Instance of <see cref="IXmlSerializer"/>.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace
            ?? throw new InvalidOperationException("Plugin namespace must not be null.");

        // Settings page — Name must match plugin Name so Jellyfin shows the
        // Settings button on the plugin card in the admin dashboard.
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Web.config.html",
                ns),
        };

        // Main dashboard page.
        // Accessible at /web/#/configurationpage?name=MediaDashDashboard
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
