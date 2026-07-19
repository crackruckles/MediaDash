using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
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
    private readonly ILogger<DuplicateFixer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DuplicateFixer"/> class.
    /// </summary>
    /// <param name="guard">The library path guard.</param>
    /// <param name="recycleBin">The recycle bin.</param>
    /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
    /// <param name="logger">The logger.</param>
    public DuplicateFixer(LibraryGuard guard, RecycleBin recycleBin, ILibraryMonitor libraryMonitor, ILogger<DuplicateFixer> logger)
    {
        _guard = guard;
        _recycleBin = recycleBin;
        _libraryMonitor = libraryMonitor;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.Duplicate;

    /// <inheritdoc />
    public Task<FixResult> FixAsync(Issue issue, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;

        string? keeperPath;
        try
        {
            using var details = JsonDocument.Parse(issue.DetailsJson);
            keeperPath = details.RootElement.TryGetProperty("keeperPath", out var kp) ? kp.GetString() : null;
        }
        catch (JsonException)
        {
            keeperPath = null;
        }

        if (string.IsNullOrEmpty(keeperPath))
        {
            return Task.FromResult(FixResult.Fail("No better copy is recorded for this duplicate; re-scan and try again."));
        }

        if (!File.Exists(issue.Path))
        {
            return Task.FromResult(FixResult.Fail("The file no longer exists; re-scan to refresh the list."));
        }

        if (!File.Exists(keeperPath))
        {
            return Task.FromResult(FixResult.Fail($"The better copy ({Path.GetFileName(keeperPath)}) no longer exists — nothing was deleted. Re-scan and review again."));
        }

        if (string.Equals(Path.GetFullPath(keeperPath), Path.GetFullPath(issue.Path), StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(FixResult.Fail("The duplicate and the better copy are the same file; nothing was deleted."));
        }

        if (!_guard.IsInsideLibrary(issue.Path))
        {
            return Task.FromResult(FixResult.Fail("The file is outside your library folders; MediaDash will not touch it."));
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

        _libraryMonitor.ReportFileSystemChanged(issue.Path);
        _logger.LogInformation("Duplicate fix: {Action}", actionText);
        return Task.FromResult(new FixResult
        {
            Success = true,
            Message = actionText,
            BytesFreed = size,
            RecyclePath = recyclePath
        });
    }
}
