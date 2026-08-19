using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Per-library aggregate: total item count, on-disk bytes, and breakdown maps for resolution buckets,
/// video codecs, and container extensions. Consumed by the Overview tab's library-breakdown charts.
/// </summary>
public sealed class LibraryStat
{
    /// <summary>Gets or sets the library's Jellyfin GUID (as an N-format string).</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the library's user-visible name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the library's collection type (movies, tvshows, music, ...), lowercased.</summary>
    public string? CollectionType { get; set; }

    /// <summary>Gets or sets the number of video items in this library.</summary>
    public int ItemCount { get; set; }

    /// <summary>Gets or sets the total on-disk bytes across all video files in this library.</summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Gets the resolution-bucket counts. Keys are <c>4K</c>, <c>1080p</c>, <c>720p</c>, <c>SD</c>,
    /// <c>Unknown</c>. Ordering isn't guaranteed by the JSON serializer — the UI walks a fixed key list.
    /// </summary>
    public Dictionary<string, int> Resolutions { get; init; } = new();

    /// <summary>
    /// Gets the video-codec-bucket counts. Keys are lowercase codec names (<c>h264</c>, <c>hevc</c>,
    /// <c>av1</c>, ...) or <c>unknown</c> when the primary stream's codec isn't cached.
    /// </summary>
    public Dictionary<string, int> Codecs { get; init; } = new();

    /// <summary>
    /// Gets the container-extension counts. Keys are lowercase extensions without the leading dot
    /// (<c>mkv</c>, <c>mp4</c>, ...) or <c>other</c> when the file has an uncommon extension.
    /// </summary>
    public Dictionary<string, int> Containers { get; init; } = new();
}
