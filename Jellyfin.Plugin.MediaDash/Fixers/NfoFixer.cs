using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Deletes a corrupt NFO metadata sidecar so Jellyfin's next library scan tries to build metadata
/// from scratch (either via configured online providers or the video's own embedded tags). Refuses
/// paths outside a configured library — <c>.nfo</c> files sit next to media, never in Jellyfin's
/// own data folder.
/// </summary>
public sealed class NfoFixer : IFixer
{
    private readonly LibraryGuard _libraryGuard;
    private readonly RecycleBin _recycleBin;
    private readonly ILogger<NfoFixer> _logger;

    /// <summary>Initializes a new instance of the <see cref="NfoFixer"/> class.</summary>
    /// <param name="libraryGuard">Confirms the NFO sits inside a configured library root.</param>
    /// <param name="recycleBin">Destination when the user's disposal setting is RecycleBin.</param>
    /// <param name="logger">The logger.</param>
    public NfoFixer(LibraryGuard libraryGuard, RecycleBin recycleBin, ILogger<NfoFixer> logger)
    {
        _libraryGuard = libraryGuard;
        _recycleBin = recycleBin;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.CorruptNfo;

    /// <inheritdoc />
    public Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!issue.Path.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(FixResult.Fail("Refused: not an .nfo file — " + issue.Path));
        }

        if (!_libraryGuard.IsInsideLibrary(issue.Path))
        {
            return Task.FromResult(FixResult.Fail("Refused: NFO is not inside a configured library — " + issue.Path));
        }

        if (!File.Exists(issue.Path))
        {
            return Task.FromResult(FixResult.Fail("The NFO no longer exists: " + issue.Path));
        }

        var config = Plugin.Instance!.Configuration;
        var disposal = config.NfoDisposal;
        var fileName = Path.GetFileName(issue.Path);
        var descriptor = string.Format(
            CultureInfo.InvariantCulture,
            "Delete corrupt NFO \"{0}\" ({1}).",
            fileName,
            disposal == DisposalMethod.RecycleBin ? "moved to recycle bin" : "permanent");

        if (config.DryRun)
        {
            return Task.FromResult(FixResult.DryRun(descriptor, issue.SizeSavings));
        }

        try
        {
            string? recyclePath = null;
            if (disposal == DisposalMethod.RecycleBin)
            {
                recyclePath = _recycleBin.MoveToBin(issue.Path);
            }
            else
            {
                File.Delete(issue.Path);
            }

            _logger.LogInformation("NfoFixer: {Desc}", descriptor);
            return Task.FromResult(new FixResult
            {
                Success = true,
                Message = descriptor,
                BytesFreed = issue.SizeSavings,
                RecyclePath = recyclePath
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "NfoFixer: could not remove {Path}", issue.Path);
            Api.Diagnostics.Record("NfoFixer.Delete", "Couldn't delete NFO \"" + issue.Path + "\": " + ex.Message + ". Check that Jellyfin has write access to the containing folder.");
            return Task.FromResult(FixResult.Fail("Couldn't delete \"" + fileName + "\": " + ex.Message));
        }
    }
}
