using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;

namespace Jellyfin.Plugin.MediaDash.Probing;

/// <summary>
/// Container-integrity probes for EPUB, PDF, MOBI, and AZW/AZW3.
/// Verifies structural correctness only — never decrypts or converts.
/// </summary>
public sealed class BookProbeService
{
    private readonly MediaDashDb _db;

    /// <summary>Initializes a new instance of the <see cref="BookProbeService"/> class.</summary>
    /// <param name="db">The MediaDash database (used for caching probe results).</param>
    public BookProbeService(MediaDashDb db)
    {
        _db = db;
    }

    /// <summary>Probes a book file, using the cache.</summary>
    /// <param name="path">Full file path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result.</returns>
    public Task<BookProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (IOException ex)
        {
            return Task.FromResult(new BookProbeResult(false, "unreadable: " + ex.Message));
        }

        if (!info.Exists)
        {
            return Task.FromResult(new BookProbeResult(false, "missing"));
        }

        var cached = _db.GetCachedFormatProbe(path, info.Length, info.LastWriteTimeUtc.Ticks);
        if (cached is not null)
        {
            return Task.FromResult(new BookProbeResult(cached.Value.Ok, cached.Value.Reason));
        }

        var result = Probe(path);
        _db.StoreFormatProbe(path, info.Length, info.LastWriteTimeUtc.Ticks, result.Ok, result.Reason);
        return Task.FromResult(result);
    }

    /// <summary>Uncached probe dispatched on file extension. Public for unit tests.</summary>
    /// <param name="path">Full file path.</param>
    /// <returns>The result.</returns>
    public static BookProbeResult Probe(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".epub" => ProbeEpub(path),
            ".pdf" => ProbePdf(path),
            ".mobi" or ".azw" or ".azw3" => ProbeMobiFamily(path),
            _ => new BookProbeResult(false, "unsupported extension: " + ext)
        };
    }

    private static BookProbeResult ProbeEpub(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var mimetype = zip.GetEntry("mimetype");
            if (mimetype is null)
            {
                return new BookProbeResult(false, "EPUB missing mimetype entry");
            }

            using var stream = mimetype.Open();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            var contents = reader.ReadToEnd().Trim();
            if (contents != "application/epub+zip")
            {
                return new BookProbeResult(false, "EPUB mimetype content wrong: '" + contents + "'");
            }

            foreach (var entry in zip.Entries)
            {
                _ = entry.CompressedLength;
            }

            return new BookProbeResult(true, null);
        }
        catch (InvalidDataException ex)
        {
            return new BookProbeResult(false, "EPUB not a valid ZIP: " + ex.Message);
        }
        catch (IOException ex)
        {
            return new BookProbeResult(false, "EPUB IO error: " + ex.Message);
        }
    }

    private static BookProbeResult ProbePdf(string path)
    {
        try
        {
            using var fs = Scanners.MediaFileHelper.OpenSharedRead(path);
            if (fs.Length < 8)
            {
                return new BookProbeResult(false, "PDF too small");
            }

            Span<byte> header = stackalloc byte[8];
            fs.ReadExactly(header);
            if (!(header[0] == 0x25 && header[1] == 0x50 && header[2] == 0x44 && header[3] == 0x46 && header[4] == 0x2D))
            {
                return new BookProbeResult(false, "PDF missing %PDF- header");
            }

            var tailLength = (int)Math.Min(1024, fs.Length);
            fs.Seek(-tailLength, SeekOrigin.End);
            Span<byte> tail = stackalloc byte[tailLength];
            fs.ReadExactly(tail);
            var tailText = Encoding.ASCII.GetString(tail);
            if (!tailText.Contains("%%EOF", StringComparison.Ordinal))
            {
                return new BookProbeResult(false, "PDF missing %%EOF trailer");
            }

            if (!tailText.Contains("startxref", StringComparison.Ordinal))
            {
                return new BookProbeResult(false, "PDF missing startxref");
            }

            return new BookProbeResult(true, null);
        }
        catch (IOException ex)
        {
            return new BookProbeResult(false, "PDF IO error: " + ex.Message);
        }
    }

    private static BookProbeResult ProbeMobiFamily(string path)
    {
        try
        {
            using var fs = Scanners.MediaFileHelper.OpenSharedRead(path);
            if (fs.Length < 78)
            {
                return new BookProbeResult(false, "MOBI too small for PalmDoc header");
            }

            fs.Seek(60, SeekOrigin.Begin);
            Span<byte> typeBytes = stackalloc byte[8];
            fs.ReadExactly(typeBytes);
            var type = Encoding.ASCII.GetString(typeBytes);
            if (type != "BOOKMOBI" && type != "TPZ ")
            {
                return new BookProbeResult(false, "MOBI type magic not recognised: '" + type + "'");
            }

            return new BookProbeResult(true, null);
        }
        catch (IOException ex)
        {
            return new BookProbeResult(false, "MOBI IO error: " + ex.Message);
        }
    }
}
