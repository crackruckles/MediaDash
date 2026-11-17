using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>Executes an approved complete media move through its durable journal.</summary>
public sealed class MediaMoveService
{
    private readonly MediaDashDb _db;
    private readonly LibraryGuard _guard;
    private readonly ProtectionService _protection;
    private readonly ILibraryManager _library;
    private readonly ILogger<MediaMoveService> _logger;
    private readonly MediaMoveUserData? _userData;
    private readonly NativeLibraryRefresher? _nativeRefresh;
    private readonly ILibraryMonitor? _monitor;

    /// <summary>Initializes a new instance of the <see cref="MediaMoveService"/> class.</summary>
    /// <param name="db">Journal, approvals and restoration markers.</param>
    /// <param name="guard">Current library scope.</param>
    /// <param name="protection">Current path protection.</param>
    /// <param name="library">Current item metadata.</param>
    /// <param name="logger">Recovery diagnostics.</param>
    /// <param name="userData">Durable preservation of native user state across reindexing.</param>
    /// <param name="nativeRefresh">Awaited bounded native membership and target metadata refresh.</param>
    /// <param name="libraryMonitor">Native temporary suppression for the planned mutation paths.</param>
    public MediaMoveService(MediaDashDb db, LibraryGuard guard, ProtectionService protection, ILibraryManager library, ILogger<MediaMoveService> logger, MediaMoveUserData? userData = null, NativeLibraryRefresher? nativeRefresh = null, ILibraryMonitor? libraryMonitor = null)
    {
        _db = db;
        _guard = guard;
        _protection = protection;
        _library = library;
        _logger = logger;
        _userData = userData;
        _nativeRefresh = nativeRefresh;
        _monitor = nativeRefresh is null ? null : libraryMonitor;
    }

    internal Action<string, int>? Checkpoint { get; set; }

    internal bool HandlesNativeRefresh => _nativeRefresh is not null;

