using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Moves misplaced files into the correct target folder (Movies vs TV).
/// Only moves within Jellyfin's known library folders — the <see cref="LibraryGuard"/> check on
/// both source and destination is what keeps the plugin from ever writing outside the library.
/// </summary>
public sealed class MediaSorterFixer : IFixer
{
    private readonly LibraryGuard _guard;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILogger<MediaSorterFixer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaSorterFixer"/> class.
    /// </summary>
    /// <param name="guard">The library path guard.</param>
    /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
    /// <param name="logger">The logger.</param>
    public MediaSorterFixer(LibraryGuard guard, ILibraryMonitor libraryMonitor, ILogger<MediaSorterFixer> logger)
    {
        _guard = guard;
        _libraryMonitor = libraryMonitor;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.Misplaced;

    /// <inheritdoc />
    public Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;

        if (!File.Exists(issue.Path))
        {
            return Task.FromResult(FixResult.Fail("The file no longer exists; re-scan to refresh the list."));
        }

        if (!_guard.IsInsideLibrary(issue.Path))
        {
            return Task.FromResult(FixResult.Fail("The file is outside your library folders; MediaDash will not touch it."));
        }

        string? targetPath;
        try
        {
            using var details = JsonDocument.Parse(issue.DetailsJson);
            targetPath = details.RootElement.TryGetProperty("targetPath", out var t) ? t.GetString() : null;
        }
        catch (JsonException)
        {
            targetPath = null;
        }

        if (string.IsNullOrEmpty(targetPath))
        {
            return Task.FromResult(FixResult.Fail("The target folder was not recorded for this move; re-scan and try again."));
        }

        var targetDir = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(targetDir) || !_guard.IsInsideLibrary(targetDir))
        {
            return Task.FromResult(FixResult.Fail("The target folder isn't inside a Jellyfin library; move refused."));
        }

        if (File.Exists(targetPath))
        {
            return Task.FromResult(FixResult.Fail("A file with the same name already exists at " + targetPath + " — nothing was moved."));
        }

        var actionText = string.Format(
            CultureInfo.InvariantCulture,
            "moved {0} → {1}",
            issue.Path,
            targetPath);

        if (config.DryRun)
        {
            return Task.FromResult(FixResult.DryRun(actionText, 0));
        }

        Directory.CreateDirectory(targetDir);
        File.Move(issue.Path, targetPath);
        _libraryMonitor.ReportFileSystemChanged(issue.Path);
        _libraryMonitor.ReportFileSystemChanged(targetPath);
        _logger.LogInformation("Media sort: {Action}", actionText);
        return Task.FromResult(new FixResult
        {
            Success = true,
            Message = actionText,
            BytesFreed = 0
        });
    }
}
