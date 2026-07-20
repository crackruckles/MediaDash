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
/// The MediaDash plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "MediaDash";

    /// <inheritdoc />
    public override string Description => "Keeps your library lean and playable: finds duplicate copies, broken files, oversized encodes and unwanted language tracks, then fixes them safely. Dry-run mode and a recycle bin protect your media by default.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("38bdb090-b763-4294-934b-b54ade4d9d6d");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets or sets the path of the file the currently-running scan or fix is working on, or null when idle.
    /// Read by the /Status endpoint so the dashboard can show what's happening under the progress bar.
    /// Never a load-bearing field — best-effort human readout only.
    /// </summary>
    public static string? CurrentActivity { get; set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
                EnableInMainMenu = true,
                DisplayName = "MediaDash",
                MenuIcon = "dashboard"
            }
        ];
    }
}
