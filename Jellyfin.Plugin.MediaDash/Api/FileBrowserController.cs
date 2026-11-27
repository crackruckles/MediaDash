using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Fixers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

// CA3003 taint-tracks user-controlled strings into File/Directory calls. Every path in this file passes through
// TryResolveInsideLibrary (Path.GetFullPath + LibraryGuard.IsInsideLibrary) or IsSimpleName before use. The analyzer
// cannot follow that indirection, so suppress for the file with the guarantee named here.
[assembly: SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Scope = "type", Target = "~T:Jellyfin.Plugin.MediaDash.Api.FileBrowserController", Justification = "All user-supplied paths are validated via TryResolveInsideLibrary/IsSimpleName before any filesystem call.")]

// The taint flows out of Delete() into RecycleBin.MoveToBin. The path is validated at the controller layer before it reaches
// the recycle bin. RecycleBin has no independent trust boundary; it always operates on paths its caller has already vetted.
[assembly: SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Scope = "member", Target = "~M:Jellyfin.Plugin.MediaDash.Fixers.RecycleBin.MoveToBin(System.String)~System.String", Justification = "Callers (fixers and FileBrowserController) validate paths via LibraryGuard before calling.")]
[assembly: SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Scope = "member", Target = "~M:Jellyfin.Plugin.MediaDash.Fixers.RecycleBin.MoveAcrossVolumes(System.String,System.String,System.Boolean)", Justification = "Only called from validated code paths.")]
// AdoptBatchByPath does its own validation: (1) Path.GetFullPath canonicalises, (2) IsMediaDashBatchName gates on the exact 28-char timestamp+GUID leaf shape, (3) PathsEqual(parent, Root) requires the path to be a direct child of the current recycle bin. A traversal-shaped input fails IsMediaDashBatchName (leaf can't contain "..") and an out-of-root input fails the parent check.
[assembly: SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Scope = "member", Target = "~M:Jellyfin.Plugin.MediaDash.Fixers.RecycleBin.AdoptBatchByPath(System.String)~System.Boolean", Justification = "Path is validated inside the method: must be a direct child of Root and its leaf must match IsMediaDashBatchName.")]
[assembly: SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Scope = "member", Target = "~M:Jellyfin.Plugin.MediaDash.Fixers.RecycleBin.ConsolidateFromRoot(System.String)~System.ValueTuple{System.Int32,System.Int32,System.Int64,System.String}", Justification = "The caller (MediaDashController.ConsolidateBin) validates SourceRoot against DiscoverOtherBinRoots — only history-derived roots that are direct grandparents of a MediaDash-shape batch folder are accepted. Arbitrary paths from a hostile admin token are rejected at the controller layer with 404.")]
[assembly: SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Scope = "member", Target = "~M:Jellyfin.Plugin.MediaDash.Fixers.RecycleBin.ConsolidateBetween(System.String,System.String)~System.ValueTuple{System.Int32,System.Int32,System.Int64,System.String}", Justification = "Pure-logic core; taint gate is enforced at the controller (see ConsolidateFromRoot justification).")]

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Admin-only file browser. Every operation is guarded by <see cref="LibraryGuard"/> on every path it touches —
/// requests referencing anything outside a configured library folder are refused.
/// Deletes route through the recycle bin (subject to the same retention as the fix pipeline).
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("MediaDash/Files")]
[Produces("application/json")]
public class FileBrowserController : ControllerBase
{
    private const string UploadTempSuffix = ".mediadash.upload.tmp";

    // Cap at 50 GB — realistic ceiling for any single media file admins would upload via a plugin
    // (Blu-ray remuxes top out at ~40 GB). Without the cap [DisableRequestSizeLimit] lets any admin
    // stream a multi-TB body and fill the host disk (denial of service).
    private const long UploadMaxBytes = 50L * 1024 * 1024 * 1024;

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly LibraryGuard _guard;
    private readonly RecycleBin _recycleBin;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _appPaths;
    private readonly Data.HistoryStore _history;
    private readonly ProtectionService _protection;
    private readonly ILogger<FileBrowserController> _logger;
    private readonly MediaMovePlanner? _movePlanner;
    private readonly MediaMoveService? _moves;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileBrowserController"/> class.
    /// </summary>
    /// <param name="guard">Library path guard.</param>
    /// <param name="recycleBin">Recycle bin.</param>
    /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface; used to locate the Jellyfin log directory for the read-only shortcut.</param>
    /// <param name="history">History-table store (05-C.3); used to record a history row per manual delete so the Recycle bin tab labels the entry correctly.</param>
    /// <param name="protection">Never-modify protection service (roadmap i-110-protected-media); refuses Delete for paths under a rule that masks ManualDelete.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="movePlanner">Complete move-preview planner.</param>
    /// <param name="moves">Durable move execution and recovery.</param>
    public FileBrowserController(LibraryGuard guard, RecycleBin recycleBin, ILibraryMonitor libraryMonitor, ILibraryManager libraryManager, IApplicationPaths appPaths, Data.HistoryStore history, ProtectionService protection, ILogger<FileBrowserController> logger, MediaMovePlanner? movePlanner = null, MediaMoveService? moves = null)
    {
        _guard = guard;
        _recycleBin = recycleBin;
        _libraryMonitor = libraryMonitor;
        _libraryManager = libraryManager;
        _appPaths = appPaths;
        _history = history;
        _protection = protection;
        _logger = logger;
        _movePlanner = movePlanner;
        _moves = moves;
    }

