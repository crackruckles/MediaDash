using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Probing;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>Durable media-move ownership and restart reconciliation.</summary>
public sealed class MediaMoveStore
{
    private readonly MediaDashConnectionFactory _connectionFactory;

    /// <summary>Initializes a new instance of the <see cref="MediaMoveStore"/> class.</summary>
    /// <param name="connectionFactory">Shared SQLite connection factory.</param>
    public MediaMoveStore(MediaDashConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    internal Operation? TryBegin(MediaMovePlan plan, long issueId = 0, string? undoOf = null)
    {
        if (!plan.HasValidDefinition() || plan.BlockingReasons.Length != 0)
        {
            return null;
        }

        var id = Guid.NewGuid().ToString("N");
        var owner = Guid.NewGuid().ToString("N");
        var manifest = MediaMoveManifest.Create(plan, id) with { UndoOf = undoOf };
        var claims = manifest.ClaimPaths();
        using var connection = _connectionFactory.Open();
        // Acquire the SQLite write lock before checking claims. Separate readers followed by
        // inserts would let two independent processes both observe a vacant overlapping path.
        using var transaction = connection.BeginTransaction(deferred: false);
        Operation? original = null;
        if (undoOf is not null)
        {
            original = Read(connection, transaction, undoOf);
            if (original is not { State: "Completed", CleanupComplete: true }
                || MediaMoveManifest.TryRead(original.ManifestJson, original.Id) is not { UndoOf: null } previous
                || !previous.HasCompletePayloads() || !IsReverse(previous, plan))
            {
                return null;
            }
        }

        if (Active(connection, transaction).Any(operation => Conflicts(operation, claims, manifest))
            || RestoreOperationStore.HasActivePathConflict(connection, transaction, claims, manifest.NativeClaims))
        {
            return null;
        }

        if (issueId > 0 && undoOf is null)
        {
            using var repeated = connection.CreateCommand();
            repeated.Transaction = transaction;
            repeated.CommandText = "SELECT 1 FROM media_move_operations WHERE issue_id=@issue AND fingerprint=@fingerprint AND state NOT IN ('Undone','Released') LIMIT 1";
            repeated.Parameters.AddWithValue("@issue", issueId);
            repeated.Parameters.AddWithValue("@fingerprint", plan.Fingerprint);
            if (repeated.ExecuteScalar() is not null)
            {
                return null;
            }
        }

        var json = manifest.Serialize();
        var claimJson = JsonSerializer.Serialize(claims);
        if (original is not null)
        {
            using var claimOriginal = connection.CreateCommand();
            claimOriginal.Transaction = transaction;
            claimOriginal.CommandText = "UPDATE media_move_operations SET state='Undoing',owner_token=@owner,updated_utc_ticks=@ticks WHERE operation_id=@id AND state='Completed' AND cleanup_complete=1";
            claimOriginal.Parameters.AddWithValue("@owner", owner);
            claimOriginal.Parameters.AddWithValue("@ticks", DateTime.UtcNow.Ticks);
            claimOriginal.Parameters.AddWithValue("@id", original.Id);
            if (claimOriginal.ExecuteNonQuery() != 1)
            {
                return null;
            }
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO media_move_operations
                (operation_id,issue_id,item_id,plan_version,fingerprint,manifest_json,claim_paths_json,state,owner_token,created_utc_ticks,updated_utc_ticks,undo_of)
            VALUES (@id,@issue,@item,1,@fingerprint,@manifest,@claims,'Prepared',@owner,@ticks,@ticks,@undo)
            """;
        insert.Parameters.AddWithValue("@id", id);
        insert.Parameters.AddWithValue("@issue", issueId);
        insert.Parameters.AddWithValue("@item", plan.ItemId.ToString("N"));
        insert.Parameters.AddWithValue("@fingerprint", plan.Fingerprint);
        insert.Parameters.AddWithValue("@manifest", json);
        insert.Parameters.AddWithValue("@claims", claimJson);
        insert.Parameters.AddWithValue("@owner", owner);
        insert.Parameters.AddWithValue("@ticks", DateTime.UtcNow.Ticks);
        insert.Parameters.AddWithValue("@undo", (object?)undoOf ?? DBNull.Value);
        insert.ExecuteNonQuery();
        transaction.Commit();
        return new Operation(id, owner, issueId, "Prepared", json, claimJson, null, null, false) { VerifiedClaims = claims, VerifiedPlan = plan, VerifiedPhysicalClaims = PhysicalClaims(manifest).ToHashSet(), VerifiedNativeClaims = manifest.NativeClaims };
    }

    internal Operation? Read(string id)
    {
        using var connection = _connectionFactory.Open();
        return Read(connection, null, id);
    }

    internal static Operation? Read(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT operation_id,owner_token,issue_id,state,manifest_json,claim_paths_json,history_id,detail,cleanup_complete FROM media_move_operations WHERE operation_id=@id";
        command.Parameters.AddWithValue("@id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Read-only identity/existence checks on canonical paths: Files routes first validate library containment; scheduled callers use recorded plans. This method never reads payload contents or changes files.")]
    internal bool HasActiveConflict(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            return true;
        }

        List<Operation> active;
        using (var connection = _connectionFactory.Open())
        {
            active = Active(connection, null);
        }

        if (active.Count == 0)
        {
            return false;
        }

        var full = Path.GetFullPath(path);
        if (active.Any(operation => Conflicts(operation, [full])))
        {
            return true;
        }

        var physical = active.SelectMany(operation => operation.VerifiedPhysicalClaims ?? []).ToHashSet();
        // Probe only outside SQLite transactions. Alias-aware point guards protect writers;
        // scan/persistence snapshots remain pure in-memory predicates.
        try
        {
            var requested = MediaMovePathClaims.Capture([full]);
            return requested.Any(claim => physical.Contains((claim.Volume, claim.FileId)))
                || active.Any(operation => MediaMovePathClaims.Overlap(operation.VerifiedNativeClaims, requested));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    internal Func<string, bool> CaptureReservationCheck() => CaptureReservations().Path;

    internal (Func<string, bool> Path, Func<Guid, bool> Item) CaptureReservations()
    {
        using var connection = _connectionFactory.Open();
        var paths = CaptureReservationCheck(connection, null, out _, out var items);
        return (paths, items);
    }

    internal bool HasActiveItemConflict(Guid itemId) => itemId != Guid.Empty && CaptureReservations().Item(itemId);

    internal static Func<string, bool> CaptureReservationCheck(SqliteConnection connection, SqliteTransaction? transaction, out bool hasReservations)
        => CaptureReservationCheck(connection, transaction, out hasReservations, out _);

    internal static Func<string, bool> CaptureReservationCheck(SqliteConnection connection, SqliteTransaction? transaction, out bool hasReservations, out Func<Guid, bool> itemReserved)
    {
        var active = Active(connection, transaction);
        hasReservations = active.Count > 0;
        var unverifiable = active.Any(operation => operation.VerifiedPlan is null);
        var itemIds = active.Where(operation => operation.VerifiedPlan is not null).SelectMany(operation => ItemClaims(operation.VerifiedPlan!)).ToHashSet();
        itemReserved = itemId => itemId != Guid.Empty && (unverifiable || itemIds.Contains(itemId));
        return path => active.Count > 0 && (NormalizeClaimPath(path) is not { } full || active.Any(operation => Conflicts(operation, [full])));
    }

    internal static bool HasActivePathConflict(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<string> paths, MediaMovePathClaims.Claim[]? nativeClaims = null)
    {
        var active = Active(connection, transaction);
        if (active.Count == 0)
        {
            return false;
        }

        var incoming = paths.Select(NormalizeClaimPath).ToArray();
        return incoming.Any(path => path is null) || active.Any(operation => Conflicts(operation, incoming!)
            || MediaMovePathClaims.Overlap(operation.VerifiedNativeClaims, nativeClaims));
    }

    internal static string? NormalizeClaimPath(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) ? Path.GetFullPath(path) : null;
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    internal IReadOnlyList<Operation> GetPendingUserData(string? path)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        var comparison = OperatingSystem.IsWindows() ? "NOCASE" : "BINARY";
        command.CommandText = """
            SELECT operation_id,owner_token,issue_id,state,manifest_json,claim_paths_json,history_id,detail,cleanup_complete
            FROM media_move_operations
            WHERE state='Completed' AND cleanup_complete=1 AND json_valid(manifest_json)
              AND EXISTS (SELECT 1 FROM json_each(manifest_json,'$.userData')
                WHERE json_extract(value,'$.applied')=0
            """ + " AND (@path IS NULL OR json_extract(value,'$.destinationPath')=@path COLLATE " + comparison + ")) ORDER BY created_utc_ticks,operation_id";
        command.Parameters.AddWithValue("@path", (object?)path ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var operations = new List<Operation>();
        while (reader.Read())
        {
            operations.Add(ReadRow(reader));
        }

        return operations;
    }

    internal bool SaveUserDataProgress(Operation operation, MediaMoveManifest updated)
    {
        var previous = MediaMoveManifest.TryRead(operation.ManifestJson, operation.Id);
        if (previous is null || (updated with { UserData = previous.UserData }).Serialize() != previous.Serialize()
            || updated.UserData.Length != previous.UserData.Length
            || previous.UserData.Zip(updated.UserData).Any(pair => pair.First.Applied && !pair.Second.Applied)
            || JsonSerializer.Serialize(previous.UserData.Select(snapshot => snapshot with { Applied = false }), MediaMovePlan.JsonOptions)
                != JsonSerializer.Serialize(updated.UserData.Select(snapshot => snapshot with { Applied = false }), MediaMovePlan.JsonOptions)
            || MediaMoveManifest.TryRead(updated.Serialize(), operation.Id) is null)
        {
            return false;
        }

        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE media_move_operations SET manifest_json=@updated WHERE operation_id=@id AND state='Completed' AND cleanup_complete=1 AND manifest_json=@previous";
        command.Parameters.AddWithValue("@updated", updated.Serialize());
        command.Parameters.AddWithValue("@id", operation.Id);
        command.Parameters.AddWithValue("@previous", operation.ManifestJson);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>Parked moves (needing review) whose reservation blocks the given plan.</summary>
    /// <param name="plan">A proposed move.</param>
    /// <returns>The parked operations that reserve part of it.</returns>
    internal IReadOnlyList<Operation> FindParkedBlocking(MediaMovePlan plan)
    {
        var incoming = new MediaMoveManifest { Plan = plan };
        var paths = incoming.ClaimPaths().Select(NormalizeClaimPath).Where(path => path is not null).Select(path => path!).ToArray();
        using var connection = _connectionFactory.Open();
        return Active(connection, null).Where(operation => operation.State == "NeedsReview" && Conflicts(operation, paths, incoming)).ToArray();
    }

    /// <summary>
    /// Ends a parked move's reservation without touching any file. Its manifest and recorded paths
    /// are kept for the record; it no longer blocks other moves.
    /// </summary>
    /// <param name="id">Operation id.</param>
    /// <param name="detail">What was done.</param>
    /// <returns>True when the operation was parked and is now released.</returns>
    internal bool Release(string id, string detail)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE media_move_operations SET state='Released', owner_token='', detail=@detail, updated_utc_ticks=@ticks WHERE operation_id=@id AND state='NeedsReview'";
        command.Parameters.AddWithValue("@detail", detail);
        command.Parameters.AddWithValue("@ticks", DateTime.UtcNow.Ticks);
        command.Parameters.AddWithValue("@id", id);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>Source and destination of every file a completed move placed, oldest first.</summary>
    /// <returns>The moves.</returns>
    internal IReadOnlyList<(string Source, string Destination)> GetCompletedMoves()
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT operation_id,manifest_json FROM media_move_operations WHERE state='Completed' ORDER BY created_utc_ticks";
        using var reader = command.ExecuteReader();
        var result = new List<(string Source, string Destination)>();
        while (reader.Read())
        {
            if (reader.IsDBNull(1) || Fixers.MediaMoveManifest.TryRead(reader.GetString(1), reader.GetString(0)) is not { } manifest)
            {
                continue;
            }

            result.AddRange(manifest.Plan.Entries.Where(entry => !entry.IsDirectory).Select(entry => (entry.Source, entry.Destination)));
        }

        return result;
    }

    internal IReadOnlyList<Operation> GetNeedsReview()
    {
        using var connection = _connectionFactory.Open();
        return Active(connection, null).Where(operation => operation.State == "NeedsReview").ToArray();
    }

    internal IReadOnlyList<Operation> GetVisible(long hiddenBeforeUtcTicks = 0)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT operation_id,owner_token,issue_id,state,manifest_json,claim_paths_json,history_id,detail,cleanup_complete FROM media_move_operations WHERE state IN ('Completed','Undone') AND cleanup_complete=1 AND (@hidden=0 OR updated_utc_ticks>@hidden) ORDER BY updated_utc_ticks DESC LIMIT 200";
        command.Parameters.AddWithValue("@hidden", hiddenBeforeUtcTicks);
        using var reader = command.ExecuteReader();
        var result = new List<Operation>();
        while (reader.Read())
        {
            result.Add(ReadRow(reader));
        }

        reader.Close();
        return Active(connection, null, hiddenBeforeUtcTicks).Concat(result).DistinctBy(operation => operation.Id).ToArray();
    }

    internal bool Update(string id, string owner, string expectedState, string state, string manifestJson, string? detail = null)
    {
        using var connection = _connectionFactory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var changed = UpdateWithinTransaction(connection, transaction, id, owner, expectedState, state, manifestJson, detail);
        transaction.Commit();
        return changed;
    }

    internal static bool UpdateWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        string owner,
        string expectedState,
        string state,
        string manifestJson,
        string? detail = null)
    {
        if (!Guid.TryParseExact(owner, "N", out _) || !TransitionAllowed(expectedState, state)
            || MediaMoveManifest.TryRead(manifestJson, id) is not { } manifest
            || ((state is "Completed" or "Undone" || manifest.CleanupComplete) && !manifest.HasCompletePayloads()))
        {
            return false;
        }

        var current = Read(connection, transaction, id);
        if (current is null || current.State != expectedState || current.OwnerToken != owner)
        {
            return false;
        }

        if (MediaMoveManifest.TryRead(current.ManifestJson, id) is not { } previous
            || JsonSerializer.Serialize(previous.NativeClaims, MediaMovePlan.JsonOptions) != JsonSerializer.Serialize(manifest.NativeClaims, MediaMovePlan.JsonOptions))
        {
            return false;
        }

        var incoming = manifest.ClaimPaths();
        if (Active(connection, transaction).Any(operation => operation.Id != id
            && !(operation.OwnerToken == owner && ((manifest.UndoOf == operation.Id && operation.State == "Undoing")
                || (current.State == "Undoing" && MediaMoveManifest.TryRead(operation.ManifestJson, operation.Id)?.UndoOf == id)))
            && Conflicts(operation, incoming, manifest)))
        {
            return false;
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE media_move_operations
            SET state=@state, manifest_json=@manifest, claim_paths_json=@claims,
                detail=@detail, cleanup_complete=@clean, updated_utc_ticks=@ticks
            WHERE operation_id=@id AND owner_token=@owner AND state=@expected AND fingerprint=@fingerprint
            """;
        update.Parameters.AddWithValue("@state", state);
        update.Parameters.AddWithValue("@manifest", manifestJson);
        update.Parameters.AddWithValue("@claims", JsonSerializer.Serialize(incoming));
        update.Parameters.AddWithValue("@detail", (object?)detail ?? DBNull.Value);
        update.Parameters.AddWithValue("@clean", manifest.CleanupComplete ? 1 : 0);
        update.Parameters.AddWithValue("@ticks", DateTime.UtcNow.Ticks);
        update.Parameters.AddWithValue("@id", id);
        update.Parameters.AddWithValue("@owner", owner);
        update.Parameters.AddWithValue("@expected", expectedState);
        update.Parameters.AddWithValue("@fingerprint", manifest.Plan.Fingerprint);
        var changed = update.ExecuteNonQuery() == 1;
        return changed;
    }

    private static bool IsReverse(MediaMoveManifest original, MediaMovePlan reverse) => reverse.ItemId == original.Plan.ItemId
        && reverse.Type == original.Plan.Type && reverse.Entries.Length == original.Plan.Entries.Length
        && reverse.Entries.Select((entry, index) => entry.Source == original.Plan.Entries[index].Destination
            && entry.Destination == original.Plan.Entries[index].Source && entry.IsDirectory == original.Plan.Entries[index].IsDirectory
            && entry.Inventory.SequenceEqual(original.Steps[index].FinalInventory)).All(valid => valid);

    internal Operation? CompleteUndo(SqliteConnection connection, SqliteTransaction transaction, string originalId, string reverseId, string owner)
    {
        var original = Read(connection, transaction, originalId);
        var reverse = Read(connection, transaction, reverseId);
        if (original is not { State: "Undoing" } || original.OwnerToken != owner
            || reverse is not { State: "Completed", CleanupComplete: true } || reverse.OwnerToken != owner
            || MediaMoveManifest.TryRead(original.ManifestJson, originalId) is not { } previous
            || MediaMoveManifest.TryRead(reverse.ManifestJson, reverseId) is not { } undone
            || undone.UndoOf != originalId || !undone.HasCompletePayloads() || !IsReverse(previous, undone.Plan))
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE media_move_operations SET state='Undone',owner_token='',updated_utc_ticks=@ticks WHERE operation_id=@id AND state='Undoing' AND owner_token=@owner";
        command.Parameters.AddWithValue("@ticks", DateTime.UtcNow.Ticks);
        command.Parameters.AddWithValue("@id", originalId);
        command.Parameters.AddWithValue("@owner", owner);
        return command.ExecuteNonQuery() == 1 ? original : null;
    }

    private static bool TransitionAllowed(string previous, string next) => (previous, next) switch
    {
        ("Prepared", "Executing" or "NeedsReview") => true,
        ("Executing", "Executing" or "Completed" or "NeedsReview") => true,
        ("Completed", "Completed" or "Undoing" or "NeedsReview") => true,
        ("Undoing", "Undoing" or "Undone" or "NeedsReview") => true,
        _ => false
    };

    private static List<Operation> Active(SqliteConnection connection, SqliteTransaction? transaction, long hiddenBeforeUtcTicks = 0)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT operation_id,owner_token,issue_id,state,manifest_json,claim_paths_json,history_id,detail,cleanup_complete
            FROM media_move_operations
            WHERE state NOT IN ('Undone','Released') AND NOT (state='Completed' AND cleanup_complete=1)
                AND (@hidden=0 OR updated_utc_ticks>@hidden)
            ORDER BY created_utc_ticks,operation_id
            """;
        command.Parameters.AddWithValue("@hidden", hiddenBeforeUtcTicks);
        using var reader = command.ExecuteReader();
        var result = new List<Operation>();
        while (reader.Read())
        {
            result.Add(ReadRow(reader));
        }

        return result;
    }

    private static Operation ReadRow(SqliteDataReader reader)
    {
        var id = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty;
        try
        {
            var operation = new Operation(
                id,
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt64(8) != 0);
            var manifest = MediaMoveManifest.TryRead(operation.ManifestJson, id);
            var claims = VerifyClaims(operation, manifest);
            if (!Guid.TryParseExact(id, "N", out _) || operation.State is not ("Prepared" or "Executing" or "Completed" or "Undoing" or "Undone" or "NeedsReview" or "Released")
                || claims is null)
            {
                Api.Diagnostics.Record("MediaMove.InvalidManifest", "The record of move '" + id + "' is damaged and cannot be read. MediaDash keeps the files it involved locked against other changes. Please report this on GitHub with the Jellyfin log so it can be repaired.");
                return operation with { State = "NeedsReview", Detail = "This move record could not be verified. Preserve its original, destination and recovery paths for review." };
            }

            return operation with
            {
                VerifiedClaims = claims, VerifiedPlan = manifest!.Plan, VerifiedPhysicalClaims = PhysicalClaims(manifest).ToHashSet(),
                VerifiedNativeClaims = MediaMovePathClaims.Valid(manifest.NativeClaims, manifest.NativeClaimPaths()) ? manifest.NativeClaims : null
            };
        }
        catch (Exception error) when (error is InvalidCastException or FormatException or OverflowException)
        {
            Api.Diagnostics.Record("MediaMove.InvalidRow", "The record of move '" + id + "' is damaged and cannot be read. Other moves of the same files are blocked until it is sorted out. Please report this on GitHub with the Jellyfin log.");
            return new Operation(id, string.Empty, 0, "NeedsReview", string.Empty, string.Empty, null, "Unverifiable move record.", false);
        }
    }

    private static string[]? VerifyClaims(Operation operation, MediaMoveManifest? manifest)
    {
        try
        {
            var claims = JsonSerializer.Deserialize<string[]>(operation.ClaimPathsJson);
            if (manifest is null || claims is not { Length: > 0 }
                || claims.Any(claim => string.IsNullOrWhiteSpace(claim) || !Path.IsPathFullyQualified(claim) || Path.GetFullPath(claim) != claim))
            {
                return null;
            }

            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            return claims.ToHashSet(comparer).SetEquals(manifest.ClaimPaths()) ? claims : null;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static IEnumerable<Guid> ItemClaims(MediaMovePlan plan) => plan.ItemBindings.Select(binding => binding.ItemId).Append(plan.ItemId).Where(id => id != Guid.Empty);

    private static IEnumerable<(ulong Volume, ulong FileId)> PhysicalClaims(MediaMoveManifest manifest) => manifest.Plan.Entries.SelectMany(entry => entry.Inventory)
        .Concat(manifest.Steps.SelectMany(step => step.StageInventory.Concat(step.RecoveryInventory).Concat(step.FinalInventory)))
        .Where(payload => payload.FileId != 0).Select(payload => (payload.Volume, payload.FileId));

    private static bool Conflicts(Operation operation, IEnumerable<string> paths, MediaMoveManifest incoming) => Conflicts(operation, paths)
        || operation.VerifiedPlan is null || ItemClaims(incoming.Plan).Intersect(ItemClaims(operation.VerifiedPlan)).Any()
        || MediaMovePathClaims.Overlap(operation.VerifiedNativeClaims, incoming.NativeClaims)
        || operation.VerifiedPhysicalClaims is null || operation.VerifiedPhysicalClaims.Overlaps(PhysicalClaims(incoming));

    private static bool Conflicts(Operation operation, IEnumerable<string> paths) => operation.VerifiedClaims is not { Length: > 0 } claims
        || paths.Any(path => claims.Any(claim => MediaMovePlanner.Overlap(path, claim)));

    internal int ReconcileAtStartup()
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        // No filesystem operation belongs here. A crash can occur on either side of every
        // journal boundary; retain the exact manifest and its claims for explicit review.
        command.CommandText = """
            UPDATE media_move_operations
            SET state='NeedsReview', owner_token='', updated_utc_ticks=@ticks,
                detail='Jellyfin stopped before this move was fully finalized. Review its original, destination and recovery paths before continuing.'
            WHERE state IN ('Prepared','Executing','Undoing') OR (state='Completed' AND cleanup_complete=0)
            """;
        command.Parameters.AddWithValue("@ticks", DateTime.UtcNow.Ticks);
        return command.ExecuteNonQuery();
    }

    internal static void LinkInsertedHistory(SqliteConnection connection, SqliteTransaction transaction, string operationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE media_move_operations SET history_id=COALESCE(history_id,last_insert_rowid()) WHERE operation_id=@id";
        command.Parameters.AddWithValue("@id", operationId);
        command.ExecuteNonQuery();
    }

    internal sealed record Operation(string Id, string OwnerToken, long IssueId, string State, string ManifestJson, string ClaimPathsJson, long? HistoryId, string? Detail, bool CleanupComplete)
    {
        internal string[]? VerifiedClaims { get; init; }

        internal MediaMovePlan? VerifiedPlan { get; init; }

        internal HashSet<(ulong Volume, ulong FileId)>? VerifiedPhysicalClaims { get; init; }

        internal MediaMovePathClaims.Claim[]? VerifiedNativeClaims { get; init; }
    }
}
