using System;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>Recoverable cleanup of a series folder after its final episode moves.</summary>
internal static class MediaMoveSourceCleanup
{
    // Unrecognised videos at least this large are treated as possible media rather than leftovers
    // (samples are a few MB); one keeps the old folder in place for review.
    internal const long MaxLeftoverVideoBytes = 200L * 1024 * 1024;

    internal static bool IsMetadata(string path)
    {
        var extension = Path.GetExtension(path);
        return Scanners.MediaFormats.Pictures.Contains(extension)
            || (extension.Equals(".nfo", StringComparison.OrdinalIgnoreCase)
                && (Path.GetFileName(path).Equals("tvshow.nfo", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(path).Equals("season.nfo", StringComparison.OrdinalIgnoreCase)));
    }

    internal static void Complete(MediaMovePlan plan, RecycleBin bin, LibraryGuard guard, ProtectionService protection, Action<Receipt> save, CancellationToken cancellationToken)
    {
        if (plan.SourceCleanup is not { } cleanup || !Directory.Exists(cleanup.Source))
        {
            return;
        }

        ValidateScope(cleanup, guard, protection);
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = MediaMoveSnapshot.CaptureIdentity(cleanup.Source, cancellationToken).Export();
        // Everything still here must have been approved as a leftover at scan time: another episode,
        // another item's file, a new or changed file, or series artwork the master folder lacks keeps
        // the folder in place.
        if (!MatchesApproval(cleanup, remaining) || remaining.Any(payload => !payload.IsDirectory
            && IsMetadata(payload.RelativePath) && string.IsNullOrEmpty(Path.GetDirectoryName(payload.RelativePath))
            && !File.Exists(Path.Combine(cleanup.Destination, payload.RelativePath))))
        {
            return;
        }

        if (!bin.CanRecycle(cleanup.Source))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var receipt = new Receipt(cleanup.Source, null, remaining);
        save(receipt);
        var recycled = bin.MoveToBin(cleanup.Source);
        receipt = receipt with { BinPath = recycled };
        save(receipt);
        try
        {
            MediaMoveSnapshot.VerifyInventory(recycled, remaining, true, cancellationToken);
        }
        catch
        {
            // A concurrent writer's files stay recoverable; restore the whole folder if possible.
            if (!Directory.Exists(cleanup.Source) && !File.Exists(cleanup.Source))
            {
                bin.Restore(recycled, cleanup.Source);
            }

            throw;
        }
    }

    internal static void Restore(MediaMoveManifest manifest, RecycleBin bin, LibraryGuard guard, ProtectionService protection, bool dryRun, CancellationToken cancellationToken)
    {
        if (manifest.RecycledSource is not { } receipt)
        {
            return;
        }

        if (!ValidReceipt(manifest.Plan, receipt))
        {
            throw new IOException("Undo stopped: the record of the old folder MediaDash recycled after this move is damaged. Find that folder in the Recycle bin tab and restore it by hand if you need it.");
        }

        var cleanup = manifest.Plan.SourceCleanup!;
        ValidateScope(cleanup, guard, protection);
        if (Directory.Exists(receipt.Source))
        {
            MediaMoveSnapshot.VerifyInventory(receipt.Source, receipt.Inventory, true, cancellationToken);
            return;
        }

        // A pending receipt survives a crash between the recycle rename and saving its returned path.
        var entry = bin.ListContents(int.MaxValue).FirstOrDefault(candidate => candidate.OriginalPath == receipt.Source
            && (receipt.BinPath is null || candidate.BinPath == receipt.BinPath)
            && PhysicalFileIdentity.TryRead(candidate.BinPath, out var identity)
            && identity.Volume == receipt.Inventory[0].Volume && identity.FileId == receipt.Inventory[0].FileId);
        if (entry.BinPath is null)
        {
            throw new IOException("Undo stopped: the old folder MediaDash recycled after this move is no longer in the recycle bin. Restore it from the Recycle bin tab, then try Undo again.");
        }

        MediaMoveSnapshot.VerifyInventory(entry.BinPath, receipt.Inventory, true, cancellationToken);
        if (!dryRun)
        {
            bin.Restore(entry.BinPath, receipt.Source);
        }
    }

    internal static bool ValidReceipt(MediaMovePlan plan, Receipt receipt)
        => plan.SourceCleanup is { } cleanup && receipt.Source == cleanup.Source
            && (receipt.BinPath is null || MediaMovePlan.Canonical(receipt.BinPath))
            && receipt.Inventory is { Length: > 0 } && MatchesApproval(cleanup, receipt.Inventory)
            && receipt.Inventory.Select(payload => payload.RelativePath).Distinct(StringComparer.Ordinal).Count() == receipt.Inventory.Length;

    private static bool MatchesApproval(MediaMovePlan.Entry cleanup, MediaMovePlan.Payload[] remaining)
        => remaining.Length > 0 && remaining[0] is { IsDirectory: true, RelativePath.Length: 0 }
            && remaining.All(payload => payload is { RelativePath: not null } && cleanup.Inventory.Any(approved => payload.IsDirectory
            ? approved.IsDirectory && approved.RelativePath == payload.RelativePath && approved.Volume == payload.Volume && approved.FileId == payload.FileId
            : approved == payload));

    private static void ValidateScope(MediaMovePlan.Entry cleanup, LibraryGuard guard, ProtectionService protection)
    {
        var config = Plugin.Instance!.Configuration;
        var roots = guard.GetEnabledLibraryLocations(config.EnabledLibraries);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!guard.IsInsideLibrary(cleanup.Source) || !LibraryGuard.IsInEnabledLocations(cleanup.Source, roots)
            || !guard.IsInsideLibrary(cleanup.Destination) || !LibraryGuard.IsInEnabledLocations(cleanup.Destination, roots)
            || roots.Any(root => string.Equals(Path.TrimEndingDirectorySeparator(root), cleanup.Source, comparison))
            || protection.IsFixerBlocked(cleanup.Source, IssueType.Ungrouped) || protection.IsAnyDescendantProtected(cleanup.Source, IssueType.Ungrouped))
        {
            throw new IOException("The old folder was kept because it is now protected or outside your enabled libraries. Delete it by hand if you no longer need it.");
        }

        MediaMoveSnapshot.ValidateAncestors(cleanup.Source);
        MediaMoveSnapshot.ValidateAncestors(cleanup.Destination);
    }

    internal sealed record Receipt(string Source, string? BinPath, MediaMovePlan.Payload[] Inventory);
}
