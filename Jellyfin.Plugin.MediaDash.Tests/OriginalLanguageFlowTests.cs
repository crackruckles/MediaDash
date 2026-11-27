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
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("Diagnostics")]
public sealed class OriginalLanguageFlowTests : IDisposable
{
    private readonly Plugin? _previous = Plugin.Instance;
    private readonly PluginConfiguration _config = new() { AllowedAudioLanguages = ["eng", "orig"], AllowedSubtitleLanguages = ["orig"], DryRun = false };

    public OriginalLanguageFlowTests()
    {
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, _config);
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, plugin);
    }

    public void Dispose() => typeof(Plugin).GetProperty("Instance")!.SetValue(null, _previous);

    private static FfprobeStreamInfo Stream(int index, string kind, string language) => new()
    {
        Index = index, CodecType = kind, CodecName = kind == "subtitle" ? "subrip" : "aac",
        Tags = new Dictionary<string, string> { ["language"] = language }
    };

    private static async Task<Issue?> Evaluate(object scanner, BaseItem item, FfprobeData probe)
        => await (Task<Issue?>)scanner.GetType().GetMethod("EvaluateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scanner, [item, "alternate.mkv", probe, CancellationToken.None])!;

    [Fact]
    public async Task ScannersKeepMetadataOriginalAndMissingScannerRecognizesExistingSubtitle()
    {
        var item = new OriginalLanguageTests.MetadataVideo();
        var probe = new FfprobeData { Streams = [Stream(1, "audio", "eng"), Stream(2, "audio", "kor"), Stream(3, "audio", "fra"), Stream(4, "subtitle", "kor")] };
        var audio = await Evaluate(new AudioLanguageScanner(null!, NullLogger<AudioLanguageScanner>.Instance), item, probe);
        Assert.NotNull(audio);
        Assert.Contains("keeps eng, kor", audio.SuggestedFix);
        Assert.Equal([3], TrackFixer.ComputeRemovableIndexes(probe, IssueType.AudioLanguage, _config, item));
        Assert.Null(await Evaluate(new SubtitleLanguageScanner(null!, NullLogger<SubtitleLanguageScanner>.Instance), item, probe));
        Assert.Null(await Evaluate(new MissingSubtitleScanner(null!, NullLogger<MissingSubtitleScanner>.Instance), item, probe));
    }

    [Fact]
    public async Task FailedMetadataLookupPreservesSpanishAndKeepsApprovedIssueVisible()
    {
        _config.AllowedAudioLanguages = ["orig"];
        _config.AudioDisposal = DisposalMethod.Permanent;
        var root = Path.Combine(Path.GetTempPath(), "md-original-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "Rafa - S01E04 - No Fear.mkv");
        try
        {
            byte[] original = [1, 2, 3, 4];
            File.WriteAllBytes(path, original);
            var db = new MediaDashDb(Path.Combine(root, "test.db"));
            var file = new FileInfo(path);
            db.Probes.StoreProbe(path, file.Length, file.LastWriteTimeUtc.Ticks,
                """{"streams":[{"index":1,"codec_type":"audio","codec_name":"aac","tags":{"language":"eng"},"disposition":{"original":1}},{"index":2,"codec_type":"audio","codec_name":"aac","tags":{"language":"spa"}}]}""");
            var item = new OriginalLanguageTests.UnavailableMetadataVideo { Id = Guid.NewGuid(), Path = path };
            var library = DispatchProxy.Create<ILibraryManager, CallProxy>();
            ((CallProxy)(object)library).Call = (method, _) => method.Name switch
            {
                "GetItemById" => item,
                "GetVirtualFolders" => new List<VirtualFolderInfo> { new() { Locations = [root] } },
                _ => null
            };
            var probe = new FfprobeService(null!, db.Probes, NullLogger<FfprobeService>.Instance);
            Assert.Empty(await new AudioLanguageScanner(probe, NullLogger<AudioLanguageScanner>.Instance)
                .ScanAsync([item], new Progress<double>(), CancellationToken.None));

            db.Issues.ReplaceDetectedIssues(IssueType.AudioLanguage, [new Issue { Type = IssueType.AudioLanguage,
                ItemId = item.Id, Path = path, DetailsJson = """{"removeIndexes":[2]}""", DetectedAtUtc = DateTime.UtcNow }]);
            var issue = Assert.Single(db.Issues.GetIssues());
            db.Issues.TryTransitionIssueStatus(issue.Id, [IssueStatus.Detected], IssueStatus.Queued);
            var config = new PluginConfigurationProvider(() => _config);
            var fixer = new TrackFixer(probe, null!, null!, new LibraryGuard(library), null!, null!, config,
                null!, NullLogger<TrackFixer>.Instance, libraryManager: library);
            var executor = new FixExecutor(db.Issues, [fixer], config, TimeProvider.System,
                new ProtectionService(db.Protection, library), NullLogger<FixExecutor>.Instance);
            var plan = new PlannedFix(PlannedFixKind.Single, db.Issues.GetIssue(issue.Id)!, []);
            var execution = await executor.ExecuteAsync(plan, true, new Progress<double>(), CancellationToken.None);
            Assert.NotNull(execution);
            Assert.Equal(FixOutcome.Failed, execution.Result.Outcome);
            Assert.Contains("original-language metadata", execution.Result.Message);
            new FixResultRecorder(db, NullLogger<FixResultRecorder>.Instance).Record(plan, execution, DateTime.UtcNow);
            Assert.Equal(IssueStatus.Queued, db.Issues.GetIssue(issue.Id)!.Status);
            var history = Assert.Single(db.History.GetHistory());
            Assert.False(history.Success);
            Assert.Equal(0, history.BytesFreed);
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally
        {
            Api.Diagnostics.RemoveMatching("Scanner.AudioLanguage", path);
            Api.Diagnostics.RemoveMatching("FixTask", path);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloaderRechecksStaleOrigPayloadAndNeverSearchesSentinel(bool metadataAvailable)
    {
        var calls = new List<string>();
        var manager = DispatchProxy.Create<ILibraryManager, CallProxy>();
        ((CallProxy)(object)manager).Call = (m, _) => m.Name == "GetItemById"
            ? metadataAvailable ? new OriginalLanguageTests.MetadataVideo() : new Video() : null;
        var provider = DispatchProxy.Create<ISubtitleManager, CallProxy>();
        ((CallProxy)(object)provider).Call = (m, args) =>
        {
            Assert.Equal("SearchSubtitles", m.Name);
            calls.Add((string)args![1]!);
            return Task.FromResult(Array.Empty<RemoteSubtitleInfo>());
        };
        var fixer = new MissingSubtitleFixer(provider, manager, NullLogger<MissingSubtitleFixer>.Instance);
        await fixer.FixAsync(new Issue { DetailsJson = "{\"missingLanguages\":[\"orig\"]}" }, null, CancellationToken.None);
        if (metadataAvailable) Assert.Equal(new[] { "kor" }, calls);
        else Assert.Empty(calls);
    }

    public class CallProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Call { get; set; } = (_, _) => null;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args);
    }
}
