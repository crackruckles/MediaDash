using System;
using System.IO;
using Jellyfin.Plugin.MediaDash.Fixers;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// Owns probe-cache tables including <c>probe_cache</c> (ffprobe JSON), <c>file_hashes</c>
/// (Duplicate scanner's Tier-0 SHA-256), <c>format_probe_cache</c> (book/comic container
/// validation), <c>decode_cache</c> (Playability decode results), and <c>audio_decode_cache</c> (successful multi-track audio checks). Extracted
/// from <see cref="MediaDashDb"/> in 05-C.4; the facade retains one-line forwarders so pre-existing
/// callers (tests + un-migrated call sites) keep working while consumers (FfprobeService,
/// FileHasher, BookProbeService, ComicProbeService) are pointed at the narrower type.
/// </summary>
/// <remarks>
/// Every method is one connection, one statement — no cross-store atomicity concerns here. All
/// caches are pure convenience caches: the plugin re-computes the underlying result if the
/// cached row is missing, so a lost entry is a performance issue, never a correctness issue.
/// </remarks>
public sealed class ProbeCacheStore
{
    private readonly MediaDashConnectionFactory _connectionFactory;

    /// <summary>Initializes a new instance of the <see cref="ProbeCacheStore"/> class.</summary>
    /// <param name="connectionFactory">Shared factory; every 05-C store family uses the same instance.</param>
    public ProbeCacheStore(MediaDashConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    internal static void InvalidateMovedPaths(SqliteConnection connection, SqliteTransaction transaction, MediaMovePlan plan)
    {
        var collation = OperatingSystem.IsWindows() ? "NOCASE" : "BINARY";
        foreach (var table in new[] { "probe_cache", "file_hashes", "format_probe_cache", "decode_cache", "audio_decode_cache" })
        {
            foreach (var entry in plan.Entries)
            {
                foreach (var path in new[] { entry.Source, entry.Destination, plan.CatalogPath })
                {
                    if (string.IsNullOrEmpty(path))
                    {
                        continue;
                    }

                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    // Identifiers are a fixed internal whitelist; all paths are bound values.
#pragma warning disable CA2100 // Both identifiers are fixed internal whitelists. Every path is bound as a value.
                    command.CommandText = "DELETE FROM " + table + " WHERE path=@path COLLATE " + collation;
                    command.Parameters.AddWithValue("@path", path);
                    if (entry.IsDirectory)
                    {
                        var prefix = Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
                        var upper = prefix[..^1] + (char)(prefix[^1] + 1);
                        command.CommandText += " OR (path>=@prefix COLLATE " + collation + " AND path<@upper COLLATE " + collation + ")";
                        command.Parameters.AddWithValue("@prefix", prefix);
                        command.Parameters.AddWithValue("@upper", upper);
                    }
#pragma warning restore CA2100

                    command.ExecuteNonQuery();
                }
            }
        }
    }

    internal bool HasCleanAudioDecode(string path, long size, long mtime, string signature)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM audio_decode_cache WHERE path=@path AND size=@size AND mtime_utc=@mtime AND signature=@signature";
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@size", size);
        cmd.Parameters.AddWithValue("@mtime", mtime);
        cmd.Parameters.AddWithValue("@signature", signature);
        return cmd.ExecuteScalar() is not null;
    }

