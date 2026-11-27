using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("Diagnostics")]
public sealed class TranscodeLogScannerTests
{
    [Fact]
    public async Task SuccessfulEncodeClearsOlderSessionsAndNewSessionsCanStillReport()
    {
        var root = Directory.CreateTempSubdirectory("md-transcode-logs-");
        var previous = Plugin.Instance;
        try
        {
            var config = new PluginConfiguration();
            var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
            typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, config);
            typeof(Plugin).GetProperty("Instance")!.SetValue(null, plugin);
            var paths = DispatchProxy.Create<IApplicationPaths, OriginalLanguageFlowTests.CallProxy>();
            ((OriginalLanguageFlowTests.CallProxy)(object)paths).Call = (method, _) => method.Name == "get_LogDirectoryPath" ? root.FullName : null;
            var library = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
            ((OriginalLanguageFlowTests.CallProxy)(object)library).Call = (_, _) => null;
            var db = new MediaDashDb(Path.Combine(root.FullName, "test.db"));
            var scanner = new TranscodeLogScanner(paths, library, NullLogger<TranscodeLogScanner>.Instance, db.History);
            var media = Path.Combine(root.FullName, "fixture.mkv");
            File.WriteAllText(media, "owned fixture");
            var fixedAt = DateTime.UtcNow.AddMinutes(-5);

            void WriteLog(string name, DateTime timestamp, bool failed)
            {
                var path = Path.Combine(root.FullName, $"FFmpeg.Transcode-{name}.log");
                File.WriteAllText(path, JsonSerializer.Serialize(new { Path = media }) + "\n\n"
                    + (failed ? "Conversion failed!" : "muxing overhead: 1.2%"));
                File.SetLastWriteTimeUtc(path, timestamp);
            }

            Task<System.Collections.Generic.IReadOnlyList<Issue>> Scan() => scanner.ScanAsync([], new Progress<double>(), CancellationToken.None);
            WriteLog("old", fixedAt.AddMinutes(-1), failed: true);
            Assert.Equal(IssueType.FailedTranscode, Assert.Single(await Scan()).Type);
            db.History.AddHistory(new HistoryEntry
            {
                Path = media, Type = IssueType.FailedTranscode, FixedAtUtc = fixedAt, Success = true, Action = "re-encoded"
            });
            Assert.Empty(await Scan());

            WriteLog("new", fixedAt.AddMinutes(1), failed: false);
            var finding = Assert.Single(await Scan());
            Assert.Equal(IssueType.HeavyTranscode, finding.Type);
            using var details = JsonDocument.Parse(finding.DetailsJson);
            Assert.Equal(1, details.RootElement.GetProperty("sessions").GetInt32());

            db.MarkRestored(Assert.Single(db.History.GetHistory()).Id);
            Assert.Equal(IssueType.FailedTranscode, Assert.Single(await Scan()).Type);
        }
        finally
        {
            typeof(Plugin).GetProperty("Instance")!.SetValue(null, previous);
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }
}
