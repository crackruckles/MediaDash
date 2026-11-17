namespace Jellyfin.Plugin.MediaDash.Configuration;

/// <summary>
/// How a fix type is executed.
/// </summary>
public enum FixMode
{
    /// <summary>The scanner does not run for this fix type.</summary>
    Off = 0,

    /// <summary>
    /// XML tombstone — pre-1.0.9 configs stored this value as the default for every fixer.
    /// Removing the enum member would make BasePlugin's XmlSerializer throw on load and reset
    /// every user setting (library selection, disposal choices, quality ceilings) to defaults.
    /// Treated as <see cref="ManualApprove"/> everywhere at runtime; UI never offers it.
    /// Plugin ctor's NormaliseFixModes rewrites it to ManualApprove and persists so the next
    /// save drops the tombstone from the user's XML.
    /// ponytail: enum slot kept purely for XML back-compat; delete after a release cycle where
    /// NormaliseFixModes has run on every active install.
    /// </summary>
    // XmlSerializer ignores Obsolete enum members, so this tombstone must remain serializable.
    DetectOnly = 1,

    /// <summary>Issues wait for explicit approval before the fix task touches them.</summary>
    ManualApprove = 2,

    /// <summary>Issues are fixed automatically on the next fix run.</summary>
    Automatic = 3
}
