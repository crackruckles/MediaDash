namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>Disk usage statistics for a single mount point.</summary>
public record DriveStats(
    string Mount,
    string Label,
    double TotalGb,
    double UsedGb,
    double FreeGb,
    double Pct);

/// <summary>A Jellyfin library folder with its type and path.</summary>
public record LibraryInfo(
    string Name,
    string Type,
    string Path);

/// <summary>Track-stripping language preferences.</summary>
public record LanguageSettings(
    string KeepAudio,
    string KeepSubs,
    bool AlwaysKeepFirst,
    bool KeepCommentary,
    bool Enabled);

/// <summary>Disk usage summary (legacy single-drive format).</summary>
public record DiskInfo(double TotalGb, double UsedGb, double FreeGb, double Pct);

/// <summary>Hardware temperatures in degrees Celsius.</summary>
public record TemperatureInfo(
    double? Cpu,
    double? Gpu,
    double? Nvme);

/// <summary>Live system performance metrics.</summary>
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

/// <summary>A single active Jellyfin playback session.</summary>
public record StreamInfo(
    string User,
    string Client,
    string Device,
    string Title,
    string Type,
    string Series,
    double ProgressPct);

/// <summary>Status of a single encode worker slot.</summary>
public record WorkerStatus(
    bool Active, string? Name, double SourceGb, string? Codec, string? StartedAt,
    int DurationS, double Pct, int ElapsedS, double TmpSizeGb, double EstFinalGb,
    double EstSavingGb, string? Fps, string? Speed, int Worker);

/// <summary>Combined encode status across all worker slots.</summary>
public record EncodeStatusResponse(
    bool Active, string? Name, double SourceGb, string? Codec, string? StartedAt,
    int DurationS, double Pct, int ElapsedS, double TmpSizeGb, double EstFinalGb,
    double EstSavingGb, string? Fps, string? Speed, WorkerStatus[] AllWorkers);

/// <summary>A file waiting in the encode queue.</summary>
public record QueueItem(
    string Name,
    double SizeGb,
    string Path);

/// <summary>Quiet-hours schedule.</summary>
public record ScheduleInfo(
    int PauseStart,
    int PauseEnd);

/// <summary>Overall plugin status snapshot.</summary>
public record StatusInfo(
    bool Paused, bool InQuietHours, bool EncodingActive, bool StrippingActive,
    StreamInfo[] JellyfinStreams, int StreamCount, ScheduleInfo Schedule, string Time);

/// <summary>Per-file result from the track-stripping log.</summary>
public record StripEntry(
    string Name, int AudioDropped, int SubsDropped, double SavedMb, string Status);

/// <summary>Per-file result from the re-encode log.</summary>
public record ReencodeEntry(
    string Name, double BeforeGb, string Codec, double AfterGb, double SavedGb,
    int ElapsedMin, string Status, bool StreamPaused);

/// <summary>A single file within a duplicate group.</summary>
public record DupeFile(string Path, string SizeFmt, long SizeBytes);

/// <summary>A group of files that share the same IMDB ID.</summary>
public record DupeGroup(
    string Imdb, string Title, string Type, int FileCount, string WastedFmt,
    long WastedBytes, DupeFile Keeper, DupeFile[] Duplicates);

/// <summary>Full duplicate-detection report.</summary>
public record DupesReport(
    int TotalGroups, long TotalWasted, string TotalWastedFmt,
    DupeGroup[] Groups, string? Generated);

/// <summary>Generic success response.</summary>
public record OkResponse(
    bool Ok,
    string? Message = null);

/// <summary>Generic error response.</summary>
public record ErrorResponse(
    string Error);

/// <summary>Result of a bulk duplicate delete operation.</summary>
public record DeleteResponse(
    bool Ok, int Deleted, double FreedGb, string[] Errors);

/// <summary>Confirmation of a saved quiet-hours schedule.</summary>
public record ScheduleSaveResponse(bool Ok, int PauseStart, int PauseEnd);
