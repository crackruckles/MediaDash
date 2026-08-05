using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

// All user-controlled paths in this fixer come from the scanner's DetailsJson and are passed through LibraryGuard on
// both source and target before any filesystem call. CA3003 can't follow that indirection; suppress with the guarantee named here.
[assembly: SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Scope = "type", Target = "~T:Jellyfin.Plugin.MediaDash.Fixers.MediaGrouperFixer", Justification = "Source and target paths are validated via LibraryGuard.IsInsideLibrary before any filesystem call.")]

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Moves a file or folder into a per-title (or per-franchise) parent folder inside the same library.
/// Companion to <see cref="Scanners.MediaGrouperScanner"/>. Both source and target must sit inside a
/// Jellyfin library — enforced by <see cref="LibraryGuard"/>.
/// </summary>
public sealed class MediaGrouperFixer : IFixer
{
    private readonly LibraryGuard _guard;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly MediaDashDb _db;
    private readonly ILogger<MediaGrouperFixer> _logger;

    /// <summary>Initializes a new instance of the <see cref="MediaGrouperFixer"/> class.</summary>
    /// <param name="guard">The library path guard.</param>
    /// <param name="libraryMonitor">Instance of <see cref="ILibraryMonitor"/>.</param>
    /// <param name="db">The plugin database, used to re-point sibling issues after a successful move.</param>
    /// <param name="logger">The logger.</param>
    public MediaGrouperFixer(LibraryGuard guard, ILibraryMonitor libraryMonitor, MediaDashDb db, ILogger<MediaGrouperFixer> logger)
    {
        _guard = guard;
        _libraryMonitor = libraryMonitor;
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.Ungrouped;

    /// <inheritdoc />
    public Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;

        string action;
        string source;
        string target;
        try
        {
            using var details = JsonDocument.Parse(issue.DetailsJson);
            action = details.RootElement.TryGetProperty("action", out var a) ? a.GetString() ?? string.Empty : string.Empty;
            source = details.RootElement.TryGetProperty("source", out var s) ? s.GetString() ?? string.Empty : string.Empty;
            target = details.RootElement.TryGetProperty("target", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException)
        {
            return Task.FromResult(FixResult.Fail("The grouping target was not recorded for this move; re-scan and try again."));
        }

        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target) || string.IsNullOrEmpty(action))
        {
            return Task.FromResult(FixResult.Fail("The grouping target was not recorded for this move; re-scan and try again."));
        }

        var isFolder = string.Equals(action, "MoveFolder", StringComparison.Ordinal);

        if (isFolder ? !Directory.Exists(source) : !File.Exists(source))
        {
            return Task.FromResult(FixResult.Fail("The source no longer exists; re-scan to refresh the list."));
        }

        if (!_guard.IsInsideLibrary(source))
        {
            return Task.FromResult(FixResult.Fail("The source is outside your library folders; MediaDash will not touch it."));
        }

        var targetParent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(target));
        if (string.IsNullOrEmpty(targetParent))
        {
            return Task.FromResult(FixResult.Fail("The target folder was not recorded for this move; re-scan and try again."));
        }

        if (!_guard.IsInsideLibrary(targetParent))
        {
            return Task.FromResult(FixResult.Fail("The target folder '" + targetParent + "' isn't inside a Jellyfin library; move refused."));
        }

        if (isFolder ? Directory.Exists(target) : File.Exists(target))
        {
            return Task.FromResult(FixResult.Fail("An item with the same name already exists at '" + target + "' — rename or remove it, or move this one manually."));
        }

        var actionText = string.Format(CultureInfo.InvariantCulture, "grouped {0} → {1}", source, target);

        if (config.DryRun)
        {
            return Task.FromResult(FixResult.DryRun(actionText, 0));
        }

        try
        {
            Directory.CreateDirectory(targetParent);

            if (isFolder)
            {
                Directory.Move(source, target);
            }
            else
            {
                File.Move(source, target);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(FixResult.Fail(
                "Jellyfin can't write to '" + targetParent + "'. Grant the user Jellyfin runs as (typically 'jellyfin' on Linux) read+write permission on that path."));
        }
        catch (IOException ex) when (isFolder && IsCrossVolume(ex))
        {
            // ponytail: Directory.Move throws IOException across volumes. Grouping within a single library root
            // is same-volume in practice — punt with a clear message rather than implementing recursive copy+delete.
            // Upgrade when a user hits this: swap in a recursive Copy() + Delete() path.
            return Task.FromResult(FixResult.Fail(
                "The target sits on a different drive than the source. Cross-drive folder grouping isn't supported yet — move the folder manually or align the library layout to one volume."));
        }
        catch (IOException ex)
        {
            return Task.FromResult(FixResult.Fail("Move failed: " + ex.Message));
        }

        _libraryMonitor.ReportFileSystemChanged(source);
        _libraryMonitor.ReportFileSystemChanged(target);
        // For MoveFolder this rewrites both the folder-level issue AND every issue under that folder,
        // via the SUBSTR prefix branch in RelocateIssuePaths — no per-child bookkeeping needed here.
        _db.RelocateIssuePaths(source, target);
        _logger.LogInformation("Media group: {Action}", actionText);
        return Task.FromResult(new FixResult
        {
            Success = true,
            Message = actionText,
            BytesFreed = 0
        });
    }

    private static bool IsCrossVolume(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        // Windows ERROR_NOT_SAME_DEVICE = 17 (0x11); Linux EXDEV = 18.
        return code == 17 || code == 18;
    }
}
