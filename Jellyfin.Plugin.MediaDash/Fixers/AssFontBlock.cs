namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Single embedded font block inside an <c>.ass</c> subtitle's <c>[Fonts]</c> section: the filename
/// declared on the <c>fontname:</c> line and the approximate on-disk cost of the whole block
/// (declaration line + UUEncoded payload).
/// </summary>
/// <param name="Filename">The value after <c>fontname:</c>.</param>
/// <param name="BytesEstimate">Rough reclaim size in bytes if this block is dropped.</param>
public sealed record AssFontBlock(string Filename, long BytesEstimate);
