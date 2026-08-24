using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Field-report spec §3.3 + §10 step 2. FileHasher + file_hashes round-trip; cache should
// short-circuit the second call so no re-read happens after we've moved the file's bytes.
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
            var hasher = new FileHasher(db, NullLogger<FileHasher>.Instance);

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
            var hasher = new FileHasher(db, NullLogger<FileHasher>.Instance);

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
            var hasher = new FileHasher(db, NullLogger<FileHasher>.Instance);
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
}
