using System;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaDash.Controllers;

[ApiController]
[Route("mediadash/api")]
[Authorize(Policy = "RequiresElevation")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class MediaDashController : ControllerBase
{
    private readonly MediaDashService     _svc;
    private readonly FileExplorerService  _fs;

    public MediaDashController(MediaDashService svc, FileExplorerService fs)
    {
        _svc = svc; _fs = fs;
    }

    // ── Core endpoints ─────────────────────────────────────────────────────

    [HttpGet("drives")]           public ActionResult<DriveStats[]>          GetDrives()        => Ok(_svc.GetDrives());
    [HttpGet("metrics")]          public ActionResult<MetricsInfo>           GetMetrics()       => _svc.GetMetrics();
    [HttpGet("streams")]          public ActionResult<StreamInfo[]>          GetStreams()        => _svc.GetStreams();
    [HttpGet("status")]           public ActionResult<StatusInfo>            GetStatus()        => _svc.GetStatus();
    [HttpGet("encode_status")]    public ActionResult<EncodeStatusResponse>  GetEncodeStatus()  => _svc.GetEncodeStatus();
    [HttpGet("encode_remaining")] public ActionResult<QueueItem[]>           GetRemaining()     => _svc.GetEncodeRemaining();
    [HttpGet("strip")]            public ActionResult<StripEntry[]>          GetStrip()         => _svc.ParseStripLog();
    [HttpGet("reencode")]         public ActionResult<ReencodeEntry[]>       GetReencode()      => _svc.ParseReencodeLog();
    [HttpGet("dupes")]            public ActionResult<DupesReport>           GetDupes()         => _svc.GetDupesReport();
    [HttpGet("libraries")]        public ActionResult<LibraryInfo[]>         GetLibraries()     => _svc.GetLibraries();
    [HttpGet("languages")]        public ActionResult<LanguageSettings>      GetLanguages()     => _svc.GetLanguageSettings();

    [HttpPost("pause")]  public ActionResult<OkResponse> Pause()  { _svc.Pause();  return new OkResponse(true, "Paused"); }
    [HttpPost("resume")] public ActionResult<OkResponse> Resume() { _svc.Resume(); return new OkResponse(true, "Resuming"); }

    [HttpPost("run")]
    public ActionResult<OkResponse> Run([FromBody] RunRequest req)
    {
        var cfg = Plugin.Instance!.Configuration;
        string? service = req.Script switch {
            "reencode" => cfg.ReencodeServiceName,
            "strip"    => cfg.StripServiceName,
            _          => null
        };
        if (string.IsNullOrWhiteSpace(service))
            return BadRequest(new ErrorResponse("Service not configured — set it in MediaDash Settings."));
        _svc.StartService(service);
        return new OkResponse(true, $"Started {service}");
    }

    [HttpPost("dupes/scan")]
    public ActionResult<OkResponse> ScanDupes()
    {
        if (!_svc.StartDupesScan())
            return NotFound(new ErrorResponse("Dupes scan script not configured."));
        return new OkResponse(true, "Scan started");
    }

    [HttpPost("dupes/delete")]
    public ActionResult<DeleteResponse> DeleteDupes([FromBody] DeleteDupesRequest? req)
        => _svc.DeleteDupes(req?.Imdb);

    [HttpPost("schedule")]
    public ActionResult<ScheduleSaveResponse> SaveSchedule([FromBody] ScheduleRequest req)
        => _svc.SaveSchedule(req.PauseStart, req.PauseEnd);

    [HttpPost("languages")]
    public ActionResult<OkResponse> SaveLanguages([FromBody] LanguageSettings req)
    {
        _svc.SaveLanguageSettings(req);
        return new OkResponse(true, "Language settings saved");
    }

    // ── File explorer ──────────────────────────────────────────────────────

    [HttpGet("fs/list")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<FsListing> FsList([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new ErrorResponse("path is required"));
        try   { return Ok(_fs.List(path)); }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new ErrorResponse(ex.Message)); }
        catch (System.IO.DirectoryNotFoundException ex) { return BadRequest(new ErrorResponse(ex.Message)); }
    }

    [HttpPost("fs/rename")]
    public ActionResult<FsOpResult> FsRename([FromBody] RenameRequest req)
        => _fs.Rename(req.Path, req.NewName);

    [HttpPost("fs/delete")]
    public ActionResult<FsOpResult> FsDelete([FromBody] PathRequest req)
        => _fs.Delete(req.Path);

    [HttpPost("fs/move")]
    public ActionResult<FsOpResult> FsMove([FromBody] MoveRequest req)
        => _fs.Move(req.SourcePath, req.DestDir);

    [HttpPost("fs/copy")]
    public ActionResult<FsOpResult> FsCopy([FromBody] MoveRequest req)
        => _fs.Copy(req.SourcePath, req.DestDir);

    // ── Auto-sort ──────────────────────────────────────────────────────────

    [HttpPost("sort/preview")]
    public async Task<ActionResult<SortPreview>> SortPreview([FromBody] PathRequest req)
    {
        try   { return Ok(await _fs.BuildSortPreview(req.Path)); }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new ErrorResponse(ex.Message)); }
    }

    [HttpPost("sort/execute")]
    public ActionResult<FsOpResult> SortExecute([FromBody] SortExecuteRequest req)
    {
        try   { return Ok(_fs.ExecuteSort(req.LibraryRoot, req.ImdbId)); }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new ErrorResponse(ex.Message)); }
    }

    [HttpPost("sort/scan")]
    public async Task<ActionResult<OkResponse>> SortScan([FromBody] PathRequest req)
    {
        var errors = new System.Collections.Generic.List<string>();
        try   { await _fs.WriteImdbMarkers(req.Path, errors); }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new ErrorResponse(ex.Message)); }
        return new OkResponse(true, errors.Count > 0
            ? $"Done with {errors.Count} warning(s): {string.Join("; ", errors)}"
            : "Scan complete — markers written");
    }
}

// ── Request bodies ────────────────────────────────────────────────────────────

public record RunRequest(string Script);
public record DeleteDupesRequest(string? Imdb);
public record ScheduleRequest(int PauseStart, int PauseEnd);
public record PathRequest(string Path);
public record RenameRequest(string Path, string NewName);
public record MoveRequest(string SourcePath, string DestDir);
public record SortExecuteRequest(string LibraryRoot, string ImdbId);
