using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Single owner of the "restore a recycled file back to a library" workflow. Every restore route
/// (History/{id}/Restore, RedownloadWarnings/{historyId}/RestoreOptimized, RecycleBin/Items/Restore
/// single + batch) funnels through this service so the owner-tokened claim table
/// (<c>restore_operations</c>) is the sole serialization point for physical access to a bin entry.
/// Extracted from MediaDashController in 05-B per plan §05-B "one restore workflow, honest claim
/// state".
/// </summary>
public sealed class RestoreService
{
    private readonly MediaDashDb _db;
    private readonly HistoryStore _history;
    private readonly RestoreOperationStore _restoreOps;
    private readonly RecycleBin _recycleBin;
    private readonly LibraryGuard _libraryGuard;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILogger<RestoreService> _logger;
    private readonly ProtectionService? _protection;
    private readonly Action<string, string> _restore;

    /// <summary>Initializes a new instance of the <see cref="RestoreService"/> class.</summary>
    /// <param name="db">DB layer for restored-paths writes (MarkRestored + MarkPathRestored*); the restored_paths surface is small enough that a dedicated store isn't worth the split.</param>
    /// <param name="history">History-table store (05-C.3): history reads (GetHistoryEntry), inserts (AddHistory for force-swap trailing entries), and acknowledgement (AcknowledgeHistoryEntry).</param>
    /// <param name="restoreOps">Restore-operation store (05-C.6): owner-tokened claim workflow against restore_operations (schema v7).</param>
    /// <param name="recycleBin">Recycle-bin file operations (Restore + MoveToBin).</param>
    /// <param name="libraryGuard">Library-scope validator (safety invariant #1).</param>
    /// <param name="libraryMonitor">Jellyfin library-change notifier.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="protection">Protection rules for managed metadata directory recovery.</param>
    /// <param name="restore">Optional filesystem restore operation for fault-injection tests.</param>
    public RestoreService(
        MediaDashDb db,
        HistoryStore history,
        RestoreOperationStore restoreOps,
        RecycleBin recycleBin,
        LibraryGuard libraryGuard,
        ILibraryMonitor libraryMonitor,
        ILogger<RestoreService> logger,
        ProtectionService? protection = null,
        Action<string, string>? restore = null)
    {
        _db = db;
        _history = history;
        _restoreOps = restoreOps;
        _recycleBin = recycleBin;
        _libraryGuard = libraryGuard;
        _libraryMonitor = libraryMonitor;
        _logger = logger;
        _protection = protection;
        _restore = restore ?? recycleBin.Restore;
    }

    // ─── History/{id}/Restore ───────────────────────────────────────────

