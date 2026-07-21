namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// One physical GPU detected on the host.
/// </summary>
public sealed class GpuInfo
{
    /// <summary>Gets or sets the zero-based GPU index. Matches nvidia-smi index / DRM card index / Windows PDH phys id.</summary>
    public int Index { get; set; }

    /// <summary>Gets or sets a human-readable name ("NVIDIA GeForce RTX 4090", "AMD Radeon 780M"), or a fallback like "GPU 0" when the driver doesn't expose one.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the utilization percentage, 0-100.</summary>
    public double Percent { get; set; }

    /// <summary>Gets or sets a short label identifying where the number came from ("NVIDIA", "Windows PDH", "Linux sysfs").</summary>
    public string Source { get; set; } = string.Empty;
}
