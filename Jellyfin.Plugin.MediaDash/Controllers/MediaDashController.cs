using System.Net.Mime;
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
    private readonly MediaDashService _svc;
    public MediaDashController(MediaDashService svc) => _svc = svc;

    [HttpGet("drives")]      public ActionResult<DriveStats[]>         GetDrives()        => Ok(_svc.GetDrives());
    [HttpGet("metrics")]     public ActionResult<MetricsInfo>          GetMetrics()       => _svc.GetMetrics();
    [HttpGet("streams")]     public ActionResult<StreamInfo[]>         GetStreams()        => _svc.GetStreams();
    [HttpGet("status")]      public ActionResult<StatusInfo>           GetStatus()        => _svc.GetStatus();
    [HttpGet("encode_status")] public ActionResult<EncodeStatusResponse> GetEncodeStatus() => _svc.GetEncodeStatus();
    [HttpGet("encode_remaining")] public ActionResult<QueueItem[]>    GetRemaining()     => _svc.GetEncodeRemaining();
    [HttpGet("strip")]       public ActionResult<StripEntry[]>         GetStrip()         => _svc.ParseStripLog();
    [HttpGet("reencode")]    public ActionResult<ReencodeEntry[]>      GetReencode()      => _svc.ParseReencodeLog();
    [HttpGet("dupes")]       public ActionResult<DupesReport>          GetDupes()         => _svc.GetDupesReport();
    [HttpGet("libraries")]   public ActionResult<LibraryInfo[]>        GetLibraries()     => _svc.GetLibraries();
    [HttpGet("languages")]   public ActionResult<LanguageSettings>     GetLanguages()     => _svc.GetLanguageSettings();

    [HttpPost("pause")]  public ActionResult<OkResponse> Pause()  { _svc.Pause();  return new OkResponse(true, "Paused"); }
    [HttpPost("resume")] public ActionResult<OkResponse> Resume() { _svc.Resume(); return new OkResponse(true, "Resuming"); }

    [HttpPost("run")]
    public ActionResult<OkResponse> Run([FromBody] RunRequest req)
    {
        var cfg = Plugin.Instance!.Configuration;
        // Map generic names to configured service names — no hardcoded script names
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
            return NotFound(new ErrorResponse("Dupes scan script not configured — set DupesScanScript in Settings."));
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
}

public record RunRequest(string Script);
public record DeleteDupesRequest(string? Imdb);
public record ScheduleRequest(int PauseStart, int PauseEnd);