    internal async Task<FixResult> UndoAsync(string operationId, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MediaMoveStore.Operation? reverse = null;
        MediaMoveStore.Operation? original = null;
        MediaMoveManifest? previous = null;
        NativeMoveWatchScope? undoDirectories = null;
        try
        {
            original = _db.Moves.Read(operationId);
            previous = original is null ? null : MediaMoveManifest.TryRead(original.ManifestJson, original.Id);
            if (original is not { State: "Completed", CleanupComplete: true } || previous is not { UndoOf: null }
                || !previous.HasCompletePayloads())
            {
                return FixResult.Refused(FixReason.MoveNeedsReview, "This move is not a fully completed operation available for Undo. Review its recorded paths before continuing.");
            }

            var entries = previous.Plan.Entries.Select((entry, index) => new MediaMovePlan.Entry(entry.Destination, entry.Source, entry.IsDirectory, previous.Steps[index].FinalInventory)).ToArray();
            var finalScopes = MediaMovePlanner.CaptureSourceScopes(entries, previous.Plan.IsGenericSelection);
            if (finalScopes.Length != previous.FinalScopes.Length || finalScopes.Any(scope => !previous.FinalScopes.Contains(scope)))
            {
                throw new IOException("The moved item's companions changed after completion. Scan and review the current files before Undo.");
            }

            var container = MediaMoveManifest.ClaimContainer(entries[0].Destination, entries[0].IsDirectory);
            while (entries.Any(entry => !LibraryGuard.IsUnder(entry.Destination, container)))
            {
                container = Path.GetDirectoryName(container) ?? throw new IOException("The original locations have no common verified move container.");
            }

            var primary = previous.Plan.Entries.FirstOrDefault(entry => LibraryGuard.IsUnder(previous.Plan.ItemPath, entry.Source))
                ?? throw new IOException("The original item's source binding could not be verified.");
            var plan = new MediaMovePlan
            {
                Type = previous.Plan.Type, ItemId = previous.Plan.ItemId,
                ItemPath = Path.GetFullPath(Path.Combine(primary.Destination, Path.GetRelativePath(primary.Source, previous.Plan.ItemPath))),
                Identity = previous.Plan.Identity, ContainerIdentity = previous.Plan.ContainerIdentity,
                IndependentFiles = previous.Plan.IndependentFiles,
                PolicyStamp = MediaMovePlanner.PolicyStamp(Plugin.Instance!.Configuration, previous.Plan.Type),
                DestinationContainer = container, DestinationParents = MediaMoveManifest.CaptureDestinationParents(entries),
                SourceScopes = finalScopes, Entries = entries,
                ItemBindings = previous.Plan.ItemBindings.Select(binding =>
                {
                    var entry = previous.Plan.Entries.First(candidate => MediaMovePlanner.Overlap(binding.Path, candidate.Source));
                    return binding with { Path = Path.GetFullPath(Path.Combine(entry.Destination, Path.GetRelativePath(entry.Source, binding.Path))) };
                }).ToArray()
            }.Seal();
            var issue = new Issue { Id = original.IssueId, Type = plan.Type, ItemId = plan.ItemId, Path = entries[0].Source };
            if (!Plugin.Instance!.Configuration.DryRun)
            {
                undoDirectories = NativeMoveWatchScope.Begin(_monitor, previous.CreatedDirectories.Select(directory => directory.Path));
            }

            var result = await ExecutePlanAsync(
                issue,
                plan,
                progress,
                () =>
            {
                reverse = _db.Moves.TryBegin(plan, original.IssueId, original.Id);
                return reverse;
            },
                undo: true,
                authorize: currentManifest =>
            {
                var current = _db.Moves.Read(original.Id);
                if (current is null || current.ManifestJson != original.ManifestJson
                    || (currentManifest is null ? current.State != "Completed" || !current.CleanupComplete
                        : current.State != "Undoing" || current.OwnerToken != reverse?.OwnerToken))
                {
                    throw new IOException("Move Undo ownership changed. Preserve the recorded locations for review.");
                }
            },
                cancellationToken,
                previous.UserData).ConfigureAwait(false);
            if (!result.Success || result.WasDryRun)
            {
                if (result.WasDryRun)
                {
                    result.Message = FixResult.DryRun("undo the complete recorded move and restore its original locations", 0).Message;
                }

                if (reverse is not null)
                {
                    _db.Moves.Update(original.Id, reverse.OwnerToken, "Undoing", "NeedsReview", previous.Serialize(), result.Message);
                }

                return result;
            }

            CleanupUndoDirectories(previous, cancellationToken);
            if (reverse is null || !_db.CompleteMoveUndo(original.Id, reverse.Id, reverse.OwnerToken))
            {
                throw new IOException("The files were restored but Undo bookkeeping did not finish. Preserve the recorded locations for review.");
            }

            result.Message = "Undid the complete recorded move and restored its original locations.";
            await RefreshCompletedMoveAsync(result, plan).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            if (original is not null && previous is not null && reverse is not null)
            {
                try
                {
                    _db.Moves.Update(original.Id, reverse.OwnerToken, "Undoing", "NeedsReview", previous.Serialize(), "Undo was cancelled. Review its recorded original and reverse operation before continuing.");
                }
                catch (Exception error) when (error is IOException or Microsoft.Data.Sqlite.SqliteException)
                {
                    _logger.LogWarning(error, "Cancelled Undo for {OperationId} could not persist review state; its existing claim remains reserved", operationId);
                }
            }

            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (original is not null && previous is not null && reverse is not null)
            {
                try
                {
                    _db.Moves.Update(original.Id, reverse.OwnerToken, "Undoing", "NeedsReview", previous.Serialize(), error.Message);
                }
                catch (Exception journalError) when (journalError is IOException or Microsoft.Data.Sqlite.SqliteException)
                {
                    _logger.LogWarning(journalError, "Undo for {OperationId} needs review; its existing move claim remains reserved", operationId);
                }
            }

            var result = FixResult.Refused(reverse is null ? FixReason.SourceChanged : FixReason.MoveNeedsReview, error.Message);
            result.MoveOperationId = reverse?.Id;
            return result;
        }
        finally
        {
            undoDirectories?.Dispose();
        }
    }

    internal Task<FixResult> ExecuteAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = MediaMovePlan.ReadIssue(issue);
        if (plan is null || plan.Type == IssueType.ManualMove || plan.BlockingReasons.Length != 0)
        {
            return Task.FromResult(FixResult.Refused(FixReason.SourceChanged, "Re-scan and approve a complete move plan before moving this item."));
        }

