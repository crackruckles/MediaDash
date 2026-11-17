using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Probing;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("Diagnostics")]
public sealed class SecondPassSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mediadash-sidecar-safety-" + Guid.NewGuid().ToString("N"));
    private readonly Plugin? _previous = Plugin.Instance;
    private readonly PluginConfiguration _config = new() { DryRun = false, DuplicateFixMode = FixMode.ManualApprove, AudioFixMode = FixMode.Automatic, SubtitleFixMode = FixMode.ManualApprove };
    private readonly MediaDashDb _db;
    private readonly ILibraryManager _manager;
    private readonly ProtectionService _protection;

    public SecondPassSafetyTests()
    {
        Directory.CreateDirectory(_root);
        _db = new MediaDashDb(Path.Combine(_root, "test.db"));
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, _config);
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, plugin);
        _manager = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)_manager).Call = (method, _) => method.Name == "GetVirtualFolders"
            ? new List<VirtualFolderInfo> { new() { Locations = [_root] } } : null;
        _protection = new ProtectionService(_db.Protection, _manager);
    }

    public void Dispose()
    {
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, _previous);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }

    [Theory]
    [InlineData(DisposalMethod.Permanent)]
    [InlineData(DisposalMethod.RecycleBin)]
    public async Task DuplicateSweep_PreservesProtectedSidecarAndFolder(DisposalMethod disposal)
    {
        _config.DuplicateDisposal = disposal;
        var folder = Directory.CreateDirectory(Path.Combine(_root, "loser")).FullName;
        var loser = Path.Combine(folder, "movie.mkv");
        var keeper = Path.Combine(_root, "keeper.mkv");
        var protectedSidecar = Path.Combine(folder, "movie.nfo");
        var ordinarySidecar = Path.Combine(folder, "movie.xml");
        File.WriteAllText(loser, "duplicate");
        File.WriteAllText(keeper, "keeper");
        File.WriteAllText(protectedSidecar, "protected metadata");
        File.WriteAllText(ordinarySidecar, "title metadata");
        _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = protectedSidecar, ScannersMask = "*", FixersMask = "*" });
        var paths = DispatchProxy.Create<IApplicationPaths, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)paths).Call = (_, _) => _root;
        var bin = new RecycleBin(paths, NullLogger<RecycleBin>.Instance, _db.History);
        var monitor = DispatchProxy.Create<ILibraryMonitor, OriginalLanguageFlowTests.CallProxy>();
        var fixer = new DuplicateFixer(new LibraryGuard(_manager), bin, monitor, _manager, NullLogger<DuplicateFixer>.Instance, _protection);
        Assert.True(PhysicalFileIdentity.TryRead(keeper, out var keeperIdentity));
        Assert.True(PhysicalFileIdentity.TryRead(loser, out var loserIdentity));
        var details = JsonSerializer.Serialize(new
        {
            keeperPath = keeper,
            keeperIdentity = new { vol = keeperIdentity.Volume.ToString(System.Globalization.CultureInfo.InvariantCulture), id = keeperIdentity.FileId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            loserIdentity = new { vol = loserIdentity.Volume.ToString(System.Globalization.CultureInfo.InvariantCulture), id = loserIdentity.FileId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        });
        var result = await fixer.FixAsync(new Issue { Type = IssueType.Duplicate, Path = loser, DetailsJson = details }, null, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(loser));
        Assert.False(File.Exists(ordinarySidecar));
        Assert.Equal("keeper", File.ReadAllText(keeper));
        Assert.Equal("protected metadata", File.ReadAllText(protectedSidecar));
        Assert.True(Directory.Exists(folder));
        if (disposal == DisposalMethod.RecycleBin)
        {
            Assert.True(File.Exists(result.RecyclePath));
            Assert.Single(result.AdditionalRecycled!);
            Assert.Equal(ordinarySidecar, result.AdditionalRecycled![0].OriginalPath);
        }
    }

    [Theory]
    [InlineData(FixMode.Off)]
    public void CombinedPolicy_RechecksSecondaryMode(FixMode mode)
    {
        var fixer = NewTrackFixer();
        var audio = new Issue { Type = IssueType.AudioLanguage, Path = Path.Combine(_root, "movie.mkv") };
        var subtitle = new Issue { Type = IssueType.SubtitleLanguage, Path = audio.Path };
        Assert.True(fixer.EvaluateCombinedPolicy(audio, subtitle, _config, () => true).Allowed);
        _config.SubtitleFixMode = mode;
        Assert.Equal(FixReason.TypeDisabled, fixer.EvaluateCombinedPolicy(audio, subtitle, _config, () => true).Reason);
    }

    [Fact]
    public void CombinedPolicy_RechecksApprovalDisableListAndSecondaryProtection()
    {
        var fixer = NewTrackFixer();
        var audio = new Issue { Type = IssueType.AudioLanguage, Path = Path.Combine(_root, "movie.mkv") };
        var subtitle = new Issue { Type = IssueType.SubtitleLanguage, Path = audio.Path };
        Assert.Equal(FixReason.ConsentChanged, fixer.EvaluateCombinedPolicy(audio, subtitle, _config, () => false).Reason);
        _config.FixerDisabled = ["SubtitleLanguage"];
        Assert.Equal(FixReason.TypeDisabled, fixer.EvaluateCombinedPolicy(audio, subtitle, _config, () => true).Reason);
        _config.FixerDisabled = [];
        _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = audio.Path, ScannersMask = "*", FixersMask = "SubtitleLanguage" });
        Assert.Equal(FixReason.Protected, fixer.EvaluateCombinedPolicy(audio, subtitle, _config, () => true).Reason);
        Assert.False(_protection.IsFixerBlocked(audio.Path, IssueType.AudioLanguage));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("N/A")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("0")]
    public async Task InterleaveCheck_UnknownDurationIsIndeterminate(string? duration)
    {
        var path = Path.Combine(_root, "movie.mkv");
        File.WriteAllText(path, "owned fixture");
        var service = new FfprobeService(null!, _db.Probes, NullLogger<FfprobeService>.Instance);
        var probe = new FfprobeData { Format = new FfprobeFormat { Duration = duration }, Streams = [new() { Index = 0, CodecType = "video" }, new() { Index = 1, CodecType = "audio" }] };
        Assert.Equal(InterleaveStatus.Indeterminate, (await service.InterleaveCheckAsync(path, probe, CancellationToken.None, false)).Status);
    }

    private TrackFixer NewTrackFixer() => new(null!, null!, null!, null!, null!, null!, new PluginConfigurationProvider(() => _config), null!, NullLogger<TrackFixer>.Instance, _protection);

    [Theory]
    [InlineData("off")]
    [InlineData("disabled")]
    [InlineData("protected")]
    public async Task Executor_UnauthorizedPartnerFallsBackToAudioOnly(string policy)
    {
        var path = Path.Combine(_root, "missing.mkv");
        foreach (var type in new[] { IssueType.AudioLanguage, IssueType.SubtitleLanguage })
        {
            _db.ReplaceDetectedIssues(type, [new Issue { Type = type, Path = path, Status = IssueStatus.Detected, DetectedAtUtc = DateTime.UtcNow }]);
            var id = _db.GetIssues(type).Single().Id;
            _db.TryTransitionIssueStatus(id, [IssueStatus.Detected], IssueStatus.Queued);
        }

        var audio = _db.GetIssues(IssueType.AudioLanguage).Single();
        var subtitle = _db.GetIssues(IssueType.SubtitleLanguage).Single();
        if (policy == "off") _config.SubtitleFixMode = FixMode.Off;
        if (policy == "disabled") _config.FixerDisabled = ["SubtitleLanguage"];
        if (policy == "protected") _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = path, ScannersMask = "*", FixersMask = "SubtitleLanguage" });
        var executor = new FixExecutor(_db.Issues, [NewTrackFixer()], new PluginConfigurationProvider(() => _config), TimeProvider.System, _protection, NullLogger<FixExecutor>.Instance);
        var result = await executor.ExecuteAsync(new PlannedFix(PlannedFixKind.TrackPair, audio, [subtitle]), true, new Progress<double>(), CancellationToken.None);
        Assert.NotNull(result);
        // Combined dispatch would refuse TypeDisabled/Protected before inspecting the source.
        Assert.Equal(FixReason.SourceMissing, result.Result.Reason);
        Assert.DoesNotContain(subtitle.Id, result.ResolvedIssueIds);
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssueStatus(subtitle.Id));
    }
}
