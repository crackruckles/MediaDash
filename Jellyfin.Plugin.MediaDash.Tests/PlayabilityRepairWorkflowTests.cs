using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Probing;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("Diagnostics")]
public sealed class PlayabilityRepairWorkflowTests : IDisposable
{
    private readonly Plugin? _previous = Plugin.Instance;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-repair-flow-" + Guid.NewGuid().ToString("N"));
    private readonly PluginConfiguration _config = new()
    {
        DryRun = false, PlayabilityFixMode = FixMode.ManualApprove,
        RepairAttemptRemux = false, RepairAttemptDropStreams = false, RepairAttemptContainerCoerce = false,
        RepairAttemptAudioReencode = false, RepairAttemptReencode = true
    };
    private readonly MediaDashDb _db;
    private readonly PlayabilityFixer _fixer;
    private readonly string _path;

    public PlayabilityRepairWorkflowTests()
    {
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "movie.mkv");
        File.WriteAllBytes(_path, new byte[] { 1, 2, 3 });
        _db = new MediaDashDb(Path.Combine(_root, "test.db"));
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, _config);
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, plugin);
        _config.RecycleBinPath = Path.Combine(_root, "bin");
        var manager = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)manager).Call = (method, _) => method.Name == "GetVirtualFolders"
            ? new List<VirtualFolderInfo> { new() { Locations = [_root] } } : null;
        var monitor = DispatchProxy.Create<ILibraryMonitor, OriginalLanguageFlowTests.CallProxy>();
        var paths = DispatchProxy.Create<IApplicationPaths, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)paths).Call = (_, _) => _root;
        var encoder = DispatchProxy.Create<IMediaEncoder, OriginalLanguageFlowTests.CallProxy>();
        var probe = new FfprobeService(encoder, _db.Probes, NullLogger<FfprobeService>.Instance);
        var bin = new RecycleBin(paths, NullLogger<RecycleBin>.Instance, _db.History);
        _fixer = new PlayabilityFixer(probe, new FfmpegExecutor(encoder, NullLogger<FfmpegExecutor>.Instance), new OutputVerifier(probe),
            new LibraryGuard(manager), bin, new VerifiedReplacementService(bin),
            new PluginConfigurationProvider(() => _config), monitor, manager, NullLogger<PlayabilityFixer>.Instance);
        CacheProbe("""{"format":{"format_name":"matroska","duration":"120"},"streams":[{"codec_type":"audio"}]}""");
    }

    private void CacheProbe(string json)
    {
        var file = new FileInfo(_path);
        _db.Probes.StoreProbe(_path, file.Length, file.LastWriteTimeUtc.Ticks, json);
    }

    private Issue Seed(string reason = "no-video")
    {
        _db.ReplaceDetectedIssues(IssueType.Playability, new[] { new Issue { Type = IssueType.Playability, Path = _path, DetailsJson = "{\"reason\":\"" + reason + "\"}", DetectedAtUtc = DateTime.UtcNow } });
        var issue = Assert.Single(_db.GetIssues());
        _db.TryTransitionIssueStatus(issue.Id, new[] { IssueStatus.Detected }, IssueStatus.Queued);
        issue.Status = IssueStatus.Queued;
        return issue;
    }

    [Theory]
    [InlineData(DisposalMethod.Permanent)]
    [InlineData(DisposalMethod.RecycleBin)]
    public async Task ExhaustedRepairPreservesSourceUntilSecondApproval(DisposalMethod disposal)
    {
        _config.PlayabilityDisposal = disposal;
        var issue = Seed();
        // The video re-encode rung cannot salvage an audio-only source: no replacement is verified.
        var failed = await _fixer.FixAsync(issue, null, CancellationToken.None);
        Assert.Equal(FixReason.RepairFailed, failed.Reason);
        Assert.True(File.Exists(_path));
        new FixResultRecorder(_db, NullLogger<FixResultRecorder>.Instance).Record(
            new PlannedFix(PlannedFixKind.Single, issue, Array.Empty<Issue>()),
            new FixExecutionResult(failed, issue.Id, Array.Empty<long>()), DateTime.UtcNow);
        var review = Assert.Single(_db.GetIssues());
        Assert.Equal(IssueStatus.Detected, review.Status);
        Assert.Equal(FixReason.ConsentChanged, (await _fixer.FixAsync(review, null, CancellationToken.None)).Reason);
        Assert.True(File.Exists(_path));
        _db.TryTransitionIssueStatus(review.Id, new[] { IssueStatus.Detected }, IssueStatus.Queued);
        review.Status = IssueStatus.Queued;
        var removed = await _fixer.FixAsync(review, null, CancellationToken.None);
        Assert.True(removed.Success);
        Assert.False(File.Exists(_path));
        if (disposal == DisposalMethod.RecycleBin) Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(removed.RecyclePath!));
        else Assert.Null(removed.RecyclePath);
    }

    [Fact]
    public async Task DeleteApprovalDoesNotRemoveAFileThatRevalidatesHealthy()
    {
        var issue = Seed();
        var file = new FileInfo(_path);
        PlayabilityRepairState.Apply(issue, file.Length, file.LastWriteTimeUtc.Ticks);
        CacheProbe("""{"format":{"format_name":"matroska","duration":"120"},"streams":[{"codec_type":"video"}]}""");
        var result = await _fixer.FixAsync(issue, null, CancellationToken.None);
        Assert.Equal(FixReason.AlreadyHealthy, result.Reason);
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public async Task OldBitrateSizeFindingNeedsDecodeEvidence()
    {
        var issue = Seed("size-truncated");
        CacheProbe("""{"format":{"format_name":"matroska","duration":"120","bit_rate":"10000000"},"streams":[{"codec_type":"video"}]}""");
        var file = new FileInfo(_path);
        _db.Probes.StoreDecode(_path, file.Length, file.LastWriteTimeUtc.Ticks, string.Empty);
        Assert.Equal(FixReason.AlreadyHealthy, (await _fixer.FixAsync(issue, null, CancellationToken.None)).Reason);
        Assert.True(File.Exists(_path));
    }

    public void Dispose()
    {
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, _previous);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