    /// <summary>
    /// Lists a directory. An empty path returns the library roots as a pseudo-root.
    /// </summary>
    /// <param name="path">The directory to list. Empty means "library roots".</param>
    /// <returns>The listing.</returns>
    [HttpGet("List")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<DirectoryListing> List([FromQuery] string? path = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            // Snapshot VirtualFolders before iterating — Jellyfin mutates this collection when a
            // library is added / removed via the dashboard, and a concurrent enumerate throws.
            var roots = _libraryManager.GetVirtualFolders()
                .ToList()
                .SelectMany(f => f.Locations)
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .Where(Directory.Exists)
                .Select(location =>
                {
                    var info = new DirectoryInfo(location);
                    return new FileEntry
                    {
                        Name = location,
                        IsDirectory = true,
                        SizeBytes = 0,
                        ModifiedUtc = info.LastWriteTimeUtc
                    };
                })
                .ToList();

            // Append the recycle-bin root as a synthetic navigation shortcut. Kept read-only:
            // TryResolveInsideLibrary still refuses recycle-bin paths for Mkdir / Rename / Move /
            // Copy / Delete / Upload / Download, so the browser can only enumerate. Restore /
            // Empty live on the dedicated Recycle bin tab.
            try
            {
                var binRoot = _recycleBin.GetEffectiveRoot();
                if (Directory.Exists(binRoot))
                {
                    var info = new DirectoryInfo(binRoot);
                    roots.Add(new FileEntry
                    {
                        Name = binRoot,
                        IsDirectory = true,
                        SizeBytes = 0,
                        ModifiedUtc = info.LastWriteTimeUtc,
                        Kind = "recycle-bin"
                    });
                }
            }
            catch (Exception ex)
            {
                // Recycle bin root inaccessible — skip the shortcut, root listing still works.
                // Broad catch: this is a non-critical navigation shortcut; anything Plugin.Configuration
                // or the RecycleBin resolver throws (NRE during startup race, InvalidOperationException on
                // partial config) must NOT 500 the entire Files/List root.
                Diagnostics.Record("FileBrowser.List", "The Files tab could not show its Recycle bin shortcut. The recycle bin is still available from the Recycle bin tab. Details: " + ex.Message);
            }

            // Same shortcut treatment for Jellyfin's log directory so admins can grab logs for bug
            // reports without leaving the plugin UI. Read-only: mutation endpoints refuse paths not
            // inside a library, and this directory sits outside every library — no write path opens
            // up. Download is explicitly carved out below to allow saving individual log files.
            try
            {
                var logsRoot = _appPaths.LogDirectoryPath;
                if (!string.IsNullOrEmpty(logsRoot) && Directory.Exists(logsRoot))
                {
                    var info = new DirectoryInfo(logsRoot);
                    roots.Add(new FileEntry
                    {
                        Name = logsRoot,
                        IsDirectory = true,
                        SizeBytes = 0,
                        ModifiedUtc = info.LastWriteTimeUtc,
                        Kind = "logs"
                    });
                }
            }
            catch (Exception ex)
            {
                // Same reasoning as the recycle-bin shortcut above: non-critical, catch broad so any
                // IApplicationPaths implementation quirk doesn't 500 the root listing.
                Diagnostics.Record("FileBrowser.List", "The Files tab could not show its Logs shortcut. Check that Jellyfin's log folder exists and that the user Jellyfin runs as can read it. Details: " + ex.Message);
            }

            return new DirectoryListing { Path = string.Empty, Parent = null, IsRoot = true, Entries = roots };
        }

        // Read-only carve-outs: allow enumeration of anything inside the recycle-bin root or the
        // Jellyfin log directory even though both sit outside every library location. Every
        // mutation endpoint continues to gate on TryResolveInsideLibrary, so no write path opens up.
        var isInsideBin = TryResolveInsideRecycleBin(path, out var binFull);
        var isInsideLogs = !isInsideBin && TryResolveInsideLogsDir(path, out binFull);
        if (!isInsideBin && !isInsideLogs && !TryResolveInsideLibrary(path, out binFull, out var forbid))
        {
            return forbid;
        }

        var full = binFull;
        if (!Directory.Exists(full))
        {
            return NotFound();
        }

        var entries = new List<FileEntry>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(full))
            {
                try
                {
                    var info = new DirectoryInfo(dir);
                    entries.Add(new FileEntry
                    {
                        Name = info.Name,
                        IsDirectory = true,
                        SizeBytes = 0,
                        ModifiedUtc = info.LastWriteTimeUtc
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Skip a single unreadable entry so one bad ACL doesn't 500 the whole listing.
                    Diagnostics.Record("FileBrowser.List", "The Files tab left out the folder '" + dir + "' because it could not be read. Check that the user Jellyfin runs as can read it. Details: " + ex.Message);
                }
            }

            foreach (var file in Directory.EnumerateFiles(full))
            {
                try
                {
                    var info = new FileInfo(file);
                    entries.Add(new FileEntry
                    {
                        Name = info.Name,
                        IsDirectory = false,
                        SizeBytes = info.Length,
                        ModifiedUtc = info.LastWriteTimeUtc
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Diagnostics.Record("FileBrowser.List", "The Files tab left out the file '" + file + "' because it could not be read. Check that the user Jellyfin runs as can read it. Details: " + ex.Message);
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Diagnostics.Record("FileBrowser.List", "The Files tab could not open '" + full + "' because Jellyfin does not have permission to read it. Give the user Jellyfin runs as read access to this folder. Details: " + ex.Message);
            return StatusCode(StatusCodes.Status403Forbidden, "Jellyfin lacks read access to " + full + ".");
        }
        catch (IOException ex)
        {
            Diagnostics.Record("FileBrowser.List", "The Files tab could not open '" + full + "'. The drive or network share may be offline or busy; check that it is connected and try again. Details: " + ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, "Could not list folder: " + ex.Message);
        }

        // Parent is the pseudo-root (empty) when 'full' is itself a library location, the recycle-bin
        // root, or the logs root; otherwise the containing directory. Users still land on the
        // file-browser root when they hit "Up" from a top-level entry.
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var trimmedFull = Path.TrimEndingDirectorySeparator(full);
        var isLibraryRoot = _libraryManager.GetVirtualFolders()
            .ToList()
            .SelectMany(f => f.Locations)
            .Any(l => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(l)), trimmedFull, cmp));
        var isRecycleBinRoot = isInsideBin && string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(_recycleBin.GetEffectiveRoot())),
            trimmedFull,
            cmp);
        var isLogsRoot = isInsideLogs && string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(_appPaths.LogDirectoryPath)),
            trimmedFull,
            cmp);

        return new DirectoryListing
        {
            Path = full,
            Parent = (isLibraryRoot || isRecycleBinRoot || isLogsRoot) ? string.Empty : Path.GetDirectoryName(full),
            IsRoot = false,
            IsRecycleBin = isInsideBin,
            IsLogsDir = isInsideLogs,
            Entries = entries
        };
    }

    private bool TryResolveInsideRecycleBin(string? userPath, out string canonical)
    {
        return TryResolveInsideRoot(userPath, _recycleBin.GetEffectiveRoot(), out canonical);
    }

    private bool TryResolveInsideLogsDir(string? userPath, out string canonical)
    {
        return TryResolveInsideRoot(userPath, _appPaths.LogDirectoryPath, out canonical);
    }

    // Shared canonicalise-and-anchor check for the two read-only shortcut roots (recycle bin, logs).
    // A path is "inside" the root when it equals the root or starts with "<root>/" — the trailing
    // separator anchor rejects "C:\logs-adjacent" from matching a "C:\logs" root.
    private static bool TryResolveInsideRoot(string? userPath, string? rootPath, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(userPath) || string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        try
        {
            canonical = Path.GetFullPath(userPath);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        var trimmed = Path.TrimEndingDirectorySeparator(canonical);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(trimmed, root, cmp))
        {
            return true;
        }

        return trimmed.StartsWith(root + Path.DirectorySeparatorChar, cmp)
            || trimmed.StartsWith(root + Path.AltDirectorySeparatorChar, cmp);
    }

    /// <summary>
    /// Creates a subdirectory inside a library folder.
    /// </summary>
    /// <param name="request">The mkdir request.</param>
    /// <returns>No content.</returns>
    [HttpPost("Mkdir")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult Mkdir([FromBody] MkdirRequest request)
    {
        if (!IsSimpleName(request.Name))
        {
            return BadRequest("Folder name contains invalid characters.");
        }

        if (!TryResolveInsideLibrary(request.Path, out var parent, out var forbid))
        {
            return forbid;
        }

        if (!Directory.Exists(parent))
        {
            return NotFound();
        }

        var target = Path.Combine(parent, request.Name);
        if (!RevalidateInsideLibrary(target, out forbid))
        {
            return forbid;
        }

        if (Directory.Exists(target) || System.IO.File.Exists(target))
        {
            return Conflict("An entry with that name already exists.");
        }

        try
        {
            Directory.CreateDirectory(target);
        }
        catch (UnauthorizedAccessException ex)
        {
            return PermissionDenied(parent, ex);
        }
        catch (IOException ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "Could not create folder: " + ex.Message);
        }

        _libraryMonitor.ReportFileSystemChanged(target);
        return NoContent();
    }

    /// <summary>
    /// Renames a file or directory in place.
    /// </summary>
    /// <param name="request">The rename request.</param>
    /// <returns>No content.</returns>
    [HttpPost("Rename")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Rename([FromBody] RenameRequest request)
    {
        if (!IsSimpleName(request.NewName))
        {
            return BadRequest("New name contains invalid characters.");
        }

        if (!TryResolveInsideLibrary(request.Path, out var source, out var forbid))
        {
            return forbid;
        }

        var parent = Path.GetDirectoryName(source);
        if (parent is null || _guard.IsLibraryRoot(source))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "A library folder itself cannot be renamed here.");
        }

        return await Move(new MoveOrCopyRequest
        {
            From = source, To = Path.Combine(parent, request.NewName),
            PlanFingerprint = request.PlanFingerprint, PreviewOnly = request.PreviewOnly
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves a file or directory to a new location. Both endpoints must be inside library folders.
    /// </summary>
    /// <param name="request">The move request.</param>
    /// <returns>No content.</returns>
    [HttpPost("Move")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Move([FromBody] MoveOrCopyRequest request)
    {
        if (_movePlanner is null || _moves is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Complete move previews are unavailable on this server.");
        }

        if (request.Selections is null || request.Selections.Count > 4096
            || (request.Selections.Count > 0 && (!string.IsNullOrEmpty(request.From) || !string.IsNullOrEmpty(request.To))))
        {
            return BadRequest("Provide either one source and destination or one complete selection of up to 4096 paths.");
        }

        var selection = request.Selections.Count == 0 ? new[] { new FileMoveSelection { From = request.From, To = request.To } } : request.Selections;
        var paths = new List<(string Source, string Destination)>();
        foreach (var selected in selection)
        {
            if (selected is null || !TryResolveInsideLibrary(selected.From, out var source, out var forbidFrom))
            {
                return StatusCode(StatusCodes.Status403Forbidden, "A selected source is outside library folders.");
            }

            if (!TryResolveInsideLibrary(selected.To, out var target, out var forbidTo))
            {
                return forbidTo;
            }

            if (!RevalidateInsideLibrary(source, out forbidFrom))
            {
                return forbidFrom;
            }

            if (!RevalidateInsideLibrary(target, out forbidTo))
            {
                return forbidTo;
            }

            if (!System.IO.File.Exists(source) && !Directory.Exists(source))
            {
                return NotFound("A selected source no longer exists. Refresh the folder before previewing again.");
            }

            paths.Add((source, target));
        }

        try
        {
            IReadOnlyList<MediaBrowser.Controller.Entities.BaseItem> peers;
            try
            {
                peers = _libraryManager.GetItemList(MediaMovePlanner.CatalogQuery());
            }
            catch (InvalidOperationException error)
            {
                _logger.LogWarning(error, "The current media catalog could not be read for a complete move preview");
                return Conflict("Jellyfin's current media catalog could not be read completely. Run its library scan and preview these files again. Nothing was moved.");
            }

            var plan = await _movePlanner.BuildExplicitAsync(paths, peers, Plugin.Instance!.Configuration, HttpContext?.RequestAborted ?? default).ConfigureAwait(false);
            var preview = FileMovePreviewDto.FromPlan(plan);
            preview.DryRun = Plugin.Instance.Configuration.DryRun;
            if (!plan.HasValidDefinition())
            {
                preview.BlockingReasons = preview.BlockingReasons.Append("The selections overlap or the complete move could not be verified. Review the source and destination paths.").ToArray();
            }

            if (request.PreviewOnly)
            {
                return Ok(preview);
            }

            if (preview.BlockingReasons.Count != 0 || request.PlanFingerprint != plan.Fingerprint)
            {
                return Conflict(preview);
            }

            var result = await _moves.ExecuteExplicitAsync(plan, request.PlanFingerprint, null, HttpContext?.RequestAborted ?? default).ConfigureAwait(false);
            if (!result.Success)
            {
                return Conflict(result);
            }

            if (!result.WasDryRun)
            {
                try
                {
                    _history.AddHistory(new Data.HistoryEntry
                    {
                        Type = Data.IssueType.ManualMove, Path = plan.Entries[0].Source,
                        Action = "Moved the complete previewed selection.", Success = true, FixedAtUtc = DateTime.UtcNow,
                        MoveOperationId = result.MoveOperationId
                    });
                }
                catch (Microsoft.Data.Sqlite.SqliteException error)
                {
                    Diagnostics.Record("MediaMove.History", "The files were moved and Undo is still available, but this move could not be added to the History tab. Nothing needs fixing. Details: " + error.Message);
                }

                foreach (var entry in _moves.HandlesNativeRefresh ? Array.Empty<MediaMovePlan.Entry>() : plan.Entries)
                {
                    foreach (var changedPath in new[] { entry.Source, entry.Destination })
                    {
                        try
                        {
                            _libraryMonitor.ReportFileSystemChanged(changedPath);
                        }
                        catch (Exception error) when (error is not OperationCanceledException)
                        {
                            _logger.LogWarning(error, "Completed move {OperationId} could not notify Jellyfin of {Path}", result.MoveOperationId, changedPath);
                            Diagnostics.Record("MediaMove.Refresh", "The move finished, but Jellyfin could not refresh '" + changedPath + "', so it may still show the old location. Run 'Scan Media Library' in the Jellyfin dashboard to update it.");
                        }
                    }
                }
            }

            return Ok(result);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Conflict("The move could not be previewed safely: " + error.Message);
        }
    }

    /// <summary>
    /// Copies a file or directory. Both endpoints must be inside library folders.
    /// </summary>
    /// <param name="request">The copy request.</param>
    /// <returns>No content.</returns>
    [HttpPost("Copy")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult Copy([FromBody] MoveOrCopyRequest request)
    {
        if (!TryResolveInsideLibrary(request.From, out var source, out var forbidFrom))
        {
            return forbidFrom;
        }

        if (!TryResolveInsideLibrary(request.To, out var target, out var forbidTo))
        {
            return forbidTo;
        }

        var sourceIsDir = Directory.Exists(source);
        if (!System.IO.File.Exists(source) && !sourceIsDir)
        {
            return NotFound();
        }

        if (System.IO.File.Exists(target) || Directory.Exists(target))
        {
            return Conflict("An entry already exists at the destination.");
        }

        if (!RevalidateInsideLibrary(source, out forbidFrom))
        {
            return forbidFrom;
        }

        if (!RevalidateInsideLibrary(target, out forbidTo))
        {
            return forbidTo;
        }

        // Review P1-3: reject copying/moving a directory into itself or into any descendant of
        // itself. Without this, CopyDirectory recurses through the target it just created and
        // builds an infinite chain until the disk or path length runs out. Applies only when the
        // source is a directory; a file copy into a subfolder is a legitimate operation.
        if (sourceIsDir && IsSelfOrDescendant(target, source))
        {
            return Conflict("Cannot copy a folder into itself or a subfolder of itself.");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (sourceIsDir)
            {
                CopyDirectory(source, target);
            }
            else
            {
                System.IO.File.Copy(source, target, overwrite: false);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            return PermissionDenied(target, ex);
        }
        catch (IOException ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "Copy failed: " + ex.Message);
        }

        _libraryMonitor.ReportFileSystemChanged(target);
        return NoContent();
    }

    /// <summary>
    /// Sends a file or directory to the recycle bin.
    /// </summary>
    /// <param name="request">The delete request.</param>
    /// <returns>No content.</returns>
    [HttpPost("Delete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Delete([FromBody] DeleteRequest request)
    {
        if (!TryResolveInsideLibrary(request.Path, out var full, out var forbid))
        {
            return forbid;
        }

        if (_libraryManager.GetVirtualFolders().SelectMany(folder => folder.Locations ?? [])
            .Any(root => LibraryGuard.IsUnder(Path.GetFullPath(root), full)))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "A library folder itself cannot be deleted here.");
        }

        if (!System.IO.File.Exists(full) && !Directory.Exists(full))
        {
            return NotFound();
        }

        if (!RevalidateInsideLibrary(full, out forbid))
        {
            return forbid;
        }

        // Roadmap i-110-protected-media: refuse manual deletes for paths under a Never-modify rule
        // that masks ManualDelete. Library containment alone is not enough — a protected folder
        // sits inside a library. Directory deletes are refused too: any file inside the tree would
        // be blocked by a scanner rule, and MoveToBin recycles the whole tree in one operation.
        if (_protection.IsFixerBlocked(full, Data.IssueType.ManualDelete))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { Error = "Path is protected by a 'Never modify' rule; remove the rule to allow deletion." });
        }

        // External audit P1 #2: when the target IS a directory, the parent-only check misses rules
        // whose protected root sits BELOW the target. Deleting /lib/show would otherwise recycle
        // /lib/show/season3 even if a rule explicitly protects it. Walk the ruleset once more
        // asking "does any rule's root sit under this path."
        if (System.IO.Directory.Exists(full)
            && _protection.IsAnyDescendantProtected(full, Data.IssueType.ManualDelete))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { Error = "This folder contains a protected sub-folder or library; remove the rule or delete the protected portion first." });
        }

        string binPath;
        long size = 0;
        try
        {
            if (System.IO.File.Exists(full))
            {
                size = new System.IO.FileInfo(full).Length;
            }
            else if (System.IO.Directory.Exists(full))
            {
                // Directory delete via the Files tab writes a HistoryEntry — before this branch
                // the size fell through as 0 and dashboard "reclaimed" totals under-reported by
                // the tree size, breaking quota accounting and per-library reclaim charts. Walk
                // the tree once; IgnoreInaccessible so a single unreadable subfolder doesn't
                // abort the size scan mid-count (matches RecycleBin.GetContents' enum options).
                try
                {
                    var enumOpts = new System.IO.EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
                    foreach (var f in System.IO.Directory.EnumerateFiles(full, "*", enumOpts))
                    {
                        try
                        {
                            size += new System.IO.FileInfo(f).Length;
                        }
                        catch (System.IO.FileNotFoundException)
                        {
                            // Concurrent removal — skip silently. Same benign race the recycle-bin
                            // size scan handles the same way.
                        }
                    }
                }
                catch (System.IO.IOException)
                {
                    // Best-effort accounting — a failed walk shouldn't block the recycle. Size stays 0.
                }
            }

            binPath = _recycleBin.MoveToBin(full);
        }
        catch (UnauthorizedAccessException ex)
        {
            return PermissionDenied(full, ex);
        }
        catch (IOException ex)
        {
            return StatusCode(StatusCodes.Status409Conflict, "Delete failed: " + ex.Message);
        }

        // Record a history row so the Recycle bin tab's join labels this entry as "Manual delete
        // via Files tab" (via RecycleReasonMapper). Without this row the entry falls into the
        // manifest-only fallback branch, which the older code also labelled as manual — but that
        // branch ALSO catches older-version auto-fix recycles whose fixer paths didn't yet write
        // history rows, mislabelling them as manual. Splitting the two lets the manifest-only
        // fallback drop the misleading "manual" wording (see MediaDashController.GetRecycleBinItems).
        try
        {
            _history.AddHistory(new Data.HistoryEntry
            {
                IssueId = 0,
                Type = Data.IssueType.ManualDelete,
                Path = full,
                Action = "Manual delete via Files tab",
                BytesFreed = size,
                RecyclePath = binPath,
                FixedAtUtc = DateTime.UtcNow,
                WasDryRun = false,
                Success = true
            });
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            Diagnostics.Record("FileBrowser.DeleteHistory", "The file was moved to the recycle bin ('" + binPath + "'), but it could not be added to the History tab. You can still restore it from the Recycle bin tab. Details: " + ex.Message);
        }

        _libraryMonitor.ReportFileSystemChanged(full);
        _logger.LogInformation("File browser recycled {Path}", full);
        return NoContent();
    }

    /// <summary>
    /// Streams the request body into a new file inside a library folder.
    /// The request body IS the file; use content-type application/octet-stream.
    /// </summary>
    /// <param name="path">The target directory (must be inside a library).</param>
    /// <param name="name">The file name to create (no separators, no "..").</param>
    /// <returns>No content on success.</returns>
    [HttpPost("Upload")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadMaxBytes)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult> Upload([FromQuery] string path, [FromQuery] string name)
    {
        if (!IsSimpleName(name))
        {
            return BadRequest("File name contains invalid characters.");
        }

        // Reject over-cap uploads before any disk I/O — the streaming counter below is the second
        // gate for chunked-encoding requests without a declared length, but that means up to 50 GB
        // hits the disk before the check fires. Content-Length short-circuits that.
        if (Request.ContentLength is long declaredLength && declaredLength > UploadMaxBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, "Upload exceeds the " + (UploadMaxBytes / (1024L * 1024 * 1024)) + " GB per-file cap.");
        }

        if (!TryResolveInsideLibrary(path, out var parent, out var forbid))
        {
            return forbid;
        }

        if (!Directory.Exists(parent))
        {
            return NotFound();
        }

        var target = Path.Combine(parent, name);
        if (!RevalidateInsideLibrary(target, out forbid))
        {
            return forbid;
        }

        if (System.IO.File.Exists(target) || Directory.Exists(target))
        {
            return Conflict("An entry with that name already exists.");
        }

        // Include a per-request GUID so two concurrent uploads to the same target (or a retry landing
        // beside an in-flight upload) get distinct temp files. Without this, the failure/cancel
        // catch-block could delete the other upload's temp mid-flight.
        var tempPath = target + "." + Guid.NewGuid().ToString("N")[..8] + UploadTempSuffix;
        System.IO.FileStream output;
        try
        {
            output = System.IO.File.Create(tempPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            return PermissionDenied(parent, ex);
        }
        catch (IOException ex)
        {
            return StatusCode(StatusCodes.Status507InsufficientStorage, "Could not create upload file: " + ex.Message);
        }

        // MD-005: track over-cap breach with a flag rather than a distinguished exception so the
        // catch block can distinguish intentional cap-refusal (→ 413) from real I/O failure (→
        // 500). Before this, both bubbled up as IOException and the ASP.NET pipeline turned the
        // over-cap case into a misleading generic server error, which chunked clients had no way
        // to interpret as "your file is too big" vs "infrastructure broke."
        var exceededCap = false;
        try
        {
            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await Request.Body.ReadAsync(buffer, HttpContext.RequestAborted).ConfigureAwait(false)) > 0)
            {
                totalRead += read;
                if (totalRead > UploadMaxBytes)
                {
                    exceededCap = true;
                    throw new IOException("Upload exceeded the " + (UploadMaxBytes / (1024L * 1024 * 1024)) + " GB per-file cap.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), HttpContext.RequestAborted).ConfigureAwait(false);
            }

            await output.FlushAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            await output.DisposeAsync().ConfigureAwait(false);

            if (!_guard.IsInsideLibrary(tempPath) || !_guard.IsInsideLibrary(target) || _guard.IsMoveReserved(target))
            {
                throw new IOException("Upload path changed after validation; refusing to finalize the file.");
            }

            System.IO.File.Move(tempPath, target);
            _libraryMonitor.ReportFileSystemChanged(target);
            _logger.LogInformation("File browser uploaded {Target}", target);
            return NoContent();
        }
        catch (Exception failure)
        {
            await output.DisposeAsync().ConfigureAwait(false);
            if (System.IO.File.Exists(tempPath))
            {
                try
                {
                    System.IO.File.Delete(tempPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Diagnostics.Record("FileBrowser.UploadCleanup", "An upload failed and its temporary file '" + tempPath + "' could not be removed. Delete that file by hand to free the space. Details: " + ex.Message);
                }
            }

            if (exceededCap)
            {
                return StatusCode(StatusCodes.Status413PayloadTooLarge, "Upload exceeds the " + (UploadMaxBytes / (1024L * 1024 * 1024)) + " GB per-file cap.");
            }

            if (failure is UnauthorizedAccessException denied)
            {
                return PermissionDenied(target, denied);
            }

            if (failure is IOException)
            {
                return StatusCode(StatusCodes.Status507InsufficientStorage, "Upload failed: " + failure.Message);
            }

            throw;
        }
    }

    /// <summary>
    /// Streams a file from a library folder to the client.
    /// </summary>
    /// <param name="path">The file to download.</param>
    /// <returns>The file bytes.</returns>
    [HttpGet("Download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Download([FromQuery] string path)
    {
        // Log files inside the Jellyfin log directory are downloadable so admins can attach them to
        // bug reports. Path is canonicalised via TryResolveInsideLogsDir before any FS call — no
        // traversal escape possible.
        string full;
        ActionResult forbid;
        if (TryResolveInsideLogsDir(path, out full))
        {
            if (!System.IO.File.Exists(full))
            {
                return NotFound();
            }

            try
            {
                var logStream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 81920, useAsync: true);
                return File(logStream, "application/octet-stream", Path.GetFileName(full), enableRangeProcessing: true);
            }
            catch (UnauthorizedAccessException ex)
            {
                return PermissionDenied(full, ex);
            }
            catch (IOException ex)
            {
                return Conflict("Download unavailable: " + ex.Message);
            }
        }

        if (!TryResolveInsideLibrary(path, out full, out forbid))
        {
            return forbid;
        }

        if (!System.IO.File.Exists(full))
        {
            return NotFound();
        }

        if (!RevalidateInsideLibrary(full, out forbid, writing: false))
        {
            return forbid;
        }

        try
        {
            var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
            return File(stream, "application/octet-stream", Path.GetFileName(full), enableRangeProcessing: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            return PermissionDenied(full, ex);
        }
        catch (IOException ex)
        {
            return Conflict("Download unavailable: " + ex.Message);
        }
    }

    private bool TryResolveInsideLibrary(string? userPath, out string canonical, out ActionResult forbid)
    {
        canonical = string.Empty;
        forbid = StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrWhiteSpace(userPath))
        {
            return false;
        }

        try
        {
            canonical = Path.GetFullPath(userPath);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }

        if (!_guard.IsInsideLibrary(canonical))
        {
            return false;
        }

        return true;
    }

    private bool RevalidateInsideLibrary(string path, out ActionResult forbid, bool writing = true)
    {
        if (writing && _guard.IsMoveReserved(path))
        {
            forbid = Conflict("This path is reserved by an unfinished move. Review its recorded paths before changing these files.");
            return false;
        }

        forbid = Conflict("Refused because the path changed after validation. Retry after checking the library folder for symlinks or junctions.");
        return _guard.IsInsideLibrary(path);
    }

    private ObjectResult PermissionDenied(string path, UnauthorizedAccessException ex)
    {
        var msg = "Jellyfin lacks write access to " + path + ". Check that the file (and its containing folder) is owned by or read+writable by the user Jellyfin runs as (usually 'jellyfin' on Linux).";
        Diagnostics.Record("FileBrowser.PermissionDenied", msg);
        _logger.LogWarning(ex, "File browser permission denied on {Path}", path);
        return StatusCode(StatusCodes.Status403Forbidden, msg);
    }

    // Review P1-3: true when candidate is the same folder as parent or lives inside it. Both
    // paths are canonicalised via Path.GetFullPath before comparison; Windows comparison is
    // case-insensitive to match how the underlying filesystem resolves lookups.
    internal static bool IsSelfOrDescendant(string candidate, string parent)
    {
        try
        {
            var canonCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            var canonParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            if (string.Equals(canonCandidate, canonParent, comparison))
            {
                return true;
            }

            return Fixers.LibraryGuard.IsUnder(canonCandidate, canonParent);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool IsSimpleName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (name is "." or "..")
        {
            return false;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        // Windows collapses trailing dots/spaces silently; refuse to create paths that would then
        // fail to be re-opened by name. Also reject reserved device names — CreateDirectory("CON")
        // aliases to the console device rather than creating a folder.
        if (name.EndsWith('.') || name.EndsWith(' '))
        {
            return false;
        }

        var baseName = Path.GetFileNameWithoutExtension(name);
        return !WindowsReservedNames.Contains(baseName);
    }

    private static void CopyDirectory(string source, string target, bool preserveTimestamps = false)
    {
        // Skip reparse points to defend against symlink cycles inside the tree — an accidental self-
        // referential link (e.g. /movies/all -> /movies) would otherwise recurse until StackOverflow
        // terminates the Jellyfin process.
        var enumOpts = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint };

        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", enumOpts))
        {
            var dst = Path.Combine(target, Path.GetFileName(file));
            System.IO.File.Copy(file, dst, overwrite: false);
            if (preserveTimestamps)
            {
                MediaMoveMetadata.Preserve(file, dst, directory: false);
            }
        }

        foreach (var sub in Directory.EnumerateDirectories(source, "*", enumOpts))
        {
            CopyDirectory(sub, Path.Combine(target, Path.GetFileName(sub)), preserveTimestamps);
        }

        if (preserveTimestamps)
        {
            MediaMoveMetadata.Preserve(source, target, directory: true);
        }
    }

    private static bool IsCrossDeviceError(IOException ex)
    {
        // Check the HResult first — message strings are localized on non-English hosts and would
        // otherwise miss real EXDEV errors ("デバイスをまたぐ…" instead of "cross-device"). Windows
        // uses ERROR_NOT_SAME_DEVICE (0x11 = 17); Linux/macOS use EXDEV (18). Fall back to the message
        // check for TFMs where the HResult happens to be repackaged.
        var code = ex.HResult & 0xFFFF;
        return code == 17 || code == 18
            || ex.Message.Contains("cross-device", StringComparison.OrdinalIgnoreCase);
    }

    internal static void CrossDeviceMove(string source, string target, bool sourceIsDir, Action<string, string>? checkpoint = null, CancellationToken cancellationToken = default)
    {
        // Compatibility entry used by recycle operations. Cross-filesystem copy/delete
        // is unsupported; a same-filesystem call keeps the complete source object.
        cancellationToken.ThrowIfCancellationRequested();
        MediaMoveSafety.RequireSameFilesystemMove(source, target);
        checkpoint?.Invoke("BeforeNativeRename", source);
        cancellationToken.ThrowIfCancellationRequested();
        MediaMoveSafety.RequireSameFilesystemMove(source, target);
        MediaMoveSafety.RenameNoCopy(source, target, sourceIsDir);
        checkpoint?.Invoke("Published", target);
    }
}
