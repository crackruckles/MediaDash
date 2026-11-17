using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Probing;

/// <summary>
/// Streams a SHA-256 over a file and caches the result by (path, size, mtime) — same eviction
/// contract as <see cref="FfprobeService.ProbeAsync"/>. Feeds the Tier-0 (byte-identical) branch
/// of the duplicate confidence ladder.
///
/// The caller is expected to gate on file-size equality before invoking this — different sizes
/// cannot be byte-identical, so hashing them wastes disk IO. Within a metadata group of 2–5
/// candidates that pre-filter keeps the cost bounded even on very large libraries.
/// </summary>
public sealed class FileHasher
{
    // 1 MiB read chunks. Larger buffers stop paying off past this on typical NVMe/SATA;
    // smaller ones burn CPU on IO overhead.
    private const int BufferSize = 1024 * 1024;

    private readonly ProbeCacheStore _probes;
    private readonly IPluginConfigurationProvider _configProvider;
    private readonly ILogger<FileHasher> _logger;

    /// <summary>Initializes a new instance of the <see cref="FileHasher"/> class.</summary>
    /// <param name="probes">Probe-cache store (05-C.4) — backs the (path, size, mtime) hash cache.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configProvider">Configuration snapshot provider.</param>
    public FileHasher(ProbeCacheStore probes, ILogger<FileHasher> logger, IPluginConfigurationProvider configProvider)
    {
        _probes = probes;
        _logger = logger;
        _configProvider = configProvider;
    }

    /// <summary>
    /// Returns a lowercase hex SHA-256 of the file at <paramref name="path"/>, using the cache
    /// when the file hasn't changed since last hash. Returns null on IO error or when the file
    /// no longer exists (caller treats null as "cannot compare byte-identical").
    /// </summary>
    /// <param name="path">Full file path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Hash string, or null when unhashable.</returns>
    public async Task<string?> HashAsync(string path, CancellationToken cancellationToken)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogDebug(ex, "FileHasher: could not stat {Path}", path);
            return null;
        }

        var size = info.Length;
        var mtime = info.LastWriteTimeUtc.Ticks;

        try
        {
            var cached = _probes.GetCachedHash(path, size, mtime);
            if (cached is not null)
            {
                return cached;
            }
        }
        catch (Exception ex)
        {
            // Cache lookup failing is not fatal — fall through and re-compute.
            _logger.LogDebug(ex, "FileHasher: cache lookup failed for {Path}", path);
        }

        string hash;
        try
        {
            // FileShare.ReadWrite | FileShare.Delete so a hasher mid-scan can't block a fixer that
            // wants to move/rename/delete this file — that combination surfaced as ERROR_SHARING_VIOLATION
            // on ~13 files per scan for a Windows user (no external holders, our own hasher was the cause).
            var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous
            });
            await using (stream.ConfigureAwait(false))
            {
                // #72: LowSystemImpactMode throttles the hash loop. SHA256.HashDataAsync is a
                // one-shot read that saturates the disk — on a 50 GB 4K master that pins the drive
                // for the whole hash. Switching to an incremental read lets us yield briefly between
                // 1 MiB chunks when the user has opted into low-impact mode, capping throughput at
                // roughly 100 MB/s per file and leaving headroom for Jellyfin / playback / the OS.
                var lowImpact = _configProvider.GetSnapshot().LowSystemImpactMode;
                using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    incremental.AppendData(buffer, 0, read);
                    if (lowImpact)
                    {
                        await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                    }
                }

                hash = Convert.ToHexString(incremental.GetCurrentHash()).ToLowerInvariant();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "FileHasher: hash failed for {Path}", path);
            return null;
        }

        try
        {
            info.Refresh();
            if (!info.Exists || info.Length != size || info.LastWriteTimeUtc.Ticks != mtime)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "FileHasher: post-hash stat failed for {Path}", path);
            return null;
        }

        try
        {
            _probes.StoreHash(path, size, mtime, hash);
        }
        catch (Exception ex)
        {
            // Persisting to cache is best-effort — the hash we computed is still returned so the
            // caller can compare. A single cache miss next run is the only downside.
            _logger.LogDebug(ex, "FileHasher: cache store failed for {Path}", path);
        }

        return hash;
    }
}
