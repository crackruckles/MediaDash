using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Fixers;
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
[assembly: SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Scope = "member", Target = "~M:Jellyfin.Plugin.MediaDash.Fixers.RecycleBin.MoveAcrossVolumes(System.String,System.String)", Justification = "Only called from validated code paths.")]

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

    private readonly LibraryGuard _guard;
    private readonly RecycleBin _recycleBin;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<FileBrowserController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileBrowserController"/> class.
    /// </summary>
    /// <param name="guard">Library path guard.</param>
    /// <param name="recycleBin">Recycle bin.</param>
    /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="logger">Logger.</param>
    public FileBrowserController(LibraryGuard guard, RecycleBin recycleBin, ILibraryMonitor libraryMonitor, ILibraryManager libraryManager, ILogger<FileBrowserController> logger)
    {
        _guard = guard;
        _recycleBin = recycleBin;
        _libraryMonitor = libraryMonitor;
        _libraryManager = libraryManager;
        _logger = logger;
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
            var roots = _libraryManager.GetVirtualFolders()
                .SelectMany(f => f.Locations)
                .Distinct(StringComparer.OrdinalIgnoreCase)
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
            return new DirectoryListing { Path = string.Empty, Parent = null, IsRoot = true, Entries = roots };
        }

        if (!TryResolveInsideLibrary(path, out var full, out var forbid))
        {
            return forbid;
        }

        if (!Directory.Exists(full))
        {
            return NotFound();
        }

        var entries = new List<FileEntry>();
        foreach (var dir in Directory.EnumerateDirectories(full))
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

        foreach (var file in Directory.EnumerateFiles(full))
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

        // Parent is the pseudo-root (empty) when 'full' is itself a library location, otherwise the containing directory.
        var isLibraryRoot = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations)
            .Any(l => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(l)), Path.TrimEndingDirectorySeparator(full), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

        return new DirectoryListing
        {
            Path = full,
            Parent = isLibraryRoot ? string.Empty : Path.GetDirectoryName(full),
            IsRoot = false,
            Entries = entries
        };
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
        if (Directory.Exists(target) || System.IO.File.Exists(target))
        {
            return Conflict("An entry with that name already exists.");
        }

        Directory.CreateDirectory(target);
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
    public ActionResult Rename([FromBody] RenameRequest request)
    {
        if (!IsSimpleName(request.NewName))
        {
            return BadRequest("New name contains invalid characters.");
        }

        if (!TryResolveInsideLibrary(request.Path, out var source, out var forbid))
        {
            return forbid;
        }

        if (!System.IO.File.Exists(source) && !Directory.Exists(source))
        {
            return NotFound();
        }

        var target = Path.Combine(Path.GetDirectoryName(source)!, request.NewName);
        if (System.IO.File.Exists(target) || Directory.Exists(target))
        {
            return Conflict("An entry with that name already exists.");
        }

        if (Directory.Exists(source))
        {
            Directory.Move(source, target);
        }
        else
        {
            System.IO.File.Move(source, target);
        }

        _libraryMonitor.ReportFileSystemChanged(source);
        _libraryMonitor.ReportFileSystemChanged(target);
        return NoContent();
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
    public ActionResult Move([FromBody] MoveOrCopyRequest request)
    {
        if (!TryResolveInsideLibrary(request.From, out var source, out var forbidFrom))
        {
            return forbidFrom;
        }

        if (!TryResolveInsideLibrary(request.To, out var target, out var forbidTo))
        {
            return forbidTo;
        }

        if (!System.IO.File.Exists(source) && !Directory.Exists(source))
        {
            return NotFound();
        }

        if (System.IO.File.Exists(target) || Directory.Exists(target))
        {
            return Conflict("An entry already exists at the destination.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (Directory.Exists(source))
        {
            Directory.Move(source, target);
        }
        else
        {
            System.IO.File.Move(source, target);
        }

        _libraryMonitor.ReportFileSystemChanged(source);
        _libraryMonitor.ReportFileSystemChanged(target);
        return NoContent();
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

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (sourceIsDir)
        {
            CopyDirectory(source, target);
        }
        else
        {
            System.IO.File.Copy(source, target, overwrite: false);
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

        if (!System.IO.File.Exists(full) && !Directory.Exists(full))
        {
            return NotFound();
        }

        _recycleBin.MoveToBin(full);
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
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Upload([FromQuery] string path, [FromQuery] string name)
    {
        if (!IsSimpleName(name))
        {
            return BadRequest("File name contains invalid characters.");
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
        if (System.IO.File.Exists(target) || Directory.Exists(target))
        {
            return Conflict("An entry with that name already exists.");
        }

        var tempPath = target + UploadTempSuffix;
        var output = System.IO.File.Create(tempPath);
        try
        {
            await Request.Body.CopyToAsync(output, HttpContext.RequestAborted).ConfigureAwait(false);
            await output.FlushAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            await output.DisposeAsync().ConfigureAwait(false);

            System.IO.File.Move(tempPath, target);
            _libraryMonitor.ReportFileSystemChanged(target);
            _logger.LogInformation("File browser uploaded {Target}", target);
            return NoContent();
        }
        catch
        {
            await output.DisposeAsync().ConfigureAwait(false);
            if (System.IO.File.Exists(tempPath))
            {
                try
                {
                    System.IO.File.Delete(tempPath);
                }
                catch (IOException)
                {
                }
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
        if (!TryResolveInsideLibrary(path, out var full, out var forbid))
        {
            return forbid;
        }

        if (!System.IO.File.Exists(full))
        {
            return NotFound();
        }

        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        return File(stream, "application/octet-stream", Path.GetFileName(full), enableRangeProcessing: true);
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

        return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            System.IO.File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var sub in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(sub, Path.Combine(target, Path.GetFileName(sub)));
        }
    }
}
