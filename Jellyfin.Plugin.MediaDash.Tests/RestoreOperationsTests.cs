using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Package 05-B regression. Pins the DB-layer owner-tokened claim contract that RestoreService
// depends on. entry_key uniqueness = the serialization point; operation_id = the owner token
// (only the caller that holds it can Complete / Abort / MarkNeedsReview). Reconciliation-at-
// startup moves Pending rows to NeedsReview so a process death mid-restore doesn't leave the
// entry stuck under a stale claim. Every scenario runs against a real temp SQLite file.
[Collection("MediaDashDb")]
public sealed class RestoreOperationsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "mediadash-restore-ops-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MediaDashDb _db;
    private string FixtureRoot => _dbPath + ".fixtures";

    private string OwnedPath(string path) => Path.Combine(FixtureRoot, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    public RestoreOperationsTests()
    {
        Directory.CreateDirectory(FixtureRoot);
        _db = new MediaDashDb(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), Path.GetDirectoryName(FixtureRoot));
        Directory.Delete(FixtureRoot, recursive: true);
    }

    // ─── TryBeginRestore ─────────────────────────────────────────────────

    [Fact]
    public void TryBeginRestore_FirstCall_ReturnsOwnerToken()
    {
        var opId = _db.TryBeginRestore(OwnedPath("/bin/movie.mkv"), historyId: 1, sourcePath: OwnedPath("/bin/movie.mkv"), destinationPath: OwnedPath("/lib/movie.mkv"));
        Assert.NotNull(opId);
        Assert.NotEmpty(opId!);
    }

    [Fact]
    public void TryBeginRestore_SecondCall_ReturnsNullWhileFirstIsPending()
    {
        var first = _db.TryBeginRestore(OwnedPath("/bin/movie.mkv"), 1, OwnedPath("/bin/movie.mkv"), OwnedPath("/lib/movie.mkv"));
        Assert.NotNull(first);

        var second = _db.TryBeginRestore(OwnedPath("/bin/movie.mkv"), 2, OwnedPath("/bin/movie.mkv"), OwnedPath("/lib/movie.mkv"));
        Assert.Null(second);
    }

    [Fact]
    public void TryBeginRestore_DifferentEntryKeys_BothSucceed()
    {
        var a = _db.TryBeginRestore(OwnedPath("/bin/a.mkv"), 1, OwnedPath("/bin/a.mkv"), OwnedPath("/lib/a.mkv"));
        var b = _db.TryBeginRestore(OwnedPath("/bin/b.mkv"), 2, OwnedPath("/bin/b.mkv"), OwnedPath("/lib/b.mkv"));
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TryBeginRestore_AfterComplete_SameEntryKeyReclaimable()
    {
        var first = _db.TryBeginRestore(OwnedPath("/bin/movie.mkv"), 1, OwnedPath("/bin/movie.mkv"), OwnedPath("/lib/movie.mkv"))!;
        Assert.True(_db.CompleteRestore(first, OwnedPath("/lib/movie.mkv")));

        var second = _db.TryBeginRestore(OwnedPath("/bin/movie.mkv"), 1, OwnedPath("/bin/movie.mkv"), OwnedPath("/lib/movie.mkv"));
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TryBeginRestore_AfterAbort_SameEntryKeyReclaimable()
    {
        var first = _db.TryBeginRestore(OwnedPath("/bin/movie.mkv"), 1, OwnedPath("/bin/movie.mkv"), OwnedPath("/lib/movie.mkv"))!;
        Assert.True(_db.AbortRestore(first));

        var second = _db.TryBeginRestore(OwnedPath("/bin/movie.mkv"), 1, OwnedPath("/bin/movie.mkv"), OwnedPath("/lib/movie.mkv"));
        Assert.NotNull(second);
    }

    [Fact]
    public void TryBeginRestore_WhileNeedsReview_ReturnsNull()
    {
        var first = _db.TryBeginRestore(OwnedPath("/bin/movie.mkv"), 1, OwnedPath("/bin/movie.mkv"), OwnedPath("/lib/movie.mkv"))!;
        Assert.True(_db.MarkRestoreNeedsReview(first, "partial mutation on disk"));

        var second = _db.TryBeginRestore(OwnedPath("/bin/movie.mkv"), 2, OwnedPath("/bin/movie.mkv"), OwnedPath("/lib/movie.mkv"));
        Assert.Null(second);
    }

    [Fact]
    public void TryBeginRestore_EmptyEntryKey_ReturnsNull()
    {
        Assert.Null(_db.TryBeginRestore(string.Empty, 1, OwnedPath("/bin/x"), OwnedPath("/lib/x")));
    }

    [Fact]
    public void TryBeginRestore_HistoryIdNullable_AcceptedForManifestOnlyRestores()
    {
        var op = _db.TryBeginRestore(OwnedPath("/bin/orphan.srt"), historyId: null, OwnedPath("/bin/orphan.srt"), OwnedPath("/lib/orphan.srt"));
        Assert.NotNull(op);
        var row = Assert.Single(_db.GetRestoreOperations());
        Assert.Null(row.HistoryId);
    }

    // ─── Owner-mismatched Complete/Abort/MarkNeedsReview ────────────────

    [Fact]
    public void CompleteRestore_UnknownOperation_ReturnsFalse()
    {
        Assert.False(_db.CompleteRestore("does-not-exist", OwnedPath("/lib/x")));
    }

    [Fact]
    public void CompleteRestore_WithStaleOwnerToken_DoesNotAffectAnotherOperation()
    {
        // Old owner from a previous crashed attempt: mustn't be able to close out a newer,
        // legitimately-owned Pending row on the same entry_key.
        var oldOp = _db.TryBeginRestore(OwnedPath("/bin/movie.mkv"), 1, OwnedPath("/bin/movie.mkv"), OwnedPath("/lib/movie.mkv"))!;
        Assert.True(_db.AbortRestore(oldOp)); // simulate old owner giving up
        var newOp = _db.TryBeginRestore(OwnedPath("/bin/movie.mkv"), 1, OwnedPath("/bin/movie.mkv"), OwnedPath("/lib/movie.mkv"))!;

        Assert.False(_db.CompleteRestore(oldOp, OwnedPath("/lib/movie.mkv")));

        var pending = Assert.Single(_db.GetRestoreOperations());
        Assert.Equal("Pending", pending.State);
        Assert.Equal(newOp, pending.OperationId);
    }

    [Fact]
    public void AbortRestore_UnknownOperation_ReturnsFalse()
    {
        Assert.False(_db.AbortRestore("does-not-exist"));
    }

    [Fact]
    public void AbortRestore_WithStaleOwnerToken_LeavesNewerClaimUntouched()
    {
        var oldOp = _db.TryBeginRestore(OwnedPath("/bin/movie.mkv"), 1, OwnedPath("/bin/movie.mkv"), OwnedPath("/lib/movie.mkv"))!;
        Assert.True(_db.AbortRestore(oldOp));
        var newOp = _db.TryBeginRestore(OwnedPath("/bin/movie.mkv"), 1, OwnedPath("/bin/movie.mkv"), OwnedPath("/lib/movie.mkv"))!;

        Assert.False(_db.AbortRestore(oldOp));

        var pending = Assert.Single(_db.GetRestoreOperations());
        Assert.Equal(newOp, pending.OperationId);
    }

    [Fact]
    public void MarkRestoreNeedsReview_UnknownOperation_ReturnsFalse()
    {
        Assert.False(_db.MarkRestoreNeedsReview("does-not-exist", "detail"));
    }

    [Fact]
    public void MarkRestoreNeedsReview_PersistsDetail()
    {
        var op = _db.TryBeginRestore(OwnedPath("/bin/movie.mkv"), 1, OwnedPath("/bin/movie.mkv"), OwnedPath("/lib/movie.mkv"))!;

        Assert.True(_db.MarkRestoreNeedsReview(op, "restore verification failed after move"));

        var row = Assert.Single(_db.GetRestoreOperations());
        Assert.Equal("NeedsReview", row.State);
        Assert.Equal("restore verification failed after move", row.Detail);
    }

    [Fact]
    public void CompleteRestore_UpdatesDestinationBeforeRemoval()
    {
        // Successful restore may re-resolve destination if a late collision forced a suffix
        // change; Complete records the actual landing path just before the row is removed.
        var op = _db.TryBeginRestore(OwnedPath("/bin/movie.mkv"), 1, OwnedPath("/bin/movie.mkv"), OwnedPath("/lib/movie.mkv"))!;

        Assert.True(_db.CompleteRestore(op, OwnedPath("/lib/movie-restored.mkv")));

        Assert.Empty(_db.GetRestoreOperations());
    }

    // ─── Concurrency ─────────────────────────────────────────────────────

    [Fact]
    public async Task TryBeginRestore_ConcurrentCallers_ExactlyOneWins()
    {
        // Plan §05-B non-negotiable: "Two requests for same row → exactly one owned operation
        // performs file mutation." Repeat 20 trials with 8 concurrent callers each; any lucky-
        // ordering false-pass in the atomic-claim implementation would eventually break.
        for (var trial = 0; trial < 20; trial++)
        {
            var entryKey = OwnedPath("/bin/concurrent-") + trial.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".mkv";
            const int Concurrency = 8;
            var results = await Task.WhenAll(Enumerable.Range(0, Concurrency)
                .Select(_ => Task.Run(() => _db.TryBeginRestore(entryKey, historyId: 1, entryKey, OwnedPath("/lib/x.mkv")))));

            var wins = results.Count(r => r is not null);
            Assert.Equal(1, wins);
            Assert.True(_db.AbortRestore(results.Single(result => result is not null)!));
        }
    }

    // ─── Reconciliation at startup ──────────────────────────────────────

    [Fact]
    public void ReconcilePendingRestoresAtStartup_PendingRowsBecomeNeedsReview()
    {
        var op = _db.TryBeginRestore(OwnedPath("/bin/movie.mkv"), 1, OwnedPath("/bin/movie.mkv"), OwnedPath("/lib/movie.mkv"))!;

        // Simulate process death: skip Complete/Abort/MarkNeedsReview. Rebuild MediaDashDb on
        // the same file — the ctor's reconciliation pass should transition Pending → NeedsReview.
        SqliteConnection.ClearAllPools();
        var reopened = new MediaDashDb(_dbPath);

        var row = Assert.Single(reopened.GetRestoreOperations());
        Assert.Equal("NeedsReview", row.State);
        Assert.Equal(op, row.OperationId);
        Assert.NotNull(row.Detail);
        Assert.Contains("Process died mid-restore", row.Detail!);
    }

    [Fact]
    public void Reconciliation_PreservesExistingNeedsReviewRows()
    {
        var op = _db.TryBeginRestore(OwnedPath("/bin/movie.mkv"), 1, OwnedPath("/bin/movie.mkv"), OwnedPath("/lib/movie.mkv"))!;
        Assert.True(_db.MarkRestoreNeedsReview(op, "partial mutation observed"));

        SqliteConnection.ClearAllPools();
        var reopened = new MediaDashDb(_dbPath);

        var row = Assert.Single(reopened.GetRestoreOperations());
        Assert.Equal("NeedsReview", row.State);
        Assert.Equal("partial mutation observed", row.Detail);
    }

    [Fact]
    public void Reconciliation_DeletesLeftoverCompletedRows()
    {
        // Simulate a crash between UPDATE-to-Completed and DELETE inside CompleteRestore by
        // writing a Completed row directly and reopening. Reconciliation should sweep it so the
        // entry_key stays free for re-restore.
        SeedRawRestoreOperation("op-completed", OwnedPath("/bin/movie.mkv"), state: "Completed");

        SqliteConnection.ClearAllPools();
        var reopened = new MediaDashDb(_dbPath);

        Assert.Empty(reopened.GetRestoreOperations());
        var reclaimed = reopened.TryBeginRestore(OwnedPath("/bin/movie.mkv"), 1, OwnedPath("/bin/movie.mkv"), OwnedPath("/lib/movie.mkv"));
        Assert.NotNull(reclaimed);
    }

    [Fact]
    public void Reconciliation_LeavesEntryKeyClaimedByNeedsReviewRow_Unreclaimable()
    {
        // Plan §05-B: NeedsReview blocks fresh claims. This is the safety invariant — do NOT
        // silently retry a partially-mutated restore.
        var op = _db.TryBeginRestore(OwnedPath("/bin/movie.mkv"), 1, OwnedPath("/bin/movie.mkv"), OwnedPath("/lib/movie.mkv"))!;

        // Simulate process death (no CompleteRestore call). Ctor reconciles Pending → NeedsReview.
        SqliteConnection.ClearAllPools();
        var reopened = new MediaDashDb(_dbPath);

        var retry = reopened.TryBeginRestore(OwnedPath("/bin/movie.mkv"), 1, OwnedPath("/bin/movie.mkv"), OwnedPath("/lib/movie.mkv"));
        Assert.Null(retry);
    }

    // ─── Helper ──────────────────────────────────────────────────────────

    private void SeedRawRestoreOperation(string operationId, string entryKey, string state)
    {
        using var conn = new SqliteConnection("Data Source=" + _dbPath + ";Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO restore_operations (operation_id, entry_key, history_id, source_path, destination_path, state, started_utc_ticks, detail) VALUES (@op, @ek, NULL, @src, @dst, @state, @ticks, NULL)";
        cmd.Parameters.AddWithValue("@op", operationId);
        cmd.Parameters.AddWithValue("@ek", entryKey);
        cmd.Parameters.AddWithValue("@src", entryKey);
        cmd.Parameters.AddWithValue("@dst", OwnedPath("/lib/x"));
        cmd.Parameters.AddWithValue("@state", state);
        cmd.Parameters.AddWithValue("@ticks", DateTime.UtcNow.Ticks);
        cmd.ExecuteNonQuery();
    }
}
