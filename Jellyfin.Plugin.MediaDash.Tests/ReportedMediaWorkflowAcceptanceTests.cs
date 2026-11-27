using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using Jellyfin.Plugin.MediaDash.Probing;
using Jellyfin.Plugin.MediaDash.Scanners;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Explicit opt-in: real bundled FFmpeg, generated media and real plugin services; no server or user media.
[Trait("Category", "ReportedIssueExternal")]
[Collection("MediaDashDb")]
public sealed class ReportedMediaWorkflowAcceptanceTests : IDisposable
{
    private readonly string _root;
    private readonly Plugin? _previous = Plugin.Instance;
    private readonly PluginConfiguration _config = new()
    {
        DryRun = false, AllowedAudioLanguages = ["orig"], AudioFixMode = FixMode.ManualApprove,
        AudioDisposal = DisposalMethod.RecycleBin, AnalyticsEnabled = false
    };
    private readonly ITestOutputHelper _output;
    private readonly string _ffmpeg;
    private readonly MediaDashDb _db;
    private readonly FfprobeService _probe;
    private readonly TrackFixer _fixer;
    private readonly FfmpegExecutor _ffmpegExecutor;
    private readonly ILibraryManager _library;
    private readonly LibraryGuard _guard;
    private readonly RecycleBin _bin;
    private readonly ILibraryMonitor _monitor;
    private readonly ProtectionService _protection;
    private readonly SpanishVideo _item;

