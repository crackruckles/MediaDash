using System;
using System.Net.Mime;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaDash.Controllers;

/// <summary>
/// REST API controller for MediaDash.
/// All endpoints require Jellyfin administrator authentication.
/// </summary>
[ApiController]
[Route("mediadash/api")]
[Authorize(Policy = "RequiresElevation")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class MediaDashController : ControllerBase
{
    private readonly MediaDashService _svc;
    private readonly FileExplorerService _fs;

    /// <summary>
    /// Initialises the controller with required services.
    /// </summary>
    /// <param name="svc">The core MediaDash data service.</param>
    /// <param name="fs">The file-explorer and auto-sort service.</param>
    public MediaDashController(MediaDashService svc, FileExplorerService fs)
    {
        _svc = svc;
        _fs = fs;
    }

    // ── Static JS asset ────────────────────────────────────────────────────

    /// <summary>
    /// Serves the dashboard JavaScript bundle as an external script.
    /// AllowAnonymous is required because the script loads before auth tokens
    /// are available in the browser context.
    /// </summary>
    [HttpGet("js/dashboard.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetDashboardJs()
    {
        var asm = Assembly.GetExecutingAssembly();
        var stream = asm.GetManifestResourceStream("Jellyfin.Plugin.MediaDash.Web.dashboard.js");
        return stream is null ? NotFound() : File(stream, "application/javascript; charset=utf-8");
    }

    // ── System overview ────────────────────────────────────────────────────

    /// <summary>Gets disk usage for all configured media directories.</summary>
    [HttpGet("drives")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<DriveStats[]> GetDrives() => Ok(_svc.GetDrives());

    /// <summary>Gets live system metrics (CPU, GPU, RAM, temperatures, disk I/O).</summary>
    [HttpGet("metrics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<MetricsInfo> GetMetrics() => _svc.GetMetrics();

    /// <summary>Gets currently active Jellyfin playback sessions.</summary>
    [HttpGet("streams")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<StreamInfo[]> GetStreams() => _svc.GetStreams();

    /// <summary>Gets the overall plugin status (encoding state, quiet hours, streams).</summary>
    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<StatusInfo> GetStatus() => _svc.GetStatus();

    // ── Encode queue ───────────────────────────────────────────────────────

    /// <summary>Gets the status of the currently running encode worker(s).</summary>
    [HttpGet("encode_status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<EncodeStatusResponse> GetEncodeStatus() => _svc.GetEncodeStatus();

    /// <summary>Gets files that are queued for re-encoding.</summary>
    [HttpGet("encode_remaining")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<QueueItem[]> GetRemaining() => _svc.GetEncodeRemaining();

    // ── Processing logs ────────────────────────────────────────────────────

    /// <summary>Parses the strip-tracks log and returns per-file results.</summary>
    [HttpGet("strip")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<StripEntry[]> GetStrip() => _svc.ParseStripLog();

    /// <summary>Parses the re-encode log and returns per-file results.</summary>
    [HttpGet("reencode")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ReencodeEntry[]> GetReencode() => _svc.ParseReencodeLog();

    // ── Duplicates ─────────────────────────────────────────────────────────

    /// <summary>Returns the latest duplicate-detection report.</summary>
    [HttpGet("dupes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<DupesReport> GetDupes() => _svc.GetDupesReport();

    // ── Libraries & configuration ──────────────────────────────────────────

    /// <summary>Returns the Jellyfin library paths used by this plugin.</summary>
    [HttpGet("libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<LibraryInfo[]> GetLibraries() => _svc.GetLibraries();

    /// <summary>Returns the current language/track-stripping settings.</summary>
    [HttpGet("languages")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<LanguageSettings> GetLanguages() => _svc.GetLanguageSettings();

    // ── Actions ────────────────────────────────────────────────────────────

    /// <summary>Pauses the encoder by writing the pause flag file.</summary>
    [HttpPost("pause")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<OkResponse> Pause()
    {
        _svc.Pause();
        return new OkResponse(true, "Paused");
    }

    /// <summary>Resumes the encoder by removing the pause flag file.</summary>
    [HttpPost("resume")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<OkResponse> Resume()
    {
        _svc.Resume();
        return new OkResponse(true, "Resuming");
    }

    /// <summary>Starts a configured systemd service (reencode or strip).</summary>
    [HttpPost("run")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<OkResponse> Run([FromBody] RunRequest req)
    {
        var cfg = Plugin.Instance!.Configuration;
        var service = req.Script switch
        {
            "reencode" => cfg.ReencodeServiceName,
            "strip"    => cfg.StripServiceName,
            _          => null,
        };

        if (string.IsNullOrWhiteSpace(service))
        {
            return BadRequest(new ErrorResponse(
                "Service not configured — set it in MediaDash Settings."));
        }

        _svc.StartService(service);
        return new OkResponse(true, $"Started {service}");
    }

    /// <summary>Starts the duplicate-detection scan script.</summary>
    [HttpPost("dupes/scan")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<OkResponse> ScanDupes()
    {
        if (!_svc.StartDupesScan())
        {
            return NotFound(new ErrorResponse(
                "Dupes scan script not configured — set DupesScanScript in Settings."));
        }

        return new OkResponse(true, "Scan started");
    }

    /// <summary>Deletes duplicate files identified by the scan report.</summary>
    /// <param name="req">Optional filter by IMDB ID. If null, deletes all duplicates.</param>
    [HttpPost("dupes/delete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<DeleteResponse> DeleteDupes([FromBody] DeleteDupesRequest? req)
        => _svc.DeleteDupes(req?.Imdb);

    /// <summary>Saves the quiet-hours schedule.</summary>
    [HttpPost("schedule")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ScheduleSaveResponse> SaveSchedule([FromBody] ScheduleRequest req)
        => _svc.SaveSchedule(req.PauseStart, req.PauseEnd);

    /// <summary>Saves language/track-stripping preferences.</summary>
    [HttpPost("languages")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<OkResponse> SaveLanguages([FromBody] LanguageSettings req)
    {
        _svc.SaveLanguageSettings(req);
        return new OkResponse(true, "Language settings saved");
    }

    // ── File explorer ──────────────────────────────────────────────────────

    /// <summary>
    /// Lists the contents of a directory, sandboxed to configured library roots.
    /// </summary>
    /// <param name="path">Absolute path to the directory to list.</param>
    [HttpGet("fs/list")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<FsListing> FsList([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest(new ErrorResponse("path is required"));
        }

        try
        {
            return Ok(_fs.List(path));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse(ex.Message));
        }
        catch (System.IO.DirectoryNotFoundException ex)
        {
            return BadRequest(new ErrorResponse(ex.Message));
        }
    }

    /// <summary>Renames a file or directory.</summary>
    [HttpPost("fs/rename")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<FsOpResult> FsRename([FromBody] RenameRequest req)
        => _fs.Rename(req.Path, req.NewName);

    /// <summary>Deletes a file or directory (directories are removed recursively).</summary>
    [HttpPost("fs/delete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<FsOpResult> FsDelete([FromBody] PathRequest req)
        => _fs.Delete(req.Path);

    /// <summary>Moves a file or directory to a new parent directory.</summary>
    [HttpPost("fs/move")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<FsOpResult> FsMove([FromBody] MoveRequest req)
        => _fs.Move(req.SourcePath, req.DestDir);

    /// <summary>Copies a file or directory to a new parent directory.</summary>
    [HttpPost("fs/copy")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<FsOpResult> FsCopy([FromBody] MoveRequest req)
        => _fs.Copy(req.SourcePath, req.DestDir);

    // ── Auto-sort ──────────────────────────────────────────────────────────

    /// <summary>
    /// Scans a library root and returns a preview of which folders would be
    /// merged based on shared IMDB IDs (resolved via TMDB).
    /// </summary>
    [HttpPost("sort/preview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SortPreview>> SortPreview([FromBody] PathRequest req)
    {
        try
        {
            return Ok(await _fs.BuildSortPreview(req.Path).ConfigureAwait(false));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse(ex.Message));
        }
    }

    /// <summary>Merges duplicate folders for a single IMDB ID into the canonical folder.</summary>
    [HttpPost("sort/execute")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<FsOpResult> SortExecute([FromBody] SortExecuteRequest req)
    {
        try
        {
            return Ok(_fs.ExecuteSort(req.LibraryRoot, req.ImdbId));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse(ex.Message));
        }
    }

    /// <summary>
    /// Scans all top-level folders in a library root, resolving each to its
    /// IMDB ID via TMDB and writing a <c>.mediadash_imdb</c> marker file.
    /// </summary>
    [HttpPost("sort/scan")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<OkResponse>> SortScan([FromBody] PathRequest req)
    {
        var errors = new System.Collections.Generic.List<string>();
        try
        {
            await _fs.WriteImdbMarkers(req.Path, errors).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse(ex.Message));
        }

        return new OkResponse(
            true,
            errors.Count > 0
                ? $"Done with {errors.Count} warning(s): {string.Join("; ", errors)}"
                : "Scan complete");
    }
}

// ── Request body records ──────────────────────────────────────────────────

/// <summary>Request body for the /run endpoint.</summary>
public sealed record RunRequest(
    /// <summary>Script identifier: "reencode" or "strip".</summary>
    string Script);

/// <summary>Request body for /dupes/delete. Null Imdb deletes all groups.</summary>
public sealed record DeleteDupesRequest(
    /// <summary>Optional IMDB ID to limit deletion to a single group.</summary>
    string? Imdb);

/// <summary>Request body for /schedule.</summary>
public sealed record ScheduleRequest(
    /// <summary>Hour (0-23) at which encoding pauses.</summary>
    int PauseStart,
    /// <summary>Hour (0-23) at which encoding resumes.</summary>
    int PauseEnd);

/// <summary>Request body for single-path operations.</summary>
public sealed record PathRequest(
    /// <summary>Absolute path to the target file or directory.</summary>
    string Path);

/// <summary>Request body for rename operations.</summary>
public sealed record RenameRequest(
    /// <summary>Current absolute path.</summary>
    string Path,
    /// <summary>New filename (no directory component).</summary>
    string NewName);

/// <summary>Request body for move and copy operations.</summary>
public sealed record MoveRequest(
    /// <summary>Absolute path of the source file or directory.</summary>
    string SourcePath,
    /// <summary>Absolute path of the destination directory.</summary>
    string DestDir);

/// <summary>Request body for auto-sort execute.</summary>
public sealed record SortExecuteRequest(
    /// <summary>Root path of the Jellyfin library to sort.</summary>
    string LibraryRoot,
    /// <summary>IMDB ID whose duplicate folders should be merged.</summary>
    string ImdbId);
