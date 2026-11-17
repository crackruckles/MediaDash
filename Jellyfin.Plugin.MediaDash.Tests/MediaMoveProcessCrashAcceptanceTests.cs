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
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Trait("Category", "ReportedIssueExternal")]
[Collection("Diagnostics")]
public sealed class MediaMoveProcessCrashAcceptanceTests
{
    [Theory]
    [InlineData("BeforePrepare")]
    [InlineData("BeforeEntry")]
    [InlineData("BeforePublish")]
    [InlineData("BeforeNativeRename")]
    [InlineData("Published")]
    [InlineData("BeforeComplete")]
    [InlineData("Completed")]
    public async Task KilledOwnedWorkerKeepsEveryPayloadAndRestartNeverReplays(string boundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "md-owned-process-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".owned-fixtures"), "MediaDash owned process crash fixture");
        var paths = CrashFixture.Paths(root);
        Directory.CreateDirectory(paths.Source);
        Directory.CreateDirectory(paths.TargetLibrary);
        Directory.CreateDirectory(Path.Combine(paths.Source, "empty"));
        File.WriteAllText(Path.Combine(paths.Source, "Film.mkv"), "owned primary " + boundary);
        File.WriteAllText(Path.Combine(paths.Source, "Film.en.srt"), "owned subtitle " + boundary);
        File.WriteAllText(Path.Combine(paths.Source, "user-notes.bin"), "owned unknown notes " + boundary);
        var expected = Directory.GetFiles(paths.Source).Select(Hash).ToArray();
        var sentinel = Path.Combine(paths.SourceLibrary, "unrelated.mkv");
        File.WriteAllText(sentinel, "unrelated approved fixture");
        var sentinelHash = Hash(sentinel);
        var database = new MediaDashDb(Path.Combine(root, "moves.db"));
        database.Issues.ReplaceDetectedIssues(IssueType.AudioLanguage, [new Issue { Type = IssueType.AudioLanguage, Path = sentinel }]);
        database.Issues.QueueDetectedIssues(IssueType.AudioLanguage);
        var held = Assert.Single(database.Issues.GetIssues(IssueType.AudioLanguage));
        using var worker = new Process
        {
            StartInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory
            }
        };
        worker.StartInfo.ArgumentList.Add("vstest");
        worker.StartInfo.ArgumentList.Add(typeof(MediaMoveProcessCrashAcceptanceTests).Assembly.Location);
        worker.StartInfo.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=Jellyfin.Plugin.MediaDash.Tests.MediaMoveProcessCrashWorkerTests.RunOwnedWorker");
        worker.StartInfo.ArgumentList.Add("--ResultsDirectory:" + Path.Combine(root, "child-results"));
        worker.StartInfo.Environment["MEDIADASH_MOVE_CRASH_ROOT"] = root;
        worker.StartInfo.Environment["MEDIADASH_MOVE_CRASH_BOUNDARY"] = boundary;
        worker.StartInfo.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        worker.StartInfo.Environment["COMPlus_DbgEnableMiniDump"] = "0";
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        try
        {
            Assert.True(worker.Start());
            stdout = worker.StandardOutput.ReadToEndAsync();
            stderr = worker.StandardError.ReadToEndAsync();
            var signal = Path.Combine(root, "at-boundary");
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (!File.Exists(signal) && !worker.HasExited && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            if (!File.Exists(signal))
            {
                if (!worker.HasExited) worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync();
                Assert.Fail("Owned worker did not reach " + boundary + ": " + await stdout + " " + await stderr);
            }

            worker.Kill(entireProcessTree: true);
            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await worker.WaitForExitAsync(exitTimeout.Token);
            Assert.NotEqual(0, worker.ExitCode);
            var before = CrashFixture.Snapshot(paths);
            var present = before.Where(pair => pair.Key.StartsWith("F:", StringComparison.Ordinal)).Select(pair => pair.Value).ToHashSet(StringComparer.Ordinal);
            Assert.All(expected, hash => Assert.Contains(hash, present));
            Assert.Equal(sentinelHash, Hash(sentinel));
            var reopened = new MediaDashDb(Path.Combine(root, "moves.db"));
            Assert.Equal(before, CrashFixture.Snapshot(paths));
            Assert.Equal(IssueStatus.Queued, reopened.Issues.GetIssue(held.Id)!.Status);
            if (boundary == "BeforePrepare")
            {
                Assert.Empty(reopened.Moves.GetVisible());
            }
            else
            {
                var operation = Assert.Single(reopened.Moves.GetNeedsReview());
                Assert.Equal(string.Empty, operation.OwnerToken);
                Assert.NotNull(MediaMoveManifest.TryRead(operation.ManifestJson, operation.Id));
                Assert.True(reopened.Moves.HasActiveConflict(paths.Source));
                Assert.True(reopened.Moves.HasActiveConflict(paths.Destination));
                var again = new MediaDashDb(Path.Combine(root, "moves.db"));
                Assert.Equal(operation.ManifestJson, Assert.Single(again.Moves.GetNeedsReview()).ManifestJson);
                Assert.Equal(before, CrashFixture.Snapshot(paths));
            }
        }
        finally
        {
            if (stdout is not null)
            {
                if (!worker.HasExited) worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync();
                File.WriteAllText(Path.Combine(root, "child-stdout.log"), await stdout);
                File.WriteAllText(Path.Combine(root, "child-stderr.log"), await stderr!);
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            // All recursive targets are explicitly owned and checked before removal.
            CrashFixture.ValidateRoot(root);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

[Trait("Category", "ReportedIssueExternal")]
[Collection("Diagnostics")]
public sealed class MediaMoveProcessCrashWorkerTests
{
    [Fact]
    public async Task RunOwnedWorker()
    {
        var root = Environment.GetEnvironmentVariable("MEDIADASH_MOVE_CRASH_ROOT");
        Assert.False(string.IsNullOrEmpty(root), "Run this worker only through the owned process-crash driver.");
        CrashFixture.ValidateRoot(root!);
        var boundary = Environment.GetEnvironmentVariable("MEDIADASH_MOVE_CRASH_BOUNDARY")!;
        var paths = CrashFixture.Paths(root!);
        var config = new PluginConfiguration { DryRun = false };
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, config);
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, plugin);
        var item = new Movie { Id = Guid.NewGuid(), Name = "Owned crash fixture", Path = Path.Combine(paths.Source, "Film.mkv") };
        var manager = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)manager).Call = (method, args) => method.Name switch
        {
            "GetVirtualFolders" => new List<VirtualFolderInfo> { new() { Locations = [paths.SourceLibrary, paths.TargetLibrary] } },
            "GetItemList" => new List<BaseItem> { item },
            "GetItemById" => args![0] is Guid id && item.Id == id ? item : null,
            _ => null
        };
        var database = new MediaDashDb(Path.Combine(root!, "moves.db"));
        var guard = new LibraryGuard(manager);
        var protection = new ProtectionService(database.Protection, manager);
        var planner = new MediaMovePlanner(guard, protection, manager, database.Moves);
        var service = new MediaMoveService(database, guard, protection, manager, NullLogger<MediaMoveService>.Instance)
        {
            Checkpoint = (point, _) =>
            {
                if (point != boundary) return;
                File.WriteAllText(Path.Combine(root!, "at-boundary"), point);
                Thread.Sleep(Timeout.Infinite);
            }
        };
        var plan = await planner.BuildExplicitAsync([(paths.Source, paths.Destination)], [item], config, CancellationToken.None);
        Assert.Empty(plan.BlockingReasons);
        var result = await service.ExecuteExplicitAsync(plan, plan.Fingerprint, null, CancellationToken.None);
        Assert.Fail("Owned worker unexpectedly passed the requested crash boundary: " + boundary + "; " + result.Message);
    }
}

internal static class CrashFixture
{
    internal static void ValidateRoot(string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var name = Path.GetFileName(full);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), Path.GetDirectoryName(full));
        Assert.StartsWith("md-owned-process-crash-", name);
        Assert.True(Guid.TryParseExact(name["md-owned-process-crash-".Length..], "N", out _));
        Assert.True(File.Exists(Path.Combine(full, ".owned-fixtures")));
    }

    internal static Locations Paths(string root)
    {
        ValidateRoot(root);
        var sourceLibrary = Path.Combine(root, "source-library");
        var targetLibrary = Path.Combine(root, "target-library");
        return new(sourceLibrary, Path.Combine(sourceLibrary, "Owned bundle"), targetLibrary, Path.Combine(targetLibrary, "Moved bundle"));
    }

    internal static SortedDictionary<string, string> Snapshot(Locations paths)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in new[] { paths.SourceLibrary, paths.TargetLibrary })
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)) result["D:" + directory] = string.Empty;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                using var stream = File.OpenRead(file);
                result["F:" + file] = Convert.ToHexString(SHA256.HashData(stream));
            }
        }

        return result;
    }

    internal sealed record Locations(string SourceLibrary, string Source, string TargetLibrary, string Destination);
}
