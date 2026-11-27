using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Scanners;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("MediaDashDb")]
public sealed class ScanTaskLibraryScanTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LibraryScanStartingDuringScannerPreservesResultsUnlessManual(bool manual, bool fail)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mediadash-scan-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previousState = Plugin.AttachedRunState;
        try
        {
            var db = new MediaDashDb(Path.Combine(directory, "scan.db"));
            db.SetState("ContentAwareDefaultsChecked", "true");
            var oldPath = Path.Combine(directory, "old.mkv");
            var newPath = Path.Combine(directory, "new.mkv");
            db.Issues.ReplaceDetectedIssues(IssueType.AudioLanguage, new[] { IssueAt(oldPath) }, null);
            var library = DispatchProxy.Create<ILibraryManager, IdleCheckTests.ProxyHandler>();
            ((IdleCheckTests.ProxyHandler)(object)library).Handler = method => method.Name switch
            {
                "GetItemList" => new List<BaseItem>(),
                "GetVirtualFolders" => new List<VirtualFolderInfo>(),
                _ => null
            };
            var manager = DispatchProxy.Create<ITaskManager, LibraryScanCancellationTests.ManagerProxy>();
            var handler = (LibraryScanCancellationTests.ManagerProxy)(object)manager;
            var jellyfinTask = DispatchProxy.Create<IScheduledTask, IdleCheckTests.ProxyHandler>();
            ((IdleCheckTests.ProxyHandler)(object)jellyfinTask).Handler = method => method.Name == "get_Key" ? "RefreshLibrary" : null;
            var worker = DispatchProxy.Create<IScheduledTaskWorker, IdleCheckTests.ProxyHandler>();
            ((IdleCheckTests.ProxyHandler)(object)worker).Handler = method => method.Name switch
            {
                "get_ScheduledTask" => jellyfinTask,
                "get_State" => TaskState.Running,
                _ => null
            };
            var scanner = new InterruptingScanner(() => handler.Executing?.Invoke(manager, new GenericEventArgs<IScheduledTaskWorker>(worker)), IssueAt(newPath), fail);
            var state = new RunState();
            if (manual)
            {
                handler.Workers = new[] { worker };
                state.ArmBypassIdleCheck(RunRole.Scan);
            }

            var config = new PluginConfigurationProvider(() => new PluginConfiguration { PauseDuringPlayback = false });
            var logger = new PhaseLogger();
            var task = new ScanTask(library, new[] { scanner }, db, null!, manager, state, config, new ProtectionService(db.Protection, library), logger);
            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
            // Jellyfin's library scan now waits for MediaDash: it is paused, and MediaDash's results stand.
            Assert.Equal(!fail ? newPath : oldPath, Assert.Single(db.Issues.GetIssues(IssueType.AudioLanguage)).Path);
            Assert.NotNull(db.GetState("LastScanCompletedUtc"));
            Assert.Contains(worker, handler.Cancelled);
            Assert.False(state.ReadSnapshot(RunRole.Scan).IsRunning);
            Assert.Null(handler.Executing);
            var materialize = Assert.Single(logger.Phases, phase => (string)phase["Phase"]! == "ItemMaterialization");
            Assert.Equal(0, materialize["ItemCount"]);
            var scanned = Assert.Single(logger.Phases, phase => (string)phase["Phase"]! == "InterruptingScanner");
            Assert.Equal(fail ? "Failed" : "Completed", scanned["Outcome"]);
            Assert.True((double)scanned["ElapsedMilliseconds"]! >= 0);
        }
        finally
        {
            Plugin.AttachedRunState = previousState;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Issue IssueAt(string path) => new() { Type = IssueType.AudioLanguage, Path = path, DetailsJson = "{}", DetectedAtUtc = DateTime.UtcNow };

    private sealed class PhaseLogger : ILogger<ScanTask>
    {
        public List<Dictionary<string, object?>> Phases { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values && values.Any(value => value.Key == "Phase"))
                Phases.Add(values.ToDictionary(value => value.Key, value => value.Value));
        }
    }

    private sealed class InterruptingScanner(Action interrupt, Issue issue, bool fail) : IScanner
    {
        public IssueType Type => IssueType.AudioLanguage;

        public Task<IReadOnlyList<Issue>> ScanAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken cancellationToken)
        {
            interrupt();
            if (fail) throw new IOException("fixture scanner failure");
            // A scanner may return partial results without observing cancellation itself.
            return Task.FromResult<IReadOnlyList<Issue>>(new[] { issue });
        }
    }
}
