using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Deletes (or recycles) the single file flagged by <see cref="Scanners.StaleContentScanner"/>.
/// Approval-only — <see cref="PluginConfiguration.NormaliseFixModes"/> clamps StaleFixMode to
/// ManualApprove, so no scheduled run will auto-queue stale items. Mirrors
/// <see cref="SuspiciousFileFixer"/>: single-file remove via <see cref="RecycleBin"/> or
/// permanent delete per <see cref="PluginConfiguration.StaleDisposal"/>.
/// </summary>
public sealed class StaleFixer : IFixer
{
    private readonly LibraryGuard _guard;
    private readonly ILibraryManager? _libraryManager;
    private readonly IEnumerable<IScanner>? _scanners;
    private readonly RecycleBin _recycleBin;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILogger<StaleFixer> _logger;

    /// <summary>Initializes a new instance of the <see cref="StaleFixer"/> class.</summary>
    /// <param name="guard">The library path guard.</param>
    /// <param name="recycleBin">The recycle bin.</param>
    /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="libraryManager">Library items for fresh stale-content validation.</param>
    /// <param name="scanners">Registered scanners, including the shared stale-content evaluator.</param>
    public StaleFixer(LibraryGuard guard, RecycleBin recycleBin, ILibraryMonitor libraryMonitor, ILogger<StaleFixer> logger, ILibraryManager? libraryManager = null, IEnumerable<IScanner>? scanners = null)
    {
        _guard = guard;
        _libraryManager = libraryManager;
        _scanners = scanners;
        _recycleBin = recycleBin;
        _libraryMonitor = libraryMonitor;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.Stale;

    /// <inheritdoc />
    public async Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (!File.Exists(issue.Path))
        {
            return FixResult.Fail(FixReason.SourceMissing, "The file no longer exists; re-scan to refresh the list.");
        }

        if (!_guard.IsInsideLibrary(issue.Path))
        {
            return FixResult.Refused(FixReason.OutsideLibrary, "The file is outside your library folders; MediaDash will not touch it.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var item = _libraryManager?.GetItemById(issue.ItemId);
        var scanner = _scanners?.FirstOrDefault(candidate => candidate.Type == IssueType.Stale);
        if (item is null || scanner is null || !string.Equals(item.Path, issue.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return FixResult.Deferred(FixReason.SourceChanged, "The current library item could not be verified. Rescan before removing it.");
        }

        var current = await scanner.ScanAsync([item], new Progress<double>(), cancellationToken).ConfigureAwait(false);
        if (!current.Any(finding => string.Equals(finding.Path, issue.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
        {
            return FixResult.NoChange(FixReason.AlreadyHealthy, "The item is no longer stale or is excluded from stale-content cleanup. It was left in place.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var size = new FileInfo(issue.Path).Length;
        var disposal = config.GetDisposal(IssueType.Stale);
        var actionText = string.Format(
            CultureInfo.InvariantCulture,
            "removed stale file {0} ({1})",
            Path.GetFileName(issue.Path),
            disposal == DisposalMethod.RecycleBin ? "kept in recycle bin" : "permanently deleted");

        if (config.DryRun)
        {
            return FixResult.DryRun(actionText, size);
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
        _logger.LogInformation("Stale file fix: {Action}", actionText);
        return new FixResult
        {
            Success = true,
            Outcome = FixOutcome.Applied,
            Message = actionText,
            BytesFreed = size,
            RecyclePath = recyclePath,
        };
    }
}
