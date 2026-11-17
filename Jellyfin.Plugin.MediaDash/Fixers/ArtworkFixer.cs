using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Recycles confirmed corrupt artwork so Jellyfin can fetch it again.
/// </summary>
public sealed class ArtworkFixer : IFixer
{
    private readonly IServerApplicationPaths _applicationPaths;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ArtworkFixer> _logger;
    private readonly RecycleBin? _recycleBin;
    private readonly ProtectionService? _protection;

    /// <summary>Initializes a new instance of the <see cref="ArtworkFixer"/> class.</summary>
    /// <param name="applicationPaths">Jellyfin server application paths (used as safety gate).</param>
    /// <param name="libraryMonitor">Notifies Jellyfin that a file changed.</param>
    /// <param name="libraryManager">Used to resolve the owning item so we can invalidate its ImageInfo before deleting the file on disk.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="recycleBin">Recovery storage for corrupt artwork.</param>
    /// <param name="protection">Current modification protections.</param>
    public ArtworkFixer(IServerApplicationPaths applicationPaths, ILibraryMonitor libraryMonitor, ILibraryManager libraryManager, ILogger<ArtworkFixer> logger, RecycleBin? recycleBin = null, ProtectionService? protection = null)
    {
        _applicationPaths = applicationPaths;
        _libraryMonitor = libraryMonitor;
        _libraryManager = libraryManager;
        _logger = logger;
        _recycleBin = recycleBin;
        _protection = protection;
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.CorruptArtwork;

    /// <inheritdoc />
    public async Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!LibraryGuard.IsUnder(Path.GetFullPath(issue.Path), _applicationPaths.InternalMetadataPath))
        {
            return FixResult.Refused(FixReason.OutsideLibrary, "Refused to touch artwork outside the Jellyfin metadata folder: " + issue.Path);
        }

        if (ArtworkScanner.EvaluateFile(issue.Path, expectedLength: null) is null)
        {
            return FixResult.Deferred(FixReason.SourceChanged, "Artwork is healthy or could not be verified. The file was left in place.");
        }

