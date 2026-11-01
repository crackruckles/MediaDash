namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Free/used bytes for one drive that holds a library folder.
/// </summary>
public sealed class DriveUsage
{
    /// <summary>Gets or sets the drive's root path (e.g., "C:\\" or "/mnt/media").</summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>Gets or sets the free bytes on the drive.</summary>
    public long FreeBytes { get; set; }

    /// <summary>Gets or sets the total bytes on the drive.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Gets or sets a value indicating whether one or more configured library folders live on this drive.</summary>
    public bool IsLibraryDrive { get; set; }

    /// <summary>Gets or sets a value indicating whether the recycle bin currently sits on this drive.
    /// Present so the Overview always surfaces the bin's drive even when it isn't a library drive — the
    /// bin can fill it up unnoticed otherwise.</summary>
    public bool IsRecycleBinDrive { get; set; }

    /// <summary>Gets or sets a value indicating whether the plugin-data directory (SQLite DB + caches)
    /// currently sits on this drive. Force-surfaced on Overview so users on small system drives see
    /// the DB volume filling up before it starves Jellyfin.</summary>
    public bool IsPluginDataDrive { get; set; }

    /// <summary>Gets or sets a value indicating whether this entry is a network share addressed by UNC
    /// path (e.g. <c>\\nas\media</c>) rather than a local drive. Shares have no SMART data, so the
    /// Overview shows a "network share" badge for them instead of the SMART pill.</summary>
    public bool IsNetworkShare { get; set; }

    /// <summary>Gets or sets a plain-language low-space warning for this drive
    /// (e.g. "Drive is 92 % full — free some space before fixes stall"), or null when the drive is
    /// below its configured threshold or the user dismissed the banner client-side. Advisory only;
    /// never blocks fixes.</summary>
    public string? LowSpaceWarning { get; set; }

    /// <summary>Gets or sets the SMART health bucket for this drive: <c>"healthy"</c>, <c>"warning"</c>,
    /// <c>"failing"</c>, or <c>"unknown"</c>. Populated only for library and recycle-bin drives.</summary>
    public string SmartHealth { get; set; } = "unknown";

    /// <summary>Gets or sets a short human-readable message explaining the SMART verdict — used as the
    /// pill's hover tooltip on the Overview (e.g., <c>"SMART self-assessment PASSED on C:"</c>).</summary>
    public string SmartMessage { get; set; } = string.Empty;

    /// <summary>Gets or sets the drive's friendly model name when the SMART source reports it.</summary>
    public string? SmartModel { get; set; }

    /// <summary>Gets or sets the current drive temperature in Celsius, when reported.</summary>
    public int? SmartTemperatureCelsius { get; set; }

    /// <summary>Gets or sets the maximum recorded drive temperature in Celsius, when reported.</summary>
    public int? SmartTemperatureMaxCelsius { get; set; }

    /// <summary>Gets or sets the wear percentage (0 = new, 100 = end-of-life) for SSDs, when reported.</summary>
    public int? SmartWearPercent { get; set; }

    /// <summary>Gets or sets the total powered-on hours reported by SMART, when available.</summary>
    public long? SmartPowerOnHours { get; set; }

    /// <summary>Gets or sets the count of uncorrected read errors reported by SMART, when available.</summary>
    public long? SmartReadErrorsUncorrected { get; set; }

    /// <summary>Gets or sets the count of uncorrected write errors reported by SMART, when available.</summary>
    public long? SmartWriteErrorsUncorrected { get; set; }
}
