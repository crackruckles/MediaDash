using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Removes the four kinds of orphaned debris the <see cref="OrphanCleanupScanner"/> flags. Re-verifies
/// at fix time — a subtitle whose companion video reappeared between scan and fix is refused rather
/// than deleted, so a live re-import doesn't get its sidecars pulled out from under it.
/// </summary>
public sealed class OrphanCleanupFixer : IFixer
{
    private readonly IApplicationPaths _appPaths;
    private readonly ILibraryManager _libraryManager;
    private readonly LibraryGuard _libraryGuard;
    private readonly RecycleBin _recycleBin;
    private readonly ILogger<OrphanCleanupFixer> _logger;

    /// <summary>Initializes a new instance of the <see cref="OrphanCleanupFixer"/> class.</summary>
    /// <param name="appPaths">Jellyfin's application paths — anchors the metadata-folder safety gate.</param>
    /// <param name="libraryManager">Used to re-verify orphan-metadata items still don't resolve.</param>
    /// <param name="libraryGuard">Confirms library-side deletions stay inside a configured library root.</param>
    /// <param name="recycleBin">Destination when the user's disposal setting is RecycleBin.</param>
    /// <param name="logger">The logger.</param>
    public OrphanCleanupFixer(
        IApplicationPaths appPaths,
        ILibraryManager libraryManager,
        LibraryGuard libraryGuard,
        RecycleBin recycleBin,
        ILogger<OrphanCleanupFixer> logger)
    {
        _appPaths = appPaths;
        _libraryManager = libraryManager;
        _libraryGuard = libraryGuard;
        _recycleBin = recycleBin;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.OrphanedDebris;

    /// <inheritdoc />
    public Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var kind = ReadKind(issue.DetailsJson);
        if (kind is null)
        {
            return Task.FromResult(FixResult.Fail("Refused: issue is missing its orphan kind tag — cannot pick a delete strategy."));
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
                return Task.FromResult(RemoveOrphanMetadata(issue));
            default:
                return Task.FromResult(FixResult.Fail("Refused: unknown orphan kind \"" + kind + "\"."));
        }
    }

    private static string? ReadKind(string detailsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            return doc.RootElement.TryGetProperty("kind", out var el) ? el.GetString() : null;
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
            return FixResult.Fail("Refused: folder is not inside a configured library — " + issue.Path);
        }

        if (!Directory.Exists(issue.Path))
        {
            return FixResult.Fail("The folder no longer exists: " + issue.Path);
        }

        // Re-verify at fix time — someone may have copied a video into the folder between scan and fix.
        if (OrphanCleanupScanner_HasVideoNow(issue.Path))
        {
            return FixResult.Fail("Nothing to remove any more — a video appeared inside \"" + Path.GetFileName(issue.Path) + "\" after the scan.");
        }

        return DisposeOfPath(issue, isDirectory: true, verb: "empty folder");
    }

    private FixResult RemoveOrphanSubtitle(Issue issue)
    {
        if (!_libraryGuard.IsInsideLibrary(issue.Path))
        {
            return FixResult.Fail("Refused: subtitle is not inside a configured library — " + issue.Path);
        }

        if (!File.Exists(issue.Path))
        {
            return FixResult.Fail("The subtitle no longer exists: " + issue.Path);
        }

        if (OrphanCleanupScanner.HasCompanionVideo(issue.Path))
        {
            return FixResult.Fail("Nothing to remove any more — a companion video appeared next to \"" + Path.GetFileName(issue.Path) + "\" after the scan.");
        }

        return DisposeOfPath(issue, isDirectory: false, verb: "orphan subtitle");
    }

    private FixResult RemoveOrphanTrickplay(Issue issue)
    {
        if (!_libraryGuard.IsInsideLibrary(issue.Path))
        {
            return FixResult.Fail("Refused: trickplay folder is not inside a configured library — " + issue.Path);
        }

        if (!Directory.Exists(issue.Path))
        {
            return FixResult.Fail("The trickplay folder no longer exists: " + issue.Path);
        }

        if (OrphanCleanupScanner.HasCompanionVideoForTrickplay(issue.Path))
        {
            return FixResult.Fail("Nothing to remove any more — the companion video reappeared next to \"" + Path.GetFileName(issue.Path) + "\" after the scan.");
        }

        return DisposeOfPath(issue, isDirectory: true, verb: "orphan trickplay folder");
    }

    private FixResult RemoveOrphanMetadata(Issue issue)
    {
        // Safety gate: the metadata folder must sit under either <DataPath>/metadata/library or
        // <DataPath>/../metadata/library (Jellyfin's InternalMetadataPath). Anywhere else and we refuse.
        var full = Path.GetFullPath(issue.Path);
        var candidate1 = Path.GetFullPath(Path.Combine(_appPaths.DataPath, "metadata", "library"));
        var candidate2 = Path.GetFullPath(Path.Combine(_appPaths.DataPath, "..", "metadata", "library"));
        if (!LibraryGuard.IsUnder(full, candidate1) && !LibraryGuard.IsUnder(full, candidate2))
        {
            return FixResult.Fail("Refused: metadata folder is not under a Jellyfin metadata root — " + issue.Path);
        }

        if (!Directory.Exists(issue.Path))
        {
            return FixResult.Fail("The metadata folder no longer exists: " + issue.Path);
        }

        // Re-verify: the item's GUID might have started resolving between scan and fix (e.g. re-import).
        if (issue.ItemId != Guid.Empty && _libraryManager.GetItemById(issue.ItemId) is not null)
        {
            return FixResult.Fail("Nothing to remove any more — the item at " + issue.ItemId + " has come back into the library.");
        }

        return DisposeOfPath(issue, isDirectory: true, verb: "orphan metadata folder");
    }

    private FixResult DisposeOfPath(Issue issue, bool isDirectory, string verb)
    {
        var config = Plugin.Instance!.Configuration;
        var disposal = config.OrphanCleanupDisposal;
        var fileName = Path.GetFileName(issue.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var descriptor = string.Format(
            CultureInfo.InvariantCulture,
            "Delete {0} \"{1}\" ({2}).",
            verb,
            fileName,
            disposal == DisposalMethod.RecycleBin ? "moved to recycle bin" : "permanent");

        if (config.DryRun)
        {
            return FixResult.DryRun(descriptor, issue.SizeSavings);
        }

        try
        {
            string? recyclePath = null;
            if (disposal == DisposalMethod.RecycleBin)
            {
                recyclePath = _recycleBin.MoveToBin(issue.Path);
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
                Message = descriptor,
                BytesFreed = issue.SizeSavings,
                RecyclePath = recyclePath
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "OrphanCleanupFixer: could not remove {Path}", issue.Path);
            Api.Diagnostics.Record("OrphanCleanupFixer.Delete", "Couldn't remove orphan \"" + issue.Path + "\": " + ex.Message + ". Check that Jellyfin has write access there.");
            return FixResult.Fail("Couldn't delete \"" + fileName + "\": " + ex.Message);
        }
    }

    private static bool OrphanCleanupScanner_HasVideoNow(string dir)
    {
        // Cheap re-check — walks the tree only until it hits any video file.
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (OrphanCleanupScanner.VideoExtensions.Contains(Path.GetExtension(f)))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // conservative: refuse deletion if the tree is unreadable
        }

        return false;
    }
}
