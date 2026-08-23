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
    // Extensions Jellyfin recognises as external subtitle / metadata / artwork sidecars beside a
    // video file. Matching an unrelated file with a coincidentally-similar name (e.g. someone's
    // "video.txt" note) would be worse than leaving a real sidecar behind, so we whitelist.
    private static readonly System.Collections.Generic.HashSet<string> SidecarExtensions
        = new(StringComparer.OrdinalIgnoreCase)
        {
            ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup", ".smi",
            ".nfo",
            ".jpg", ".jpeg", ".png", ".webp", ".tbn", ".bif"
        };

    private readonly LibraryGuard _guard;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly MediaDashDb _db;
    private readonly ILogger<MediaSorterFixer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaSorterFixer"/> class.
    /// </summary>
    /// <param name="guard">The library path guard.</param>
    /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
    /// <param name="db">The plugin database, used to re-point sibling issues after a successful move.</param>
    /// <param name="logger">The logger.</param>
    public MediaSorterFixer(LibraryGuard guard, ILibraryMonitor libraryMonitor, MediaDashDb db, ILogger<MediaSorterFixer> logger)
    {
        _guard = guard;
        _libraryMonitor = libraryMonitor;
        _db = db;
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
        // Path.GetPathRoot returns "/" for every Linux path, so use RecycleBin.FindDriveForPath which
        // resolves the deepest-matching mount point on Linux and the drive letter on Windows.
        var sourceDrive = RecycleBin.FindDriveForPath(issue.Path);
        var targetDrive = RecycleBin.FindDriveForPath(targetPath);
        var targetRoot = targetDrive?.RootDirectory.FullName;
        if (sourceDrive is not null && targetDrive is not null
            && !string.Equals(sourceDrive.RootDirectory.FullName, targetDrive.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var fileSize = new FileInfo(issue.Path).Length;
                var free = targetDrive.AvailableFreeSpace;
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

        // Cross-volume moves in .NET are copy+delete. If the process is killed mid-copy, the target
        // has a partial file at the final path — subsequent retries fail the pre-check "already exists"
        // and the file doesn't match SweepOrphanSidecars' *.mediadash.tmp* pattern so nothing cleans it.
        // Fix (B2): stage cross-volume writes to a .mediadash.tmp name, then rename into place. The
        // sweeper already recognises .mediadash.tmp and same-volume rename is atomic. Same-volume moves
        // stay a bare File.Move — it's already atomic there and the tmp indirection would add nothing.
        var isCrossVolume = sourceDrive is not null && targetDrive is not null
            && !string.Equals(sourceDrive.RootDirectory.FullName, targetDrive.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase);
        var stagingPath = isCrossVolume
            ? Path.Combine(targetDir, Path.GetFileNameWithoutExtension(targetPath) + ".mediadash.tmp" + Path.GetExtension(targetPath))
            : null;

        try
        {
            if (isCrossVolume && stagingPath is not null)
            {
                File.Move(issue.Path, stagingPath);
                File.Move(stagingPath, targetPath);
            }
            else
            {
                File.Move(issue.Path, targetPath);
            }
        }
        catch (UnauthorizedAccessException)
        {
            TryDeleteStaging(stagingPath);
            var offender = File.Exists(targetPath) ? issue.Path : targetDir;
            return Task.FromResult(FixResult.Fail(
                "Jellyfin can't write to '" + offender + "'. Grant the user Jellyfin runs as (typically 'jellyfin' on Linux) read+write permission on that path."));
        }
        catch (IOException ex) when (IsOutOfSpace(ex))
        {
            TryDeleteStaging(stagingPath);
            return Task.FromResult(FixResult.Fail(
                "The target drive filled up mid-move; the original was left in place. Free some space on " + (targetRoot ?? "the target drive") + " and try again."));
        }
        catch (IOException ex)
        {
            TryDeleteStaging(stagingPath);
            // Catch-all for other IOException shapes (e.g. TOCTOU race: something appeared at targetPath
            // between the pre-check and File.Move). Without this catch, the exception bubbles to
            // FixTask's generic handler which records a diagnostic but writes NO History row —
            // the audit trail loses the failure entirely.
            return Task.FromResult(FixResult.Fail(
                "Couldn't move '" + Path.GetFileName(issue.Path) + "' → '" + targetPath + "': " + ex.Message));
        }

        _libraryMonitor.ReportFileSystemChanged(issue.Path);
        _libraryMonitor.ReportFileSystemChanged(targetPath);
        _db.RelocateIssuePaths(issue.Path, targetPath);

        // Sidecar-aware move (field report B1): once the primary moved successfully, take the
        // same-stem .srt / .ass / .nfo / artwork with it. Otherwise OrphanCleanupScanner would
        // later flag those files as orphans and OrphanCleanupFixer would delete them.
        // Best-effort: an individual sidecar failure is logged but doesn't undo the primary move.
        MoveSidecars(issue.Path, targetPath);

        _logger.LogInformation("Media sort: {Action}", actionText);
        return Task.FromResult(new FixResult
        {
            Success = true,
            Message = actionText,
            BytesFreed = 0
        });
    }

    private void MoveSidecars(string sourceMedia, string targetMedia)
    {
        var sourceDir = Path.GetDirectoryName(sourceMedia);
        var targetDir = Path.GetDirectoryName(targetMedia);
        if (string.IsNullOrEmpty(sourceDir) || string.IsNullOrEmpty(targetDir) || !Directory.Exists(sourceDir))
        {
            return;
        }

        var sourceStem = Path.GetFileNameWithoutExtension(sourceMedia);
        var targetStem = Path.GetFileNameWithoutExtension(targetMedia);
        if (string.IsNullOrEmpty(sourceStem) || string.IsNullOrEmpty(targetStem))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(file, sourceMedia, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Match "<stem>." and "<stem>-" prefixes: covers movie.en.srt, movie.nfo,
            // movie-poster.jpg, movie-thumb.jpg. A bare "<stem>" with just an extension change
            // (movie.txt) is also matched via the "<stem>." prefix.
            if (!name.StartsWith(sourceStem + ".", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith(sourceStem + "-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!SidecarExtensions.Contains(Path.GetExtension(name)))
            {
                continue;
            }

            // Rewrite the stem: source "Movie (2001).en.srt" with target stem "Movie (2001)-1080p"
            // becomes "Movie (2001)-1080p.en.srt". Keeps every character after the stem intact.
            var suffix = name.Substring(sourceStem.Length);
            var destName = targetStem + suffix;
            var destPath = Path.Combine(targetDir, destName);

            if (File.Exists(destPath))
            {
                Api.Diagnostics.Record(
                    "MediaSorterFixer.SidecarCollision",
                    "Skipped sidecar move for '" + file + "' → '" + destPath + "': the target already has a file with that name. Rename the existing file and re-scan to migrate the sidecar.");
                continue;
            }

            try
            {
                File.Move(file, destPath);
                _libraryMonitor.ReportFileSystemChanged(file);
                _libraryMonitor.ReportFileSystemChanged(destPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Api.Diagnostics.Record(
                    "MediaSorterFixer.SidecarMoveFailed",
                    "Sidecar '" + file + "' did not move alongside the video: " + ex.Message + ". OrphanCleanup may later flag it — move it by hand to '" + destPath + "' if you want to keep it.");
            }
        }
    }

    // ERROR_DISK_FULL (0x70) on Windows, ENOSPC (28) on Linux/macOS. HResult on IOException surfaces both.
    private static bool IsOutOfSpace(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code == 0x70 || code == 28;
    }

    // Best-effort cleanup of the .mediadash.tmp staging file when a cross-volume move throws mid-way.
    // SweepOrphanSidecars would catch anything we miss on a subsequent fix run, but doing it inline
    // means retries don't see stale files.
    private static void TryDeleteStaging(string? stagingPath)
    {
        if (string.IsNullOrEmpty(stagingPath))
        {
            return;
        }

        try
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
        catch (Exception)
        {
            // Best effort — SweepOrphanSidecars will get it next time.
        }
    }
}
