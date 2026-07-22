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
        if (string.IsNullOrEmpty(targetDir))
        {
            return Task.FromResult(FixResult.Fail("The target folder was not recorded for this move; re-scan and try again."));
        }

        if (!Directory.Exists(targetDir))
        {
            return Task.FromResult(FixResult.Fail("The target folder no longer exists: '" + targetDir + "'. Update the folder path in Settings → Libraries → Media sorter, then re-scan."));
        }

        if (!_guard.IsInsideLibrary(targetDir))
        {
            return Task.FromResult(FixResult.Fail("The target folder '" + targetDir + "' isn't inside a Jellyfin library; move refused. MediaDash will not move files outside your libraries."));
        }

        if (File.Exists(targetPath))
        {
            return Task.FromResult(FixResult.Fail("A file with the same name already exists at '" + targetPath + "' — nothing was moved. Rename or remove the existing file, or move this one manually."));
        }

        // Cross-volume moves in .NET are copy+delete, which requires target free-space equal to the file's size.
        // Same-volume moves are a metadata-only rename, so the check is skipped in that case.
        var sourceRoot = Path.GetPathRoot(Path.GetFullPath(issue.Path));
        var targetRoot = Path.GetPathRoot(Path.GetFullPath(targetPath));
        if (!string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(targetRoot))
        {
            try
            {
                var fileSize = new FileInfo(issue.Path).Length;
                var free = new DriveInfo(targetRoot).AvailableFreeSpace;
                const long safetyMarginBytes = 100L * 1024 * 1024;
                if (free < fileSize + safetyMarginBytes)
                {
                    var neededMb = (fileSize + safetyMarginBytes) / (1024 * 1024);
                    var freeMb = free / (1024 * 1024);
                    return Task.FromResult(FixResult.Fail(
                        "Not enough free space on the target drive (" + targetRoot + "): needs about " + neededMb.ToString(CultureInfo.InvariantCulture) +
                        " MB, has " + freeMb.ToString(CultureInfo.InvariantCulture) + " MB free."));
                }
            }
            catch (IOException)
            {
                // Drive info can throw on obscure filesystems (network shares that have disappeared, etc.).
                // Fall through and let File.Move surface the real problem.
            }
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

        try
        {
            File.Move(issue.Path, targetPath);
        }
        catch (UnauthorizedAccessException)
        {
            var offender = File.Exists(targetPath) ? issue.Path : targetDir;
            return Task.FromResult(FixResult.Fail(
                "Jellyfin can't write to '" + offender + "'. Grant the user Jellyfin runs as (typically 'jellyfin' on Linux) read+write permission on that path."));
        }
        catch (IOException ex) when (IsOutOfSpace(ex))
        {
            return Task.FromResult(FixResult.Fail(
                "The target drive filled up mid-move; the original was left in place. Free some space on " + (targetRoot ?? "the target drive") + " and try again."));
        }

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

    // ERROR_DISK_FULL (0x70) on Windows, ENOSPC (28) on Linux/macOS. HResult on IOException surfaces both.
    private static bool IsOutOfSpace(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code == 0x70 || code == 28;
    }
}
