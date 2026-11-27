using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Field-report spec §3.3 + §10 step 2. FileHasher + file_hashes round-trip; cache should
// short-circuit the second call so no re-read happens after we've moved the file's bytes.
[Collection("MediaDashDb")]
public sealed class FileHasherTests
{
    [Fact]
    public async Task HashAsync_ReturnsSha256_AndCachesByPathSizeMtime()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "mediadash-hash-" + Path.GetRandomFileName() + ".db");
        var filePath = Path.Combine(Path.GetTempPath(), "mediadash-hashfile-" + Path.GetRandomFileName() + ".bin");
        try
        {
            File.WriteAllBytes(filePath, Encoding.UTF8.GetBytes("hello world"));
            var db = new MediaDashDb(dbPath);
            var hasher = new FileHasher(db.Probes, NullLogger<FileHasher>.Instance, new HashConfig(false));

            var hash1 = await hasher.HashAsync(filePath, CancellationToken.None);
            Assert.NotNull(hash1);
            // SHA-256("hello world") = b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9
            Assert.Equal("b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9", hash1);

            // Look up via the cache directly — must be present with the same value.
            var info = new FileInfo(filePath);
            var cached = db.GetCachedHash(filePath, info.Length, info.LastWriteTimeUtc.Ticks);
            Assert.Equal(hash1, cached);

            // Second call returns the same hash (cache hit path).
            var hash2 = await hasher.HashAsync(filePath, CancellationToken.None);
            Assert.Equal(hash1, hash2);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    [Fact]
    public async Task HashAsync_ChangedMtime_ReComputes()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "mediadash-hash-" + Path.GetRandomFileName() + ".db");
        var filePath = Path.Combine(Path.GetTempPath(), "mediadash-hashfile-" + Path.GetRandomFileName() + ".bin");
        try
        {
            File.WriteAllBytes(filePath, Encoding.UTF8.GetBytes("alpha"));
            var db = new MediaDashDb(dbPath);
            var hasher = new FileHasher(db.Probes, NullLogger<FileHasher>.Instance, new HashConfig(false));

            var first = await hasher.HashAsync(filePath, CancellationToken.None);
            Assert.NotNull(first);

            // Rewrite with different bytes AND bump the mtime forward so the cache key differs.
            File.WriteAllBytes(filePath, Encoding.UTF8.GetBytes("beta with different bytes"));
            File.SetLastWriteTimeUtc(filePath, File.GetLastWriteTimeUtc(filePath).AddMinutes(5));

            var second = await hasher.HashAsync(filePath, CancellationToken.None);
            Assert.NotNull(second);
            Assert.NotEqual(first, second);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    [Fact]
    public async Task HashAsync_MissingFile_ReturnsNull()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "mediadash-hash-" + Path.GetRandomFileName() + ".db");
        try
        {
            var db = new MediaDashDb(dbPath);
            var hasher = new FileHasher(db.Probes, NullLogger<FileHasher>.Instance, new HashConfig(false));
            var result = await hasher.HashAsync(Path.Combine(Path.GetTempPath(), "definitely-does-not-exist-" + Path.GetRandomFileName()), CancellationToken.None);
            Assert.Null(result);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultiChunkHashMatchesSha256(bool lowImpact)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mediadash-hash-chunks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "media.bin");
            var bytes = new byte[4 * 1024 * 1024 + 17];
            new Random(72).NextBytes(bytes);
            File.WriteAllBytes(path, bytes);
            var db = new MediaDashDb(Path.Combine(directory, "hash.db"));
            var hasher = new FileHasher(db.Probes, NullLogger<FileHasher>.Instance, new HashConfig(lowImpact));
            var elapsed = Stopwatch.StartNew();
            var actual = await hasher.HashAsync(path, CancellationToken.None);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), actual);
            if (lowImpact)
            {
                Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(40), "Low-impact hashing must yield between chunks.");
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationDuringLowImpactHashDoesNotCachePartialDigest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mediadash-hash-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "media.bin");
            File.WriteAllBytes(path, new byte[16 * 1024 * 1024]);
            var db = new MediaDashDb(Path.Combine(directory, "hash.db"));
            var hasher = new FileHasher(db.Probes, NullLogger<FileHasher>.Instance, new HashConfig(true));
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hasher.HashAsync(path, stop.Token));
            var file = new FileInfo(path);
            Assert.Null(db.Probes.GetCachedHash(path, file.Length, file.LastWriteTimeUtc.Ticks));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class HashConfig(bool lowImpact) : IPluginConfigurationProvider
    {
        public PluginConfiguration GetSnapshot() => new() { LowSystemImpactMode = lowImpact };
    }
}
