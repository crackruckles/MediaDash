using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Jellyfin.Plugin.MediaDash.Analytics;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Compat;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Probing;
using Jellyfin.Plugin.MediaDash.Scanners;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// These assertions describe the repairs we need, including defects that are still open.
[Collection("MediaDashDb")]
public sealed class ReportedIssueRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-reported-" + Guid.NewGuid().ToString("N"));
    private readonly Plugin? _previousPlugin = Plugin.Instance;
    private readonly RunState? _previousState = Plugin.AttachedRunState;
    private readonly FixRunSummary? _previousSummary = Plugin.LastFixRun;
    private readonly PluginConfiguration _config = new() { DryRun = true, PauseDuringPlayback = false, AnalyticsEnabled = false };
    private readonly Guid _libraryId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly MediaDashDb _db;
    private readonly List<BaseItem> _moveOwners = [];

    public ReportedIssueRegressionTests()
    {
        Directory.CreateDirectory(Media);
        Directory.CreateDirectory(Metadata);
        _db = new MediaDashDb(Path.Combine(_root, "test.db"));
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, _config);
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, plugin);
    }

    private string Media => Path.Combine(_root, "media");
    private string Metadata => Path.Combine(_root, "metadata");

    [Fact]
    public void Issue81_LegacyXmlMigrationPreservesConsentAndUnrelatedSettings()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var legacy = new PluginConfiguration
        {
            DryRun = false, EnabledLibraries = ["selected-library"], AudioDisposal = DisposalMethod.Permanent,
            RecycleBinPauseFixesAtGb = 0, AllowedAudioLanguages = ["spa", "orig"], FixerDisabled = ["CorruptArtwork"]
        };
        foreach (var property in typeof(PluginConfiguration).GetProperties().Where(p => p.PropertyType == typeof(FixMode)))
        {
            property.SetValue(legacy, FixMode.ManualApprove);
        }
        legacy.PlayabilityFixMode = FixMode.Off;
        legacy.TranscodeFixMode = FixMode.Automatic;
        using var xml = new StringWriter();
        serializer.Serialize(xml, legacy);
        // Build legacy XML independently: the current serializer must not define which old values exist.
        using var input = new StringReader(xml.ToString().Replace(">ManualApprove<", ">DetectOnly<", StringComparison.Ordinal));
        object? loadedObject = null;
        var loadError = Record.Exception(() => loadedObject = serializer.Deserialize(input));
        Assert.Null(loadError);
        var loaded = Assert.IsType<PluginConfiguration>(loadedObject);
        Assert.True(loaded.NormaliseFixModes());
        using var saved = new StringWriter();
        serializer.Serialize(saved, loaded);
        using var restart = new StringReader(saved.ToString());
        var reloaded = (PluginConfiguration)serializer.Deserialize(restart)!;
        Assert.False(reloaded.NormaliseFixModes());
        Assert.Equal(FixMode.ManualApprove, reloaded.AudioFixMode);
        Assert.Equal(FixMode.Off, reloaded.PlayabilityFixMode);
        Assert.Equal(FixMode.Automatic, reloaded.TranscodeFixMode);
        Assert.False(reloaded.DryRun);
        Assert.Equal(new[] { "selected-library" }, reloaded.EnabledLibraries);
        Assert.Equal(new[] { "spa", "orig" }, reloaded.AllowedAudioLanguages);
        Assert.Equal(new[] { "CorruptArtwork" }, reloaded.FixerDisabled);
        Assert.Equal(DisposalMethod.Permanent, reloaded.AudioDisposal);
        Assert.Equal(0, reloaded.RecycleBinPauseFixesAtGb);
        Assert.False(FixPolicyEvaluator.Evaluate(reloaded, IssueType.AudioLanguage, IssueStatus.Detected,
            FixIntent.Scheduled, true, false, false).Allowed);
        Assert.True(FixPolicyEvaluator.Evaluate(reloaded, IssueType.AudioLanguage, IssueStatus.Queued,
            FixIntent.Approved, true, false, false).Allowed);
    }

    [Theory]
    [InlineData(IssueType.OrphanedDebris, false)]
    [InlineData(IssueType.OrphanedDebris, true)]
    [InlineData(IssueType.CorruptArtwork, false)]
    [InlineData(IssueType.CorruptArtwork, true)]
    public async Task Issue86_ApprovedManagedMetadataReachesItsSpecializedFixer(IssueType type, bool selectedLibrary)
    {
        var library = Library();
        var app = Proxy<IServerApplicationPaths>((method, _) => method.Name switch
        {
            "get_InternalMetadataPath" => Metadata,
            "get_DataPath" => Path.Combine(_root, "data"),
            _ => null
        });
        var guard = new LibraryGuard(library, app);
        var orphanId = Guid.NewGuid();
        var path = type == IssueType.CorruptArtwork ? Path.Combine(Metadata, "poster.png")
            : Path.Combine(Metadata, "library", orphanId.ToString("N")[..2], orphanId.ToString("N"));
        if (type == IssueType.CorruptArtwork) File.WriteAllBytes(path, []);
        else Directory.CreateDirectory(path);
        _config.EnabledLibraries = selectedLibrary ? [_libraryId.ToString("N")] : [];
        var issue = Queue(type, path, type == IssueType.CorruptArtwork ? _ownerId : orphanId, "{\"kind\":\"OrphanMetadata\"}");
        IFixer fixer = type == IssueType.CorruptArtwork
            ? new ArtworkFixer(app, Proxy<ILibraryMonitor>((_, _) => null), library, NullLogger<ArtworkFixer>.Instance)
            : new OrphanCleanupFixer(guard, null!, NullLogger<OrphanCleanupFixer>.Instance);
        await RunMetadataTask(library, fixer);
        Assert.Equal(1, Plugin.LastFixRun!.Attempted);
        Assert.Equal(0, Plugin.LastFixRun.SkippedLibraryDisabled);
        Assert.True(Assert.Single(_db.History.GetHistory()).WasDryRun);
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(issue.Id)!.Status);
        Assert.True(File.Exists(path) || Directory.Exists(path));
        Assert.False(guard.IsInsideLibrary(path));
    }

    [Fact]
    public async Task Issue86_ManagedArtworkStillRespectsADisabledOwnerLibrary()
    {
        _config.EnabledLibraries = [Guid.NewGuid().ToString("N")];
        var path = Path.Combine(Metadata, "poster.png");
        File.WriteAllBytes(path, []);
        var issue = Queue(IssueType.CorruptArtwork, path, _ownerId, "{}");
        var library = Library();
        var app = Proxy<IServerApplicationPaths>((method, _) => method.Name == "get_InternalMetadataPath" ? Metadata : null);
        await RunMetadataTask(library, new ArtworkFixer(app, Proxy<ILibraryMonitor>((_, _) => null), library, NullLogger<ArtworkFixer>.Instance));
        Assert.Equal(0, Plugin.LastFixRun!.Attempted);
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(issue.Id)!.Status);
        Assert.Empty(_db.History.GetHistory());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Issue86_AnOrphanKindTagCannotAuthorizeAnOutsideMetadataDirectory()
    {
        _config.DryRun = false;
        var path = Path.Combine(_root, "metadata-evil", "library", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var protectedBytes = Path.Combine(path, "poster.png");
        File.WriteAllText(protectedBytes, "owned bytes must survive");
        var library = Library();
        var fixer = new OrphanCleanupFixer(new LibraryGuard(library), null!, NullLogger<OrphanCleanupFixer>.Instance);
        var result = await fixer.FixAsync(new Issue { Type = IssueType.OrphanedDebris, Path = path,
            ItemId = Guid.NewGuid(), DetailsJson = "{\"kind\":\"OrphanMetadata\"}" }, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(FixReason.OutsideLibrary, result.Reason);
        Assert.Equal("owned bytes must survive", File.ReadAllText(protectedBytes));
    }

    [Fact]
    public async Task Issue7_PairedSubtitleDirectorySurvivesScanAndStaleApproval()
    {
        var season = Path.Combine(Media, "Season 01");
        var subtitles = Directory.CreateDirectory(Path.Combine(season, "Subtitles")).FullName;
        var debris = Directory.CreateDirectory(Path.Combine(season, "Debris")).FullName;
        File.WriteAllText(Path.Combine(season, "Show S01E01.avi"), "episode");
        var sidecar = Path.Combine(subtitles, "Show S01E01.en.sdh.srt");
        File.WriteAllText(sidecar, "subtitle");

        var issues = new List<Issue>();
        OrphanCleanupScanner.DetectEmptyFolders([Media], issues, CancellationToken.None);
        var fixer = new OrphanCleanupFixer(new LibraryGuard(Library()), null!, NullLogger<OrphanCleanupFixer>.Instance);
        var result = await fixer.FixAsync(new Issue
        {
            Type = IssueType.OrphanedDebris,
            Path = subtitles,
            DetailsJson = "{\"kind\":\"EmptyFolder\"}"
        }, null, CancellationToken.None);

        Assert.Equal(FixOutcome.NoChange, result.Outcome);
        Assert.Equal(debris, Assert.Single(issues).Path);
        Assert.Equal("subtitle", File.ReadAllText(sidecar));
    }

    [Fact]
    public void Issue87_OriginalOnlyKeepsSpanishAndRemovesEnglishWhenMetadataSaysSpanish()
    {
        var source = new FfprobeData { Streams = [Audio(1, "eng"), Audio(2, "spa")] };
        var config = new PluginConfiguration { AllowedAudioLanguages = ["orig"] };
        Assert.Equal(new[] { 1 }, TrackFixer.ComputeRemovableIndexes(source, IssueType.AudioLanguage, config, new SpanishVideo()));
        Assert.Empty(TrackFixer.ComputeRemovableIndexes(source, IssueType.AudioLanguage, config, new Video()));
        Assert.True(TrackFixer.RetainedTracksMatch(source, new FfprobeData { Streams = [Audio(0, "spa")] }, [1]));
        Assert.False(TrackFixer.RetainedTracksMatch(source, new FfprobeData { Streams = [Audio(0, "eng")] }, [1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Issue88_NativeDecoderFailureIsNotEvidenceOfCorruptArtwork(bool wrongArchitecture)
    {
        var path = Path.Combine(Metadata, "healthy.png");
        File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII="));
        var bridge = SkiaSharpBridge.Instance;
        var field = typeof(SkiaSharpBridge).GetField("_decodeStream", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previous = field.GetValue(bridge);
        field.SetValue(bridge, typeof(ReportedIssueRegressionTests).GetMethod(wrongArchitecture ? nameof(WrongNativeDecoder) : nameof(MissingNativeDecoder)));
        try
        {
            Assert.Null(ArtworkScanner.EvaluateFile(path, null));
            Assert.True(File.Exists(path));
        }
        finally { field.SetValue(bridge, previous); }
    }

    [Fact]
    public void Issue88_DecoderInspectionFailureIsNotCorruption()
    {
        var path = Path.Combine(Metadata, "healthy.png");
        File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII="));
        var bridge = SkiaSharpBridge.Instance;
        var field = typeof(SkiaSharpBridge).GetField("_widthProperty", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previous = field.GetValue(bridge);
        field.SetValue(bridge, typeof(string).GetProperty("Length"));
        try
        {
            Assert.Null(ArtworkScanner.EvaluateFile(path, null));
            Assert.True(File.Exists(path));
        }
        finally { field.SetValue(bridge, previous); }
    }

    [Fact]
    public async Task Issue88_OwnerLookupFailurePreservesArtworkAndReportsFailure()
    {
        _config.DryRun = false;
        var path = Path.Combine(Metadata, "broken.png");
        File.WriteAllBytes(path, []);
        var app = Proxy<IServerApplicationPaths>((method, _) => method.Name == "get_InternalMetadataPath" ? Metadata : null);
        var library = Proxy<ILibraryManager>((method, _) => method.Name == "GetItemById" ? throw new IOException("owned lookup failure") : null);
        var fixer = new ArtworkFixer(app, Proxy<ILibraryMonitor>((_, _) => null), library, NullLogger<ArtworkFixer>.Instance);
        var result = await fixer.FixAsync(new Issue { Type = IssueType.CorruptArtwork, Path = path, ItemId = _ownerId }, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Issue92_OutputHeaderFailureMustNotBeClassifiedAsSourceCorruption()
    {
        Assert.False(FfmpegExecutor.IsCorruptSourceError("[out#0/matroska @ 0x1] Could not write header (incorrect codec parameters ?): Invalid data found when processing input"));
        Assert.True(FfmpegExecutor.IsCorruptSourceError("[matroska,webm @ 0x1] EBML header parsing failed"));
    }

    [Fact]
    public async Task Issue43_CompanionCollisionMustNotReportACompletedMove()
    {
        _config.DryRun = false;
        var source = Path.Combine(Media, "Movie.mkv");
        var target = Path.Combine(Media, "Movie", "Movie.mkv");
        File.WriteAllText(source, "owned video");
        File.WriteAllText(Path.Combine(Media, "Movie.en.srt"), "owned subtitle");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(Path.Combine(Media, "Movie", "Movie.en.srt"), "existing subtitle");
        var issue = MediaMoveTestApproval.Create(_db, _config, _moveOwners, IssueType.Ungrouped, source, target);
        var result = await Grouper(Library()).FixAsync(issue, null, CancellationToken.None);
        Assert.NotEqual(FixOutcome.Applied, result.Outcome);
        Assert.Equal(FixReason.DestinationCollision, result.Reason);
        Assert.Equal("owned subtitle", File.ReadAllText(Path.Combine(Media, "Movie.en.srt")));
        Assert.Equal("existing subtitle", File.ReadAllText(Path.Combine(Media, "Movie", "Movie.en.srt")));
        Assert.True(File.Exists(source) || File.Exists(target));
    }

    [Fact]
    public async Task Reddit_RefusedMoveStaysVisibleAndDoesNotClaimACompletedFix()
    {
        _config.DryRun = false;
        var source = Path.Combine(Media, "Movie.mkv");
        var target = Path.Combine(Media, "Movie", "Movie.mkv");
        File.WriteAllText(source, "owned source");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "occupied destination");
        var issue = MediaMoveTestApproval.Create(_db, _config, _moveOwners, IssueType.Ungrouped, source, target);
        var library = Library();
        var plan = new PlannedFix(PlannedFixKind.Single, issue, []);
        var execution = await Executor(library, Grouper(library)).ExecuteAsync(plan, true, new Progress<double>(), CancellationToken.None);
        Assert.NotNull(execution);
        Assert.False(execution.Result.Success);
        new FixResultRecorder(_db, NullLogger<FixResultRecorder>.Instance).Record(plan, execution, DateTime.UtcNow);
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(issue.Id)!.Status);
        Assert.Equal(0, Assert.Single(_db.History.GetHistory()).BytesFreed);
        Assert.Equal("owned source", File.ReadAllText(source));
        Assert.Equal("occupied destination", File.ReadAllText(target));
    }

    [Fact]
    public void Issue81_ProgressPayloadDoesNotRequireDatabaseDriveOrLibraryReads()
    {
        var tasks = Proxy<ITaskManager>((method, _) => method.Name == "get_ScheduledTasks" ? Array.Empty<IScheduledTaskWorker>() : throw new InvalidOperationException(method.Name));
        var state = new RunState();
        state.SetPauseReason(RunRole.Fix, "owned explanation");
        var service = new StatusQueryService(null!, null!, null!, null!, null!, tasks, state);
        var progress = service.BuildProgress();
        Assert.Equal("owned explanation", progress.FixPauseReason);
        Assert.False(progress.IsScanning);
        Assert.False(progress.IsFixing);
        Assert.True(JsonSerializer.SerializeToElement(progress).TryGetProperty("System", out var system));
        Assert.True(system.GetProperty("RamUsedBytes").GetInt64() > 0);
    }

    [Fact]
    public void HiddenPerformancePanelDoesNotSampleResourcesOnProgressPolls()
    {
        _config.ShowSystemPerformance = false;
        var tasks = Proxy<ITaskManager>((method, _) => method.Name == "get_ScheduledTasks" ? Array.Empty<IScheduledTaskWorker>() : throw new InvalidOperationException(method.Name));
        var service = new StatusQueryService(null!, null!, null!, null!, null!, tasks, new RunState());
        var system = Assert.IsType<SystemStats>(service.BuildProgress().System);
        Assert.False(system.SystemStatsAvailable);
        Assert.Equal(0, system.RamUsedBytes);
    }

    [Fact]
    public void ScanDiagnosticsCaptureStageRecordsEvenWhenInformationLoggingIsDisabled()
    {
        _config.DiagnosticsEnabled = true;
        using (var capture = RunDiagnosticsRecording.Start(_db, NullLogger.Instance, true)!)
        {
            using var stage = RunDiagnostics.Start(NullLogger.Instance, "scan", "TestScanner");
            Assert.NotNull(stage);
            stage.Finish("Completed", 5, 2, null, null);
            capture.Complete("Completed");
        }

        var records = RunDiagnosticsRecording.Read(_db)!.Value.GetProperty("Records");
        Assert.Equal(2, records.GetArrayLength());
        Assert.Equal("start", records[0].GetProperty("Phase").GetString());
        Assert.Equal(2, records[1].GetProperty("Findings").GetInt32());
        Assert.True(records[1].TryGetProperty("JellyfinCpuPeakPercent", out _));
        Assert.True(records[1].TryGetProperty("ActiveOperationNames", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixDiagnosticsPersistOnlyWhenEnabledWithoutReplacingTheScan(bool enabled)
    {
        _config.DiagnosticsEnabled = enabled;
        _db.SetState("LastScanDiagnostics", "{\"Outcome\":\"Previous scan\"}");
        var library = Library();
        var orphanId = Guid.NewGuid();
        var path = Path.Combine(Metadata, "library", orphanId.ToString("N")[..2], orphanId.ToString("N"));
        Directory.CreateDirectory(path);
        Queue(IssueType.OrphanedDebris, path, orphanId, "{\"kind\":\"OrphanMetadata\"}");
        await RunMetadataTask(library, new OrphanCleanupFixer(new LibraryGuard(library), null!, NullLogger<OrphanCleanupFixer>.Instance));
        Assert.Equal(1, Plugin.LastFixRun!.Attempted);
        Assert.Equal("{\"Outcome\":\"Previous scan\"}", _db.GetState("LastScanDiagnostics"));
        var stored = _db.GetState("LastFixDiagnostics");
        if (!enabled)
        {
            Assert.Null(stored);
            return;
        }

        var fix = JsonSerializer.Deserialize<JsonElement>(Assert.IsType<string>(stored));
        Assert.Equal("Completed", fix.GetProperty("Outcome").GetString());
        var finished = fix.GetProperty("Records").EnumerateArray().Single(record =>
            record.GetProperty("Component").GetString() == "OrphanCleanupFixer" && record.GetProperty("Phase").GetString() == "finish");
        Assert.Equal("fix", finished.GetProperty("Kind").GetString());
        Assert.Equal(1, finished.GetProperty("FixTargets").GetInt32());
        Assert.True(finished.TryGetProperty("HostOrContainerCpuPeakPercent", out _));
    }

    [Fact]
    public async Task CancelledFixRunRetainsItsDiagnostics()
    {
        _config.DiagnosticsEnabled = true;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunMetadataTask(Library(), null!, cancellation.Token));
        var fix = JsonSerializer.Deserialize<JsonElement>(_db.GetState("LastFixDiagnostics")!);
        Assert.Equal("Cancelled", fix.GetProperty("Outcome").GetString());
        Assert.NotEqual(JsonValueKind.Null, fix.GetProperty("FinishedAtUtc").ValueKind);
    }

    [Fact]
    public void ScanDiagnosticsEndpointRejectsDisabledModeAndReturnsTheStoredScanWhenEnabled()
    {
        var controller = (MediaDashController)RuntimeHelpers.GetUninitializedObject(typeof(MediaDashController));
        typeof(MediaDashController).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, _db);
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(controller.GetLastScanDiagnostics());
        _config.DiagnosticsEnabled = true;
        using (var capture = RunDiagnosticsRecording.Start(_db, NullLogger.Instance, true)!)
        {
            capture.Complete("Completed");
        }

        _db.SetState("LastFixDiagnostics", "{\"Outcome\":\"Cancelled\"}");

        var result = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(controller.GetLastScanDiagnostics());
        Assert.Equal("Completed", JsonSerializer.SerializeToElement(result.Value).GetProperty("Scan").GetProperty("Outcome").GetString());
        Assert.Equal("Cancelled", JsonSerializer.SerializeToElement(result.Value).GetProperty("Fix").GetProperty("Outcome").GetString());
        _config.DiagnosticsEnabled = false;
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(controller.GetLastScanDiagnostics());
    }

    [Fact]
    public void ScannerDiagnosticsRetainActualCombinedThroughputPeaksInsteadOfAverages()
    {
        _config.DiagnosticsEnabled = true;
        var time = new RunDiagnosticsTests.TestClock();
        using (var capture = RunDiagnosticsRecording.Start(_db, NullLogger.Instance, true)!)
        using (var stage = new RunDiagnostics(NullLogger.Instance, "scan", "TestScanner", time, capture))
        {
            stage.RecordSample(new RunDiagnostics.Sample(time.GetTimestamp(), (10, 20), (100, 200)), new SystemStats());
            time.Advance(TimeSpan.FromSeconds(2));
            stage.RecordSample(new RunDiagnostics.Sample(time.GetTimestamp(), (70, 40), (120, 300)),
                new SystemStats { SystemCpuPercent = 60, SystemRamUsedBytes = 900, GpuPercent = 30 });
            time.Advance(TimeSpan.FromSeconds(2));
            stage.RecordSample(new RunDiagnostics.Sample(time.GetTimestamp(), (80, 50), (200, 320)),
                new SystemStats { SystemCpuPercent = 20, SystemRamUsedBytes = 100, GpuPercent = 5 });
            time.Advance(TimeSpan.FromSeconds(2));
            stage.RecordSample(new RunDiagnostics.Sample(time.GetTimestamp(), (1, 1000000), (1, 1000000)), new SystemStats());
            stage.Finish("Completed", 10, 7, null, null);
            capture.Complete("Completed");
        }

        var record = RunDiagnosticsRecording.Read(_db)!.Value.GetProperty("Records")[0];
        Assert.Equal(40, record.GetProperty("HostDiskPeakBytesPerSecond").GetDouble());
        Assert.Equal(60, record.GetProperty("NetworkInterfacePeakBytesPerSecond").GetDouble());
        Assert.Equal(60, record.GetProperty("HostOrContainerCpuPeakPercent").GetDouble());
        Assert.Equal(900, record.GetProperty("HostOrContainerMemoryPeakBytes").GetInt64());
        Assert.Equal(30, record.GetProperty("HostGpuPeakPercent").GetDouble());
    }

    private ILibraryManager Library() => Proxy<ILibraryManager>((method, args) => method.Name switch
    {
        "GetVirtualFolders" => new List<VirtualFolderInfo> { new() { Name = "Owned", ItemId = _libraryId.ToString("N"), Locations = [Media] } },
        "GetItemList" => args![0] is InternalItemsQuery { ItemIds.Length: > 0 }
            ? Array.Empty<BaseItem>() : new BaseItem[] { new CollectionFolder { Id = _libraryId, Name = "Owned", Path = Media } },
        "GetItemById" => args![0] is Guid moveId && _moveOwners.Find(item => item.Id == moveId) is { } moveOwner ? moveOwner : args![0] is Guid id && id == _ownerId ? new Video
        {
            Id = _ownerId, ParentId = _libraryId, Path = Path.Combine(Media, "Movie.mkv"),
            ImageInfos = [new() { Path = Path.Combine(Metadata, "poster.png"), Type = ImageType.Primary }]
        } : args![0] is Guid parentId && parentId == _libraryId ? new CollectionFolder { Id = _libraryId, Name = "Owned", Path = Media } : null,
        _ => null
    });

    private MediaGrouperFixer Grouper(ILibraryManager library) => new(new MediaMoveService(_db, new LibraryGuard(library),
        new ProtectionService(_db.Protection, library), library, NullLogger<MediaMoveService>.Instance));

    private async Task RunMetadataTask(ILibraryManager library, IFixer fixer, CancellationToken cancellationToken = default)
    {
        var state = new RunState();
        var task = new FixTask(_db.Issues, null!, null!, null!,
            new AnalyticsReporter(_db.History, _db.Issues, null!, NullLogger<AnalyticsReporter>.Instance),
            new LibraryGuard(library, Proxy<IServerApplicationPaths>((method, _) => method.Name == "get_InternalMetadataPath" ? Metadata : null)), state, new PluginConfigurationProvider(() => _config), Executor(library, fixer),
            new FixResultRecorder(_db, NullLogger<FixResultRecorder>.Instance), NullLogger<FixTask>.Instance, _db);
        state.ArmBypassIdleCheck(RunRole.Fix);
        await task.ExecuteAsync(new Progress<double>(), cancellationToken);
    }

    private FixExecutor Executor(ILibraryManager library, IFixer fixer) => new(_db.Issues, [fixer], new PluginConfigurationProvider(() => _config),
        TimeProvider.System, new ProtectionService(_db.Protection, library), NullLogger<FixExecutor>.Instance);

    private Issue Queue(IssueType type, string path, Guid itemId, string details)
    {
        _db.Issues.ReplaceDetectedIssues(type, [new Issue { Type = type, Path = path, ItemId = itemId, DetailsJson = details, DetectedAtUtc = DateTime.UtcNow }]);
        var issue = Assert.Single(_db.Issues.GetIssues(type));
        _db.Issues.TryTransitionIssueStatus(issue.Id, [IssueStatus.Detected], IssueStatus.Queued);
        return _db.Issues.GetIssue(issue.Id)!;
    }

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> call) where T : class
    {
        var proxy = DispatchProxy.Create<T, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)proxy).Call = call;
        return proxy;
    }

    private static FfprobeStreamInfo Audio(int index, string language) => new()
    {
        Index = index, CodecType = "audio", CodecName = "aac", Tags = new Dictionary<string, string> { ["language"] = language }
    };

    public static object MissingNativeDecoder(Stream stream) => throw new DllNotFoundException("owned missing native decoder");
    public static object WrongNativeDecoder(Stream stream) => throw new BadImageFormatException("owned incompatible native decoder");
    public sealed class SpanishVideo : Video { public string GetInheritedOriginalLanguage() => "es"; }

    public void Dispose()
    {
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, _previousPlugin);
        Plugin.AttachedRunState = _previousState;
        Plugin.LastFixRunBacking = _previousSummary;
        Diagnostics.RemoveMatching("FixTask.NoRunnable", "No fixes ran");
        Diagnostics.RemoveMatching("MediaGrouperFixer.SidecarCollision", "Movie.en.srt");
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}
