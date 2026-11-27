namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>Average rates for one disk between polls.</summary>
/// <param name="Name">Operating-system disk identifier.</param>
/// <param name="ReadBytesPerSecond">Read rate, if available.</param>
/// <param name="WriteBytesPerSecond">Write rate, if available.</param>
public sealed record DiskRates(string Name, double? ReadBytesPerSecond, double? WriteBytesPerSecond);
