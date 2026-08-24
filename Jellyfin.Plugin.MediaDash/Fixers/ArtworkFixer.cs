using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Deletes a corrupt artwork file so Jellyfin can re-fetch it on the next library scan.
/// </summary>
public sealed class ArtworkFixer : IFixer
{
    private readonly IServerApplicationPaths _applicationPaths;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ArtworkFixer> _logger;

    /// <summary>Initializes a new instance of the <see cref="ArtworkFixer"/> class.</summary>
    /// <param name="applicationPaths">Jellyfin server application paths (used as safety gate).</param>
    /// <param name="libraryMonitor">Notifies Jellyfin that a file changed.</param>
    /// <param name="libraryManager">Used to resolve the owning item so we can invalidate its ImageInfo before deleting the file on disk.</param>
    /// <param name="logger">The logger.</param>
    public ArtworkFixer(IServerApplicationPaths applicationPaths, ILibraryMonitor libraryMonitor, ILibraryManager libraryManager, ILogger<ArtworkFixer> logger)
    {
        _applicationPaths = applicationPaths;
        _libraryMonitor = libraryMonitor;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.CorruptArtwork;

    /// <inheritdoc />
    public async Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // Defense in depth: ArtworkScanner already gates on InternalMetadataPath, but refuse here too.
        // Use the canonical LibraryGuard.IsUnder helper — a raw StartsWith would accept sibling
        // directories with the same prefix (e.g. "metadata-evil" under "metadata").
        if (!LibraryGuard.IsUnder(Path.GetFullPath(issue.Path), _applicationPaths.InternalMetadataPath))
        {
            return FixResult.Fail("Refused to touch artwork outside the Jellyfin metadata folder: " + issue.Path);
        }

        var fileName = Path.GetFileName(issue.Path);
        var actionText = $"Delete corrupt artwork {fileName} — Jellyfin's ImageInfo cleared so /Items/{{id}}/Images/Primary stops 404-ing.";

        if (Plugin.Instance!.Configuration.DryRun)
        {
            return FixResult.DryRun(actionText, bytesFreed: 0);
        }

        // Clear the item's ImageInfo entry FIRST, then delete the file. Doing it in this order
        // means Jellyfin's /Items/{id}/Images/Primary handler never has a window where the DB
        // still references a file that's already gone (the 2026-08-22 "Could not find file
        // poster.jpg" bug report). If the ImageInfo update fails, we still fall through to the
        // file delete — a stale row is a lesser evil than corrupt artwork sitting on disk — and
        // the next full library scan will reconcile.
        await InvalidateItemImageAsync(issue, cancellationToken).ConfigureAwait(false);

        if (!DeleteArtworkFile(issue.Path))
        {
            return FixResult.Fail("Could not delete artwork file (missing or access denied): " + issue.Path);
        }

        // Notify Jellyfin the path changed so any watcher-based caches also drop it.
        _libraryMonitor.ReportFileSystemChanged(issue.Path);

        _logger.LogInformation("ArtworkFixer: {Action}", actionText);
        return new FixResult { Success = true, Message = actionText, BytesFreed = 0 };
    }

    private async Task InvalidateItemImageAsync(Issue issue, CancellationToken cancellationToken)
    {
        if (issue.ItemId == Guid.Empty)
        {
            return;
        }

        BaseItem? item;
        try
        {
            item = _libraryManager.GetItemById(issue.ItemId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ArtworkFixer: could not look up item {ItemId} to invalidate ImageInfo — proceeding with file delete.", issue.ItemId);
            return;
        }

        var image = item?.ImageInfos?.FirstOrDefault(i => i is not null
            && !string.IsNullOrEmpty(i.Path)
            && string.Equals(i.Path, issue.Path, StringComparison.OrdinalIgnoreCase));
        if (item is null || image is null)
        {
            return;
        }

        try
        {
            item.RemoveImage(image);
            await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Persistence failures here shouldn't block the on-disk delete. Log and move on;
            // the next library scan reconciles.
            _logger.LogWarning(ex, "ArtworkFixer: could not persist ImageInfo removal for item {ItemId} ({Path}) — file delete still proceeds; next library scan will reconcile.", issue.ItemId, issue.Path);
        }
    }

    /// <summary>
    /// Deletes the artwork file at <paramref name="path"/>.
    /// Exposed internal for unit tests.
    /// </summary>
    /// <param name="path">Full path to the artwork file to delete.</param>
    /// <returns>True when the file existed and was deleted; false when missing or on I/O error.</returns>
    internal static bool DeleteArtworkFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
