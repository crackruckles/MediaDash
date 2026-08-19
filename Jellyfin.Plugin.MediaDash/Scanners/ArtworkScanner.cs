using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Compat;
using Jellyfin.Plugin.MediaDash.Data;
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

    /// <summary>Initializes a new instance of the <see cref="ArtworkScanner"/> class.</summary>
    /// <param name="applicationPaths">Jellyfin server application paths (anchors the metadata-folder guard).</param>
    /// <param name="logger">The logger.</param>
    public ArtworkScanner(IServerApplicationPaths applicationPaths, ILogger<ArtworkScanner> logger)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public IssueType Type => IssueType.CorruptArtwork;

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
        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (IOException)
        {
            return "unreadable";
        }

        if (!info.Exists)
        {
            return "missing";
        }

        if (info.Length == 0)
        {
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

        try
        {
            using var stream = File.OpenRead(path);
            var bridge = SkiaSharpBridge.Instance;
            if (!bridge.IsAvailable)
            {
                // Host has no SkiaSharp loaded — treat as unverifiable, don't flag. Zero-byte
                // and size-mismatch checks above still catch the common corruption cases.
                return null;
            }

            var result = bridge.Decode(stream);
            if (!result.Ok)
            {
                return result.Reason is null
                    ? "decode failed"
                    : "decode error: " + result.Reason;
            }

            if (result.Width <= 0 || result.Height <= 0)
            {
                return "decode produced zero-dimension bitmap";
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "decode error: " + ex.Message;
        }

        return null;
    }
}
