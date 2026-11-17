using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("Diagnostics")]
public sealed class MediaMoveRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-move-journal-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_root, "journal.db");

    public MediaMoveRecoveryTests() => Directory.CreateDirectory(_root);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database }.ToString());
        connection.Open();
        return connection;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Fact]
    public void FreshAndRepeatedMigrationCreateTheDurableJournalAndHistoryLink()
    {
        _ = new MediaDashDb(Database);
        using (var connection = Open())
        {
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='media_move_operations'"));
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM pragma_table_info('history') WHERE name='move_operation_id'"));
            Assert.Equal((long)MediaDashSchema.Version, Scalar(connection, "PRAGMA user_version"));
        }

        _ = new MediaDashDb(Database);
        using var repeated = Open();
        Assert.Equal(1L, Scalar(repeated, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='media_move_operations'"));
    }

    [Fact]
    public void RestartPreservesAnIncompleteManifestAndNeverReplaysFilesystemWork()
    {
        _ = new MediaDashDb(Database);
        var source = Path.Combine(_root, "source.mkv");
        var target = Path.Combine(_root, "destination.mkv");
        File.WriteAllText(source, "original");
        File.WriteAllText(target, "new import");
        using (var connection = Open())
        {
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name='media_move_operations'"));
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO media_move_operations (operation_id,issue_id,item_id,plan_version,manifest_json,claim_paths_json,state,owner_token,created_utc_ticks,updated_utc_ticks) VALUES (@id,0,@item,1,'{}','[]','Executing',@owner,1,1)";
            insert.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("@item", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("@owner", Guid.NewGuid().ToString("N"));
            insert.ExecuteNonQuery();
        }

        _ = new MediaDashDb(Database);
        using var reopened = Open();
        Assert.Equal("NeedsReview", Scalar(reopened, "SELECT state FROM media_move_operations"));
        Assert.Equal("{}", Scalar(reopened, "SELECT manifest_json FROM media_move_operations"));
        Assert.Equal("original", File.ReadAllText(source));
        Assert.Equal("new import", File.ReadAllText(target));
    }

    private MediaMovePlan Plan(string name = "source.mkv", string destination = "target/source.mkv")
    {
        var source = Path.GetFullPath(Path.Combine(_root, name));
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, name);
        return new MediaMovePlan
        {
            ItemId = Guid.NewGuid(), Type = IssueType.Ungrouped,
            Identity = MediaMovePlan.Hash("owned identity"), PolicyStamp = MediaMovePlan.Hash("owned policy"),
            Entries = [new(source, Path.GetFullPath(Path.Combine(_root, destination)), false, MediaMoveSnapshot.Capture(source).Export())]
        }.Seal();
    }

    [Fact]
    public async Task ConcurrentOverlappingClaimsHaveExactlyOneOwner()
    {
        var database = new MediaDashDb(Database);
        var plan = Plan();
        Assert.True(plan.HasValidFingerprint(), "The controlled plan fingerprint must be stable before claims are tested.");
        Assert.True(plan.HasValidDefinition(), System.Text.Json.JsonSerializer.Serialize(plan, MediaMovePlan.JsonOptions));
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => database.Moves.TryBegin(plan))));
        var operation = Assert.Single(results, result => result is not null)!;
        Assert.Equal("Prepared", operation.State);
        Assert.True(database.Moves.HasActiveConflict(plan.Entries[0].Source));
        Assert.True(database.Moves.HasActiveConflict(plan.Entries[0].Destination));
        Assert.False(database.Moves.HasActiveConflict(Path.Combine(_root, "independent.mkv")));
        Assert.Equal(operation.Id, database.Moves.Read(operation.Id)!.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisjointPathsCannotClaimTheSameCatalogOwnerOrFamily(bool family)
    {
        var database = new MediaDashDb(Database);
        var first = Plan();
        var next = Plan("independent.mkv", "other/independent.mkv");
        Assert.NotNull(database.Moves.TryBegin(first));
        var overlapping = (next with
        {
            ItemId = family ? next.ItemId : first.ItemId,
            ItemBindings = family ? [new(first.ItemId, next.Entries[0].Source, first.Identity)] : []
        }).Seal();
        Assert.True(overlapping.HasValidDefinition());
        Assert.Null(database.Moves.TryBegin(overlapping));
        Assert.NotNull(database.Moves.TryBegin(next));
        Assert.Equal("independent.mkv", File.ReadAllText(next.Entries[0].Source));
    }

    [Fact]
    public void ADirectoryClaimBlocksDescendantsAndTheirParents()
    {
        var database = new MediaDashDb(Database);
        var file = Plan("folder/source.mkv");
        var folder = Path.GetDirectoryName(file.Entries[0].Source)!;
        var plan = (file with { Entries = [new(folder, Path.Combine(_root, "destination"), true, MediaMoveSnapshot.Capture(folder).Export())] }).Seal();
        Assert.NotNull(database.Moves.TryBegin(plan));
        Assert.True(database.Moves.HasActiveConflict(Path.Combine(folder, "new-child.mkv")));
        Assert.True(database.Moves.HasActiveConflict(_root));
        Assert.False(database.Moves.HasActiveConflict(Path.Combine(_root, "folder-other")));
    }

    [Fact]
    public void OnlyTheCurrentOwnerAndExpectedStateCanAdvanceAClaim()
    {
        var database = new MediaDashDb(Database);
        var operation = database.Moves.TryBegin(Plan());
        Assert.NotNull(operation);
        Assert.False(database.Moves.Update(operation.Id, Guid.NewGuid().ToString("N"), "Prepared", "Executing", operation.ManifestJson));
        Assert.False(database.Moves.Update(operation.Id, operation.OwnerToken, "Executing", "Completed", operation.ManifestJson));
        Assert.True(database.Moves.Update(operation.Id, operation.OwnerToken, "Prepared", "Executing", operation.ManifestJson));
        Assert.Equal("Executing", database.Moves.Read(operation.Id)!.State);
    }

    [Fact]
    public void RestartRevokesTheOldOwnerAndRetainsClaimsWithoutHistory()
    {
        var database = new MediaDashDb(Database);
        var plan = Plan();
        var operation = database.Moves.TryBegin(plan);
        Assert.NotNull(operation);
        _ = new MediaDashDb(Database);
        var retained = Assert.Single(database.Moves.GetNeedsReview());
        Assert.Equal(operation.Id, retained.Id);
        Assert.Null(retained.HistoryId);
        Assert.True(database.Moves.HasActiveConflict(plan.Entries[0].Source));
        Assert.False(database.Moves.Update(operation.Id, operation.OwnerToken, "NeedsReview", "Executing", operation.ManifestJson));
        Assert.Null(database.Moves.TryBegin(plan));
    }

    [Fact]
    public void ACorruptActiveManifestCannotHideItsPathsOrCauseReplay()
    {
        var database = new MediaDashDb(Database);
        var plan = Plan();
        var operation = database.Moves.TryBegin(plan);
        Assert.NotNull(operation);
        using (var connection = Open())
        {
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE media_move_operations SET manifest_json='not json' WHERE operation_id=@id";
            corrupt.Parameters.AddWithValue("@id", operation.Id);
            corrupt.ExecuteNonQuery();
        }

        _ = new MediaDashDb(Database);
        Assert.Single(database.Moves.GetNeedsReview());
        Assert.True(database.Moves.HasActiveConflict(plan.Entries[0].Source));
        Assert.Equal("source.mkv", File.ReadAllText(plan.Entries[0].Source));
        Assert.False(File.Exists(plan.Entries[0].Destination));
    }

    [Fact]
    public void ARecomputedFingerprintDoesNotAuthorizeAnInvalidPayloadPath()
    {
        var database = new MediaDashDb(Database);
        var original = Plan();
        var entry = original.Entries[0];
        var invalid = (original with { Entries = [entry with { Inventory = [entry.Inventory[0] with { RelativePath = "../foreign.mkv" }] }] }).Seal();
        Assert.Null(database.Moves.TryBegin(invalid));
        Assert.False(database.Moves.HasActiveConflict(entry.Source));
    }

    [Fact]
    public void CorruptionDuringExecutionCannotBeOverwrittenByTheCachedOwner()
    {
        var database = new MediaDashDb(Database);
        var operation = database.Moves.TryBegin(Plan())!;
        Assert.True(database.Moves.Update(operation.Id, operation.OwnerToken, "Prepared", "Executing", operation.ManifestJson));
        using (var connection = Open())
        {
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE media_move_operations SET manifest_json='broken' WHERE operation_id=@id";
            corrupt.Parameters.AddWithValue("@id", operation.Id);
            corrupt.ExecuteNonQuery();
        }

        Assert.False(database.Moves.Update(operation.Id, operation.OwnerToken, "Executing", "Executing", operation.ManifestJson));
        Assert.Equal("NeedsReview", database.Moves.Read(operation.Id)!.State);
    }

    [Fact]
    public void AnUnexecutedManifestCannotBeMarkedCompletedOrReleaseItsClaims()
    {
        var database = new MediaDashDb(Database);
        var plan = Plan();
        var operation = database.Moves.TryBegin(plan)!;
        Assert.True(database.Moves.Update(operation.Id, operation.OwnerToken, "Prepared", "Executing", operation.ManifestJson));
        var manifest = MediaMoveManifest.TryRead(operation.ManifestJson, operation.Id)! with { CleanupComplete = true };
        Assert.False(database.Moves.Update(operation.Id, operation.OwnerToken, "Executing", "Completed", manifest.Serialize()));
        Assert.True(database.Moves.HasActiveConflict(plan.Entries[0].Source));
    }

    [Fact]
    public void HistoryPreservesTheExactDurableOperationReference()
    {
        var database = new MediaDashDb(Database);
        var plan = Plan();
        var operation = database.Moves.TryBegin(plan)!;
        database.History.AddHistory(new HistoryEntry
        {
            Type = IssueType.Ungrouped, Path = plan.Entries[0].Source, FixedAtUtc = DateTime.UtcNow,
            Action = "owned move attempt", MoveOperationId = operation.Id
        });
        var saved = Assert.Single(database.History.GetHistory());
        Assert.Equal(operation.Id, saved.MoveOperationId);
        Assert.Equal(operation.Id, database.History.GetHistoryEntry(saved.Id)!.MoveOperationId);
    }

    [Fact]
    public void ScheduledOutcomeLinksJournalAndHistoryInTheSameTransaction()
    {
        var database = new MediaDashDb(Database);
        var plan = Plan();
        var operation = database.Moves.TryBegin(plan)!;
        database.RecordFixOutcome([new HistoryEntry
        {
            Type = IssueType.Ungrouped, Path = plan.Entries[0].Source, FixedAtUtc = DateTime.UtcNow,
            Action = "owned scheduled attempt", MoveOperationId = operation.Id
        }], [], [], IssueStatus.Fixed);
        var history = Assert.Single(database.History.GetHistory());
        Assert.Equal(operation.Id, history.MoveOperationId);
        Assert.Equal(history.Id, database.Moves.Read(operation.Id)!.HistoryId);
    }

    private static Issue Queue(MediaDashDb database, IssueType type, MediaMovePlan plan, string details)
    {
        database.Issues.ReplaceDetectedIssues(type, [new Issue
        {
            Type = type, ItemId = plan.ItemId, Path = plan.Entries[0].Source, DetailsJson = details,
            DetectedAtUtc = DateTime.UtcNow
        }]);
        var issue = Assert.Single(database.Issues.GetIssues(type));
        Assert.Equal(StatusTransitionResult.Applied, database.Issues.TryTransitionIssueStatus(issue.Id, [IssueStatus.Detected], IssueStatus.Queued));
        return database.Issues.GetIssue(issue.Id)!;
    }

    [Fact]
    public void LegacyMoverApprovalRequiresANewPlanWhileOtherApprovalsSurviveRestart()
    {
        var database = new MediaDashDb(Database);
        var plan = Plan();
        var legacy = Queue(database, IssueType.Ungrouped, plan, "{\"action\":\"MoveFile\"}");
        var audio = Queue(database, IssueType.AudioLanguage, plan, "{}");
        _ = new MediaDashDb(Database);
        var saved = database.Issues.GetIssue(legacy.Id)!;
        Assert.Equal(IssueStatus.Detected, saved.Status);
        Assert.True(saved.HasBlockingWarnings);
        Assert.Equal(IssueStatus.Queued, database.Issues.GetIssue(audio.Id)!.Status);
        Assert.Equal("source.mkv", File.ReadAllText(plan.Entries[0].Source));
    }

    [Fact]
    public void StructurallyCurrentMoveApprovalSurvivesAnIdleRestart()
    {
        var database = new MediaDashDb(Database);
        var plan = Plan();
        var details = System.Text.Json.JsonSerializer.Serialize(new { movePlan = plan }, MediaMovePlan.JsonOptions);
        var issue = Queue(database, IssueType.Ungrouped, plan, details);
        _ = new MediaDashDb(Database);
        Assert.Equal(IssueStatus.Queued, database.Issues.GetIssue(issue.Id)!.Status);
        Assert.Equal(details, database.Issues.GetIssue(issue.Id)!.DetailsJson);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("source-only")]
    public void CorruptClaimsAreVisibleForReviewAndCannotExposeUnreservedStaging(string corruption)
    {
        var database = new MediaDashDb(Database);
        var plan = Plan();
        var operation = database.Moves.TryBegin(plan)!;
        using (var connection = Open())
        {
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE media_move_operations SET claim_paths_json=@claims WHERE operation_id=@id";
            corrupt.Parameters.AddWithValue("@id", operation.Id);
            corrupt.Parameters.AddWithValue("@claims", corruption == "source-only" ? System.Text.Json.JsonSerializer.Serialize(new[] { plan.Entries[0].Source }) : corruption);
            corrupt.ExecuteNonQuery();
        }

        Assert.Equal("NeedsReview", database.Moves.Read(operation.Id)!.State);
        Assert.Single(database.Moves.GetNeedsReview());
        Assert.True(database.Moves.HasActiveConflict(Path.Combine(_root, "unrelated.mkv")));
    }

    [Fact]
    public void AJournalLinkFailureRollsBackHistoryAndQueueTogether()
    {
        var database = new MediaDashDb(Database);
        var plan = Plan();
        var details = System.Text.Json.JsonSerializer.Serialize(new { movePlan = plan }, MediaMovePlan.JsonOptions);
        var issue = Queue(database, IssueType.Ungrouped, plan, details);
        var operation = database.Moves.TryBegin(plan, issue.Id)!;
        using (var connection = Open())
        {
            using var block = connection.CreateCommand();
            block.CommandText = "CREATE TRIGGER reject_move_history_link BEFORE UPDATE ON media_move_operations BEGIN SELECT RAISE(ABORT,'owned write failure'); END";
            block.ExecuteNonQuery();
        }

        Assert.Throws<SqliteException>(() => database.RecordFixOutcome([new HistoryEntry
        {
            IssueId = issue.Id, Type = IssueType.Ungrouped, Path = issue.Path, Action = "owned failure", FixedAtUtc = DateTime.UtcNow,
            MoveOperationId = operation.Id
        }], [issue.Id], [IssueStatus.Queued], IssueStatus.Fixed));
        Assert.Empty(database.History.GetHistory());
        Assert.Equal(IssueStatus.Queued, database.Issues.GetIssue(issue.Id)!.Status);
        Assert.Null(database.Moves.Read(operation.Id)!.HistoryId);
        Assert.True(database.Moves.HasActiveConflict(issue.Path));
    }

    [Fact]
    public void AMalformedHistoryLinkDoesNotHideTheHistoryRowOrOfferAnOperation()
    {
        var database = new MediaDashDb(Database);
        database.History.AddHistory(new HistoryEntry { Type = IssueType.Ungrouped, Path = "owned", Action = "owned malformed link", FixedAtUtc = DateTime.UtcNow });
        using (var connection = Open())
        {
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE history SET move_operation_id=x'010203'";
            corrupt.ExecuteNonQuery();
        }

        var history = Assert.Single(database.History.GetHistory());
        Assert.Null(history.MoveOperationId);
        Assert.Equal("owned malformed link", history.Action);
    }

    [Fact]
    public void CompletedClaimsRemainUntilFinalCleanupIsDurablyRecorded()
    {
        var database = new MediaDashDb(Database);
        var plan = Plan();
        var operation = database.Moves.TryBegin(plan)!;
        Assert.True(database.Moves.Update(operation.Id, operation.OwnerToken, "Prepared", "Executing", operation.ManifestJson));
        Directory.CreateDirectory(Path.GetDirectoryName(plan.Entries[0].Destination)!);
        File.Move(plan.Entries[0].Source, plan.Entries[0].Destination);
        var manifest = MediaMoveManifest.TryRead(operation.ManifestJson, operation.Id)!;
        manifest = manifest with { Steps = [manifest.Steps[0] with { Phase = "Complete", FinalInventory = MediaMoveSnapshot.Capture(plan.Entries[0].Destination).Export() }] };
        Assert.True(database.Moves.Update(operation.Id, operation.OwnerToken, "Executing", "Completed", manifest.Serialize()));
        Assert.True(database.Moves.HasActiveConflict(plan.Entries[0].Source));
        Assert.True(database.Moves.Update(operation.Id, operation.OwnerToken, "Completed", "Completed", (manifest with { CleanupComplete = true }).Serialize()));
        Assert.False(database.Moves.HasActiveConflict(plan.Entries[0].Source));
        _ = new MediaDashDb(Database);
        Assert.Empty(database.Moves.GetNeedsReview());
        Assert.Equal("Completed", database.Moves.Read(operation.Id)!.State);
    }

    [Fact]
    public void UpgradeFromVersionElevenPreservesHistoryAndUnrelatedApprovals()
    {
        var database = new MediaDashDb(Database);
        var plan = Plan();
        var audio = Queue(database, IssueType.AudioLanguage, plan, "{\"owned\":true}");
        database.History.AddHistory(new HistoryEntry { Type = IssueType.AudioLanguage, Path = audio.Path, Action = "existing history", FixedAtUtc = DateTime.UtcNow });
        using (var connection = Open())
        {
            using var oldSchema = connection.CreateCommand();
            oldSchema.CommandText = "DROP TABLE media_move_operations; ALTER TABLE history DROP COLUMN move_operation_id; PRAGMA user_version=11";
            oldSchema.ExecuteNonQuery();
        }

        var upgraded = new MediaDashDb(Database);
        Assert.Equal("existing history", Assert.Single(upgraded.History.GetHistory()).Action);
        Assert.Equal(IssueStatus.Queued, upgraded.Issues.GetIssue(audio.Id)!.Status);
        Assert.Equal("{\"owned\":true}", upgraded.Issues.GetIssue(audio.Id)!.DetailsJson);
        using var reopened = Open();
        Assert.Equal((long)MediaDashSchema.Version, Scalar(reopened, "PRAGMA user_version"));
        Assert.Equal(1L, Scalar(reopened, "SELECT COUNT(*) FROM pragma_table_info('history') WHERE name='move_operation_id'"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"version\":99}")]
    [InlineData("{\"version\":1,\"plan\":null}")]
    public void UnknownOrMalformedActiveManifestNeverThrowsOrDropsItsReservation(string invalid)
    {
        var database = new MediaDashDb(Database);
        var plan = Plan();
        var operation = database.Moves.TryBegin(plan)!;
        using (var connection = Open())
        {
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE media_move_operations SET manifest_json=@json WHERE operation_id=@id";
            corrupt.Parameters.AddWithValue("@json", invalid);
            corrupt.Parameters.AddWithValue("@id", operation.Id);
            corrupt.ExecuteNonQuery();
        }

        Assert.Equal("NeedsReview", database.Moves.Read(operation.Id)!.State);
        Assert.Single(database.Moves.GetNeedsReview());
        Assert.True(database.Moves.HasActiveConflict(plan.Entries[0].Source));
        Assert.Null(database.Moves.TryBegin(plan));
    }

    [Fact]
    public void ACompletedMoveWithoutHistoryCannotReplayTheSameQueuedApproval()
    {
        var database = new MediaDashDb(Database);
        var plan = Plan();
        var issue = Queue(database, IssueType.Ungrouped, plan, System.Text.Json.JsonSerializer.Serialize(new { movePlan = plan }, MediaMovePlan.JsonOptions));
        var operation = database.Moves.TryBegin(plan, issue.Id)!;
        Assert.True(database.Moves.Update(operation.Id, operation.OwnerToken, "Prepared", "Executing", operation.ManifestJson));
        Directory.CreateDirectory(Path.GetDirectoryName(plan.Entries[0].Destination)!);
        File.Move(plan.Entries[0].Source, plan.Entries[0].Destination);
        var manifest = MediaMoveManifest.TryRead(operation.ManifestJson, operation.Id)!;
        manifest = manifest with { CleanupComplete = true, Steps = [manifest.Steps[0] with { Phase = "Complete", FinalInventory = MediaMoveSnapshot.Capture(plan.Entries[0].Destination).Export() }] };
        Assert.True(database.Moves.Update(operation.Id, operation.OwnerToken, "Executing", "Completed", manifest.Serialize()));
        Assert.Empty(database.History.GetHistory());
        File.Move(plan.Entries[0].Destination, plan.Entries[0].Source); // external recovery retains the original identity
        Assert.Equal(IssueStatus.Queued, database.Issues.GetIssue(issue.Id)!.Status);
        Assert.Null(database.Moves.TryBegin(plan, issue.Id));
    }

    [Fact]
    public void OnePlanCannotMoveAContainerAndItsChildAsIndependentEntries()
    {
        var database = new MediaDashDb(Database);
        var plan = Plan("folder/source.mkv");
        var folder = Path.GetDirectoryName(plan.Entries[0].Source)!;
        var overlapping = (plan with { Entries = [new(folder, Path.Combine(_root, "destination"), true, MediaMoveSnapshot.Capture(folder).Export()), plan.Entries[0]] }).Seal();
        Assert.Null(database.Moves.TryBegin(overlapping));
    }

    [Fact]
    public void TheClaimedDestinationContainerMustActuallyContainThePlannedTargets()
    {
        var database = new MediaDashDb(Database);
        var plan = (Plan() with { DestinationContainer = Path.Combine(_root, "unrelated") }).Seal();
        Assert.Null(database.Moves.TryBegin(plan));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void APreparedMoveAndARecycleRestoreCannotOwnTheSameDestination()
    {
        var database = new MediaDashDb(Database);
        var plan = Plan();
        Assert.NotNull(database.Moves.TryBegin(plan));
        Assert.Null(database.TryBeginRestore("owned bin entry", 1, Path.Combine(_root, "bin", "source.mkv"), plan.Entries[0].Destination));
    }

    [Fact]
    public void UpgradeFromVersionTwelvePreservesUncertainRestoreAndBlocksUnknownAliases()
    {
        var database = new MediaDashDb(Database);
        var plan = Plan();
        var source = Path.Combine(_root, "bin", "held.mkv");
        var target = Path.Combine(_root, "restored", "held.mkv");
        var owner = database.TryBeginRestore("legacy owned entry", null, source, target);
        Assert.NotNull(owner);
        using (var connection = Open())
        {
            using var old = connection.CreateCommand();
            old.CommandText = "ALTER TABLE restore_operations DROP COLUMN native_claims_json; PRAGMA user_version=12";
            old.ExecuteNonQuery();
        }

        var upgraded = new MediaDashDb(Database);
        var held = Assert.Single(upgraded.GetRestoreOperations());
        Assert.Equal(owner, held.OperationId);
        Assert.Equal("NeedsReview", held.State);
        Assert.Equal(source, held.SourcePath);
        Assert.Equal(target, held.DestinationPath);
        Assert.Null(upgraded.Moves.TryBegin(plan));
        Assert.True(File.Exists(plan.Entries[0].Source));
        Assert.False(File.Exists(plan.Entries[0].Destination));
        using var reopened = Open();
        Assert.Equal((long)MediaDashSchema.Version, Scalar(reopened, "PRAGMA user_version"));
        Assert.Equal(1L, Scalar(reopened, "SELECT COUNT(*) FROM pragma_table_info('restore_operations') WHERE name='native_claims_json'"));
        Assert.Equal(1L, Scalar(reopened, "SELECT COUNT(*) FROM restore_operations WHERE native_claims_json IS NULL"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")] 
    [InlineData("[{\"fileId\":1}]")]
    public void MalformedRestoreNativeClaimsNeverReleaseUnknownAliases(string json)
    {
        var database = new MediaDashDb(Database);
        var plan = Plan();
        Assert.NotNull(database.TryBeginRestore("held", null, Path.Combine(_root, "bin", "held.mkv"), Path.Combine(_root, "restore", "held.mkv")));
        using (var connection = Open())
        {
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE restore_operations SET native_claims_json=@json";
            corrupt.Parameters.AddWithValue("@json", json);
            corrupt.ExecuteNonQuery();
        }

        Assert.Null(database.Moves.TryBegin(plan));
        Assert.Null(database.TryBeginRestore("other", null, Path.Combine(_root, "other-bin"), Path.Combine(_root, "other-target")));
    }

    [Fact]
    public void NativeClaimsCannotBeRewrittenWhileAdvancingJournalProgress()
    {
        var database = new MediaDashDb(Database);
        var operation = database.Moves.TryBegin(Plan())!;
        var manifest = MediaMoveManifest.TryRead(operation.ManifestJson, operation.Id)!;
        var changed = manifest.NativeClaims!.Select(claim => claim with { FileId = claim.FileId + 1 }).ToArray();
        Assert.False(database.Moves.Update(operation.Id, operation.OwnerToken, "Prepared", "Executing", (manifest with { NativeClaims = changed }).Serialize()));
        Assert.Equal(operation.ManifestJson, database.Moves.Read(operation.Id)!.ManifestJson);
        Assert.True(database.Moves.Update(operation.Id, operation.OwnerToken, "Prepared", "Executing", manifest.Serialize()));
    }

    [Fact]
    public void APendingRecycleRestoreBlocksAnOverlappingMoveClaim()
    {
        var database = new MediaDashDb(Database);
        var plan = Plan();
        Assert.NotNull(database.TryBeginRestore("owned bin entry", 1, Path.Combine(_root, "bin", "source.mkv"), plan.Entries[0].Destination));
        Assert.Null(database.Moves.TryBegin(plan));
    }

    [Fact]
    public async Task ConcurrentMoveAndRestoreClaimsHaveOnlyOneOwner()
    {
        var database = new MediaDashDb(Database);
        var plan = Plan();
        var move = Task.Run(() => database.Moves.TryBegin(plan));
        var restore = Task.Run(() => database.TryBeginRestore("owned bin entry", 1, Path.Combine(_root, "bin", "source.mkv"), plan.Entries[0].Destination));
        await Task.WhenAll(move, restore);
        var moved = await move;
        var restored = await restore;
        Assert.True((moved is null) != (restored is null), "The shared SQLite claim boundary must select exactly one owner.");
    }
}
