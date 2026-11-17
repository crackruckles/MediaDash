using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using Jellyfin.Plugin.MediaDash.Scanners;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Removes the four kinds of orphaned debris the <see cref="OrphanCleanupScanner"/> flags. Re-verifies
/// at fix time — a subtitle whose companion video reappeared between scan and fix is refused rather
/// than deleted, so a live re-import doesn't get its sidecars pulled out from under it.
/// </summary>
public sealed class OrphanCleanupFixer : IFixer
{
    private readonly LibraryGuard _libraryGuard;
    private readonly RecycleBin _recycleBin;
    private readonly ILogger<OrphanCleanupFixer> _logger;
    private readonly ProtectionService? _protection;

    /// <summary>Initializes a new instance of the <see cref="OrphanCleanupFixer"/> class.</summary>
    /// <param name="libraryGuard">Confirms library-side deletions stay inside a configured library root.</param>
    /// <param name="recycleBin">Destination when the user's disposal setting is RecycleBin.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="protection">Protection rules for whole-directory cleanup.</param>
    public OrphanCleanupFixer(
        LibraryGuard libraryGuard,
        RecycleBin recycleBin,
        ILogger<OrphanCleanupFixer> logger,
        ProtectionService? protection = null)
    {
        _libraryGuard = libraryGuard;
        _recycleBin = recycleBin;
        _logger = logger;
        _protection = protection;
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.OrphanedDebris;

    /// <inheritdoc />
    public Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_libraryGuard.IsMoveReserved(issue.Path, issue.ItemId))
        {
            return Task.FromResult(FixResult.Refused(FixReason.MoveNeedsReview, "This path belongs to an unfinished move. Review that move before cleaning up its files."));
        }

        var kind = ReadKind(issue.DetailsJson);
        if (kind is null)
        {
            return Task.FromResult(FixResult.Refused(FixReason.SourceChanged, "Refused: issue is missing its orphan kind tag — cannot pick a delete strategy."));
        }