    internal RestoreOutcome RestoreFromHistory(long historyId, bool force)
    {
        var entry = _history.GetHistoryEntry(historyId);
        if (entry is null)
        {
            return NotFound();
        }

        // Defense in depth: history rows from older MediaDash versions or a hand-edited DB can
        // carry a target path outside the current library set. Without this guard, a poisoned row
        // could direct MoveAcrossVolumes to write to arbitrary paths (e.g. /etc/cron.d/) that
        // Jellyfin can reach — arbitrary file write escalation.
        var artworkIssue = entry.Type == IssueType.CorruptArtwork ? _db.Issues.GetIssue(entry.IssueId) : null;
        var managedArtwork = artworkIssue is { Type: IssueType.CorruptArtwork }
            && string.Equals(artworkIssue.Path, entry.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && _libraryGuard.IsArtworkInEnabledLocations(entry.Path, artworkIssue.ItemId, _libraryGuard.GetEnabledLibraryLocations(Plugin.Instance!.Configuration.EnabledLibraries), requireImage: false);
        var orphanIssue = entry.Type == IssueType.OrphanedDebris ? _db.Issues.GetIssue(entry.IssueId) : null;
        var orphanCandidate = orphanIssue is { Type: IssueType.OrphanedDebris }
            && OrphanCleanupFixer.ReadKind(orphanIssue.DetailsJson) == Scanners.OrphanCleanupScanner.KindOrphanMetadata
            && string.Equals(orphanIssue.Path, entry.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        PhysicalFileIdentity.Value recoveryIdentity = default;
        if (orphanCandidate && (!Directory.Exists(entry.RecyclePath) || !_recycleBin.IsOwnedRecoveryPath(entry.RecyclePath, entry.Path)
            || !PhysicalFileIdentity.TryRead(entry.RecyclePath!, out recoveryIdentity) || recoveryIdentity.IsReparsePoint))
        {
            return Conflict(FixReason.SourceChanged, "The metadata recovery source could not be verified as an owned recycle entry for this folder. It was left in place.");
        }

        var managedOrphan = orphanCandidate && _libraryGuard.IsOrphanMetadataInEnabledLocations(entry.Path, orphanIssue!.ItemId, _libraryGuard.GetEnabledLibraryLocations(Plugin.Instance!.Configuration.EnabledLibraries));
        if (_recycleBin.IsManagedMetadataBinPath(entry.RecyclePath)
            && (!(managedArtwork || managedOrphan) || !_recycleBin.IsOwnedRecoveryPath(entry.RecyclePath, entry.Path)))
        {
            return Conflict(FixReason.SourceChanged, "The managed metadata recovery source or destination could not be verified. It was left in place.");
        }

        if (!managedArtwork && !managedOrphan && !_libraryGuard.IsInsideLibrary(entry.Path))
        {
            return Conflict(FixReason.OutsideLibrary, "Refused: the target path '" + entry.Path + "' is not inside a configured Jellyfin library.");
        }

        // MD-012: orphan cleanup can recycle whole directories (empty per-title folders after a
        // duplicate video is removed). Accept both file and directory recycle entries.
        if (entry.Restored || string.IsNullOrEmpty(entry.RecyclePath)
            || !(File.Exists(entry.RecyclePath) || Directory.Exists(entry.RecyclePath)))
        {
            return Conflict(FixReason.SourceMissing, "This file is no longer in the recycle bin.");
        }

        var initialTarget = entry.Path;
        var initialSuffixed = false;

        // Collision on either kind — a file OR a folder already sitting at the original path.
        var pendingSwapPath = default(string);
        if (PathExists(entry.Path))
        {
            if (!force)
            {
                // Non-destructive default: land next to the current file with a suffix.
                initialTarget = ResolveNonCollidingRestorePath(entry.Path);
                initialSuffixed = true;
            }
            else
            {
                pendingSwapPath = entry.Path;
            }
        }

        if (MediaMoveSafety.RequiresCopyDelete(entry.RecyclePath!, initialTarget))
        {
            return Conflict(FixReason.DestinationCollision, "Restore requires a same-filesystem rename; current content was left in place.");
        }

        string? swappedBinPath = null;
        var operationId = _restoreOps.TryBeginRestore(entry.RecyclePath!, historyId, entry.RecyclePath!, initialTarget);
        if (operationId is null)
        {
            return MoveReservationRefusal(entry.RecyclePath, entry.Path, initialTarget)
                ?? Conflict(FixReason.AlreadyHealthy, "This bin entry is already being restored (or a prior restore left it in NeedsReview — check the restore_operations table).");
        }

        try
        {
            if (managedOrphan)
            {
                if (!_libraryGuard.IsOrphanMetadataInEnabledLocations(entry.Path, orphanIssue!.ItemId, _libraryGuard.GetEnabledLibraryLocations(Plugin.Instance!.Configuration.EnabledLibraries))
                    || _libraryGuard.GetEnabledLibraryLocations(Plugin.Instance!.Configuration.EnabledLibraries).Count == 0)
                {
                    _restoreOps.AbortRestore(operationId);
                    return Conflict(FixReason.OutsideLibrary, "The metadata destination or its missing owner could not be verified. Recovery content remains in the recycle bin.");
                }

                if (_protection?.IsFixerBlocked(entry.Path, entry.Type) == true || _protection?.IsAnyDescendantProtected(entry.Path, entry.Type) == true)
                {
                    _restoreOps.AbortRestore(operationId);
                    return Conflict(FixReason.Protected, "The metadata destination contains protected content. Recovery content remains in the recycle bin.");
                }

                if (!_recycleBin.IsOwnedRecoveryPath(entry.RecyclePath, entry.Path)
                    || !PhysicalFileIdentity.TryRead(entry.RecyclePath!, out var currentRecovery) || currentRecovery != recoveryIdentity)
                {
                    _restoreOps.AbortRestore(operationId);
                    return Conflict(FixReason.SourceChanged, "The metadata recovery source changed during verification. It was left in place.");
                }
            }

            // Force-swap the current occupant to the bin as its own restorable history row. Done
            // AFTER the claim so a concurrent force restore of the same entry couldn't race with us.
            if (pendingSwapPath is not null)
            {
                try
                {
                    swappedBinPath = managedArtwork || managedOrphan
                        ? _recycleBin.MoveManagedMetadataToBin(pendingSwapPath)
                        : _recycleBin.MoveToBin(pendingSwapPath);
                    _history.AddHistory(new HistoryEntry
                    {
                        IssueId = managedArtwork || managedOrphan ? entry.IssueId : 0,
                        Type = entry.Type,
                        Path = pendingSwapPath,
                        Action = "Swapped out to recycle bin so an older copy could be restored in its place.",
                        BytesFreed = 0,
                        RecyclePath = swappedBinPath,
                        FixedAtUtc = DateTime.UtcNow,
                        WasDryRun = false,
                        Success = true
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                {
                    return RollBackFailedRestore(operationId, swappedBinPath, pendingSwapPath, ex);
                }
            }

            try
            {
                _restore(entry.RecyclePath!, initialTarget);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return RollBackFailedRestore(operationId, swappedBinPath, initialTarget, ex);
            }

            try
            {
                _db.MarkRestored(historyId);
                _db.MarkPathRestored(entry.Path, entry.Type);
                if (initialSuffixed)
                {
                    _db.MarkPathRestored(initialTarget, entry.Type);
                }
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            {
                // Filesystem work already landed successfully. Do NOT auto-retry — the plan is
                // explicit that a DB failure after a successful FS restore must be reported as
                // partial mutation so the user can reconcile manually.
                _restoreOps.MarkRestoreNeedsReview(operationId, "Filesystem restore succeeded but DB bookkeeping (history.restored / restored_paths) failed: " + ex.Message + ". Inspect the destination on disk and clean up the restore_operations row manually.");
                _logger.LogWarning(ex, "Restore filesystem work succeeded but DB writes failed for op {Op}", operationId);
                return new RestoreOutcome(Success: false, FixReason.RecoveryRequired, initialTarget, initialSuffixed, "Restore succeeded on disk but bookkeeping failed. See Errors tab.");
            }

            _libraryMonitor.ReportFileSystemChanged(initialTarget);
            if (initialSuffixed)
            {
                _libraryMonitor.ReportFileSystemChanged(entry.Path);
            }

            _restoreOps.CompleteRestore(operationId, initialTarget);
            return new RestoreOutcome(Success: true, FixReason.None, initialTarget, initialSuffixed, TechnicalDetail: null);
        }
        catch
        {
            // Any unhandled exception releases the claim so retry through the same endpoint works.
            _restoreOps.AbortRestore(operationId);
            throw;
        }
    }

    // ─── RedownloadWarnings/{historyId}/RestoreOptimized ────────────────

    private RestoreOutcome RollBackFailedRestore(string operationId, string? swappedBinPath, string target, Exception failure)
    {
        if (swappedBinPath is not null)
        {
            try
            {
                _restore(swappedBinPath, target);
            }
            catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
            {
                var detail = "Restore failed and the current copy could not be put back. Recover it from " + swappedBinPath + ": " + rollbackError.Message;
                _restoreOps.MarkRestoreNeedsReview(operationId, detail);
                return new RestoreOutcome(false, FixReason.RecoveryRequired, target, false, detail);
            }
        }

        _restoreOps.AbortRestore(operationId);
        return Conflict(FixReason.DestinationCollision, failure.Message);
    }

    internal RestoreOutcome RestoreOptimizedCopy(long historyId, bool force)
    {
        var entry = _history.GetHistoryEntry(historyId);
        if (entry is null)
        {
            return NotFound();
        }

        if (string.IsNullOrEmpty(entry.RecyclePath))
        {
            return Conflict(FixReason.SourceMissing, "This history row has no recorded recycle path — the original file wasn't sent to the bin.");
        }

        // Defense in depth: history rows from older MediaDash versions (or a hand-edited DB) can
        // carry a Path outside any currently-configured library. Same guard as RestoreFromHistory.
        if (!_libraryGuard.IsInsideLibrary(entry.Path))
        {
            return Conflict(FixReason.OutsideLibrary, "Refused: the target path '" + entry.Path + "' is not inside a configured Jellyfin library.");
        }

        // Pass the source's on-disk path so SelectOptimizedTwin can prefer manifest-matched
        // candidates when two same-basename bin files sit in the fix's time window.
        var twin = _recycleBin.FindOptimizedTwin(entry.RecyclePath!, entry.FixedAtUtc, entry.Path);
        if (twin is null)
        {
            return Conflict(FixReason.SourceMissing, "Couldn't find an unambiguous optimized twin in the recycle bin. It may have been purged, or multiple candidates were found. Use the Recycle bin tab to pick manually, or use the History tab's Restore to bring back the original instead.");
        }

        // Claim on the twin's bin path — the actual physical entry being restored. Two callers
        // targeting the same twin (even via different history rows) serialize here.
        if (MediaMoveSafety.RequiresCopyDelete(twin.Value.BinPath, entry.Path))
        {
            return Conflict(FixReason.DestinationCollision, "Restore requires a same-filesystem rename; current content was left in place.");
        }

        string? swappedBinPath = null;
        var operationId = _restoreOps.TryBeginRestore(twin.Value.BinPath, historyId, twin.Value.BinPath, entry.Path);
        if (operationId is null)
        {
            return MoveReservationRefusal(twin.Value.BinPath, entry.Path)
                ?? Conflict(FixReason.AlreadyHealthy, "This optimized twin has already been restored (or a concurrent restore is in flight, or a prior attempt left it in NeedsReview).");
        }

        try
        {
            if (File.Exists(entry.Path))
            {
                if (!force)
                {
                    _restoreOps.AbortRestore(operationId);
                    return Conflict(FixReason.DestinationCollision, "A file already exists at " + entry.Path + ". Use force=true to send it to the recycle bin first, then restore the optimized copy in its place.");
                }

                try
                {
                    swappedBinPath = _recycleBin.MoveToBin(entry.Path);
                    _history.AddHistory(new HistoryEntry
                    {
                        IssueId = 0,
                        Type = entry.Type,
                        Path = entry.Path,
                        Action = "Swapped out to recycle bin so the optimized copy could be restored in its place.",
                        BytesFreed = 0,
                        RecyclePath = swappedBinPath,
                        FixedAtUtc = DateTime.UtcNow,
                        WasDryRun = false,
                        Success = true
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                {
                    return RollBackFailedRestore(operationId, swappedBinPath, entry.Path, ex);
                }
            }

            try
            {
                _restore(twin.Value.BinPath, entry.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return RollBackFailedRestore(operationId, swappedBinPath, entry.Path, ex);
            }

            try
            {
                _history.AcknowledgeHistoryEntry(historyId);
                _db.MarkPathRestored(entry.Path, entry.Type);
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            {
                _restoreOps.MarkRestoreNeedsReview(operationId, "Filesystem restore succeeded but DB bookkeeping (acknowledge/restored_paths) failed: " + ex.Message + ". Inspect the destination on disk and clean up the restore_operations row manually.");
                _logger.LogWarning(ex, "Optimized-copy restore filesystem work succeeded but DB writes failed for op {Op}", operationId);
                return new RestoreOutcome(Success: false, FixReason.RecoveryRequired, entry.Path, Suffixed: false, "Restore succeeded on disk but bookkeeping failed. See Errors tab.");
            }

            _libraryMonitor.ReportFileSystemChanged(entry.Path);

            _restoreOps.CompleteRestore(operationId, entry.Path);
            return new RestoreOutcome(Success: true, FixReason.None, entry.Path, Suffixed: false, TechnicalDetail: null);
        }
        catch
        {
            _restoreOps.AbortRestore(operationId);
            throw;
        }
    }

    // ─── RecycleBin/Items/Restore (manifest-only) ───────────────────────

    // Returns a RestoreOutcome shaped for the single-item HTTP endpoint. For batch use
    // <see cref="RestoreManifestEntryFromListing"/> which reuses a caller-computed listing.
    internal RestoreOutcome RestoreManifestEntry(string binPath)
    {
        if (string.IsNullOrEmpty(binPath))
        {
            return new RestoreOutcome(Success: false, FixReason.None, RestoredTo: null, Suffixed: false, TechnicalDetail: "Missing bin path.", IsNotFound: false);
        }

        var entries = _recycleBin.ListContents(limit: 5000);
        return RestoreManifestEntryFromListing(binPath, entries);
    }

    // Batch-friendly variant. Callers that restore many bin paths reuse the ListContents result
    // across iterations to avoid enumerating the bin per entry (mirrors the pre-refactor
    // RestoreOneBinPath helper).
    internal RestoreOutcome RestoreManifestEntryFromListing(
        string binPath,
        IReadOnlyList<(string FileName, string BinPath, long SizeBytes, DateTime RecycledAtUtc, string? OriginalPath)> entries)
    {
        var entry = entries.FirstOrDefault(e => string.Equals(e.BinPath, binPath, StringComparison.OrdinalIgnoreCase));
        if (entry.BinPath is null)
        {
            return NotFound("Unknown bin path — not currently in the recycle bin.");
        }

        if (string.IsNullOrEmpty(entry.OriginalPath))
        {
            return Conflict(FixReason.SourceMissing, "This bin file has no origin manifest — MediaDash doesn't know where it came from. Restore it manually from the Files tab's Recycle bin shortcut.");
        }

        var insideLibrary = _libraryGuard.IsInsideLibrary(entry.OriginalPath);
        var dedicatedMetadataBin = _recycleBin.IsManagedMetadataBinPath(entry.BinPath);
        if (dedicatedMetadataBin || !insideLibrary)
        {
            var historyId = _history.FindActiveManagedMetadataRecycleHistory(entry.BinPath, entry.OriginalPath);
            if (historyId.HasValue)
            {
                return RestoreFromHistory(historyId.Value, force: false);
            }

            if (dedicatedMetadataBin)
            {
                return Conflict(FixReason.SourceMissing, "This metadata entry has no active recovery history. It was left in place.");
            }
        }

        if (!insideLibrary)
        {
            return Conflict(FixReason.OutsideLibrary, "Refused: the origin path '" + entry.OriginalPath + "' is not inside a configured Jellyfin library.");
        }

        var initialTarget = ResolveNonCollidingRestorePath(entry.OriginalPath);
        var initialSuffixed = !string.Equals(initialTarget, entry.OriginalPath, StringComparison.Ordinal);

        var operationId = _restoreOps.TryBeginRestore(entry.BinPath, historyId: null, entry.BinPath, initialTarget);
        if (operationId is null)
        {
            return MoveReservationRefusal(entry.BinPath, entry.OriginalPath, initialTarget)
                ?? Conflict(FixReason.AlreadyHealthy, "This bin entry is already being restored (or a prior restore left it in NeedsReview — check the restore_operations table).");
        }

        try
        {
            try
            {
                _recycleBin.Restore(entry.BinPath, initialTarget);
            }
            catch (IOException ex)
            {
                _restoreOps.AbortRestore(operationId);
                return Conflict(FixReason.DestinationCollision, ex.Message);
            }

            try
            {
                // Manual restore recorded as its own history row (Type = Duplicate as a placeholder
                // — the manifest doesn't carry an IssueType, and Duplicate is what the pre-refactor
                // code used verbatim). Restored=true so it doesn't re-appear as an active bin entry.
                _history.AddHistory(new HistoryEntry
                {
                    IssueId = 0,
                    Type = Data.IssueType.Duplicate,
                    Path = entry.OriginalPath!,
                    Action = initialSuffixed
                        ? "Restored bin file (with -restored suffix) via Recycle bin tab: " + initialTarget
                        : "Restored bin file via Recycle bin tab: " + initialTarget,
                    BytesFreed = 0,
                    RecyclePath = entry.BinPath,
                    FixedAtUtc = DateTime.UtcNow,
                    WasDryRun = false,
                    Success = true,
                    Restored = true
                });

                _db.MarkPathRestoredForAnyType(entry.OriginalPath!);
                if (initialSuffixed)
                {
                    _db.MarkPathRestoredForAnyType(initialTarget);
                }
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            {
                _restoreOps.MarkRestoreNeedsReview(operationId, "Filesystem restore succeeded but DB bookkeeping (history/restored_paths) failed: " + ex.Message + ". Inspect the destination on disk and clean up the restore_operations row manually.");
                _logger.LogWarning(ex, "Manifest restore filesystem work succeeded but DB writes failed for op {Op}", operationId);
                return new RestoreOutcome(Success: false, FixReason.RecoveryRequired, initialTarget, initialSuffixed, "Restore succeeded on disk but bookkeeping failed. See Errors tab.");
            }

            _libraryMonitor.ReportFileSystemChanged(initialTarget);

            _restoreOps.CompleteRestore(operationId, initialTarget);
            return new RestoreOutcome(Success: true, FixReason.None, initialTarget, initialSuffixed, TechnicalDetail: null);
        }
        catch
        {
            _restoreOps.AbortRestore(operationId);
            throw;
        }
    }

    // ─── helpers ────────────────────────────────────────────────────────

    // Path-kind-agnostic existence probe. Restore targets can be files (media files, sidecars,
    // NFOs) or directories (per-title folders from orphan cleanup / Files-tab folder recycles).
    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    // Returns the original path when it's free; otherwise the first non-existing path of the form
    // <dir>/<name>-restored<ext>, <name>-restored-2<ext>, …. Directory-aware — MediaDash recycles
    // per-title folders (orphan cleanup + Files-tab folder delete route through the same
    // MoveToBin), so restore targets may be directories. See MediaDashController's pre-05-B
    // implementation for the release audit finding #5 rationale. Mirrored here so RestoreService
    // has no dependency on the controller; the controller keeps a thin forwarder for callers
    // (tests) that reach the resolver directly.
    internal static string ResolveNonCollidingRestorePath(string originalPath)
    {
        if (!PathExists(originalPath))
        {
            return originalPath;
        }

        var dir = Path.GetDirectoryName(originalPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(originalPath);
        var ext = Path.GetExtension(originalPath);
        var candidate = Path.Combine(dir, name + "-restored" + ext);
        var counter = 2;
        while (PathExists(candidate))
        {
            candidate = Path.Combine(dir, name + "-restored-" + counter.ToString(System.Globalization.CultureInfo.InvariantCulture) + ext);
            counter++;
        }

        return candidate;
    }

    private RestoreOutcome? MoveReservationRefusal(params string?[] paths) => paths.Any(path => !string.IsNullOrEmpty(path) && _db.Moves.HasActiveConflict(path))
        ? Conflict(FixReason.MoveNeedsReview, "This restore overlaps an unfinished move. Review the recorded move before restoring into its original, destination or recovery locations.") : null;

    private static RestoreOutcome NotFound(string? detail = null)
        => new(Success: false, FixReason.SourceMissing, RestoredTo: null, Suffixed: false, TechnicalDetail: detail, IsNotFound: true);

    private static RestoreOutcome Conflict(FixReason reason, string detail)
        => new(Success: false, reason, RestoredTo: null, Suffixed: false, TechnicalDetail: detail);
}
