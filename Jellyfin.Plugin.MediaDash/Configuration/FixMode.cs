namespace Jellyfin.Plugin.MediaDash.Configuration;

/// <summary>
/// How a fix type is executed.
/// </summary>
public enum FixMode
{
    /// <summary>The scanner does not run for this fix type.</summary>
    Off = 0,

    /// <summary>Issues are detected and shown, but never fixed.</summary>
    DetectOnly = 1,

    /// <summary>Issues wait for explicit approval before the fix task touches them.</summary>
    ManualApprove = 2,

    /// <summary>Issues are fixed automatically on the next fix run.</summary>
    Automatic = 3
}
