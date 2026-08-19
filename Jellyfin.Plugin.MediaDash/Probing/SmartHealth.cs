namespace Jellyfin.Plugin.MediaDash.Probing;

/// <summary>Coarse SMART health verdict rendered on the Overview drives card.</summary>
public enum SmartHealth
{
    /// <summary>smartctl not available, still probing, or the device could not be resolved.</summary>
    Unknown,

    /// <summary>SMART self-assessment passed and no pre-fail attributes are past threshold.</summary>
    Healthy,

    /// <summary>Self-assessment passed but at least one attribute is flagged — schedule a replacement.</summary>
    Warning,

    /// <summary>SMART self-assessment failed — the drive is predicted to fail; back up now.</summary>
    Failing
}
