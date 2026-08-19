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
            // Snapshot VirtualFolders before iterating — Jellyfin mutates this collection when a
            // library is added / removed via the dashboard, and a concurrent enumerate throws.
            var roots = _libraryManager.GetVirtualFolders()
                .ToList()
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
                    Diagnostics.Record("FileBrowser.List", "Skipping unreadable subfolder '" + dir + "': " + ex.Message + ".");
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
                    Diagnostics.Record("FileBrowser.List", "Skipping unreadable file '" + file + "': " + ex.Message + ".");
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Diagnostics.Record("FileBrowser.List", "Access denied listing '" + full + "': " + ex.Message + ". Grant the Jellyfin user read access on the folder.");
            return StatusCode(StatusCodes.Status403Forbidden, "Jellyfin lacks read access to " + full + ".");
        }
        catch (IOException ex)
        {
            Diagnostics.Record("FileBrowser.List", "IO error listing '" + full + "': " + ex.Message + ".");
            return StatusCode(StatusCodes.Status500InternalServerError, "Could not list folder: " + ex.Message);
        }

        // Parent is the pseudo-root (empty) when 'full' is itself a library location, otherwise the containing directory.
        var isLibraryRoot = _libraryManager.GetVirtualFolders()
            .ToList()
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
        // Pre-check kept as fast path for a clean 409; the Move-time check below is the real atomicity
        // guarantee — Directory.Move throws on collision, and File.Move (default overwrite:false)
        // uses rename(2) on Linux which is kernel-level atomic. A concurrent rename to the same name
        // can no longer silently overwrite the first request's result.
        if (System.IO.File.Exists(target) || Directory.Exists(target))
        {
            return Conflict("An entry with that name already exists.");
        }

        try
        {
            if (Directory.Exists(source))
            {
                Directory.Move(source, target);
            }
            else
            {
                System.IO.File.Move(source, target, overwrite: false);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            return PermissionDenied(source, ex);
        }
        catch (IOException) when (System.IO.File.Exists(target) || Directory.Exists(target))
        {
            return Conflict("An entry with that name already exists (created by a concurrent request).");
        }
        catch (IOException ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "Rename failed: " + ex.Message);
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

        var sourceIsDir = Directory.Exists(source);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (sourceIsDir)
            {
                Directory.Move(source, target);
            }
            else
            {
                // overwrite:false — on Linux this is a kernel-atomic rename(2). A concurrent Move to the
                // same target throws IOException and gets translated to 409 below rather than silently
                // clobbering the winner's result.
                System.IO.File.Move(source, target, overwrite: false);
            }
        }
        catch (IOException ex2) when (!IsCrossDeviceError(ex2) && (System.IO.File.Exists(target) || Directory.Exists(target)))
        {
            return Conflict("An entry already exists at the destination (created by a concurrent request).");
        }
        catch (IOException ex) when (IsCrossDeviceError(ex))
        {
            // Bind-mount setups: /movies and /media/shared-movies can both live under the same Jellyfin
            // library while the container sees them as separate filesystems. rename() returns EXDEV — the
            // .NET APIs surface it as IOException with "Invalid cross-device link". Fall back to copy →
            // rename → delete-source with a staging suffix so an interruption never leaves a half-copied
            // file under its final name.
            try
            {
                CrossDeviceMove(source, target, sourceIsDir);
            }
            catch (UnauthorizedAccessException innerAuth)
            {
                return PermissionDenied(source + " -> " + target, innerAuth);
            }
            catch (IOException innerIo)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Move failed: " + innerIo.Message);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            return PermissionDenied(source + " -> " + target, ex);
        }
        catch (IOException ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "Move failed: " + ex.Message);
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

        if (!System.IO.File.Exists(full) && !Directory.Exists(full))
        {
            return NotFound();
        }

        try
        {
            _recycleBin.MoveToBin(full);
        }
        catch (UnauthorizedAccessException ex)
        {
            return PermissionDenied(full, ex);
        }
        catch (IOException ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "Delete failed: " + ex.Message);
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
            return StatusCode(StatusCodes.Status500InternalServerError, "Could not create upload file: " + ex.Message);
        }

        try
        {
            // Byte-counted copy — stop and 413 if the body exceeds the cap rather than filling the disk.
            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await Request.Body.ReadAsync(buffer, HttpContext.RequestAborted).ConfigureAwait(false)) > 0)
            {
                totalRead += read;
                if (totalRead > UploadMaxBytes)
                {
                    throw new IOException("Upload exceeded the " + (UploadMaxBytes / (1024L * 1024 * 1024)) + " GB per-file cap.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), HttpContext.RequestAborted).ConfigureAwait(false);
            }

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
                catch (IOException ex)
                {
                    Diagnostics.Record("FileBrowser.UploadCleanup", "Upload failed and the temp file at '" + tempPath + "' could not be removed: " + ex.Message + ". Delete it manually to reclaim the space.");
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

    private ObjectResult PermissionDenied(string path, UnauthorizedAccessException ex)
    {
        var msg = "Jellyfin lacks write access to " + path + ". Check that the file (and its containing folder) is owned by or read+writable by the user Jellyfin runs as (usually 'jellyfin' on Linux).";
        Diagnostics.Record("FileBrowser.PermissionDenied", msg);
        _logger.LogWarning(ex, "File browser permission denied on {Path}", path);
        return StatusCode(StatusCodes.Status403Forbidden, msg);
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
                System.IO.File.SetLastWriteTimeUtc(dst, System.IO.File.GetLastWriteTimeUtc(file));
            }
        }

        foreach (var sub in Directory.EnumerateDirectories(source, "*", enumOpts))
        {
            CopyDirectory(sub, Path.Combine(target, Path.GetFileName(sub)), preserveTimestamps);
        }

        if (preserveTimestamps)
        {
            Directory.SetLastWriteTimeUtc(target, Directory.GetLastWriteTimeUtc(source));
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

    internal static void CrossDeviceMove(string source, string target, bool sourceIsDir)
    {
        // Copy under a marker suffix first — if we die mid-copy, the partial payload is obvious and never
        // sits under the final name. Only after the copy verifies do we rename → delete-source.
        var staging = target + ".mediadash-moving";

        try
        {
            if (sourceIsDir)
            {
                CopyDirectory(source, staging, preserveTimestamps: true);
            }
            else
            {
                System.IO.File.Copy(source, staging, overwrite: false);
                var copiedInfo = new FileInfo(staging);
                var sourceInfo = new FileInfo(source);
                if (copiedInfo.Length != sourceInfo.Length)
                {
                    throw new IOException("Copy verification failed: size mismatch (" + copiedInfo.Length + " vs " + sourceInfo.Length + " bytes).");
                }

                System.IO.File.SetLastWriteTimeUtc(staging, System.IO.File.GetLastWriteTimeUtc(source));
            }

            // Same-filesystem rename now — this one is a true atomic rename.
            if (sourceIsDir)
            {
                Directory.Move(staging, target);
            }
            else
            {
                System.IO.File.Move(staging, target);
            }

            // Source removal LAST, so any failure above still leaves the user's original intact.
            if (sourceIsDir)
            {
                Directory.Delete(source, recursive: true);
            }
            else
            {
                System.IO.File.Delete(source);
            }
        }
        catch
        {
            // Any failure: sweep the staging file/dir. The user's source is untouched — that's the
            // invariant this fallback guarantees.
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
                else if (System.IO.File.Exists(staging))
                {
                    System.IO.File.Delete(staging);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            throw;
        }
    }
}
