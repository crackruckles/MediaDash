namespace Jellyfin.Plugin.MediaDash.Api;

public record DriveStats(string Mount, string Label, double TotalGb, double UsedGb, double FreeGb, double Pct);
public record LibraryInfo(string Name, string Type, string Path);
public record LanguageSettings(string KeepAudio, string KeepSubs, bool AlwaysKeepFirst, bool KeepCommentary, bool Enabled);
public record DiskInfo(double TotalGb, double UsedGb, double FreeGb, double Pct);
public record TemperatureInfo(double? Cpu, double? Gpu, double? Nvme);
public record MetricsInfo(double CpuPct, int[] PerCore, long MemUsedMb, long MemTotalMb, double MemPct,
    int GpuPct, long VramUsedMb, long VramTotalMb, TemperatureInfo Temps, long DiskReadGb, long DiskWriteGb);
public record StreamInfo(string User, string Client, string Device, string Title, string Type, string Series, double ProgressPct);
public record WorkerStatus(bool Active, string? Name, double SourceGb, string? Codec, string? StartedAt,
    int DurationS, double Pct, int ElapsedS, double TmpSizeGb, double EstFinalGb, double EstSavingGb,
    string? Fps, string? Speed, int Worker);
public record EncodeStatusResponse(bool Active, string? Name, double SourceGb, string? Codec, string? StartedAt,
    int DurationS, double Pct, int ElapsedS, double TmpSizeGb, double EstFinalGb, double EstSavingGb,
    string? Fps, string? Speed, WorkerStatus[] AllWorkers);
public record QueueItem(string Name, double SizeGb, string Path);
public record ScheduleInfo(int PauseStart, int PauseEnd);
public record StatusInfo(bool Paused, bool InQuietHours, bool EncodingActive, bool StrippingActive,
    StreamInfo[] JellyfinStreams, int StreamCount, ScheduleInfo Schedule, string Time);
public record StripEntry(string Name, int AudioDropped, int SubsDropped, double SavedMb, string Status);
public record ReencodeEntry(string Name, double BeforeGb, string Codec, double AfterGb, double SavedGb, int ElapsedMin, string Status, bool StreamPaused);
public record DupeFile(string Path, string SizeFmt, long SizeBytes);
public record DupeGroup(string Imdb, string Title, string Type, int FileCount, string WastedFmt, long WastedBytes, DupeFile Keeper, DupeFile[] Duplicates);
public record DupesReport(int TotalGroups, long TotalWasted, string TotalWastedFmt, DupeGroup[] Groups, string? Generated);
public record OkResponse(bool Ok, string? Message = null);
public record ErrorResponse(string Error);
public record DeleteResponse(bool Ok, int Deleted, double FreedGb, string[] Errors);
public record ScheduleSaveResponse(bool Ok, int PauseStart, int PauseEnd);
