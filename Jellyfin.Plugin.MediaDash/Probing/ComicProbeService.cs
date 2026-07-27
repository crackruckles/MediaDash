using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;

namespace Jellyfin.Plugin.MediaDash.Probing;

/// <summary>
/// Container-integrity probes for CBZ (ZIP), CBR (RAR), and CB7 (7z) comic archives.
/// Verifies the archive parses and contains at least one image entry.
/// </summary>
public sealed class ComicProbeService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".avif"
    };

    private readonly MediaDashDb _db;

    /// <summary>Initializes a new instance of the <see cref="ComicProbeService"/> class.</summary>
    /// <param name="db">The MediaDash database.</param>
    public ComicProbeService(MediaDashDb db)
    {
        _db = db;
    }

    /// <summary>Probes a comic file, using the format_probe_cache.</summary>
    /// <param name="path">Full file path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result.</returns>
    public Task<ComicProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (IOException ex)
        {
            return Task.FromResult(new ComicProbeResult(false, "unreadable: " + ex.Message));
        }

        if (!info.Exists)
        {
            return Task.FromResult(new ComicProbeResult(false, "missing"));
        }

        var cached = _db.GetCachedFormatProbe(path, info.Length, info.LastWriteTimeUtc.Ticks);
        if (cached is not null)
        {
            return Task.FromResult(new ComicProbeResult(cached.Value.Ok, cached.Value.Reason));
        }

        var result = Probe(path);
        _db.StoreFormatProbe(path, info.Length, info.LastWriteTimeUtc.Ticks, result.Ok, result.Reason);
        return Task.FromResult(result);
    }

    /// <summary>Uncached probe. Public for unit tests.</summary>
    /// <param name="path">Full file path.</param>
    /// <returns>The result.</returns>
    public static ComicProbeResult Probe(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".cbz" => ProbeCbz(path),
            ".cbr" => ProbeCbr(path),
            ".cb7" => ProbeCb7(path),
            _ => new ComicProbeResult(false, "unsupported extension: " + ext)
        };
    }

    private static ComicProbeResult ProbeCbz(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var hasImage = zip.Entries.Any(e => ImageExtensions.Contains(Path.GetExtension(e.FullName)));
            if (!hasImage)
            {
                return new ComicProbeResult(false, "CBZ archive has no image entries");
            }

            foreach (var entry in zip.Entries)
            {
                _ = entry.CompressedLength;
            }

            return new ComicProbeResult(true, null);
        }
        catch (InvalidDataException ex)
        {
            return new ComicProbeResult(false, "CBZ not a valid ZIP: " + ex.Message);
        }
        catch (IOException ex)
        {
            return new ComicProbeResult(false, "CBZ IO error: " + ex.Message);
        }
    }

    private static ComicProbeResult ProbeCbr(string path)
    {
        try
        {
            using var archive = RarArchive.OpenArchive(path);
            var hasImage = archive.Entries.Any(e => ImageExtensions.Contains(Path.GetExtension(e.Key ?? string.Empty)));
            if (!hasImage)
            {
                return new ComicProbeResult(false, "CBR archive has no image entries");
            }

            return new ComicProbeResult(true, null);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or SharpCompressException)
        {
            return new ComicProbeResult(false, "CBR read error: " + ex.Message);
        }
    }

    private static ComicProbeResult ProbeCb7(string path)
    {
        try
        {
            using var archive = SevenZipArchive.OpenArchive(path);
            var hasImage = archive.Entries.Any(e => ImageExtensions.Contains(Path.GetExtension(e.Key ?? string.Empty)));
            if (!hasImage)
            {
                return new ComicProbeResult(false, "CB7 archive has no image entries");
            }

            return new ComicProbeResult(true, null);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or SharpCompressException)
        {
            return new ComicProbeResult(false, "CB7 read error: " + ex.Message);
        }
    }
}