    internal void StoreCleanAudioDecode(string path, long size, long mtime, string signature)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO audio_decode_cache (path, size, mtime_utc, signature)
            VALUES (@path, @size, @mtime, @signature)
            ON CONFLICT(path) DO UPDATE SET size=@size, mtime_utc=@mtime, signature=@signature
            """;
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@size", size);
        cmd.Parameters.AddWithValue("@mtime", mtime);
        cmd.Parameters.AddWithValue("@signature", signature);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Looks up a cached ffprobe result that is still valid for the file's current size and modification time.</summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">Current file size in bytes.</param>
    /// <param name="mtimeUtcTicks">Current last-write time in UTC ticks.</param>
    /// <returns>The cached ffprobe JSON, or null when absent or stale.</returns>
    public string? GetCachedProbe(string path, long size, long mtimeUtcTicks)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT json FROM probe_cache WHERE path = @path AND size = @size AND mtime_utc = @mtime";
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@size", size);
        cmd.Parameters.AddWithValue("@mtime", mtimeUtcTicks);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Stores an ffprobe result in the cache, replacing any previous entry for the path.</summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">File size in bytes at probe time.</param>
    /// <param name="mtimeUtcTicks">Last-write time in UTC ticks at probe time.</param>
    /// <param name="json">Raw ffprobe JSON output.</param>
    public void StoreProbe(string path, long size, long mtimeUtcTicks, string json)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO probe_cache (path, size, mtime_utc, probed_at_utc, json)
            VALUES (@path, @size, @mtime, @probedAt, @json)
            ON CONFLICT(path) DO UPDATE SET size = @size, mtime_utc = @mtime, probed_at_utc = @probedAt, json = @json
            """;
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@size", size);
        cmd.Parameters.AddWithValue("@mtime", mtimeUtcTicks);
        cmd.Parameters.AddWithValue("@probedAt", DateTime.UtcNow.Ticks);
        cmd.Parameters.AddWithValue("@json", json);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Looks up a cached full-file SHA-256 hash that is still valid for the file's current size
    /// and mtime. Feeds the Tier-0 (byte-identical) branch of the duplicate confidence ladder.
    /// </summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">Current file size in bytes.</param>
    /// <param name="mtimeUtcTicks">Current last-write time in UTC ticks.</param>
    /// <returns>The cached lowercase hex hash, or null when absent or stale.</returns>
    public string? GetCachedHash(string path, long size, long mtimeUtcTicks)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT hash FROM file_hashes WHERE path = @path AND size = @size AND mtime = @mtime";
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@size", size);
        cmd.Parameters.AddWithValue("@mtime", mtimeUtcTicks);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Stores a computed hash, replacing any previous entry for the same (path, size, mtime).</summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">File size at hash time.</param>
    /// <param name="mtimeUtcTicks">Last-write time at hash time.</param>
    /// <param name="hash">Lowercase hex SHA-256.</param>
    public void StoreHash(string path, long size, long mtimeUtcTicks, string hash)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO file_hashes (path, size, mtime, hash)
            VALUES (@path, @size, @mtime, @hash)
            ON CONFLICT(path, size, mtime) DO UPDATE SET hash = @hash
            """;
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@size", size);
        cmd.Parameters.AddWithValue("@mtime", mtimeUtcTicks);
        cmd.Parameters.AddWithValue("@hash", hash);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Looks up a cached format-probe result that is still valid for the file's current size and mtime.</summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">Current file size in bytes.</param>
    /// <param name="mtimeUtcTicks">Current last-write time in UTC ticks.</param>
    /// <returns>The cached result, or null when absent or stale.</returns>
    public FormatProbeResult? GetCachedFormatProbe(string path, long size, long mtimeUtcTicks)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ok, reason FROM format_probe_cache WHERE path = @path AND size = @size AND mtime_utc = @mtime";
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@size", size);
        cmd.Parameters.AddWithValue("@mtime", mtimeUtcTicks);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var ok = reader.GetInt32(0) != 0;
        var reason = reader.IsDBNull(1) ? null : reader.GetString(1);
        return new FormatProbeResult(ok, reason);
    }

    /// <summary>Stores a format-probe result, replacing any previous entry for the path.</summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">File size in bytes at probe time.</param>
    /// <param name="mtimeUtcTicks">Last-write time in UTC ticks at probe time.</param>
    /// <param name="ok">True when the file's container parsed cleanly.</param>
    /// <param name="reason">Human-readable failure reason when <paramref name="ok"/> is false, else null.</param>
    public void StoreFormatProbe(string path, long size, long mtimeUtcTicks, bool ok, string? reason)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO format_probe_cache (path, size, mtime_utc, probed_at_utc, ok, reason)
            VALUES (@path, @size, @mtime, @probedAt, @ok, @reason)
            ON CONFLICT(path) DO UPDATE SET size = @size, mtime_utc = @mtime, probed_at_utc = @probedAt, ok = @ok, reason = @reason
            """;
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@size", size);
        cmd.Parameters.AddWithValue("@mtime", mtimeUtcTicks);
        cmd.Parameters.AddWithValue("@probedAt", DateTime.UtcNow.Ticks);
        cmd.Parameters.AddWithValue("@ok", ok ? 1 : 0);
        cmd.Parameters.AddWithValue("@reason", (object?)reason ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Looks up a cached decode-check result that is still valid for the file's current size and modification time.</summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">Current file size in bytes.</param>
    /// <param name="mtimeUtcTicks">Current last-write time in UTC ticks.</param>
    /// <returns>Empty string when the file decoded cleanly, the error text when it did not, or null when not cached.</returns>
    public string? GetCachedDecode(string path, long size, long mtimeUtcTicks)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        // Older builds cached infrastructure timeouts as corruption; retry those instead.
        cmd.CommandText = "SELECT error FROM decode_cache WHERE path = @path AND size = @size AND mtime_utc = @mtime AND error <> 'decode check timed out'";
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@size", size);
        cmd.Parameters.AddWithValue("@mtime", mtimeUtcTicks);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Stores a decode-check result, replacing any previous entry for the path.</summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">File size in bytes at check time.</param>
    /// <param name="mtimeUtcTicks">Last-write time in UTC ticks at check time.</param>
    /// <param name="error">Empty string for a clean decode, otherwise the error text.</param>
    public void StoreDecode(string path, long size, long mtimeUtcTicks, string error)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO decode_cache (path, size, mtime_utc, checked_at_utc, error)
            VALUES (@path, @size, @mtime, @checkedAt, @error)
            ON CONFLICT(path) DO UPDATE SET size = @size, mtime_utc = @mtime, checked_at_utc = @checkedAt, error = @error
            """;
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@size", size);
        cmd.Parameters.AddWithValue("@mtime", mtimeUtcTicks);
        cmd.Parameters.AddWithValue("@checkedAt", DateTime.UtcNow.Ticks);
        cmd.Parameters.AddWithValue("@error", error);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Looks up a cached interleave-check result that is still valid for the file's current size and modification time.</summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">Current file size in bytes.</param>
    /// <param name="mtimeUtcTicks">Current last-write time in UTC ticks.</param>
    /// <returns>The measured audio/video offset in seconds (0 when the file is fine), or null when not cached.</returns>
    public double? GetCachedInterleave(string path, long size, long mtimeUtcTicks)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT offset_seconds FROM interleave_cache WHERE path = @path AND size = @size AND mtime_utc = @mtime";
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@size", size);
        cmd.Parameters.AddWithValue("@mtime", mtimeUtcTicks);
        return cmd.ExecuteScalar() is double offset ? offset : null;
    }

    /// <summary>Stores an interleave-check result, replacing any previous entry for the path.</summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">File size in bytes at check time.</param>
    /// <param name="mtimeUtcTicks">Last-write time in UTC ticks at check time.</param>
    /// <param name="offsetSeconds">The measured audio/video offset in seconds; 0 when the file is fine.</param>
    public void StoreInterleave(string path, long size, long mtimeUtcTicks, double offsetSeconds)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO interleave_cache (path, size, mtime_utc, checked_at_utc, offset_seconds)
            VALUES (@path, @size, @mtime, @checkedAt, @offset)
            ON CONFLICT(path) DO UPDATE SET size = @size, mtime_utc = @mtime, checked_at_utc = @checkedAt, offset_seconds = @offset
            """;
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@size", size);
        cmd.Parameters.AddWithValue("@mtime", mtimeUtcTicks);
        cmd.Parameters.AddWithValue("@checkedAt", DateTime.UtcNow.Ticks);
        cmd.Parameters.AddWithValue("@offset", offsetSeconds);
        cmd.ExecuteNonQuery();
    }
}
