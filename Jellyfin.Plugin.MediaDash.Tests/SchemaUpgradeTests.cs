using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Package 05-A regression. Owns the schema/migration contract that used to live inline on
// MediaDashDb. Each fixture synthesises a small historical DB (v0-shape without the columns /
// tables later migrations add), seeds a couple of representative rows, runs
// MediaDashSchema.CreateTables + Migrate, then asserts (a) the migration reached v6, (b) live
// data survived, (c) the one-time semantic cleanups fired, and (d) re-running the migration is
// a no-op. Runs against a real temp SQLite file rather than an audit DB — the plan explicitly
// forbids depending on a developer's audit DB of unknown provenance.
[Collection("MediaDashDb")]
public sealed class SchemaUpgradeTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "mediadash-schema-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public void FreshInstall_CreatesEveryTableAndReachesTargetVersion()
    {
        using (var conn = OpenRaw())
        {
            MediaDashSchema.CreateTables(conn);
            MediaDashSchema.Migrate(conn);
        }

        Assert.Equal(MediaDashSchema.Version, ReadUserVersion());
        foreach (var table in new[] { "issues", "probe_cache", "decode_cache", "format_probe_cache", "file_hashes", "history", "diagnostics", "plugin_state", "restored_paths", "restore_operations" })
        {
            Assert.True(TableExists(table), "Missing table " + table);
        }
    }

    [Fact]
    public void Migrate_IsIdempotent_WhenRerunAgainstSameDb()
    {
        // Twice-opened DB matches the plan's "run each upgrade twice to prove idempotence".
        using (var conn = OpenRaw())
        {
            MediaDashSchema.CreateTables(conn);
            MediaDashSchema.Migrate(conn);
        }

        using (var conn = OpenRaw())
        {
            MediaDashSchema.CreateTables(conn);
            MediaDashSchema.Migrate(conn);
        }

        Assert.Equal(MediaDashSchema.Version, ReadUserVersion());
    }

    [Fact]
    public void V0Upgrade_PurgesDecodeCacheAndBackfillsFailedActionsToSuccessZero()
    {
        // Create a v0-shape DB: history without the success / acknowledged columns; decode_cache
        // with some pre-fix stale entries; issues without the confidence column; no restored_paths
        // table. Seed one row per pre-existing table so we can verify preservation vs cleanup.
        SeedV0Db(
            seedDecodeCache: true,
            seedHistoryRows: [
                ("Fix failed: file not found", "/lib/failed.mkv"),
                ("Deleted duplicate", "/lib/dupe.mkv"),
            ]);

        using (var conn = OpenRaw())
        {
            MediaDashSchema.CreateTables(conn);
            MediaDashSchema.Migrate(conn);
        }

        Assert.Equal(MediaDashSchema.Version, ReadUserVersion());
        // v1: decode_cache purged.
        Assert.Equal(0, CountRows("decode_cache"));
        // v2: success column added + back-fill of "Fix failed*" rows.
        Assert.True(HasColumn("history", "success"));
        Assert.True(HasColumn("history", "acknowledged")); // v3 also ran
        var failedSuccess = ReadScalarInt("SELECT success FROM history WHERE path = '/lib/failed.mkv'");
        Assert.Equal(0, failedSuccess);
        var okSuccess = ReadScalarInt("SELECT success FROM history WHERE path = '/lib/dupe.mkv'");
        Assert.Equal(1, okSuccess);
        // v4: confidence column added.
        Assert.True(HasColumn("issues", "confidence"));
        // v5: restored_paths table created.
        Assert.True(TableExists("restored_paths"));
    }

    [Fact]
    public void V5Upgrade_PurgesDecodeCacheAndClearsQueuedPlayabilityIssues()
    {
        // Seed at v5 shape: full v5 tables (including restored_paths + acknowledged + confidence),
        // one Playability issue that must be cleared by v6, one non-Playability issue that must
        // survive, plus decode_cache entries that must be purged. History rows must be preserved.
        SeedFullSchemaDbAtVersion(5,
            seedDecodeCache: true,
            seedIssues: [
                ((int)IssueType.Playability, "/lib/broken.mkv"),
                ((int)IssueType.Duplicate, "/lib/keep.mkv"),
            ],
            seedRestoredPaths: [("/lib/user-restored.mkv", (int)IssueType.Duplicate)]);

        using (var conn = OpenRaw())
        {
            MediaDashSchema.CreateTables(conn);
            MediaDashSchema.Migrate(conn);
        }

        Assert.Equal(MediaDashSchema.Version, ReadUserVersion());
        // v6: decode_cache purged.
        Assert.Equal(0, CountRows("decode_cache"));
        // v6: Queued Playability issues cleared.
        Assert.Equal(0, ReadScalarInt("SELECT COUNT(*) FROM issues WHERE type = " + (int)IssueType.Playability));
        // Non-Playability issues preserved.
        Assert.Equal(1, ReadScalarInt("SELECT COUNT(*) FROM issues WHERE type = " + (int)IssueType.Duplicate));
        // Restored-paths row preserved across the migration.
        Assert.Equal(1, CountRows("restored_paths"));
    }

    [Fact]
    public void Issue94UpgradeInvalidatesDecodeVerdictsAndOldPlayabilityApprovalsOnce()
    {
        SeedFullSchemaDbAtVersion(14, seedDecodeCache: true,
            seedIssues: [((int)IssueType.Playability, "/lib/glitch.mp3"), ((int)IssueType.Duplicate, "/lib/keep.mkv")],
            seedRestoredPaths: [("/lib/restored.mp3", (int)IssueType.Playability)]);
        InsertRawIssue((int)IssueType.Playability, Guid.NewGuid().ToString("N"), (int)IssueStatus.Detected, DateTime.UtcNow.Ticks, "/lib/review.mp3");
        InsertRawIssue((int)IssueType.Playability, Guid.NewGuid().ToString("N"), (int)IssueStatus.Dismissed, DateTime.UtcNow.Ticks, "/lib/ignored.mp3");
        InsertRawHistory((int)IssueType.Playability, "/lib/history.mp3", DateTime.UtcNow.Ticks);
        using (var conn = OpenRaw())
        {
            using var seed = conn.CreateCommand();
            seed.CommandText = "INSERT INTO audio_decode_cache VALUES ('/lib/glitch.mp3', 1, 1, 'sampled-v1')";
            seed.ExecuteNonQuery();
        }
        using (var conn = OpenRaw()) MediaDashSchema.Migrate(conn);
        Assert.Equal(0, CountRows("decode_cache"));
        Assert.Equal(0, CountRows("audio_decode_cache"));
        Assert.Equal(1, ReadScalarInt("SELECT COUNT(*) FROM issues WHERE type = " + (int)IssueType.Playability));
        Assert.Equal(2, CountRows("issues"));
        Assert.Equal(1, CountRows("history"));
        Assert.Equal(1, CountRows("restored_paths"));
        using (var conn = OpenRaw())
        {
            using var seed = conn.CreateCommand();
            seed.CommandText = "INSERT INTO decode_cache VALUES ('/lib/new.mp3', 1, 1, 1, '')";
            seed.ExecuteNonQuery();
            MediaDashSchema.Migrate(conn);
        }
        Assert.Equal(1, CountRows("decode_cache"));
    }

    [Fact]
    public void AlreadyAtTargetVersion_MigrateIsANoOp()
    {
        // Seed a fully-migrated DB with rows in every table; running Migrate must leave every
        // table untouched (specifically: decode_cache and Playability issues must NOT be purged
        // again, and no v7 restore_operations churn).
        SeedFullSchemaDbAtVersion(MediaDashSchema.Version,
            seedDecodeCache: true,
            seedIssues: [
                ((int)IssueType.Playability, "/lib/broken.mkv"),
                ((int)IssueType.Duplicate, "/lib/dupe.mkv"),
            ],
            seedRestoredPaths: []);
        var decodeBefore = CountRows("decode_cache");
        var issuesBefore = CountRows("issues");

        using (var conn = OpenRaw())
        {
            MediaDashSchema.Migrate(conn);
        }

        Assert.Equal(decodeBefore, CountRows("decode_cache"));
        Assert.Equal(issuesBefore, CountRows("issues"));
    }

    [Fact]
    public void MigrationSurvivesMalformedRowsAndReturnsReadableDb()
    {
        // Seed v5 with a mixture of good + malformed rows (out-of-range enum on issues, garbled
        // GUID on issues, malformed ticks on history). The migration must not throw; the DB must
        // afterwards remain readable via MediaDashDb; malformed rows must be observable through
        // the guarded readers (no throw, but skipped or clamped).
        SeedFullSchemaDbAtVersion(5, seedDecodeCache: false, seedIssues: [], seedRestoredPaths: []);
        InsertRawIssue(type: 9999, itemId: Guid.NewGuid().ToString("N"), status: (int)IssueStatus.Detected, ticks: DateTime.UtcNow.Ticks, path: "/lib/bad-enum.mkv");
        InsertRawIssue(type: (int)IssueType.Duplicate, itemId: "not-a-guid", status: (int)IssueStatus.Detected, ticks: DateTime.UtcNow.Ticks, path: "/lib/bad-guid.mkv");
        InsertRawHistory(type: (int)IssueType.Duplicate, path: "/lib/overflow.mkv", ticks: long.MaxValue);
        InsertRawIssue(type: (int)IssueType.Duplicate, itemId: Guid.NewGuid().ToString("N"), status: (int)IssueStatus.Detected, ticks: DateTime.UtcNow.Ticks, path: "/lib/ok.mkv");

        using (var conn = OpenRaw())
        {
            MediaDashSchema.CreateTables(conn);
            MediaDashSchema.Migrate(conn);
        }

        SqliteConnection.ClearAllPools();
        var db = new MediaDashDb(_dbPath);
        var issues = db.GetIssues();
        Assert.Contains(issues, i => i.Path == "/lib/ok.mkv");
        Assert.DoesNotContain(issues, i => i.Path == "/lib/bad-enum.mkv");
        Assert.DoesNotContain(issues, i => i.Path == "/lib/bad-guid.mkv");
        var history = db.GetHistory();
        Assert.Contains(history, h => h.Path == "/lib/overflow.mkv"); // clamped, not dropped
    }

    // ─── helpers ─────────────────────────────────────────────────────────

    private SqliteConnection OpenRaw()
    {
        var conn = new SqliteConnection("Data Source=" + _dbPath);
        conn.Open();
        return conn;
    }

    private int ReadUserVersion()
    {
        using var conn = OpenRaw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
    }

    private bool TableExists(string table)
    {
        using var conn = OpenRaw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name";
        cmd.Parameters.AddWithValue("@name", table);
        return cmd.ExecuteScalar() is not null;
    }

    private bool HasColumn(string table, string column)
    {
        using var conn = OpenRaw();
        using var cmd = conn.CreateCommand();
#pragma warning disable CA2100 // table name is a compile-time literal from the caller
        cmd.CommandText = "PRAGMA table_info(" + table + ")";
#pragma warning restore CA2100
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private int CountRows(string table)
    {
        using var conn = OpenRaw();
        using var cmd = conn.CreateCommand();
#pragma warning disable CA2100 // table name is a compile-time literal from the caller
        cmd.CommandText = "SELECT COUNT(*) FROM " + table;
#pragma warning restore CA2100
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
    }

    private int ReadScalarInt(string sql)
    {
        using var conn = OpenRaw();
        using var cmd = conn.CreateCommand();
#pragma warning disable CA2100 // caller-provided literal SQL from within this test file
        cmd.CommandText = sql;
#pragma warning restore CA2100
        var raw = cmd.ExecuteScalar();
        return Convert.ToInt32(raw ?? 0, CultureInfo.InvariantCulture);
    }

    private void SeedV0Db(bool seedDecodeCache, (string Action, string Path)[] seedHistoryRows)
    {
        using var conn = OpenRaw();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                CREATE TABLE issues (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    type INTEGER NOT NULL,
                    item_id TEXT NOT NULL,
                    path TEXT NOT NULL,
                    details TEXT NOT NULL DEFAULT '{}',
                    suggested_fix TEXT NOT NULL DEFAULT '',
                    size_savings INTEGER NOT NULL DEFAULT 0,
                    status INTEGER NOT NULL DEFAULT 0,
                    detected_at_utc INTEGER NOT NULL
                );
                CREATE TABLE probe_cache (
                    path TEXT PRIMARY KEY,
                    size INTEGER NOT NULL,
                    mtime_utc INTEGER NOT NULL,
                    probed_at_utc INTEGER NOT NULL,
                    json TEXT NOT NULL
                );
                CREATE TABLE decode_cache (
                    path TEXT PRIMARY KEY,
                    size INTEGER NOT NULL,
                    mtime_utc INTEGER NOT NULL,
                    checked_at_utc INTEGER NOT NULL,
                    error TEXT NOT NULL
                );
                CREATE TABLE history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    issue_id INTEGER NOT NULL,
                    type INTEGER NOT NULL,
                    path TEXT NOT NULL,
                    action TEXT NOT NULL,
                    bytes_freed INTEGER NOT NULL DEFAULT 0,
                    recycle_path TEXT NULL,
                    fixed_at_utc INTEGER NOT NULL,
                    dry_run INTEGER NOT NULL DEFAULT 0,
                    restored INTEGER NOT NULL DEFAULT 0
                );
                PRAGMA user_version = 0;";
            cmd.ExecuteNonQuery();
        }

        if (seedDecodeCache)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO decode_cache (path, size, mtime_utc, checked_at_utc, error) VALUES ('/lib/stale.mkv', 1, 1, 1, 'old-error')";
            cmd.ExecuteNonQuery();
        }

        foreach (var (action, path) in seedHistoryRows)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO history (issue_id, type, path, action, bytes_freed, recycle_path, fixed_at_utc, dry_run, restored) VALUES (0, @type, @path, @action, 0, NULL, @ticks, 0, 0)";
            cmd.Parameters.AddWithValue("@type", (int)IssueType.Duplicate);
            cmd.Parameters.AddWithValue("@path", path);
            cmd.Parameters.AddWithValue("@action", action);
            cmd.Parameters.AddWithValue("@ticks", DateTime.UtcNow.Ticks);
            cmd.ExecuteNonQuery();
        }
    }

    private void SeedFullSchemaDbAtVersion(int version, bool seedDecodeCache, (int Type, string Path)[] seedIssues, (string Path, int Type)[] seedRestoredPaths)
    {
        using (var conn = OpenRaw())
        {
            MediaDashSchema.CreateTables(conn);
            MediaDashSchema.Migrate(conn);
        }

        using (var conn = OpenRaw())
        {
            using var setVersion = conn.CreateCommand();
#pragma warning disable CA2100 // literal test-controlled int
            setVersion.CommandText = "PRAGMA user_version = " + version.ToString(CultureInfo.InvariantCulture);
#pragma warning restore CA2100
            setVersion.ExecuteNonQuery();

            if (seedDecodeCache)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO decode_cache (path, size, mtime_utc, checked_at_utc, error) VALUES ('/lib/stale.mkv', 1, 1, 1, 'old-error')";
                cmd.ExecuteNonQuery();
            }

            foreach (var (type, path) in seedIssues)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO issues (type, item_id, path, details, suggested_fix, size_savings, status, detected_at_utc, confidence) VALUES (@type, @itemId, @path, '{}', '', 0, @status, @ticks, NULL)";
                cmd.Parameters.AddWithValue("@type", type);
                cmd.Parameters.AddWithValue("@itemId", Guid.NewGuid().ToString("N"));
                cmd.Parameters.AddWithValue("@path", path);
                cmd.Parameters.AddWithValue("@status", (int)IssueStatus.Queued);
                cmd.Parameters.AddWithValue("@ticks", DateTime.UtcNow.Ticks);
                cmd.ExecuteNonQuery();
            }

            foreach (var (path, type) in seedRestoredPaths)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO restored_paths (path, type, restored_at_utc) VALUES (@path, @type, @ticks)";
                cmd.Parameters.AddWithValue("@path", path);
                cmd.Parameters.AddWithValue("@type", type);
                cmd.Parameters.AddWithValue("@ticks", DateTime.UtcNow.Ticks);
                cmd.ExecuteNonQuery();
            }
        }
    }

    private void InsertRawIssue(int type, string itemId, int status, long ticks, string path)
    {
        using var conn = OpenRaw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO issues (type, item_id, path, details, suggested_fix, size_savings, status, detected_at_utc, confidence) VALUES (@type, @itemId, @path, '{}', '', 0, @status, @ticks, NULL)";
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@itemId", itemId);
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@ticks", ticks);
        cmd.ExecuteNonQuery();
    }

    private void InsertRawHistory(int type, string path, long ticks)
    {
        using var conn = OpenRaw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO history (issue_id, type, path, action, bytes_freed, recycle_path, fixed_at_utc, dry_run, restored, success, acknowledged) VALUES (0, @type, @path, 'seeded', 0, NULL, @ticks, 0, 0, 1, 0)";
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@ticks", ticks);
        cmd.ExecuteNonQuery();
    }
}
