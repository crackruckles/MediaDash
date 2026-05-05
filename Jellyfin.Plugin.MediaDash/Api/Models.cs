namespace Jellyfin.Plugin.MediaDash.Api;

// ── Disk ──────────────────────────────────────────────────────────────────────

public record DiskInfo(double TotalGb, double UsedGb, double FreeGb, double Pct);

// ── System metrics ────────────────────────────────────────────────────────────

public record TemperatureInfo(double? Cpu, double? Gpu, double? Nvme);

public record MetricsInfo(
    double CpuPct,
    int[] PerCore,
    long MemUsedMb,
    long MemTotalMb,
    double MemPct,
    int GpuPct,
    long VramUsedMb,
    long VramTotalMb,
    TemperatureInfo Temps,
    long DiskReadGb,
    long DiskWriteGb);

// ── Active Jellyfin streams ───────────────────────────────────────────────────

public record StreamInfo(
    string User,
    string Client,
    string Device,
    string Title,
    string Type,
    string Series,
    double ProgressPct);

// ── Encoder status ────────────────────────────────────────────────────────────

public record WorkerStatus(
    bool Active,
    string? Name,
    double SourceGb,
    string? Codec,
    string? StartedAt,
    int DurationS,
    double Pct,
    int ElapsedS,
    double TmpSizeGb,
    double EstFinalGb,
    double EstSavingGb,
    string? Fps,
    string? Speed,
    int Worker);

public record EncodeStatusResponse(
    bool Active,
    string? Name,
    double SourceGb,
    string? Codec,
    string? StartedAt,
    int DurationS,
    double Pct,
    int ElapsedS,
    double TmpSizeGb,
    double EstFinalGb,
    double EstSavingGb,
    string? Fps,
    string? Speed,
    WorkerStatus[] AllWorkers);

// ── Encode remaining queue ────────────────────────────────────────────────────

public record QueueItem(string Name, double SizeGb, string Path);

// ── Status overview ───────────────────────────────────────────────────────────

public record ScheduleInfo(int PauseStart, int PauseEnd);

public record StatusInfo(
    bool Paused,
    bool InQuietHours,
    bool EncodingActive,
    bool StrippingActive,
    StreamInfo[] JellyfinStreams,
    int StreamCount,
    ScheduleInfo Schedule,
    string Time);

// ── Strip log ─────────────────────────────────────────────────────────────────

public record StripEntry(
    string Name,
    int AudioDropped,
    int SubsDropped,
    double SavedMb,
    string Status);

// ── Reencode log ──────────────────────────────────────────────────────────────

public record ReencodeEntry(
    string Name,
    double BeforeGb,
    string Codec,
    double AfterGb,
    double SavedGb,
    int ElapsedMin,
    string Status,
    bool StreamPaused);

// ── Dupes report ──────────────────────────────────────────────────────────────

public record DupeFile(string Path, string SizeFmt, long SizeBytes);

public record DupeGroup(
    string Imdb,
    string Title,
    string Type,
    int FileCount,
    string WastedFmt,
    long WastedBytes,
    DupeFile Keeper,
    DupeFile[] Duplicates);

public record DupesReport(
    int TotalGroups,
    long TotalWasted,
    string TotalWastedFmt,
    DupeGroup[] Groups,
    string? Generated);

// ── Action responses ─────────────────────────────────────────────────────────

public record OkResponse(bool Ok, string? Message = null);
public record ErrorResponse(string Error);
public record DeleteResponse(bool Ok, int Deleted, double FreedGb, string[] Errors);
public record ScheduleSaveResponse(bool Ok, int PauseStart, int PauseEnd);