    public ReportedMediaWorkflowAcceptanceTests(ITestOutputHelper output)
    {
        _output = output;
        _ffmpeg = Environment.GetEnvironmentVariable("MEDIADASH_TEST_FFMPEG") ?? "";
        Assert.True(File.Exists(_ffmpeg), "Set MEDIADASH_TEST_FFMPEG to Jellyfin's bundled ffmpeg executable.");
        var fixtureRoot = Environment.GetEnvironmentVariable("MEDIADASH_TEST_UNC_ROOT") ?? Path.GetTempPath();
        Assert.True(Directory.Exists(fixtureRoot), "The owned fixture parent must already exist.");
        _root = Path.Combine(fixtureRoot, "md-media-acceptance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _db = new MediaDashDb(Path.Combine(_root, "test.db"));
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, _config);
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, plugin);
        _item = new SpanishVideo { Id = Guid.NewGuid(), Name = "Owned", ProductionYear = 2026, Path = Path.Combine(_root, "Owned.mkv") };
        _library = Proxy<ILibraryManager>((method, _) => method.Name switch
        {
            "GetVirtualFolders" => new List<VirtualFolderInfo> { new() { ItemId = "owned", Locations = [_root] } },
            "GetItemById" => _item, "GetItemList" => new BaseItem[] { _item }, _ => null
        });
        var encoder = Proxy<IMediaEncoder>((method, _) => method.Name switch
        {
            "get_EncoderPath" => _ffmpeg,
            "get_ProbePath" => Path.Combine(Path.GetDirectoryName(_ffmpeg)!, "ffprobe" + Path.GetExtension(_ffmpeg)),
            _ => null
        });
        var paths = Proxy<IServerApplicationPaths>((method, _) => method.Name == "get_DataPath" ? Path.Combine(_root, "data") : null);
        _guard = new LibraryGuard(_library);
        _monitor = Proxy<ILibraryMonitor>((_, _) => null);
        _protection = new ProtectionService(_db.Protection, _library);
        _bin = new RecycleBin(paths, NullLogger<RecycleBin>.Instance, _db.History);
        _probe = new FfprobeService(encoder, _db.Probes, NullLogger<FfprobeService>.Instance);
        _ffmpegExecutor = new FfmpegExecutor(encoder, NullLogger<FfmpegExecutor>.Instance);
        _fixer = new TrackFixer(_probe, _ffmpegExecutor,
            new OutputVerifier(_probe), _guard, _bin, new VerifiedReplacementService(_bin),
            new PluginConfigurationProvider(() => _config), _monitor, NullLogger<TrackFixer>.Instance, _protection, _library);
    }

    [Fact]
    public async Task SuccessfulMultiTrackDecodeIsReusedOnlyForUnchangedScanInput()
    {
        await Generate();
        var media = await _probe.ProbeAsync(_item.Path, CancellationToken.None);
        Assert.NotNull(media);
        Assert.True(media.Streams!.Count(stream => stream.CodecType == "audio") > 1);
        Assert.Null(await _probe.FindBrokenAudioStreamAsync(_item.Path, media, useCache: true, CancellationToken.None));

        var withoutEncoder = new FfprobeService(Proxy<IMediaEncoder>((_, _) => null), _db.Probes, NullLogger<FfprobeService>.Instance);
        Assert.Null(await withoutEncoder.FindBrokenAudioStreamAsync(_item.Path, media, useCache: true, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => withoutEncoder.FindBrokenAudioStreamAsync(_item.Path, media, CancellationToken.None));

        File.SetLastWriteTimeUtc(_item.Path, File.GetLastWriteTimeUtc(_item.Path).AddMinutes(-1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => withoutEncoder.FindBrokenAudioStreamAsync(_item.Path, media, useCache: true, CancellationToken.None));

        var readOnlySample = Environment.GetEnvironmentVariable("MEDIADASH_TEST_READONLY_AUDIO_PATH");
        if (!string.IsNullOrWhiteSpace(readOnlySample))
        {
            var file = new FileInfo(readOnlySample);
            var size = file.Length;
            var modified = file.LastWriteTimeUtc;
            var sample = await _probe.ProbeAsync(file.FullName, CancellationToken.None);
            Assert.NotNull(sample);
            var tracks = sample.Streams!.Count(stream => stream.CodecType == "audio");
            Assert.True(tracks > 1);
            var elapsed = Stopwatch.StartNew();
            Assert.Null(await _probe.FindBrokenAudioStreamAsync(file.FullName, sample, useCache: true, CancellationToken.None));
            _output.WriteLine($"Read-only NAS, {size} bytes, {tracks} audio tracks, combined decode with empty MediaDash cache: {elapsed.Elapsed.TotalSeconds:F3}s");
            elapsed.Restart();
            Assert.Null(await withoutEncoder.FindBrokenAudioStreamAsync(file.FullName, sample, useCache: true, CancellationToken.None));
            _output.WriteLine($"Warm cache, no encoder configured: {elapsed.Elapsed.TotalMilliseconds:F3}ms");
            file.Refresh();
            Assert.Equal(size, file.Length);
            Assert.Equal(modified, file.LastWriteTimeUtc);
        }
    }

    [Fact]
    public async Task SampledPlaybackChecksSecondaryAudioAndDoesNotSatisfyFullDecode()
    {
        await Generate();
        var media = await _probe.ProbeAsync(_item.Path, CancellationToken.None);
        Assert.NotNull(media);
        Assert.Null(await _probe.CheckSampledPlaybackAsync(_item.Path, media, CancellationToken.None));
        var withoutEncoder = new FfprobeService(Proxy<IMediaEncoder>((_, _) => null), _db.Probes, NullLogger<FfprobeService>.Instance);
        Assert.Null(await withoutEncoder.CheckSampledPlaybackAsync(_item.Path, media, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => withoutEncoder.FindBrokenAudioStreamAsync(_item.Path, media, useCache: true, CancellationToken.None));

        var damaged = Path.Combine(_root, "sample-damaged-secondary.mkv");
        await Run("-v", "error", "-i", _item.Path, "-map", "0", "-c", "copy", "-bsf:a:1", "noise=amount=1", damaged);
        var damagedMedia = await _probe.ProbeAsync(damaged, CancellationToken.None);
        Assert.NotNull(damagedMedia);
        Assert.NotNull(await _probe.CheckSampledPlaybackAsync(damaged, damagedMedia, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => withoutEncoder.CheckSampledPlaybackAsync(damaged, damagedMedia, CancellationToken.None));

        _config.ThoroughPlayabilityCheck = false;
        var scanner = new PlayabilityScanner(_probe, new BookProbeService(_db.Probes), new ComicProbeService(_db.Probes), NullLogger<PlayabilityScanner>.Instance);
        var movie = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Path = damaged };
        var issue = Assert.Single(await scanner.ScanAsync([movie], new Progress<double>(), CancellationToken.None));
        Assert.Contains("sampled playback check", issue.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CombinedDecodeFailureIdentifiesBrokenSecondaryTrackWithoutCachingSuccess()
    {
        await Generate();
        var damaged = Path.Combine(_root, "damaged-secondary.mkv");
        await Run("-v", "error", "-i", _item.Path, "-map", "0", "-c", "copy", "-bsf:a:1", "noise=amount=1", damaged);
        var media = await _probe.ProbeAsync(damaged, CancellationToken.None);
        Assert.NotNull(media);
        var audio = media.Streams!.Where(stream => stream.CodecType == "audio").ToArray();
        Assert.Equal(2, audio.Length);
        var result = await _probe.FindBrokenAudioStreamAsync(damaged, media, useCache: true, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(audio[1].Index, result.Value.Stream.Index);
        var withoutEncoder = new FfprobeService(Proxy<IMediaEncoder>((_, _) => null), _db.Probes, NullLogger<FfprobeService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => withoutEncoder.FindBrokenAudioStreamAsync(damaged, media, useCache: true, CancellationToken.None));
    }

    [Fact]
    public async Task ProbeWaitsForActiveFfmpegAndCanCancelWhileQueued()
    {
        await Generate();
        using var cancelEncode = new CancellationTokenSource();
        var (encode, started) = HoldEncoder(cancelEncode.Token);
        try
        {
            await started.WaitAsync(TimeSpan.FromSeconds(10));
            using var cancelWaiting = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _probe.ProbeAsync(_item.Path, cancelWaiting.Token));
            Assert.False(encode.IsCompleted);
        }
        finally
        {
            await cancelEncode.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => encode);
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var probe = await _probe.ProbeAsync(_item.Path, deadline.Token);
        Assert.NotNull(probe);
        Assert.NotNull(probe.Streams);
        Assert.Contains(probe.Streams, stream => stream.CodecType == "video");
    }

    [Fact]
    public async Task WaitingDoesNotSpendFfmpegRuntimeBudget()
    {
        using var cancelEncode = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (encode, started) = HoldEncoder(cancelEncode.Token);
        Task<string?>? queued = null;
        try
        {
            await started.WaitAsync(TimeSpan.FromSeconds(10));
            queued = _ffmpegExecutor.RunAsync(["-nostdin", "-f", "lavfi", "-i", "sine=duration=0.1", "-f", "null", "-"],
                TimeSpan.FromSeconds(2), deadline.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(2200), deadline.Token);
            Assert.False(queued.IsCompleted);
        }
        finally
        {
            await cancelEncode.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => encode);
        }

        Assert.Null(await queued);
    }

    [Fact]
    public async Task MusicConverterSharesTheSlotAndCanRetryAfterQueueCancellation()
    {
        var source = Path.Combine(_root, "owned.wav");
        await Run("-v", "error", "-f", "lavfi", "-i", "sine=duration=1", source);
        var target = Path.Combine(_root, "converted.flac");
        var converter = new MusicConverter(_ffmpeg);
        using var cancelEncode = new CancellationTokenSource();
        var (encode, started) = HoldEncoder(cancelEncode.Token);
        try
        {
            await started.WaitAsync(TimeSpan.FromSeconds(10));
            using var cancelWaiting = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => converter.ConvertAsync(source, target, null, cancelWaiting.Token));
            Assert.False(File.Exists(target));
        }
        finally
        {
            await cancelEncode.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => encode);
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await converter.ConvertAsync(source, target, null, deadline.Token);
        Assert.True(result.Success, result.Reason);
        await Run("-v", "error", "-xerror", "-i", target, "-map", "0:a:0", "-f", "null", "-");
    }

    private (Task<string?> Encode, Task Started) HoldEncoder(CancellationToken token)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var encode = _ffmpegExecutor.RunAsync(["-nostdin", "-re", "-f", "lavfi", "-i", "sine=frequency=440", "-t", "30", "-f", "null", "-"],
            TimeSpan.FromSeconds(40), token, new InlineProgress(started), totalDurationSeconds: 30);
        return (encode, started.Task);
    }

    private sealed class InlineProgress(TaskCompletionSource started) : IProgress<double>
    {
        public void Report(double value) => started.TrySetResult();
    }

    [Fact]
    public async Task Issue92_RealOutputHeaderFailurePreservesOriginalAndApproval()
    {
        // Controlled container/extension mismatch retains an MP4 timecode data track that Matroska cannot copy.
        await Generate(mp4: true);
        var original = Hash(_item.Path);
        var issue = await DetectAndQueue();
        var result = await Execute(issue);
        Assert.False(result.Success);
        Assert.Equal(FixReason.ProcessFailed, result.Reason);
        Assert.Contains("original", result.Message);
        Assert.Equal(original, Hash(_item.Path));
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(issue.Id)!.Status);
        Assert.Equal(0, Assert.Single(_db.History.GetHistory()).BytesFreed);
        Assert.Contains(Diagnostics.RecentAll(), entry => entry.Source == "Ffmpeg.Error" && entry.Message.Contains(_root, StringComparison.Ordinal));
        await Decode(_item.Path);
        _output.WriteLine(result.Message);
    }

    [Fact]
    public async Task Issue92_RetainedInventoryMismatchPreservesOriginalAndApproval()
    {
        await Generate();
        var original = Hash(_item.Path);
        var issue = await DetectAndQueue();
        var inventory = (await _probe.ProbeAsync(_item.Path, default))!;
        // Inject a stale cached inventory, not a claimed reproduction of the reporter's media cause.
        var subtitle = inventory.Streams!.Single(stream => stream.CodecType == "subtitle");
        subtitle.Tags = new Dictionary<string, string>(subtitle.Tags!) { ["language"] = "fra" };
        var file = new FileInfo(_item.Path);
        _db.Probes.StoreProbe(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks, JsonSerializer.Serialize(inventory));
        var result = await Execute(issue);
        Assert.False(result.Success);
        Assert.Equal(FixReason.VerificationFailed, result.Reason);
        Assert.Equal(original, Hash(_item.Path));
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(issue.Id)!.Status);
        Assert.False(Assert.Single(_db.History.GetHistory()).Success);
        await Decode(_item.Path);
        _output.WriteLine(result.Message);
    }

    [Fact]
    public async Task Issue89_RealUncReencodePreservesRecycleAndRestores()
    {
        Assert.True(OperatingSystem.IsWindows() && _root.StartsWith(@"\\", StringComparison.Ordinal), "Set MEDIADASH_TEST_UNC_ROOT to an owned Windows UNC directory.");
        await Generate();
        _config.TranscodeFixMode = FixMode.ManualApprove;
        _config.TranscodeDisposal = DisposalMethod.RecycleBin;
        _config.PreferredCodec = "h264";
        _config.SoftwareEncodePreset = EncodePreset.Faster;
        _config.RenameAfterTranscode = false;
        var original = Hash(_item.Path);
        var before = (await _probe.ProbeAsync(_item.Path, default))!;
        _db.Issues.ReplaceDetectedIssues(IssueType.Quality, [new Issue { Type = IssueType.Quality, ItemId = _item.Id, Path = _item.Path, DetectedAtUtc = DateTime.UtcNow }]);
        var issue = Assert.Single(_db.Issues.GetIssues());
        Assert.Equal(StatusTransitionResult.Applied, _db.Issues.TryTransitionIssueStatus(issue.Id, [IssueStatus.Detected], IssueStatus.Queued));
        var server = Proxy<IServerConfigurationManager>((method, _) => method.Name == "GetConfiguration" ? new EncodingOptions() : null);
        var fixer = new TranscodeFixer(_probe, _ffmpegExecutor, new OutputVerifier(_probe), _guard, new VerifiedReplacementService(_bin),
            new PluginConfigurationProvider(() => _config), _monitor, _library, server, _db.Issues, _protection, NullLogger<TranscodeFixer>.Instance);
        Assert.Null(FreeSpaceGuard.DescribeShortfall(_item.Path, 2));
        var result = await Execute(_db.Issues.GetIssue(issue.Id)!, fixer);
        Assert.True(result.Success, result.Message);
        Assert.Equal(original, Hash(result.RecyclePath!));
        Assert.False(original.SequenceEqual(Hash(_item.Path)));
        Assert.True(TrackFixer.RetainedTracksMatch(before, await _probe.ProbeAsync(_item.Path, default), []));
        await Decode(_item.Path);
        var history = Assert.Single(_db.History.GetHistory());
        var restore = new RestoreService(_db, _db.History, _db.RestoreOps, _bin, _guard, _monitor, NullLogger<RestoreService>.Instance, _protection);
        Assert.True(restore.RestoreFromHistory(history.Id, true).Success);
        Assert.Equal(original, Hash(_item.Path));
        await Decode(_item.Path);
        _output.WriteLine("UNC re-encode, retained inventory, full decode, recycle hash and exact-byte restoration passed at " + _root);
    }

    [Fact]
    public async Task CanonicalRenameAfterRealReencodeCanUndoRenameThenRestoreOriginal()
    {
        await Generate();
        _config.TranscodeFixMode = FixMode.ManualApprove;
        _config.TranscodeDisposal = DisposalMethod.RecycleBin;
        _config.PreferredCodec = "h264";
        _config.SoftwareEncodePreset = EncodePreset.Faster;
        _config.MaxResolutionHeight = 90;
        _config.TargetContainer = "mkv";
        _config.RenameAfterTranscode = true;
        var original = Hash(_item.Path);
        var sidecar = Path.ChangeExtension(_item.Path, ".fr.srt");
        File.WriteAllText(sidecar, "1\n00:00:00,000 --> 00:00:01,000\nOwned external subtitle\n");
        var subtitleHash = Hash(sidecar);
        _db.Issues.ReplaceDetectedIssues(IssueType.Quality, [new Issue { Type = IssueType.Quality, ItemId = _item.Id, Path = _item.Path, DetectedAtUtc = DateTime.UtcNow }]);
        _db.Issues.QueueDetectedIssues(IssueType.Quality);
        var issue = Assert.Single(_db.Issues.GetIssues(IssueType.Quality));
        var planner = new MediaMovePlanner(_guard, _protection, _library, _db.Moves);
        var moves = new MediaMoveService(_db, _guard, _protection, _library, NullLogger<MediaMoveService>.Instance);
        var server = Proxy<IServerConfigurationManager>((method, _) => method.Name == "GetConfiguration" ? new EncodingOptions() : null);
        var fixer = new TranscodeFixer(_probe, _ffmpegExecutor, new OutputVerifier(_probe), _guard, new VerifiedReplacementService(_bin),
            new PluginConfigurationProvider(() => _config), _monitor, _library, server, _db.Issues, _protection, NullLogger<TranscodeFixer>.Instance, planner, moves, _db.History);
        var result = await Execute(_db.Issues.GetIssue(issue.Id)!, fixer);
        Assert.True(result.Success, result.Message);
        var renamed = Path.Combine(_root, "Owned (2026) - 90p.mkv");
        Assert.True(File.Exists(renamed), result.Message);
        Assert.False(File.Exists(_item.Path));
        var encoded = Hash(renamed);
        Assert.Equal(90, (await _probe.ProbeAsync(renamed, default))!.Streams!.First(stream => stream.CodecType == "video").Height);
        Assert.Equal(subtitleHash, Hash(Path.ChangeExtension(renamed, ".fr.srt")));
        Assert.Equal(original, Hash(result.RecyclePath!));
        await Decode(renamed);
        Assert.Equal(IssueStatus.Fixed, _db.Issues.GetIssue(issue.Id)!.Status);
        var history = _db.History.GetHistory();
        var reencodeHistory = Assert.Single(history, row => row.Type == IssueType.Quality);
        var renameHistory = Assert.Single(history, row => row.Type == IssueType.ManualMove);
        Assert.Null(reencodeHistory.MoveOperationId);
        Assert.True((await moves.UndoAsync(renameHistory.MoveOperationId!, null, default)).Success);
        Assert.Equal(encoded, Hash(_item.Path));
        Assert.Equal(_item.Path, _db.Issues.GetIssue(issue.Id)!.Path);
        Assert.Equal(subtitleHash, Hash(sidecar));
        await Decode(_item.Path);
        var restore = new RestoreService(_db, _db.History, _db.RestoreOps, _bin, _guard, _monitor, NullLogger<RestoreService>.Instance, _protection);
        Assert.True(restore.RestoreFromHistory(reencodeHistory.Id, true).Success);
        Assert.Equal(original, Hash(_item.Path));
        await Decode(_item.Path);
        _output.WriteLine("Real re-encode -> actual 90p canonical name -> companion rename -> separate Undo -> original exact-byte restoration passed.");
    }

    [Fact]
    public async Task Issue87_RealOriginalAudioCleanupPreservesRetainedPacketsAndRestores()
    {
        await Generate();
        var original = Hash(_item.Path);
        var video = await StreamHash(_item.Path, "v:0");
        var spanish = await StreamHash(_item.Path, "a:1");
        var subtitle = await StreamHash(_item.Path, "s:0");
        var before = (await _probe.ProbeAsync(_item.Path, default))!;
        var issue = await DetectAndQueue();
        var result = await Execute(issue);
        Assert.True(result.Success, result.Message);
        Assert.Equal(FixOutcome.Applied, result.Outcome);
        Assert.Equal(IssueStatus.Fixed, _db.Issues.GetIssue(issue.Id)!.Status);
        Assert.Equal(original, Hash(result.RecyclePath!));
        var after = (await _probe.ProbeAsync(_item.Path, default))!;
        var audio = Assert.Single(after.Streams!, stream => stream.CodecType == "audio");
        Assert.Equal("spa", audio.Tags!["language"]);
        Assert.True(TrackFixer.RetainedTracksMatch(before, after, [1]));
        Assert.Equal(video, await StreamHash(_item.Path, "v:0"));
        Assert.Equal(spanish, await StreamHash(_item.Path, "a:0"));
        Assert.Equal(subtitle, await StreamHash(_item.Path, "s:0"));
        await Decode(_item.Path);
        Assert.Empty(await new AudioLanguageScanner(_probe, NullLogger<AudioLanguageScanner>.Instance).ScanAsync([_item], new Progress<double>(), default));
        var history = Assert.Single(_db.History.GetHistory());
        var restore = new RestoreService(_db, _db.History, _db.RestoreOps, _bin, _guard, _monitor, NullLogger<RestoreService>.Instance, _protection);
        Assert.True(restore.RestoreFromHistory(history.Id, true).Success);
        Assert.Equal(original, Hash(_item.Path));
        await Decode(_item.Path);
        _output.WriteLine("Generated Spanish-original workflow passed: retained packet hashes, rescan, recycle and exact-byte restore. Not reporter Rafa acceptance.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Issue94RecoverableMp3GlitchDoesNotRequireRepair(bool coverArt)
    {
        var path = await GenerateDamagedMp3(coverArt);
        var original = Hash(path);
        var probe = (await _probe.ProbeAsync(path, default))!;
        Assert.Null(await _probe.DecodeCheckAsync(path, 30, default, isAudioOnly: true));
        Assert.Null(await _probe.DecodeStreamAsync(path, 0, default));
        var scanner = new PlayabilityScanner(_probe, new BookProbeService(_db.Probes), new ComicProbeService(_db.Probes), NullLogger<PlayabilityScanner>.Instance);
        var item = new MediaBrowser.Controller.Entities.Audio.Audio { Id = Guid.NewGuid(), Path = path };
        Assert.Empty(await scanner.ScanAsync([item], new Progress<double>(), default));
        var stale = new Issue { Type = IssueType.Playability, Path = path, DetailsJson = """{"reason":"decode-error"}""" };
        Assert.Equal(FixOutcome.NoChange, (await NewPlayabilityFixer().FixAsync(stale, null, default)).Outcome);
        Assert.Equal(original, Hash(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Issue94Mp3AudioRepairPreservesContainerTagsAndArtwork(bool coverArt)
    {
        var path = await GenerateDamagedMp3(coverArt);
        var original = Hash(path);
        var probe = (await _probe.ProbeAsync(path, default))!;
        var issue = new Issue { Type = IssueType.Playability, Path = path };
        // Force the rung precondition independently of the now-tolerant playability classifier.
        var task = (Task<string?>)typeof(PlayabilityFixer).GetMethod("TryRung4aAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(NewPlayabilityFixer(), [issue, probe, new Lazy<Task<IReadOnlyList<int>>>(() => Task.FromResult<IReadOnlyList<int>>([0])), CancellationToken.None])!;
        var output = await task;
        Assert.NotNull(output);
        var repaired = (await _probe.ProbeAsync(output, default))!;
        Assert.Equal("mp3", Assert.Single(repaired.Streams!, s => s.CodecType == "audio").CodecName);
        Assert.Equal(coverArt, repaired.Streams!.Any(s => s.IsAttachedPicture));
        await Run("-v", "error", "-xerror", "-i", output, "-vn", "-f", "null", "-");
        Assert.Contains("Issue 94", await Run("-v", "error", "-i", output, "-f", "ffmetadata", "-"));
        Assert.Equal(original, Hash(path));
        var identity = new FileInfo(path);
        var swap = (Task<FixResult?>)typeof(PlayabilityFixer).GetMethod("TrySwapRepairedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(NewPlayabilityFixer(), [issue, output, "audio re-encoded", false, probe, identity.Length, identity.LastWriteTimeUtc.Ticks, CancellationToken.None])!;
        var result = await swap;
        Assert.NotNull(result);
        Assert.True(result.Success, result.Message);
        Assert.Equal(original, Hash(result.RecyclePath!));
        await Run("-v", "error", "-xerror", "-i", path, "-vn", "-f", "null", "-");
        _db.History.AddHistory(new HistoryEntry { IssueId = 0, Type = IssueType.Playability, Path = path,
            RecyclePath = result.RecyclePath, Success = true, FixedAtUtc = DateTime.UtcNow });
        var history = Assert.Single(_db.History.GetHistory());
        var restore = new RestoreService(_db, _db.History, _db.RestoreOps, _bin, _guard, _monitor, NullLogger<RestoreService>.Instance, _protection);
        Assert.True(restore.RestoreFromHistory(history.Id, true).Success);
        Assert.Equal(original, Hash(path));
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public async Task Issue94SubstantiallyDamagedMp3RemainsAPlayabilityFailure(double damagedFraction)
    {
        var path = await GenerateDamagedMp3(coverArt: false, damagedFraction);
        Assert.NotNull(await _probe.DecodeCheckAsync(path, 30, default, isAudioOnly: true));
        Assert.NotNull(await _probe.DecodeStreamAsync(path, 0, default));
        var original = Hash(path);
        _config.RepairAttemptRemux = false;
        _config.RepairAttemptDropStreams = false;
        _config.RepairAttemptContainerCoerce = false;
        _config.RepairAttemptAudioReencode = true;
        _config.RepairAttemptReencode = false;
        _db.Issues.ReplaceDetectedIssues(IssueType.Playability, [new Issue { Type = IssueType.Playability, Path = path,
            DetailsJson = """{"reason":"decode-error"}""", DetectedAtUtc = DateTime.UtcNow }]);
        var issue = Assert.Single(_db.Issues.GetIssues());
        _db.Issues.TryTransitionIssueStatus(issue.Id, [IssueStatus.Detected], IssueStatus.Queued);
        var result = await Execute(_db.Issues.GetIssue(issue.Id)!, NewPlayabilityFixer());
        Assert.Equal(FixReason.RepairFailed, result.Reason);
        Assert.Equal(original, Hash(path));
        Assert.Equal(IssueStatus.Detected, _db.Issues.GetIssue(issue.Id)!.Status);
        Assert.Empty(_db.Issues.QueueDetectedIssues(IssueType.Playability, requireConsent: true));
    }

    [Theory]
    [InlineData("flac", "flac", "flac")]
    [InlineData("wav", "pcm_s16le", "pcm_s16le")]
    [InlineData("wav", "pcm_s24le", "pcm_s24le")]
    [InlineData("wav", "pcm_s32le", "pcm_s32le")]
    [InlineData("wav", "pcm_f32le", "pcm_f32le")]
    [InlineData("aiff", "pcm_s16be", "pcm_s16be")]
    [InlineData("aiff", "pcm_s24be", "pcm_s24be")]
    [InlineData("aiff", "pcm_s32be", "pcm_s32be")]
    [InlineData("aiff", "pcm_f32be", "pcm_f32be")]
    [InlineData("ogg", "libvorbis", "vorbis")]
    [InlineData("opus", "libopus", "opus")]
    [InlineData("m4a", "aac", "aac")]
    [InlineData("aac", "aac", "aac")]
    [InlineData("wma", "wmav2", "wmav2")]
    public async Task Issue94AudioRepairUsesAnEncoderCompatibleWithItsContainer(string ext, string encoder, string codec)
    {
        var path = Path.Combine(_root, "audio." + ext);
        await Run("-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-c:a", encoder, path);
        var probe = (await _probe.ProbeAsync(path, default))!;
        var task = (Task<string?>)typeof(PlayabilityFixer).GetMethod("TryRung4aAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(NewPlayabilityFixer(), [new Issue { Path = path }, probe, new Lazy<Task<IReadOnlyList<int>>>(() => Task.FromResult<IReadOnlyList<int>>([0])), CancellationToken.None])!;
        var output = await task;
        Assert.NotNull(output);
        Assert.Equal(codec, Assert.Single((await _probe.ProbeAsync(output, default))!.Streams!, s => s.CodecType == "audio").CodecName);
        if (encoder.StartsWith("pcm_", StringComparison.Ordinal)) Assert.Equal(await StreamHash(path, "a:0"), await StreamHash(output, "a:0"));
        await Run("-v", "error", "-xerror", "-i", output, "-f", "null", "-");
    }

    [Fact]
    public async Task Issue94TolerantDecodeStillDetectsATruncatedShortVideo()
    {
        var path = Path.Combine(_root, "truncated.mp4");
        await Run("-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=12:duration=12",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=12", "-c:v", "libx264", "-preset", "ultrafast",
            "-c:a", "aac", "-movflags", "+faststart", path);
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length * 2 / 5)]);
        var probe = (await _probe.ProbeAsync(path, default))!;
        Assert.True(probe.TryGetDuration(out var duration));
        var scanner = new PlayabilityScanner(_probe, new BookProbeService(_db.Probes), new ComicProbeService(_db.Probes), NullLogger<PlayabilityScanner>.Instance);
        var issue = Assert.Single(await scanner.ScanAsync([new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Path = path }], new Progress<double>(), default));
        Assert.Contains("decode-error", issue.DetailsJson);
        Assert.NotNull(await _probe.DecodeCheckAsync(path, duration, default));
    }

    [Fact]
    public async Task Issue94DiagnosticWordsInMetadataDoNotMakeHealthyAudioBroken()
    {
        var path = Path.Combine(_root, "healthy.flac");
        await Run("-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-c:a", "flac",
            "-metadata", "title=File ended prematurely Permission denied", path);
        Assert.Null(await _probe.DecodeCheckAsync(path, 3, default, isAudioOnly: true));
    }

    private PlayabilityFixer NewPlayabilityFixer() => new(_probe, _ffmpegExecutor, new OutputVerifier(_probe), _guard, _bin,
        new VerifiedReplacementService(_bin), new PluginConfigurationProvider(() => _config), _monitor, _library,
        NullLogger<PlayabilityFixer>.Instance, _protection);

    private async Task<string> GenerateDamagedMp3(bool coverArt, double damagedFraction = 0)
    {
        var path = Path.Combine(_root, "glitch.mp3");
        var args = new List<string> { "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=30" };
        if (coverArt)
        {
            var cover = Path.Combine(_root, "cover.jpg");
            await Run("-v", "error", "-f", "lavfi", "-i", "color=red:size=32x32:duration=1", "-frames:v", "1", cover);
            args.AddRange(["-i", cover, "-map", "0:a", "-map", "1:v", "-c:v", "copy", "-disposition:v", "attached_pic"]);
        }
        args.AddRange(["-c:a", "libmp3lame", "-b:a", "192k", "-metadata", "title=Issue 94", path]);
        await Run(args.ToArray());
        var probePath = Path.Combine(Path.GetDirectoryName(_ffmpeg)!, "ffprobe" + Path.GetExtension(_ffmpeg));
        using var packets = JsonDocument.Parse(await RunProgram(probePath, "-v", "error", "-select_streams", "a", "-show_packets", "-show_entries", "packet=pos,size", "-of", "json", path));
        var frames = packets.RootElement.GetProperty("packets").EnumerateArray().ToArray();
        var bytes = File.ReadAllBytes(path);
        foreach (var frame in damagedFraction > 0 ? frames.Skip(2).Take((int)((frames.Length - 4) * damagedFraction)) : frames.Skip(frames.Length / 2).Take(1))
        {
            var pos = int.Parse(frame.GetProperty("pos").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            var size = int.Parse(frame.GetProperty("size").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            Array.Fill(bytes, (byte)255, pos + 4, size - 4);
        }
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private async Task Generate(bool mp4 = false)
    {
        var sub = Path.Combine(_root, "owned.srt");
        File.WriteAllText(sub, "1\n00:00:00,000 --> 00:00:02,000\nOwned subtitle\n");
        await Run("-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=12:duration=3",
            "-f", "lavfi", "-i", "sine=frequency=400:duration=3", "-f", "lavfi", "-i", "sine=frequency=800:duration=3",
            "-i", sub, "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:s", "-c:v", "libx264", "-preset", "ultrafast",
            "-c:a", "aac", "-c:s", mp4 ? "mov_text" : "srt", "-metadata:s:a:0", "language=eng", "-metadata:s:a:1", "language=spa",
            "-metadata:s:s:0", "language=spa", "-disposition:a:0", "default", "-disposition:a:1", "0", "-disposition:s:0", "hearing_impaired",
            "-timecode", "00:00:00:00", "-f", mp4 ? "mp4" : "matroska", _item.Path);
        await Decode(_item.Path);
    }

    private async Task<Issue> DetectAndQueue()
    {
        var found = await new AudioLanguageScanner(_probe, NullLogger<AudioLanguageScanner>.Instance).ScanAsync([_item], new Progress<double>(), default);
        _db.Issues.ReplaceDetectedIssues(IssueType.AudioLanguage, found);
        var issue = Assert.Single(_db.Issues.GetIssues());
        Assert.Equal(StatusTransitionResult.Applied, _db.Issues.TryTransitionIssueStatus(issue.Id, [IssueStatus.Detected], IssueStatus.Queued));
        return _db.Issues.GetIssue(issue.Id)!;
    }

    private async Task<FixResult> Execute(Issue issue, IFixer? fixer = null)
    {
        var plan = new PlannedFix(PlannedFixKind.Single, issue, []);
        var executor = new FixExecutor(_db.Issues, [fixer ?? _fixer], new PluginConfigurationProvider(() => _config), TimeProvider.System, _protection, NullLogger<FixExecutor>.Instance);
        var execution = await executor.ExecuteAsync(plan, true, new Progress<double>(), default);
        Assert.NotNull(execution);
        new FixResultRecorder(_db, NullLogger<FixResultRecorder>.Instance).Record(plan, execution, DateTime.UtcNow);
        return execution.Result;
    }

    private Task<string> Decode(string path) => Run("-v", "error", "-xerror", "-i", path, "-map", "0:v", "-map", "0:a", "-f", "null", "-");
    private Task<string> StreamHash(string path, string stream) => Run("-v", "error", "-i", path, "-map", "0:" + stream, "-c", "copy", "-f", "hash", "-hash", "sha256", "-");

    private Task<string> Run(params string[] args) => RunProgram(_ffmpeg, args);

    private static async Task<string> RunProgram(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        var error = await stderr;
        Assert.True(process.ExitCode == 0, error);
        return (await stdout).Trim();
    }

    private static byte[] Hash(string path) => SHA256.HashData(File.ReadAllBytes(path));
    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> call) where T : class
    {
        var proxy = DispatchProxy.Create<T, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)proxy).Call = call;
        return proxy;
    }

    public sealed class SpanishVideo : MediaBrowser.Controller.Entities.Movies.Movie { public string GetInheritedOriginalLanguage() => "es"; }

    public void Dispose()
    {
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, _previous);
        Diagnostics.RemoveMatching("Ffmpeg.Error", _root);
        Diagnostics.RemoveMatching("FixTask", _root);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