        // Per-kind safety gates + live re-verification. Any failure returns FixResult.Fail rather
        // than proceeding — stale detections must not cause deletions.
        switch (kind)
        {
            case OrphanCleanupScanner.KindEmptyFolder:
                return Task.FromResult(RemoveEmptyFolder(issue));
            case OrphanCleanupScanner.KindOrphanSubtitle:
                return Task.FromResult(RemoveOrphanSubtitle(issue));
            case OrphanCleanupScanner.KindOrphanTrickplay:
                return Task.FromResult(RemoveOrphanTrickplay(issue));
            case OrphanCleanupScanner.KindOrphanMetadata:
                return Task.FromResult(RemoveOrphanMetadata(issue, cancellationToken));
            default:
                return Task.FromResult(FixResult.Refused(FixReason.UnsupportedFormat, "Refused: unknown orphan kind \"" + kind + "\"."));
        }
    }

    internal static string? ReadKind(string detailsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            // Phase 1: guard root + property shape (F-015/F-016 pattern).
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty("kind", out var el) || el.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return el.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private FixResult RemoveEmptyFolder(Issue issue)
    {
        if (!_libraryGuard.IsInsideLibrary(issue.Path))
        {
            return FixResult.Refused(FixReason.OutsideLibrary, "Refused: folder is not inside a configured library — " + issue.Path);
        }

        if (!Directory.Exists(issue.Path))
        {
            return FixResult.Fail(FixReason.SourceMissing, "The folder no longer exists: " + issue.Path);
        }

        if (OrphanCleanupScanner.IsCompanionFolder(issue.Path) || OrphanCleanupScanner.SubtreeHasMediaOrCompanions(issue.Path))
        {
            return FixResult.NoChange(FixReason.NothingToRemove, "The folder could not be confirmed free of media or companion files. It was left in place.");
        }

        return DisposeOfPath(issue, isDirectory: true, verb: "empty folder");
    }

    private FixResult RemoveOrphanSubtitle(Issue issue)
    {
        if (!_libraryGuard.IsInsideLibrary(issue.Path))
        {
            return FixResult.Refused(FixReason.OutsideLibrary, "Refused: subtitle is not inside a configured library — " + issue.Path);
        }

        if (!File.Exists(issue.Path))
        {
            return FixResult.Fail(FixReason.SourceMissing, "The subtitle no longer exists: " + issue.Path);
        }

        if (OrphanCleanupScanner.HasCompanionVideo(issue.Path))
        {
            return FixResult.NoChange(FixReason.NothingToRemove, "Nothing to remove any more — a companion video appeared next to \"" + Path.GetFileName(issue.Path) + "\" after the scan.");
        }

        return DisposeOfPath(issue, isDirectory: false, verb: "orphan subtitle");
    }

    private FixResult RemoveOrphanTrickplay(Issue issue)
    {
        if (!_libraryGuard.IsInsideLibrary(issue.Path))
        {
            return FixResult.Refused(FixReason.OutsideLibrary, "Refused: trickplay folder is not inside a configured library — " + issue.Path);
        }

        if (!Directory.Exists(issue.Path))
        {
            return FixResult.Fail(FixReason.SourceMissing, "The trickplay folder no longer exists: " + issue.Path);
        }

        if (OrphanCleanupScanner.HasCompanionVideoForTrickplay(issue.Path))
        {
            return FixResult.NoChange(FixReason.NothingToRemove, "Nothing to remove any more — the companion video reappeared next to \"" + Path.GetFileName(issue.Path) + "\" after the scan.");
        }

        return DisposeOfPath(issue, isDirectory: true, verb: "orphan trickplay folder");
    }

    private FixResult RemoveOrphanMetadata(Issue issue, CancellationToken cancellationToken)
    {
        if (!_libraryGuard.IsOrphanMetadataPath(issue.Path, issue.ItemId))
        {
            return FixResult.Refused(FixReason.OutsideLibrary, "Refused: metadata folder is not under a Jellyfin metadata root — " + issue.Path);
        }

        if (!Directory.Exists(issue.Path))
        {
            return FixResult.Fail(FixReason.SourceMissing, "The metadata folder no longer exists: " + issue.Path);
        }

        if (!PhysicalFileIdentity.TryRead(issue.Path, out var identity) || identity.IsReparsePoint)
        {
            return FixResult.Deferred(FixReason.SourceChanged, "Could not verify the metadata folder identity. It was left in place.");
        }

        return DisposeOfPath(issue, isDirectory: true, verb: "orphan metadata folder", identity, cancellationToken);
    }

    private FixResult DisposeOfPath(Issue issue, bool isDirectory, string verb, PhysicalFileIdentity.Value? metadataIdentity = null, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance!.Configuration;
        var previewOnly = config.DryRun;
        if (metadataIdentity.HasValue)
        {
            var refusal = VerifyMetadataForDisposal(issue, metadataIdentity.Value, cancellationToken, out config);
            if (refusal is not null)
            {
                return refusal;
            }

            previewOnly |= config.DryRun;
            if (!previewOnly)
            {
                if (!OrphanMetadataObservation.IsCurrent(issue, out var observation, cancellationToken))
                {
                    return MetadataChanged();
                }

                // Hashing may be slow on remote storage; authorization must follow the full read.
                refusal = VerifyMetadataForDisposal(issue, metadataIdentity.Value, cancellationToken, out config);
                if (refusal is not null)
                {
                    return refusal;
                }

                previewOnly |= config.DryRun;
                if (!observation.IsCurrent(cancellationToken))
                {
                    return MetadataChanged();
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        if (isDirectory && !metadataIdentity.HasValue)
        {
            if (_libraryGuard.IsLibraryRoot(issue.Path)
                || System.Linq.Enumerable.Any(_libraryGuard.GetEnabledLibraryLocations(null), root => LibraryGuard.IsUnder(root, issue.Path)))
            {
                return FixResult.Refused(FixReason.OutsideLibrary, "A library folder or its parent cannot be removed.");
            }

            if (_protection?.IsFixerBlocked(issue.Path, issue.Type) == true || _protection?.IsAnyDescendantProtected(issue.Path, issue.Type) == true)
            {
                return FixResult.Refused(FixReason.Protected, "The folder contains protected content. It was left in place.");
            }
        }

        var disposal = config.OrphanCleanupDisposal;
        var fileName = Path.GetFileName(issue.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var descriptor = string.Format(
            CultureInfo.InvariantCulture,
            "Delete {0} \"{1}\" ({2}).",
            verb,
            fileName,
            disposal == DisposalMethod.RecycleBin ? "moved to recycle bin" : "permanent");

        if (previewOnly || config.DryRun)
        {
            return FixResult.DryRun(descriptor, issue.SizeSavings);
        }

        try
        {
            string? recyclePath = null;
            if (disposal == DisposalMethod.RecycleBin)
            {
                recyclePath = metadataIdentity.HasValue
                    ? _recycleBin.MoveManagedMetadataToBin(issue.Path)
                    : _recycleBin.MoveToBin(issue.Path);
            }
            else if (isDirectory)
            {
                Directory.Delete(issue.Path, recursive: true);
            }
            else
            {
                File.Delete(issue.Path);
            }

            _logger.LogInformation("OrphanCleanupFixer: {Desc}", descriptor);
            return new FixResult
            {
                Success = true,
                Outcome = FixOutcome.Applied,
                Message = descriptor,
                BytesFreed = issue.SizeSavings,
                RecyclePath = recyclePath
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "OrphanCleanupFixer: could not remove {Path}", issue.Path);
            if (ex.Message == MediaMoveSafety.UnsafeSourceDeletionMessage)
            {
                return FixResult.Fail(FixReason.RepairFailed, "Could not recycle orphan. Choose a recycle bin folder on the same mount as the source. The source was left in place.", ex.Message);
            }

            Api.Diagnostics.Record("OrphanCleanupFixer.Delete", "Couldn't remove orphan \"" + issue.Path + "\": " + ex.Message + ". Check that Jellyfin has write access there.");
            return FixResult.Fail(FixReason.AccessDenied, "Couldn't delete \"" + fileName + "\" — check that Jellyfin has write access to the containing folder.", ex.Message);
        }
    }

    private FixResult? VerifyMetadataForDisposal(Issue issue, PhysicalFileIdentity.Value identity, CancellationToken cancellationToken, out PluginConfiguration config)
    {
        cancellationToken.ThrowIfCancellationRequested();
        config = Plugin.Instance!.Configuration;
        if (!config.DryRun && !_libraryGuard.HasConfirmingMetadataScan(issue))
        {
            return FixResult.Deferred(FixReason.SourceChanged, "Metadata cleanup is waiting for confirmation. Run Jellyfin's Scan Media Library task after this finding was detected, then retry. The folder and approval were left in place.");
        }

        if (!config.OrphanScanMetadata || !_libraryGuard.IsOrphanMetadataInEnabledLocations(issue.Path, issue.ItemId, _libraryGuard.GetEnabledLibraryLocations(config.EnabledLibraries))
            || !LibraryGuard.HasOnlyManagedMetadata(issue.Path))
        {
            return FixResult.Deferred(FixReason.SourceChanged, "The metadata folder's scope, missing owner or cache-only contents could not be verified. It was left in place.");
        }

        config = Plugin.Instance!.Configuration;
        var enabled = _libraryGuard.GetEnabledLibraryLocations(config.EnabledLibraries).Count > 0;
        var policy = FixPolicyEvaluator.Evaluate(config, issue.Type, IssueStatus.Queued, FixIntent.Approved, enabled, false, false);
        if (!config.OrphanScanMetadata || !policy.Allowed)
        {
            return FixResult.Deferred(FixReason.TypeDisabled, "Metadata cleanup or its enabled library scope changed. The folder was left in place.");
        }

        if (_protection?.IsFixerBlocked(issue.Path, issue.Type) == true || _protection?.IsAnyDescendantProtected(issue.Path, issue.Type) == true)
        {
            return FixResult.Refused(FixReason.Protected, "The metadata folder contains protected content. It was left in place.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return !PhysicalFileIdentity.TryRead(issue.Path, out var current) || current != identity || current.IsReparsePoint
            ? MetadataChanged() : null;
    }

    private static FixResult MetadataChanged() => FixResult.Deferred(
        FixReason.SourceChanged,
        "The metadata folder changed or its original observation is unavailable. Undo this approval and rescan before approving the new finding. The folder was left in place.");
}
