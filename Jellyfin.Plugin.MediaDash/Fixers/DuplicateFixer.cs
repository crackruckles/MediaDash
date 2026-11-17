using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Removes the losing copy of a duplicate group, but only after re-checking that the better copy still exists.
/// </summary>
public sealed class DuplicateFixer : IFixer
{
    private readonly LibraryGuard _guard;
    private readonly RecycleBin _recycleBin;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<DuplicateFixer> _logger;
    private readonly ProtectionService? _protection;

    /// <summary>
    /// Initializes a new instance of the <see cref="DuplicateFixer"/> class.
    /// </summary>
    /// <param name="guard">The library path guard.</param>
    /// <param name="recycleBin">The recycle bin.</param>
    /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
    /// <param name="libraryManager">Jellyfin library manager. Used to actively purge the losing
    /// copy's item row after recycle, so a rescan can't re-flag it as unplayable ghost.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="protection">Checks protection before disposing each sidecar.</param>
    public DuplicateFixer(LibraryGuard guard, RecycleBin recycleBin, ILibraryMonitor libraryMonitor, ILibraryManager libraryManager, ILogger<DuplicateFixer> logger, ProtectionService? protection = null)
    {
        _guard = guard;
        _recycleBin = recycleBin;
        _libraryMonitor = libraryMonitor;
        _libraryManager = libraryManager;
        _logger = logger;
        _protection = protection;
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.Duplicate;

    /// <inheritdoc />
    public Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;

        string? keeperPath = null;
        (ulong Volume, ulong FileId)? observedKeeper = null;
        (ulong Volume, ulong FileId)? observedLoser = null;
        try
        {
            using var details = JsonDocument.Parse(issue.DetailsJson);
            // Phase 1: guard root shape + property type. TryGetProperty on non-object or
            // GetString on non-string throws InvalidOperationException, not JsonException.
            if (details.RootElement.ValueKind == JsonValueKind.Object
                && details.RootElement.TryGetProperty("keeperPath", out var kp)
                && kp.ValueKind == JsonValueKind.String)
            {
                keeperPath = kp.GetString();
                observedKeeper = ReadObservedIdentity(details.RootElement, "keeperIdentity");
                observedLoser = ReadObservedIdentity(details.RootElement, "loserIdentity");
            }
        }
        catch (JsonException)
        {
        }

        if (string.IsNullOrEmpty(keeperPath))
        {
            return Task.FromResult(FixResult.Refused(FixReason.SourceChanged, "No better copy is recorded for this duplicate; re-scan and try again."));
        }

        if (!File.Exists(issue.Path))
        {
            return Task.FromResult(FixResult.Fail(FixReason.SourceMissing, "The file no longer exists; re-scan to refresh the list."));
        }

        if (!File.Exists(keeperPath))
        {
            return Task.FromResult(FixResult.Fail(FixReason.SourceMissing, $"The better copy ({Path.GetFileName(keeperPath)}) no longer exists — nothing was deleted. Re-scan and review again."));
        }

        // R07 (audit 2026-09-19): revalidate keeper integrity immediately before deletion.
        // Pre-fix the fixer only checked keeper existence — a keeper truncated to 0 bytes (or
        // materially shrunk) between scan and fix would still be trusted, and the fixer would
        // delete a nonempty loser in favour of the invalid keeper. Refuse on any suspicious
        // shrinkage; user re-scans to pick a fresh keeper.
        if (!KeeperStillViable(keeperPath, issue.DetailsJson, out var viabilityReason))
        {
            return Task.FromResult(FixResult.Refused(FixReason.SourceChanged, viabilityReason!));
        }

        if (string.Equals(Path.GetFullPath(keeperPath), Path.GetFullPath(issue.Path), StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(FixResult.NoChange(FixReason.NothingToRemove, "The duplicate and the better copy are the same file; nothing was deleted."));
        }

        if (!_guard.IsInsideLibrary(issue.Path))
        {
            return Task.FromResult(FixResult.Refused(FixReason.OutsideLibrary, "The file is outside your library folders; MediaDash will not touch it."));
        }

        // Defense in depth against a scanner ever writing a keeperPath outside the library — recycling
        // issue.Path when the "better copy" it references lives outside any library means we're
        // silently losing content in favour of a file the user can't reach through Jellyfin.
        if (!_guard.IsInsideLibrary(keeperPath))
        {
            return Task.FromResult(FixResult.Refused(FixReason.OutsideLibrary, "Refused: the recorded better copy '" + keeperPath + "' is outside your library folders."));
        }

        // Belt-and-braces behind the DB-level auto-queue gate in FixTask/MediaDashDb: under
        // Automatic mode we only ever expect confidence-≥-threshold rows to reach the fixer.
        // If one slips through (data-migration edge case, race with a config change) refuse the
        // delete and surface a message so the user can approve manually with intent.
        // See docs/field-reports (2026-08-22 duplicate rework spec §6).
        if (config.GetFixMode(IssueType.Duplicate) == FixMode.Automatic
            && issue.Confidence is double conf
            && conf < config.DuplicateAutoFixConfidence)
        {
            return Task.FromResult(FixResult.Refused(FixReason.ConsentChanged, string.Format(
                CultureInfo.InvariantCulture,
                "Confidence {0:F2} is below the auto-fix threshold {1:F2}. Approve manually from the Issues tab if you're sure — Automatic mode won't auto-delete low-confidence duplicates.",
                conf,
                config.DuplicateAutoFixConfidence)));
        }

        // Task-2 alias recheck: re-read physical identity for both paths at disposal time. If the
        // loser or keeper has become a symlink / junction, or they now resolve to the same physical
        // file (hardlink alias created, or user replaced the loser with a link to the keeper
        // between scan and fix), refuse so we don't break the user's link or claim the link's
        // bytes as freed. Platform read failures are inconclusive — refuse rather than guess.
        var keeperIdent = Probing.PhysicalFileIdentity.TryRead(keeperPath, out var k) ? (Probing.PhysicalFileIdentity.Value?)k : null;
        var loserIdent = Probing.PhysicalFileIdentity.TryRead(issue.Path, out var l) ? (Probing.PhysicalFileIdentity.Value?)l : null;
        if (keeperIdent is null || loserIdent is null)
        {
            return Task.FromResult(FixResult.Refused(FixReason.SourceChanged, "Could not read physical file identity for the duplicate pair; the disposal is refused to avoid breaking a link. Re-scan and try again."));
        }

        if (loserIdent.Value.IsReparsePoint || keeperIdent.Value.IsReparsePoint)
        {
            return Task.FromResult(FixResult.Refused(FixReason.SourceChanged, "The duplicate pair now contains a symlink or junction; MediaDash will not break a filesystem link. Re-scan — the link will be excluded from duplicate findings."));
        }

        if (keeperIdent.Value == loserIdent.Value)
        {
            return Task.FromResult(FixResult.Refused(FixReason.NothingToRemove, "The duplicate and the better copy are hardlinks to the same physical file — nothing would be freed by deleting one entry. Re-scan to drop this finding."));
        }

        if (loserIdent.Value.LinkCount > 1)
        {
            return Task.FromResult(FixResult.Refused(FixReason.SourceChanged, "This file has additional hardlinks pointing at the same bytes; deleting it would not free the storage and might break the other link. Re-scan — hardlinked files are excluded from duplicate findings."));
        }

        // Approval covers the two physical files observed by the scanner, not whatever later
        // occupies their paths. Legacy rows cannot establish that consent and require a rescan.
        if (observedKeeper is null || observedLoser is null
            || observedKeeper.Value != (keeperIdent.Value.Volume, keeperIdent.Value.FileId)
            || observedLoser.Value != (loserIdent.Value.Volume, loserIdent.Value.FileId))
        {
            return Task.FromResult(FixResult.Refused(FixReason.SourceChanged, "The duplicate pair no longer matches the files recorded at scan time, or its recorded identities are missing. Nothing was deleted; re-scan and approve the duplicate again."));
        }

        var size = new FileInfo(issue.Path).Length;
        var disposal = config.GetDisposal(IssueType.Duplicate);
        var sizeText = size >= 1_073_741_824
            ? string.Format(CultureInfo.InvariantCulture, "{0:F1} GB", size / 1_073_741_824.0)
            : string.Format(CultureInfo.InvariantCulture, "{0:F0} MB", size / 1_048_576.0);
        var actionText = string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} ({2}) — better copy kept: {3}",
            disposal == DisposalMethod.RecycleBin ? "moved to recycle bin" : "permanently deleted",
            Path.GetFileName(issue.Path),
            sizeText,
            Path.GetFileName(keeperPath));

