using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Removes files that cannot be played — but only after re-verifying at fix time that the file is still broken.
/// A file that probes and decodes cleanly is never removed, whatever the scan said.
/// </summary>
public sealed class PlayabilityFixer : IFixer
{
    private readonly FfprobeService _ffprobe;
    private readonly LibraryGuard _guard;
    private readonly RecycleBin _recycleBin;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILogger<PlayabilityFixer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayabilityFixer"/> class.
    /// </summary>
    /// <param name="ffprobe">The probe service.</param>
    /// <param name="guard">The library path guard.</param>
    /// <param name="recycleBin">The recycle bin.</param>
    /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
    /// <param name="logger">The logger.</param>
    public PlayabilityFixer(
        FfprobeService ffprobe,
        LibraryGuard guard,
        RecycleBin recycleBin,
        ILibraryMonitor libraryMonitor,
        ILogger<PlayabilityFixer> logger)
    {
        _ffprobe = ffprobe;
        _guard = guard;
        _recycleBin = recycleBin;
        _libraryMonitor = libraryMonitor;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.Playability;

    /// <inheritdoc />
    public async Task<FixResult> FixAsync(Issue issue, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (!File.Exists(issue.Path))
        {
            return FixResult.Fail("The file no longer exists; re-scan to refresh the list.");
        }

        if (!_guard.IsInsideLibrary(issue.Path))
        {
            return FixResult.Fail("The file is outside your library folders; MediaDash will not touch it.");
        }

        var stillBroken = await IsStillBrokenAsync(issue, cancellationToken).ConfigureAwait(false);
        if (!stillBroken)
        {
            return FixResult.Fail("The file plays fine now — nothing was removed. Re-scan to clear this issue.");
        }

        var size = new FileInfo(issue.Path).Length;
        var disposal = config.GetDisposal(IssueType.Playability);
        var actionText = string.Format(
            CultureInfo.InvariantCulture,
            "removed unplayable file {0} ({1})",
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
        _logger.LogInformation("Playability fix: {Action}", actionText);
        return new FixResult
        {
            Success = true,
            Message = actionText,
            BytesFreed = size,
            RecyclePath = recyclePath
        };
    }

    private async Task<bool> IsStillBrokenAsync(Issue issue, CancellationToken cancellationToken)
    {
        var probe = await _ffprobe.ProbeAsync(issue.Path, cancellationToken).ConfigureAwait(false);
        if (probe is null || probe.Error is not null || probe.Streams is null || probe.Streams.Count == 0)
        {
            return true;
        }

        if (!probe.Streams.Any(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!double.TryParse(probe.Format?.Duration, System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) || duration <= 0)
        {
            return true;
        }

        // The probe looks fine — if the scan flagged a decode error, confirm it by test-playing again.
        var wasDecodeError = false;
        try
        {
            using var details = JsonDocument.Parse(issue.DetailsJson);
            wasDecodeError = details.RootElement.TryGetProperty("reason", out var r) && r.GetString() == "decode-error";
        }
        catch (JsonException)
        {
        }

        if (!wasDecodeError)
        {
            return false;
        }

        var decodeError = await _ffprobe.DecodeCheckAsync(issue.Path, duration, cancellationToken).ConfigureAwait(false);
        return decodeError is not null;
    }
}