        return ExecutePlanAsync(issue, plan, progress, () => _db.Moves.TryBegin(plan, issue.Id), undo: false, authorize: null, cancellationToken);
    }

    internal Task<FixResult> ExecuteExplicitAsync(MediaMovePlan plan, string fingerprint, IProgress<double>? progress, CancellationToken cancellationToken, long completingIssueId = 0, Action? authorize = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.Type != IssueType.ManualMove || !plan.HasValidDefinition() || plan.BlockingReasons.Length != 0 || plan.Fingerprint != fingerprint)
        {
            return Task.FromResult(FixResult.Refused(FixReason.SourceChanged, "Review the complete current move preview before moving these files."));
        }

        var issue = new Issue { Id = completingIssueId, Type = plan.Type, ItemId = plan.ItemId, Path = plan.Entries[0].Source };
        return ExecutePlanAsync(issue, plan, progress, () => _db.Moves.TryBegin(plan, completingIssueId), undo: false, authorize: _ => authorize?.Invoke(), cancellationToken);
    }

    private async Task<FixResult> ExecutePlanAsync(
        Issue issue,
        MediaMovePlan plan,
        IProgress<double>? progress,
        Func<MediaMoveStore.Operation?> acquire,
        bool undo,
        Action<MediaMoveManifest?>? authorize,
        CancellationToken cancellationToken,
        MediaMoveUserData.Snapshot[]? priorUserData = null)
    {
        var result = await ExecutePlanCoreAsync(issue, plan, progress, acquire, undo, authorize, cancellationToken, priorUserData).ConfigureAwait(false);
        if (!undo && result.Success && !result.WasDryRun)
        {
            await RefreshCompletedMoveAsync(result, plan).ConfigureAwait(false);
        }

        return result;
    }

    private async Task RefreshCompletedMoveAsync(FixResult result, MediaMovePlan plan)
    {
        if (_nativeRefresh is null)
        {
            return;
        }

        try
        {
            var operationId = result.MoveOperationId ?? throw new InvalidOperationException("A completed move has no native-indexing journal identity.");
            var itemPaths = plan.ItemBindings.Select(binding => binding.Path);
            if (plan.ItemId != Guid.Empty)
            {
                itemPaths = itemPaths.Append(plan.ItemPath);
            }

            var destinations = new List<string>();
            foreach (var source in itemPaths.Distinct())
            {
                var entry = plan.Entries.FirstOrDefault(candidate => LibraryGuard.IsUnder(source, candidate.Source));
                if (entry is null)
                {
                    continue; // A related catalog container can remain in place.
                }

                destinations.Add(Path.GetFullPath(Path.Combine(entry.Destination, Path.GetRelativePath(entry.Source, source))));
            }

            // A native book or audiobook resolver can represent a selected
            // directory only through its contained payload. Refresh that bound
            // item as well as directory membership before clearing scan guidance.
            var refreshPaths = plan.Entries.SelectMany(entry => new[] { entry.Source, entry.Destination }).Concat(destinations);
            var metadataDeferred = false;
            try
            {
                await _nativeRefresh.RefreshMoveAsync(refreshPaths, [], CancellationToken.None).ConfigureAwait(false);
            }
            catch (NativeLibraryRefresher.MoveMetadataRefreshDeferredException)
            {
                metadataDeferred = true;
            }

            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var destination in destinations.Distinct())
            {
                var found = _library.FindByPath(destination, null);
                if (found is null || _library.GetItemById(found.Id) is not { } item || !string.Equals(item.Path, destination, comparison))
                {
                    throw new InvalidOperationException("Jellyfin has not indexed a moved catalog item at its destination.");
                }
            }

            _userData?.Reconcile(operationId: operationId);
            var reconciliationComplete = !plan.Entries.Any(entry => File.Exists(entry.Source) || Directory.Exists(entry.Source))
                && _db.Moves.Read(operationId) is { } completed
                && MediaMoveManifest.TryRead(completed.ManifestJson, completed.Id)?.UserData.Any(snapshot => !snapshot.Applied) == false;
            if (metadataDeferred)
            {
                const string explanation = " Automatic metadata refresh is pending to preserve Undo. A Jellyfin library scan can write local sidecars and make Undo unavailable.";
                result.Message = result.Message.Replace(" Run Jellyfin's library scan to finish indexing and restore watched state.", string.Empty, StringComparison.Ordinal);
                if (!reconciliationComplete)
                {
                    result.Message += " Restoration of watched state is still pending.";
                }

                result.Message += explanation;
                return;
            }

            if (reconciliationComplete)
            {
                result.Message = result.Message.Replace(" Run Jellyfin's library scan to finish indexing and restore watched state.", string.Empty, StringComparison.Ordinal);
            }
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Completed move {OperationId} still needs native indexing", result.MoveOperationId);
            var explanation = " Run Jellyfin's library scan to finish indexing and restore watched state.";
            if (!result.Message.Contains(explanation, StringComparison.Ordinal))
            {
                result.Message += explanation;
            }

            Api.Diagnostics.Record("Move.NativeIndex", "The files moved, but native indexing remains pending for operation " + result.MoveOperationId + ": " + error.Message);
        }
    }

    private Task<FixResult> ExecutePlanCoreAsync(
        Issue issue,
        MediaMovePlan plan,
        IProgress<double>? progress,
        Func<MediaMoveStore.Operation?> acquire,
        bool undo,
        Action<MediaMoveManifest?>? authorize,
        CancellationToken cancellationToken,
        MediaMoveUserData.Snapshot[]? priorUserData = null)
    {
        MediaMoveStore.Operation? operation = null;
        MediaMoveManifest? manifest = null;
        var state = "Prepared";
        NativeMoveWatchScope? watcher = null;
        try
        {
            Check(allowPreview: true, requireOwner: true);
            foreach (var entry in plan.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (MediaMoveSafety.RequiresCopyDelete(entry.Source, entry.Destination))
                {
                    throw new MoveRefusal(FixReason.MoveNeedsReview, MediaMoveSafety.UnsafeSourceDeletionMessage);
                }

                MediaMoveSnapshot.VerifyInventory(entry.Source, entry.Inventory, true, cancellationToken);
                MediaMoveSnapshot.ValidateAncestors(entry.Destination);
                RequireVacant(entry.Destination);
            }

            if (Plugin.Instance!.Configuration.DryRun)
            {
                return Task.FromResult(FixResult.DryRun("move the complete approved item and its companions", 0));
            }

            Checkpoint?.Invoke("BeforePrepare", -1);
            cancellationToken.ThrowIfCancellationRequested();
            operation = acquire() ?? throw new MoveRefusal(FixReason.MoveNeedsReview, "Another operation or a previous move attempt reserves this item. Review the recorded move or restore before continuing.");
            manifest = MediaMoveManifest.TryRead(operation.ManifestJson, operation.Id)!;
            manifest = manifest with { UserData = _userData?.Capture(plan, priorUserData) ?? [] };
            Save("Executing");
            watcher = NativeMoveWatchScope.Begin(_monitor, plan.Entries.SelectMany(entry => new[] { entry.Source, entry.Destination })
                .Concat(manifest.Steps.SelectMany(step => new[] { step.Stage, step.Recovery })));
            for (var index = 0; index < plan.Entries.Length; index++)
            {
                var entry = plan.Entries[index];
                Checkpoint?.Invoke("BeforeEntry", index);
                cancellationToken.ThrowIfCancellationRequested();
                Check(false, index == 0);
                ValidateDirectories();
                EnsureParents(Path.GetDirectoryName(entry.Destination)!);
                Checkpoint?.Invoke("BeforePublish", index);
                Check(false, index == 0);
                ValidateDirectories();
                MediaMoveSnapshot.VerifyInventory(entry.Source, entry.Inventory, true, cancellationToken);
                MediaMoveSnapshot.ValidateAncestors(entry.Destination);
                RequireVacant(entry.Destination);
                MediaMoveSafety.RequireSameFilesystemMove(entry.Source, entry.Destination);
                var publishSource = entry.Source;
                var publishInventory = entry.Inventory;
                SetStep(index, manifest.Steps[index] with { Phase = "Publishing", FinalInventory = publishInventory });
                Checkpoint?.Invoke("BeforeNativeRename", index);
                cancellationToken.ThrowIfCancellationRequested();
                Check(false, index == 0);
                ValidateDirectories();
                MediaMoveSnapshot.VerifyInventory(publishSource, publishInventory, true, cancellationToken);
                MediaMoveSnapshot.ValidateAncestors(entry.Destination);
                MediaMoveSafety.RenameNoCopy(publishSource, entry.Destination, entry.IsDirectory);
                Checkpoint?.Invoke("Published", index);
                MediaMoveSnapshot.VerifyInventory(entry.Destination, publishInventory, true, cancellationToken);
                RequireVacant(entry.Source);
                Check(false, false);
                SetStep(index, manifest.Steps[index] with { Phase = "Complete", FinalInventory = MediaMoveSnapshot.Capture(entry.Destination, cancellationToken).Export() });
                progress?.Report((index + 1d) / plan.Entries.Length * 0.9);
            }

            Checkpoint?.Invoke("BeforeComplete", -1);
            Check(false, false);
            for (var index = 0; index < plan.Entries.Length; index++)
            {
                var entry = plan.Entries[index];
                MediaMoveSnapshot.VerifyInventory(entry.Destination, manifest.Steps[index].FinalInventory, true, cancellationToken);
                RequireVacant(entry.Source);
            }

            var finalEntries = plan.Entries.Select((entry, index) => new MediaMovePlan.Entry(entry.Destination, entry.Source, entry.IsDirectory, manifest.Steps[index].FinalInventory)).ToArray();
            manifest = manifest with { FinalScopes = MediaMovePlanner.CaptureSourceScopes(finalEntries, plan.IsGenericSelection) };
            Save("Completed");
            Checkpoint?.Invoke("Completed", -1);
            manifest = manifest with { CleanupComplete = true };
            Save("Completed");
            progress?.Report(1);
            return Task.FromResult(new FixResult
            {
                Success = true, Outcome = FixOutcome.Applied, MoveOperationId = operation.Id,
                Message = "Moved the complete approved item and its companions."
                    + (manifest.UserData.Any(snapshot => !snapshot.Applied) ? " Run Jellyfin's library scan to finish indexing and restore watched state." : string.Empty)
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or OperationCanceledException)
        {
            if (operation is not null && manifest is not null)
            {
                try
                {
                    // Record rollback intent before touching any survivor. A database failure
                    // leaves the durable in-flight manifest and all claims for startup review.
                    for (var index = plan.Entries.Length - 1; index >= 0 && state == "Executing"; index--)
                    {
                        var entry = plan.Entries[index];
                        if (manifest.Steps[index].StageInventory.Length > 0)
                        {
                            var step = manifest.Steps[index];
                            if (step.RecoveryInventory.Length > 0 && (File.Exists(step.Recovery) || Directory.Exists(step.Recovery)))
                            {
                                Check(false, false, checkScope: false);
                                RequireVacant(entry.Source);
                                MediaMoveSnapshot.VerifyInventory(step.Recovery, step.RecoveryInventory, true, CancellationToken.None);
                                SetStep(index, step with { Phase = "RollingBack" });
                                MediaMoveSnapshot.ValidateAncestors(entry.Source);
                                MediaMoveSafety.RenameNoCopy(step.Recovery, entry.Source, entry.IsDirectory);
                                MediaMoveSnapshot.VerifyInventory(entry.Source, entry.Inventory, true, CancellationToken.None);
                                SetStep(index, manifest.Steps[index] with { Phase = "RolledBack" });
                            }

                            continue;
                        }

                        if (manifest.Steps[index].Phase is not ("Publishing" or "Complete"))
                        {
                            continue;
                        }

                        Check(false, false, checkScope: false);
                        ValidateDirectories();
                        if (!File.Exists(entry.Destination) && !Directory.Exists(entry.Destination))
                        {
                            continue;
                        }

                        RequireVacant(entry.Source);
                        MediaMoveSnapshot.VerifyInventory(entry.Destination, entry.Inventory, true, CancellationToken.None);
                        MediaMoveSnapshot.ValidateAncestors(entry.Source);
                        SetStep(index, manifest.Steps[index] with { Phase = "RollingBack" });
                        MediaMoveSafety.RenameNoCopy(entry.Destination, entry.Source, entry.IsDirectory);
                        MediaMoveSnapshot.VerifyInventory(entry.Source, entry.Inventory, true, CancellationToken.None);
                        SetStep(index, manifest.Steps[index] with { Phase = "RolledBack" });
                    }

                    Save("NeedsReview", error.Message);
                }
                catch (Exception recoveryError) when (recoveryError is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                {
                    _logger.LogWarning(recoveryError, "Move {OperationId} needs review; preserve all recorded source, destination and recovery paths", operation.Id);
                    try
                    {
                        Save("NeedsReview", error.Message + " Recovery: " + recoveryError.Message);
                    }
                    catch (Exception journalError) when (journalError is IOException or Microsoft.Data.Sqlite.SqliteException)
                    {
                        _logger.LogWarning(journalError, "Move {OperationId} could not persist its review state; its in-flight claims remain reserved", operation.Id);
                    }
                }
            }

            if (error is OperationCanceledException)
            {
                throw;
            }

            Api.Diagnostics.Record("MediaMove.Refused", error.Message);
            if (operation is null && issue.Type is IssueType.Ungrouped or IssueType.Misplaced && error is IOException
                && (error is not MoveRefusal rejected || rejected.Reason == FixReason.SourceChanged))
            {
                try
                {
                    _db.Issues.InvalidateStaleMoveApproval(
                        issue.Id,
                        plan.Fingerprint,
                        "This move preview is no longer current. Scan again and review its complete current paths before approving. " + error.Message);
                }
                catch (Microsoft.Data.Sqlite.SqliteException approvalError)
                {
                    _logger.LogWarning(approvalError, "Could not revoke stale move preview {IssueId}; its old inventory remains a fix-time veto", issue.Id);
                }
            }

            var result = FixResult.Refused(
                operation is null ? error is MoveRefusal refusal ? refusal.Reason : FixReason.SourceChanged : FixReason.MoveNeedsReview,
                operation is null ? error.Message : "The move stopped and needs review. Preserve its recorded source and destination paths. " + error.Message);
            result.MoveOperationId = operation?.Id;
            return Task.FromResult(result);
        }
        finally
        {
            watcher?.Dispose();
        }

        void Check(bool allowPreview, bool requireOwner, bool checkScope = true)
        {
            authorize?.Invoke(manifest);
            Validate(issue, plan, allowPreview, requireOwner, manifest, checkScope, undo);
        }

        void Save(string next, string? detail = null)
        {
            var saved = next == "Completed" && manifest!.CleanupComplete
                ? _db.FinalizeMove(operation!.Id, operation.OwnerToken, state, manifest)
                : _db.Moves.Update(operation!.Id, operation.OwnerToken, state, next, manifest!.Serialize(), detail);
            if (!saved)
            {
                throw new IOException("Move ownership or its durable journal changed. Preserve the recorded paths for review.");
            }

            state = next;
        }

        void SetStep(int index, MediaMoveManifest.Step step)
        {
            var steps = manifest!.Steps.ToArray();
            steps[index] = step;
            manifest = manifest with { Steps = steps };
            Save(state);
        }

        void EnsureParents(string path)
        {
            if (Directory.Exists(path))
            {
                MediaMoveSnapshot.ValidateAncestors(path);
                return;
            }

            if (!LibraryGuard.IsUnder(path, plan.DestinationContainer))
            {
                throw new IOException("The approved destination's parent no longer exists. Re-scan before moving.");
            }

            EnsureParents(Path.GetDirectoryName(path)!);
            RequireVacant(path);
            manifest = manifest! with { CreatedDirectories = [.. manifest.CreatedDirectories, new(path, 0, 0)] };
            Save(state);
            MediaMoveSafety.CreateDirectoryNoMerge(path);
            if (!PhysicalFileIdentity.TryRead(path, out var identity) || identity.IsReparsePoint)
            {
                throw new IOException("The new destination directory could not be verified.");
            }

            manifest = manifest with { CreatedDirectories = [.. manifest.CreatedDirectories[..^1], new(path, identity.Volume, identity.FileId)] };
            Save(state);
        }

        void ValidateDirectories()
        {
            foreach (var directory in manifest!.DestinationParents.Concat(manifest.CreatedDirectories))
            {
                MediaMoveSnapshot.ValidateAncestors(directory.Path);
                if (!PhysicalFileIdentity.TryRead(directory.Path, out var identity) || identity.IsReparsePoint
                    || identity.Volume != directory.Volume || identity.FileId != directory.FileId)
                {
                    throw new IOException("A recorded destination directory changed. Preserve the recorded paths for review.");
                }
            }
        }
    }

    private void Validate(Issue issue, MediaMovePlan plan, bool allowPreview, bool requireOwner, MediaMoveManifest? manifest, bool checkScope = true, bool undo = false)
    {
        var config = Plugin.Instance!.Configuration;
        var current = _db.Issues.GetIssue(issue.Id);
        var explicitMove = plan.Type == IssueType.ManualMove;
        if (!undo)
        {
            foreach (var binding in plan.ItemBindings)
            {
                var member = ReadCatalogItem(binding.ItemId);
                if (member is null || MediaMovePlanner.Identity(member, manifest is null ? null : plan) != binding.Identity
                    || string.IsNullOrWhiteSpace(member.Path) || !Path.IsPathFullyQualified(member.Path)
                    || !string.Equals(Path.TrimEndingDirectorySeparator(MediaMovePlanner.NormalizeMovedBinding(member.Path, manifest is null ? null : plan)), binding.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    throw new IOException("A known member of this item or family changed after preview. Scan again and review the complete move.");
                }
            }
        }

        if ((!undo && !explicitMove && (current is null || current.Status != IssueStatus.Queued || MediaMovePlan.ReadIssue(current)?.Fingerprint != plan.Fingerprint
                || !FixPolicyEvaluator.Evaluate(config, issue.Type, current.Status, FixIntent.Approved, true, false, false).Allowed))
            || (!allowPreview && config.DryRun) || MediaMovePlanner.PolicyStamp(config, issue.Type) != plan.PolicyStamp)
        {
            throw new IOException("The approval or move policy changed. Re-scan and approve the current item.");
        }

        var owner = undo || (explicitMove && plan.ItemId == Guid.Empty) ? null : ReadCatalogItem(plan.ItemId);
        if ((!undo && requireOwner && owner is null && (!explicitMove || plan.ItemId != Guid.Empty)) || (owner is not null && MediaMovePlanner.Identity(owner, manifest is null ? null : plan) != plan.Identity))
        {
            throw new IOException("The Jellyfin item identity changed. Re-scan and approve the current item.");
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.IsNullOrEmpty(plan.ItemPath) || (owner is not null && !string.Equals(owner.Path, plan.ItemPath, comparison)
            && !(explicitMove && plan.CatalogPath.Length > 0 && string.Equals(owner.Path, plan.CatalogPath, comparison))
            && (requireOwner || !plan.Entries.Any(entry => LibraryGuard.IsUnder(plan.ItemPath, entry.Source)
                && string.Equals(owner.Path, Path.GetFullPath(Path.Combine(entry.Destination, Path.GetRelativePath(entry.Source, plan.ItemPath))), comparison)))))
        {
            throw new IOException("The Jellyfin item's source path changed. Re-scan and approve the current item.");
        }

        if (!undo && checkScope && !explicitMove && owner is MediaBrowser.Controller.Entities.Movies.Movie
            && MediaMovePlanner.HasForeignMovieDestination(plan.DestinationContainer, plan.Entries, manifest))
        {
            throw new MoveRefusal(FixReason.MoveNeedsReview, "The destination movie folder contains content outside this approved move. Review its complete contents instead of merging automatically.");
        }

        var roots = _guard.GetEnabledLibraryLocations(config.EnabledLibraries);
        if (plan.DestinationParents is not { Length: > 0 })
        {
            throw new IOException("The approved destination identity is missing. Re-scan before moving.");
        }

        foreach (var parent in plan.DestinationParents)
        {
            MediaMoveSnapshot.ValidateAncestors(parent.Path);
            if (!PhysicalFileIdentity.TryRead(parent.Path, out var identity) || identity.IsReparsePoint
                || identity.Volume != parent.Volume || identity.FileId != parent.FileId)
            {
                throw new IOException("The approved destination parent changed. Re-scan before moving.");
            }
        }

        foreach (var entry in plan.Entries)
        {
            foreach (var path in new[] { entry.Source, entry.Destination })
            {
                if (!_guard.IsInsideLibrary(path) || !LibraryGuard.IsInEnabledLocations(path, roots))
                {
                    throw new MoveRefusal(FixReason.OutsideLibrary, "The move no longer fits currently enabled libraries. Preserve all recorded paths for review.");
                }

                var protectionType = explicitMove ? IssueType.ManualDelete : issue.Type;
                if (_protection.IsFixerBlocked(path, protectionType) || _protection.IsAnyDescendantProtected(path, protectionType))
                {
                    throw new MoveRefusal(FixReason.Protected, "The move includes a protected path. Preserve all recorded paths for review.");
                }
            }
        }

        if (checkScope)
        {
            foreach (var parent in plan.Entries.Where(entry => !entry.IsDirectory).Select(entry => Path.GetDirectoryName(entry.Source)!).Distinct(StringComparer.Ordinal))
            {
                var scope = plan.SourceScopes.FirstOrDefault(candidate => string.Equals(candidate.Path, parent, comparison));
                if (scope is null || !_guard.IsInsideLibrary(parent) || !roots.Any(root => LibraryGuard.IsUnder(parent, root)))
                {
                    throw new IOException("The approved companion ownership scope could not be verified. Re-scan before moving.");
                }

                var restoredNames = new System.Collections.Generic.List<string>();
                var ignored = new System.Collections.Generic.HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                if (manifest is not null)
                {
                    for (var index = 0; index < plan.Entries.Length; index++)
                    {
                        var entry = plan.Entries[index];
                        if (string.Equals(Path.GetDirectoryName(entry.Destination), parent, comparison)
                            && manifest.Steps[index].Phase is not ("Prepared" or "Copying" or "StageVerified" or "RolledBack"))
                        {
                            ignored.Add(Path.GetFileName(entry.Destination));
                        }

                        if (string.Equals(Path.GetDirectoryName(entry.Source), parent, comparison)
                            && manifest.Steps[index].Phase is not ("Prepared" or "Copying" or "StageVerified" or "RolledBack")
                            && !File.Exists(entry.Source) && !Directory.Exists(entry.Source))
                        {
                            restoredNames.Add(Path.GetFileName(entry.Source));
                        }
                    }

                    foreach (var owned in manifest.CreatedDirectories.Select(directory => directory.Path)
                        .Concat(manifest.Steps.SelectMany(step => new[] { step.Stage, step.Recovery })))
                    {
                        if (string.Equals(Path.GetDirectoryName(owned), parent, comparison))
                        {
                            ignored.Add(Path.GetFileName(owned));
                        }
                    }
                }

                var actual = MediaMovePlanner.ReadSourceNames(parent, plan.Entries, plan.IsGenericSelection).Where(name => !ignored.Contains(name)).Concat(restoredNames);
                if (MediaMovePlanner.SourceScopeFingerprint(actual) != scope.Fingerprint)
                {
                    throw new IOException("The item or its companions changed after approval. Re-scan and preview the complete item before moving.");
                }
            }
        }
    }

    private static void RequireVacant(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new MoveRefusal(FixReason.DestinationCollision, "A file or folder already exists at '" + path + "'. Nothing will be overwritten or merged.");
        }
    }

    private void CleanupUndoDirectories(MediaMoveManifest previous, CancellationToken cancellationToken)
    {
        foreach (var directory in previous.CreatedDirectories.OrderByDescending(directory => directory.Path.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateDirectory(directory);

            // New files remain where their owner put them. Never recursively remove a container
            // merely because the original move created it. A racing new child makes Delete fail.
            if (!Directory.EnumerateFileSystemEntries(directory.Path).Any())
            {
                Checkpoint?.Invoke("BeforeUndoDirectoryDelete", -1);
                cancellationToken.ThrowIfCancellationRequested();
                ValidateDirectory(directory);
                Directory.Delete(directory.Path, recursive: false);
            }
        }

        void ValidateDirectory(MediaMoveManifest.OwnedDirectory directory)
        {
            var roots = _guard.GetEnabledLibraryLocations(Plugin.Instance!.Configuration.EnabledLibraries);
            if (!_guard.IsInsideLibrary(directory.Path) || !LibraryGuard.IsInEnabledLocations(directory.Path, roots)
                || _protection.IsFixerBlocked(directory.Path, previous.Plan.Type == IssueType.ManualMove ? IssueType.ManualDelete : previous.Plan.Type)
                || _protection.IsAnyDescendantProtected(directory.Path, previous.Plan.Type == IssueType.ManualMove ? IssueType.ManualDelete : previous.Plan.Type))
            {
                throw new IOException("An Undo folder is no longer enabled or is protected. Preserve its recorded paths for review.");
            }

            MediaMoveSnapshot.ValidateAncestors(directory.Path);
            if (!PhysicalFileIdentity.TryRead(directory.Path, out var identity) || identity.IsReparsePoint
                || identity.Volume != directory.Volume || identity.FileId != directory.FileId)
            {
                throw new IOException("A folder created by the original move changed. Undo preserved it for review.");
            }
        }
    }

    private MediaBrowser.Controller.Entities.BaseItem? ReadCatalogItem(Guid id)
    {
        try
        {
            return _library.GetItemById(id);
        }
        catch (InvalidOperationException error)
        {
            _logger.LogWarning(error, "The native catalog could not revalidate move item {ItemId}", id);
            throw new MoveRefusal(FixReason.MoveNeedsReview, "Jellyfin's current item metadata could not be read. Run its library scan and review the recorded move before continuing.");
        }
    }

    private sealed class MoveRefusal(FixReason reason, string message) : IOException(message)
    {
        internal FixReason Reason { get; } = reason;
    }
}