        if (config.DryRun)
        {
            return Task.FromResult(FixResult.DryRun(actionText, size));
        }

        string? recyclePath = null;
        if (disposal == DisposalMethod.RecycleBin)
        {
            recyclePath = _recycleBin.MoveToBin(issue.Path);
        }
        else
        {
            File.Delete(issue.Path);
        }

        // Sidecar + empty-folder sweep. The removed video usually lives in its own per-title
        // folder alongside .nfo, poster/backdrop/clearlogo/thumb artwork, and sometimes external
        // subs. Leaving those behind orphans the folder — Jellyfin re-indexes it as an empty movie
        // record every scan (user report: Star Wars - Despecialized (1977)/ left with 5 sidecars
        // and the folder itself after the .iso was recycled).
        var additionalRecycled = new List<RecycledSidecar>();
        SweepDedicatedFolderSidecars(issue.Path, keeperPath, disposal, additionalRecycled);

        _libraryMonitor.ReportFileSystemChanged(issue.Path);
        // Actively reap the Jellyfin item row for the losing copy — ReportFileSystemChanged's
        // async debounce is lost on Jellyfin restart, and the ghost item would then be re-flagged
        // as "unplayable, file gone" by MediaDash's next scan. Best-effort; failure is logged.
        if (issue.ItemId != Guid.Empty)
        {
            try
            {
                var item = _libraryManager.GetItemById(issue.ItemId);
                if (item is not null)
                {
                    _libraryManager.DeleteItem(item, new MediaBrowser.Controller.Library.DeleteOptions
                    {
                        DeleteFileLocation = false,
                        DeleteFromExternalProvider = false
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to actively purge Jellyfin item {ItemId} for {Path} after duplicate recycle", issue.ItemId, issue.Path);
            }
        }

        _logger.LogInformation("Duplicate fix: {Action} (confidence {Confidence}, details {Details})", actionText, issue.Confidence, issue.DetailsJson);
        return Task.FromResult(new FixResult
        {
            Success = true,
            Outcome = FixOutcome.Applied,
            Message = actionText,
            BytesFreed = size,
            RecyclePath = recyclePath,
            AdditionalRecycled = additionalRecycled
        });
    }

    private static (ulong Volume, ulong FileId)? ReadObservedIdentity(JsonElement details, string name)
    {
        if (!details.TryGetProperty(name, out var identity) || identity.ValueKind != JsonValueKind.Object
            || !identity.TryGetProperty("vol", out var volume) || volume.ValueKind != JsonValueKind.String
            || !identity.TryGetProperty("id", out var fileId) || fileId.ValueKind != JsonValueKind.String
            || !ulong.TryParse(volume.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedVolume)
            || !ulong.TryParse(fileId.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedFileId))
        {
            return null;
        }

        return (parsedVolume, parsedFileId);
    }

    /// <summary>
    /// R07 (audit 2026-09-19): fix-time keeper viability gate. Refuses the delete when the
    /// current keeper is empty OR has shrunk materially below its scan-time size (recorded in
    /// <c>DetailsJson.keeper.Size</c>). Pure and testable — file existence + <see cref="FileInfo.Length"/>
    /// only; no probe / hash / library dependency so no per-fix cost beyond a Length read.
    /// </summary>
    /// <param name="keeperPath">Path to the "better copy" the scanner picked.</param>
    /// <param name="detailsJson">The issue's DetailsJson blob, potentially carrying keeper.Size.</param>
    /// <param name="reason">Human-readable refusal reason on false; null on true.</param>
    /// <returns>True when the keeper is still safe to trust as "better copy".</returns>
    public static bool KeeperStillViable(string keeperPath, string detailsJson, out string? reason)
    {
        reason = null;
        if (string.IsNullOrEmpty(keeperPath) || !File.Exists(keeperPath))
        {
            reason = string.Format(
                CultureInfo.InvariantCulture,
                "The better copy ({0}) no longer exists — nothing was deleted. Re-scan and review again.",
                keeperPath is null ? "<unknown>" : Path.GetFileName(keeperPath));
            return false;
        }

        long currentSize;
        try
        {
            currentSize = new FileInfo(keeperPath).Length;
        }
        catch (IOException ex)
        {
            reason = "Could not measure the better copy (" + Path.GetFileName(keeperPath) + "): " + ex.Message + ". Nothing was deleted; re-scan and review.";
            return false;
        }

        if (currentSize <= 0)
        {
            reason = string.Format(
                CultureInfo.InvariantCulture,
                "The better copy ({0}) is now 0 bytes — something modified or truncated it after the scan. Nothing was deleted; re-scan and review.",
                Path.GetFileName(keeperPath));
            return false;
        }

        long? recordedKeeperSize = null;
        try
        {
            using var details = JsonDocument.Parse(detailsJson);
            if (details.RootElement.ValueKind == JsonValueKind.Object
                && details.RootElement.TryGetProperty("keeper", out var kw)
                && kw.ValueKind == JsonValueKind.Object
                && kw.TryGetProperty("Size", out var ks)
                && ks.ValueKind == JsonValueKind.Number
                && ks.TryGetInt64(out var s))
            {
                recordedKeeperSize = s;
            }
        }
        catch (JsonException)
        {
        }

        if (recordedKeeperSize is long recorded && recorded > 0 && currentSize < recorded / 2)
        {
            reason = string.Format(
                CultureInfo.InvariantCulture,
                "The better copy ({0}) is now {1} bytes but was {2} bytes at scan time. Something modified or truncated it after the scan. Nothing was deleted; re-scan and review.",
                Path.GetFileName(keeperPath),
                currentSize,
                recorded);
            return false;
        }

        return true;
    }

    // Recycles remaining sidecars and prunes the containing folder ONLY when the folder is a
    // dedicated per-title folder (contains no other video/audio, no sub-directories, is not itself
    // a library root, and does not also house the keeper). Anything ambiguous is left alone —
    // false-positive sweep of a mixed folder would take out unrelated media.
    private void SweepDedicatedFolderSidecars(string videoPath, string keeperPath, DisposalMethod disposal, List<RecycledSidecar> additionalRecycled)
    {
        var folder = Path.GetDirectoryName(videoPath);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return;
        }

        if (!_guard.IsInsideLibrary(folder) || _guard.IsLibraryRoot(folder))
        {
            return;
        }

        // Co-mingled: keeper lives in the same folder. Sweeping would take out the keeper's own
        // sidecars — bail.
        var keeperFolder = Path.GetDirectoryName(keeperPath);
        if (!string.IsNullOrEmpty(keeperFolder)
            && string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(keeperFolder)),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Any sub-directory (extras/, featurettes/, etc.) means the folder still holds content
        // that isn't ours to touch. Refuse.
        try
        {
            if (Directory.EnumerateDirectories(folder).Any())
            {
                return;
            }
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        List<string> remainingFiles;
        try
        {
            remainingFiles = Directory.EnumerateFiles(folder).ToList();
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        // Any leftover video or audio file means this isn't a dedicated per-title folder — refuse
        // the sweep. Better to leave sidecars than take out the other movie.
        foreach (var f in remainingFiles)
        {
            var ext = Path.GetExtension(f);
            if (MediaFormats.All.Contains(ext))
            {
                return;
            }
        }

        foreach (var f in remainingFiles)
        {
            var stem = Path.GetFileNameWithoutExtension(videoPath);
            var name = Path.GetFileName(f);
            if (!name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith(stem + "-", StringComparison.OrdinalIgnoreCase)
                && !DuplicateScanner.IsSidecarPath(f))
            {
                continue;
            }

            if (_protection?.IsFixerBlocked(f, IssueType.Duplicate) == true)
            {
                continue;
            }

            try
            {
                if (disposal == DisposalMethod.RecycleBin)
                {
                    var binPath = _recycleBin.MoveToBin(f);
                    additionalRecycled.Add(new RecycledSidecar
                    {
                        OriginalPath = f,
                        RecyclePath = binPath,
                        Action = "Recycled sidecar from duplicate's dedicated folder (" + Path.GetFileName(videoPath) + ")"
                    });
                }
                else
                {
                    File.Delete(f);
                }
            }
            catch (IOException ex)
            {
                Api.Diagnostics.Record("DuplicateFixer.SidecarSweep", "Failed to sweep '" + f + "': " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                Api.Diagnostics.Record("DuplicateFixer.SidecarSweep", "Access denied sweeping '" + f + "': " + ex.Message);
            }
        }

        try
        {
            if (Directory.EnumerateFileSystemEntries(folder).Any())
            {
                return;
            }

            Directory.Delete(folder, recursive: false);
        }
        catch (IOException ex)
        {
            // Folder wasn't empty after sweep — a race, or a hidden file we didn't enumerate.
            // Not fatal: the video is recycled, sidecars are recycled, folder just persists.
            Api.Diagnostics.Record("DuplicateFixer.FolderPrune", "Could not remove '" + folder + "' after sweep: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            Api.Diagnostics.Record("DuplicateFixer.FolderPrune", "Access denied removing '" + folder + "': " + ex.Message);
        }
    }
}