        var config = Plugin.Instance!.Configuration;
        var guard = new LibraryGuard(_libraryManager, _applicationPaths);
        BaseItem? item;
        try
        {
            if (!guard.IsIssueInEnabledLocations(issue, guard.GetEnabledLibraryLocations(config.EnabledLibraries)))
            {
                return FixResult.Refused(FixReason.LibraryDisabled, "Could not verify this artwork's owner in an enabled library. The file was left in place.");
            }

            item = _libraryManager.GetItemById(issue.ItemId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FixResult.Fail(FixReason.RepairFailed, "Could not verify this artwork's owner. The file was left in place.", ex.Message);
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var image = item?.ImageInfos?.FirstOrDefault(i => i is not null && string.Equals(i.Path, issue.Path, comparison));
        if (item is null || image is null)
        {
            return FixResult.Refused(FixReason.SourceChanged, "Artwork ownership changed. The file was left in place.");
        }

        if (_protection?.IsFixerBlocked(issue.Path, issue.Type) == true
            || _protection?.IsFixerBlocked(item.Path, issue.Type) == true)
        {
            return FixResult.Refused(FixReason.Protected, "This artwork or its owning item is protected from modification.");
        }

        var hasIdentity = PhysicalFileIdentity.TryRead(issue.Path, out var identity);
        var missing = hasIdentity ? null : ObserveMissing(issue.Path);
        if (!hasIdentity && missing is null)
        {
            return FixResult.Refused(FixReason.SourceChanged, "Could not verify this artwork or confirm that its storage is accessible. The image binding was left in place.");
        }

        var actionText = missing is null
            ? $"Recycle corrupt artwork {Path.GetFileName(issue.Path)} so Jellyfin can fetch it again. The original can be restored from History."
            : $"Clear the confirmed missing artwork binding for {Path.GetFileName(issue.Path)} so Jellyfin can fetch it again.";
        if (config.DryRun)
        {
            return FixResult.DryRun(actionText, bytesFreed: 0);
        }

        if (missing is null && (_recycleBin is null || identity.IsReparsePoint))
        {
            return FixResult.Refused(FixReason.SourceChanged, "Could not verify artwork identity and recovery storage. The file was left in place.");
        }

        var priorSameTypePaths = item.ImageInfos.Where(other => other.Type == image.Type).Select(other => other.Path).ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var persisted = false;
        try
        {
            if (missing is not null)
            {
                if (ObserveMissing(issue.Path) != missing)
                {
                    throw new IOException("Artwork storage or absence changed before saving ImageInfo.");
                }

                item.RemoveImage(image);
                await _libraryManager.UpdateItemAsync(item, null!, ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
                persisted = true;
                cancellationToken.ThrowIfCancellationRequested();
                var owner = _libraryManager.GetItemById(issue.ItemId);
                if (owner is null || Plugin.Instance!.Configuration.DryRun
                    || !guard.IsArtworkInEnabledLocations(issue.Path, issue.ItemId, guard.GetEnabledLibraryLocations(Plugin.Instance.Configuration.EnabledLibraries), requireImage: false)
                    || _protection?.IsFixerBlocked(issue.Path, issue.Type) == true
                    || _protection?.IsFixerBlocked(owner.Path, issue.Type) == true
                    || ObserveMissing(issue.Path) != missing)
                {
                    throw new IOException("Artwork ownership, protection, storage or absence changed while saving ImageInfo.");
                }

                return new FixResult { Success = true, Outcome = FixOutcome.Applied, Message = actionText };
            }

            using var source = new FileStream(issue.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var hash = await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false);
            if (ArtworkScanner.EvaluateFile(issue.Path, expectedLength: null) is null)
            {
                return FixResult.Deferred(FixReason.SourceChanged, "Artwork changed or could not be verified. The file was left in place.");
            }

            item.RemoveImage(image);
            await _libraryManager.UpdateItemAsync(item, null!, ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
            persisted = true;
            cancellationToken.ThrowIfCancellationRequested();

            // The repository await can outlive a provider replacing artwork at the same path.
            if (!guard.IsArtworkInEnabledLocations(issue.Path, issue.ItemId, guard.GetEnabledLibraryLocations(Plugin.Instance!.Configuration.EnabledLibraries), requireImage: false))
            {
                throw new IOException("Artwork identity or owner changed while saving ImageInfo.");
            }

            var currentOwner = _libraryManager.GetItemById(issue.ItemId);
            if (currentOwner is null)
            {
                throw new IOException("Artwork owner disappeared while saving ImageInfo.");
            }

            using (var currentFile = new FileStream(issue.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                var currentHash = await SHA256.HashDataAsync(currentFile, cancellationToken).ConfigureAwait(false);
                if (!currentHash.AsSpan().SequenceEqual(hash))
                {
                    throw new IOException("Artwork contents changed while saving ImageInfo.");
                }
            }

            if (Plugin.Instance!.Configuration.DryRun
                || _protection?.IsFixerBlocked(issue.Path, issue.Type) == true
                || _protection?.IsFixerBlocked(currentOwner.Path, issue.Type) == true)
            {
                throw new IOException("Artwork protection or dry-run settings changed while saving ImageInfo.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!PhysicalFileIdentity.TryRead(issue.Path, out var current) || current != identity)
            {
                throw new IOException("Artwork identity changed before recycling.");
            }

            var recycled = _recycleBin!.MoveManagedMetadataToBin(issue.Path);
            try
            {
                _libraryMonitor.ReportFileSystemChanged(issue.Path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Artwork recycled to {Path}, but the library notification failed.", recycled);
            }

            return new FixResult { Success = true, Outcome = FixOutcome.Applied, Message = actionText, RecyclePath = recycled };
        }
        catch (Exception ex)
        {
            // Restore only our removal; providers may have added or replaced other images meanwhile.
            if (!item.ImageInfos.Any(currentImage => string.Equals(currentImage.Path, image.Path, comparison))
                && !item.ImageInfos.Any(currentImage => currentImage.Type == image.Type && !priorSameTypePaths.Contains(currentImage.Path)))
            {
                item.ImageInfos = item.ImageInfos.Append(image).ToArray();
            }

            if (persisted)
            {
                try
                {
                    await _libraryManager.UpdateItemAsync(item, null!, ItemUpdateType.ImageUpdate, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackError)
                {
                    _logger.LogError(rollbackError, "Could not restore ImageInfo for {Path}; artwork bytes were preserved.", issue.Path);
                }
            }

            if (ex is OperationCanceledException)
            {
                throw;
            }

            _logger.LogWarning(ex, "ArtworkFixer: refused disposal of {Path}.", issue.Path);
            if (ex.Message == MediaMoveSafety.UnsafeSourceDeletionMessage)
            {
                return FixResult.Fail(FixReason.RepairFailed, "Could not recycle artwork. Choose a recycle bin folder on the same mount as the source. The source was left in place.", ex.Message);
            }

            return FixResult.Fail(FixReason.RepairFailed, "Could not safely recycle artwork. The source was left in place: " + issue.Path, ex.Message);
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

    private MissingArtworkObservation? ObserveMissing(string path)
    {
        try
        {
            var root = Path.GetFullPath(_applicationPaths.InternalMetadataPath);
            var parent = Path.GetDirectoryName(path);
            if (parent is null || !LibraryGuard.IsUnder(parent, root))
            {
                return null;
            }

            MediaMoveSnapshot.ValidateAncestors(path);
            if (!PhysicalFileIdentity.TryRead(root, out var rootIdentity) || rootIdentity.IsReparsePoint
                || !PhysicalFileIdentity.TryRead(parent, out var parentIdentity) || parentIdentity.IsReparsePoint)
            {
                return null;
            }

            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (Directory.EnumerateFileSystemEntries(parent).Any(entry => string.Equals(entry, path, comparison)))
            {
                return null;
            }

            try
            {
                using var existing = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                return null;
            }
            catch (FileNotFoundException)
            {
                return PhysicalFileIdentity.TryRead(root, out var currentRoot) && currentRoot == rootIdentity
                    && PhysicalFileIdentity.TryRead(parent, out var currentParent) && currentParent == parentIdentity
                    ? new MissingArtworkObservation(rootIdentity, parentIdentity) : null;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record MissingArtworkObservation(PhysicalFileIdentity.Value Root, PhysicalFileIdentity.Value Parent);
}
