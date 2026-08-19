using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Deletes a corrupt artwork file so Jellyfin can re-fetch it on the next library scan.
/// </summary>
public sealed class ArtworkFixer : IFixer
{
    private readonly IServerApplicationPaths _applicationPaths;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILogger<ArtworkFixer> _logger;

    /// <summary>Initializes a new instance of the <see cref="ArtworkFixer"/> class.</summary>
    /// <param name="applicationPaths">Jellyfin server application paths (used as safety gate).</param>
    /// <param name="libraryMonitor">Notifies Jellyfin that a file changed.</param>
    /// <param name="logger">The logger.</param>
    public ArtworkFixer(IServerApplicationPaths applicationPaths, ILibraryMonitor libraryMonitor, ILogger<ArtworkFixer> logger)
    {
        _applicationPaths = applicationPaths;
        _libraryMonitor = libraryMonitor;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.CorruptArtwork;

    /// <inheritdoc />
    public Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // Defense in depth: ArtworkScanner already gates on InternalMetadataPath, but refuse here too.
        // Use the canonical LibraryGuard.IsUnder helper — a raw StartsWith would accept sibling
        // directories with the same prefix (e.g. "metadata-evil" under "metadata").
        if (!LibraryGuard.IsUnder(Path.GetFullPath(issue.Path), _applicationPaths.InternalMetadataPath))
        {
            return Task.FromResult(FixResult.Fail("Refused to touch artwork outside the Jellyfin metadata folder: " + issue.Path));
        }

        var fileName = Path.GetFileName(issue.Path);
        var actionText = $"Delete corrupt artwork {fileName} — Jellyfin will re-fetch on next library scan.";

        if (Plugin.Instance!.Configuration.DryRun)
        {
            return Task.FromResult(FixResult.DryRun(actionText, bytesFreed: 0));
        }

        if (!DeleteArtworkFile(issue.Path))
        {
            return Task.FromResult(FixResult.Fail("Could not delete artwork file (missing or access denied): " + issue.Path));
        }

        // Notify Jellyfin the path changed so it drops the cached ImageInfo promptly.
        _libraryMonitor.ReportFileSystemChanged(issue.Path);

        _logger.LogInformation("ArtworkFixer: {Action}", actionText);
        // ponytail: rely on Jellyfin's scheduled library scan to re-fetch; RefreshMetadata requires IDirectoryService/IFileSystem scaffolding with no existing usage in the plugin.
        return Task.FromResult(new FixResult { Success = true, Message = actionText, BytesFreed = 0 });
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
