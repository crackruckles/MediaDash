using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Compat;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Detects local artwork files (poster / backdrop / thumb / logo) that are zero-byte,
/// truncated, or fail to decode. Only inspects files inside Jellyfin's own metadata folder;
/// user-placed artwork alongside the media file is never touched.
/// </summary>
public sealed class ArtworkScanner : IScanner
{
    private readonly IServerApplicationPaths _applicationPaths;
    private readonly ILogger<ArtworkScanner> _logger;
    private readonly ProtectionService? _protection;

    /// <summary>Initializes a new instance of the <see cref="ArtworkScanner"/> class.</summary>
    /// <param name="applicationPaths">Jellyfin server application paths (anchors the metadata-folder guard).</param>
    /// <param name="protection">Rules excluding protected paths from analysis.</param>
    /// <param name="logger">The logger.</param>
    public ArtworkScanner(IServerApplicationPaths applicationPaths, ILogger<ArtworkScanner> logger, ProtectionService? protection = null)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
        _protection = protection;
    }

    /// <inheritdoc />
    public IssueType Type => IssueType.CorruptArtwork;

    /// <inheritdoc />
    public bool AlwaysUnscoped => true;

    /// <inheritdoc />
    public Task<IReadOnlyList<Issue>> ScanAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var issues = new List<Issue>();
        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[i];
            var images = item.ImageInfos;
            if (images is null)
            {
                continue;
            }

            foreach (var image in images)
            {
                // Skip nulls, blank paths, and remote URLs — only local files can be corrupt on disk.
                // ponytail: IsLocalFile doesn't exist on ItemImageInfo in 10.11.8; URL check covers the same intent.
                if (image is null || string.IsNullOrEmpty(image.Path)
                    || image.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Only touch Jellyfin-managed artwork — anchored to the actual InternalMetadataPath prefix.
                // A user library folder named "metadata" or "metadata-evil" is NOT a match; the shared
                // LibraryGuard.IsUnder helper enforces the separator boundary that a raw StartsWith misses.
                if (!Fixers.LibraryGuard.IsUnder(Path.GetFullPath(image.Path), _applicationPaths.InternalMetadataPath))
                {
                    continue;
                }

                if (_protection?.IsScannerBlocked(image.Path, Type) == true)
                {
                    continue;
                }

                // ItemImageInfo has no Length field on 10.11.8; pass null — EvaluateFile derives length from disk.
                var reason = EvaluateFile(image.Path, expectedLength: null);
                if (reason is null)
                {
                    continue;
                }

                issues.Add(new Issue
                {
                    Type = Type,
                    ItemId = item.Id,
                    Path = image.Path,
                    Status = IssueStatus.Detected,
                    DetectedAtUtc = DateTime.UtcNow,
                    SizeSavings = 0,
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        reason,
                        imageType = image.Type.ToString()
                    }),
                    SuggestedFix = "Artwork is unreadable. Approve to delete this file and let Jellyfin re-fetch it."
                });
            }

            progress.Report((i + 1) * 100.0 / items.Count);
        }

        progress.Report(100);
        return Task.FromResult<IReadOnlyList<Issue>>(issues);
    }

    /// <summary>
    /// Evaluates a single artwork file. Returns null when the file is fine; a human-readable
    /// reason when it is corrupt. Extracted for direct unit-testing without Jellyfin DI.
    /// </summary>
    /// <param name="path">Full path to the artwork file.</param>
    /// <param name="expectedLength">Length recorded in Jellyfin's ImageInfo, or null when unknown.</param>
    /// <returns>Null when the file is fine, else a reason string.</returns>
    internal static string? EvaluateFile(string path, long? expectedLength)
    {
        try
        {
            return EvaluateFileCore(path, expectedLength);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? EvaluateFileCore(string path, long? expectedLength)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (IOException)
        {
            return null;
        }

        if (!info.Exists)
        {
            return "missing";
        }

        if (info.Length == 0)
        {
            try
            {
                using var readable = MediaFileHelper.OpenSharedRead(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            return "empty file";
        }

        if (expectedLength is long expected && expected != info.Length)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "size mismatch: expected {0} bytes, file is {1}",
                expected,
                info.Length);
        }

        FileStream stream;
        try
        {
            stream = MediaFileHelper.OpenSharedRead(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // #88: a file held exclusively by another process (sharing violation) or an ACL
            // denial is a transient/environment problem, not proof of on-disk corruption.
            // Returning null leaves the file alone until a later scan can read it cleanly.
            return null;
        }

        using (stream)
        {
            // #88: only raster formats are validated by the reflected SKBitmap raster decoder.
            // SVG, HEIC, AVIF and anything else without a known raster magic header are not
            // corruption signals from this scanner — refuse to flag. Prevents the reporter's
            // valid SVG from being classified corrupt and the fixer deleting it.
            if (!IsKnownRasterSignature(stream))
            {
                return null;
            }

            stream.Position = 0;

            var bridge = SkiaSharpBridge.Instance;
            if (!bridge.IsAvailable)
            {
                return null;
            }

            try
            {
                var result = bridge.Decode(stream);
                if (!result.Ok)
                {
                    return result.Reason is null
                        ? null
                        : "decode error: " + result.Reason;
                }

                if (result.Width <= 0 || result.Height <= 0)
                {
                    return "decode produced zero-dimension bitmap";
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Peeks the first bytes of the file and returns true only when they match a raster image
    /// signature the SkiaSharp bridge actually validates (PNG / JPEG / GIF / BMP / WEBP / TIFF).
    /// Vector or unknown formats return false — the scanner then leaves them alone instead of
    /// treating a decoder refusal as proven corruption. Internal for direct unit testing.
    /// </summary>
    /// <param name="stream">Open stream positioned at the start of the file.</param>
    /// <returns>True when the leading bytes match a validated raster format.</returns>
    internal static bool IsKnownRasterSignature(Stream stream)
    {
        Span<byte> head = stackalloc byte[12];
        var read = stream.Read(head);
        if (read < 3)
        {
            return false;
        }

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (read >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
            && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A)
        {
            return true;
        }

        // JPEG: FF D8 FF
        if (head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
        {
            return true;
        }

        // GIF: "GIF87a" / "GIF89a"
        if (read >= 6 && head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x38)
        {
            return true;
        }

        // BMP: "BM"
        if (head[0] == 0x42 && head[1] == 0x4D)
        {
            return true;
        }

        // TIFF: "II*\0" or "MM\0*"
        if (read >= 4
            && ((head[0] == 0x49 && head[1] == 0x49 && head[2] == 0x2A && head[3] == 0x00)
                || (head[0] == 0x4D && head[1] == 0x4D && head[2] == 0x00 && head[3] == 0x2A)))
        {
            return true;
        }

        // WEBP: "RIFF" .... "WEBP"
        if (read >= 12 && head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46
            && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50)
        {
            return true;
        }

        return false;
    }
}
