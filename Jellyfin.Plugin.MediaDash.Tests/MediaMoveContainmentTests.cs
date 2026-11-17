using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Task-1 containment regressions for the mover fixers. These exercise the five dangerous
/// failure modes the handoff calls out explicitly:
///
/// 1. <c>PromotionFailurePreservesSourceOrVerifiedRecovery</c> — the sorter's historical
///    <c>File.Move → File.Move</c> staging path deleted its own staging copy on promotion
///    failure, destroying the sole surviving copy. The fixer must route cross-volume moves
///    through <see cref="MediaMoveSafety.CrossVolumeMove"/> and leave the source intact when
///    the publish seam fails.
/// 2. <c>CancellationBeforeMutationLeavesAllPathsUntouched</c> — a cancelled fix must observe
///    the cancellation BEFORE any filesystem mutation.
/// 3. <c>ProtectedDescendantBlocksQueuedFolderMove</c> — a folder move is refused when any
///    descendant is on the Never-modify list.
/// 4. <c>DisabledDestinationIsRefused</c> — the destination must sit under a currently-enabled
///    library root; a library the user disabled between scan and fix must not receive content.
/// 5. <c>DetailsSourceMustMatchApprovedPath</c> — the fixer binds to the exact scanned source
///    and refuses when issue.Path has drifted (e.g. a sibling operation ran
///    RelocateIssuePaths).
/// </summary>
[Collection("Diagnostics")]
public sealed class MediaMoveContainmentTests : IDisposable
{
    private readonly Plugin? _previous = Plugin.Instance;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-mover-containment-" + Guid.NewGuid().ToString("N"));
    private readonly PluginConfiguration _config = new() { DryRun = false, UngroupedFixMode = FixMode.ManualApprove, MisplacedFixMode = FixMode.ManualApprove };
    private readonly List<MediaBrowser.Controller.Entities.BaseItem> _items = [];
    private readonly MediaDashDb _db;
    private readonly LibraryGuard _guard;
    private readonly ILibraryMonitor _monitor;
    private readonly ILibraryManager _manager;
    private bool _rejectUntypedCatalog;
    private bool _failCatalog;

    public MediaMoveContainmentTests()
    {
        Directory.CreateDirectory(_root);
        _db = new MediaDashDb(Path.Combine(_root, "test.db"));
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, _config);
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, plugin);
        var manager = System.Reflection.DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)manager).Call = (method, args) => method.Name switch
        {
            "GetVirtualFolders" => new List<VirtualFolderInfo> { new() { Locations = [_root] } },
            "GetItemList" => _failCatalog || (_rejectUntypedCatalog && args![0] is MediaBrowser.Controller.Entities.InternalItemsQuery query && query.IncludeItemTypes.Length == 0)
                ? throw new InvalidOperationException("Cannot deserialize unknown native placeholder type.") : _items,
            "GetItemById" => _items.Find(item => args![0] is Guid id && item.Id == id),
            _ => null
        };
        _guard = new LibraryGuard(manager);
        _manager = manager;
        _monitor = System.Reflection.DispatchProxy.Create<ILibraryMonitor, OriginalLanguageFlowTests.CallProxy>();
    }

    public void Dispose()
    {
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, _previous);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private MediaMoveService NewService(LibraryGuard? guard = null, ProtectionService? protection = null, ILibraryManager? manager = null) =>
        new(_db, guard ?? _guard, protection ?? new ProtectionService(_db.Protection, manager ?? _manager), manager ?? _manager, NullLogger<MediaMoveService>.Instance);

    private MediaSorterFixer NewSorter() => new(NewService());
    private MediaGrouperFixer NewGrouper(ProtectionService? protection = null) => new(NewService(protection: protection));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoEnabledLibraryMatchRefusesBeforeMutation(bool group)
    {
        _config.EnabledLibraries = ["deleted-library"];
        var source = Write("source.mkv", "payload");
        var target = Path.Combine(_root, "target", "source.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var result = group
            ? await NewGrouper().FixAsync(UngroupedIssue(source, target, "MoveFile"), null, default)
            : await NewSorter().FixAsync(MisplacedIssue(source, target), null, default);
        Assert.Equal(FixOutcome.Refused, result.Outcome);
        Assert.Equal(FixReason.OutsideLibrary, result.Reason);
        Assert.Equal("payload", File.ReadAllText(source));
        Assert.False(File.Exists(target));
    }

    private Issue MisplacedIssue(string sourcePath, string targetPath, string? approvedSource = null) =>
        MediaMoveTestApproval.Create(_db, _config, _items, IssueType.Misplaced, sourcePath, targetPath, approvedSource);

    private Issue UngroupedIssue(string sourcePath, string target, string action = "MoveFolder", string? detailsSource = null) =>
        MediaMoveTestApproval.Create(_db, _config, _items, IssueType.Ungrouped, sourcePath, target, detailsSource, action);

    [Theory]
    [InlineData("dismiss")]
    [InlineData("disable")]
    [InlineData("protect")]
    [InlineData("cancel")]
    public void CurrentConsentAndPolicyAreRecheckedBeforeAtomicRename(string change)
    {
        var source = Write("source.mkv", "original");
        var target = Path.Combine(_root, "target.mkv");
        var issue = UngroupedIssue(source, target, "MoveFile");
        _db.Issues.ReplaceDetectedIssues(issue.Type, new[] { issue });
        _db.Issues.QueueDetectedIssues(issue.Type);
        issue = Assert.Single(_db.Issues.GetIssues(issue.Type));
        var protection = new ProtectionService(_db.Protection, libraryManager: null);
        using var cts = new CancellationTokenSource();
        var error = Record.Exception(() => MediaMoveSafety.CrossVolumeMove(source, target, false, (step, _) =>
        {
            if (step == "BeforeNativeRename")
            {
                if (change == "dismiss") _db.TryTransitionIssueStatus(issue.Id, new[] { IssueStatus.Queued }, IssueStatus.Dismissed);
                if (change == "disable") _config.FixerDisabled = ["Ungrouped"];
                if (change == "protect")
                {
                    _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = source, ScannersMask = "*", FixersMask = "*" });
                    protection.Invalidate();
                }
                if (change == "cancel") cts.Cancel();
            }
            MediaMoveSafety.Revalidate(issue, _guard, protection, _db.Issues, source, target, cts.Token);
        }, cts.Token));
        if (change == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsAssignableFrom<IOException>(error);
        Assert.Equal("original", File.ReadAllText(source));
        Assert.False(File.Exists(target));
    }

    private Api.FileBrowserController NewBrowser(LibraryGuard guard, ILibraryManager manager, MediaMoveService? moves = null)
    {
        var paths = DispatchProxy.Create<MediaBrowser.Common.Configuration.IApplicationPaths, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)paths).Call = (method, _) => method.Name == "get_DataPath" ? _root : null;
        return new Api.FileBrowserController(guard, new RecycleBin(paths, NullLogger<RecycleBin>.Instance, _db.History),
            _monitor, manager, paths, _db.History, new ProtectionService(_db.Protection, manager), NullLogger<Api.FileBrowserController>.Instance, new MediaMovePlanner(guard, new ProtectionService(_db.Protection, manager), manager, _db.Moves), moves ?? NewService(guard: guard, manager: manager));
    }

    [Fact]
    public async Task BrowserDoesNotRequeueBroadNotificationsAfterBoundedMoveEvenWhenIndexingIsPending()
    {
        var source = Write("Selected.opaque", "original");
        var target = Path.Combine(_root, "target", "Selected.opaque");
        var broad = new List<string>();
        ((OriginalLanguageFlowTests.CallProxy)(object)_monitor).Call = (method, args) =>
        {
            if (method.Name == "ReportFileSystemChanged") broad.Add((string)args![0]!);
            return null;
        };
        var files = DispatchProxy.Create<MediaBrowser.Model.IO.IFileSystem, OriginalLanguageFlowTests.CallProxy>();
        using var native = new ScheduledTasks.NativeLibraryRefresher(_manager, files, (_, _, _) => Task.CompletedTask);
        var moves = new MediaMoveService(_db, _guard, new ProtectionService(_db.Protection, _manager), _manager,
            NullLogger<MediaMoveService>.Instance, nativeRefresh: native, libraryMonitor: _monitor);
        var browser = NewBrowser(_guard, _manager, moves);
        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await browser.Move(new Api.MoveOrCopyRequest { From = source, To = target, PreviewOnly = true }));
        var preview = Assert.IsType<Api.FileMovePreviewDto>(response.Value);
        response = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await browser.Move(new Api.MoveOrCopyRequest { From = source, To = target, PlanFingerprint = preview.Fingerprint }));
        var result = Assert.IsType<FixResult>(response.Value);
        Assert.True(result.Success, result.Message);
        Assert.Equal("original", File.ReadAllText(target));
        Assert.Contains("index", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(broad);
    }

    [Fact]
    public async Task BrowserSameVolumeMoveHonoursProtection()
    {
        var source = Write("protected.mkv", "protected");
        var target = Path.Combine(_root, "target.mkv");
        _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = source, ScannersMask = "*", FixersMask = "*" });
        var result = await NewBrowser(_guard, _manager).Move(new Api.MoveOrCopyRequest { From = source, To = target });
        Assert.IsNotType<Microsoft.AspNetCore.Mvc.NoContentResult>(result);
        Assert.Equal("protected", File.ReadAllText(source));
        Assert.False(File.Exists(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowserPreviewHandlesNativePlaceholderAndCatalogFailureWithoutMutation(bool catalogFailure)
    {
        var source = Write("Film.mkv", "video");
        var subtitles = Write("Film.fr.srt", "subtitles");
        _items.Add(new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Name = "Film", Path = source });
        _rejectUntypedCatalog = true;
        _failCatalog = catalogFailure;
        var target = Path.Combine(_root, "target", "Film.mkv");
        var result = await NewBrowser(_guard, _manager).Move(new Api.MoveOrCopyRequest { From = source, To = target, PreviewOnly = true });
        if (catalogFailure)
        {
            Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(result);
        }
        else
        {
            var response = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
            Assert.Equal(2, Assert.IsType<Api.FileMovePreviewDto>(response.Value).Paths.Count);
        }

        Assert.Equal("video", File.ReadAllText(source));
        Assert.Equal("subtitles", File.ReadAllText(subtitles));
        Assert.False(File.Exists(target));
        Assert.Empty(_db.Moves.GetVisible());
    }

    [Fact]
    public async Task BrowserMovePreviewShowsCompanionsWithoutChangingAnyFiles()
    {
        var source = Write("Film.mkv", "video");
        _items.Add(new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Name = "Film", Path = source });
        var subtitles = Write("Film.fr.srt", "subtitles");
        var target = Path.Combine(_root, "target", "Film.mkv");
        var result = await NewBrowser(_guard, _manager).Move(new Api.MoveOrCopyRequest { From = source, To = target, PreviewOnly = true });
        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
        var preview = Assert.IsType<Api.FileMovePreviewDto>(response.Value);
        Assert.Contains(preview.Paths, path => path.From == subtitles && path.To == Path.ChangeExtension(target, ".fr.srt"));
        Assert.Equal("video", File.ReadAllText(source));
        Assert.Equal("subtitles", File.ReadAllText(subtitles));
        Assert.False(Directory.Exists(Path.GetDirectoryName(target)));
        Assert.Empty(_db.Moves.GetVisible());
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("history-failure")]
    [InlineData("notify-failure")]
    [InlineData("dry-run")]
    public async Task BrowserMoveRequiresCurrentPreviewAndKeepsCompletionVisibleWhenBookkeepingFails(string mode)
    {
        _config.DryRun = mode == "dry-run";
        var source = Write("Selected.opaque", "original");
        var target = Path.Combine(_root, "target", "Selected.opaque");
        var browser = NewBrowser(_guard, _manager);
        var held = Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(await browser.Move(new Api.MoveOrCopyRequest { From = source, To = target }));
        var preview = Assert.IsType<Api.FileMovePreviewDto>(held.Value);
        Assert.Empty(_db.Moves.GetVisible());
        File.WriteAllText(source, "new bytes");
        var stale = Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(await browser.Move(new Api.MoveOrCopyRequest { From = source, To = target, PlanFingerprint = preview.Fingerprint }));
        preview = Assert.IsType<Api.FileMovePreviewDto>(stale.Value);
        Assert.Empty(_db.Moves.GetVisible());
        if (mode == "history-failure")
        {
            using var connection = _db.ConnectionFactory.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_manual_history BEFORE INSERT ON history BEGIN SELECT RAISE(ABORT,'controlled manual History failure'); END";
            command.ExecuteNonQuery();
        }
        if (mode == "notify-failure") ((OriginalLanguageFlowTests.CallProxy)(object)_monitor).Call = (_, _) => throw new IOException("controlled native notification failure");
        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await browser.Move(new Api.MoveOrCopyRequest { From = source, To = target, PlanFingerprint = preview.Fingerprint }));
        var result = Assert.IsType<FixResult>(response.Value);
        Assert.True(result.Success, result.Message);
        if (mode == "dry-run")
        {
            Assert.True(result.WasDryRun);
            Assert.Empty(_db.Moves.GetVisible());
            Assert.Empty(_db.History.GetHistory());
            Assert.Equal("new bytes", File.ReadAllText(source));
            Assert.False(File.Exists(target));
            return;
        }

        Assert.Equal("new bytes", File.ReadAllText(target));
        Assert.False(File.Exists(source));
        var operation = Assert.Single(_db.Moves.GetVisible());
        Assert.Equal(result.MoveOperationId, operation.Id);
        Assert.Equal("Completed", operation.State);
        if (mode != "history-failure") Assert.Equal(operation.Id, Assert.Single(_db.History.GetHistory()).MoveOperationId);
        else Assert.Empty(_db.History.GetHistory());
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("move")]
    [InlineData("copy")]
    [InlineData("delete")]
    [InlineData("mkdir")]
    [InlineData("upload")]
    public async Task BrowserWritesRefuseReservedMovePaths(string action)
    {
        var source = Write("reserved/film.mkv", "original");
        var target = Path.Combine(_root, "target.mkv");
        var plan = new MediaMovePlan { Type = IssueType.Ungrouped, ItemId = Guid.NewGuid(),
            Entries = [new(Path.GetDirectoryName(source)!, Path.Combine(_root, "destination"), true,
                MediaMoveSnapshot.Capture(Path.GetDirectoryName(source)!).Export())] }.Seal();
        Assert.NotNull(_db.Moves.TryBegin(plan));
        var browser = NewBrowser(new LibraryGuard(_manager, moves: _db.Moves), _manager);
        browser.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };
        browser.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("uploaded payload"));
        Microsoft.AspNetCore.Mvc.ActionResult result = action switch
        {
            "rename" => await browser.Rename(new Api.RenameRequest { Path = source, NewName = "renamed.mkv" }),
            "move" => await browser.Move(new Api.MoveOrCopyRequest { From = source, To = target }),
            "copy" => browser.Copy(new Api.MoveOrCopyRequest { From = source, To = target }),
            "delete" => browser.Delete(new Api.DeleteRequest { Path = source }),
            "mkdir" => browser.Mkdir(new Api.MkdirRequest { Path = Path.GetDirectoryName(source)!, Name = "new-folder" }),
            _ => await browser.Upload(Path.GetDirectoryName(source)!, "uploaded.mkv")
        };
        Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(result);
        Assert.Equal("original", File.ReadAllText(source));
        Assert.False(File.Exists(target));
        Assert.Single(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(source)!));
    }

    [Theory]
    [Trait("Category", "ReportedIssueExternal")]
    [InlineData("sort")]
    [InlineData("group")]
    [InlineData("browser")]
    public async Task ActualCrossVolumeDispatchPreservesPayloads(string entryPoint)
    {
        var parent = Environment.GetEnvironmentVariable("MEDIADASH_TEST_MOVE_TARGET_ROOT") ?? "";
        Assert.True(Path.IsPathFullyQualified(parent) && Directory.Exists(parent), "Set MEDIADASH_TEST_MOVE_TARGET_ROOT to an owned writable directory on another filesystem.");
        var targetDirectory = Path.Combine(parent, "md-cross-move-" + Guid.NewGuid().ToString("N"));
        Assert.True(LibraryGuard.IsUnder(Path.GetFullPath(targetDirectory), parent));
        Directory.CreateDirectory(targetDirectory);
        try
        {
            var source = Write("Cross.mkv", "primary payload");
            var companion = Write("Cross.en.srt", "subtitle payload");
            var target = Path.Combine(targetDirectory, "Cross.mkv");
            Assert.True(MediaMoveSafety.IsCrossVolume(source, target), "Fixture roots must reside on different actual volumes.");
            var manager = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
            ((OriginalLanguageFlowTests.CallProxy)(object)manager).Call = (method, args) => method.Name switch
            {
                "GetVirtualFolders" => new List<VirtualFolderInfo> { new() { Locations = [_root, targetDirectory] } },
                "GetItemList" => _items,
            "GetItemById" => _items.Find(item => args![0] is Guid id && item.Id == id),
                _ => null
            };
            var guard = new LibraryGuard(manager);
            var protection = new ProtectionService(_db.Protection, manager);
            if (entryPoint == "browser")
            {
                var browser = NewBrowser(guard, manager);
                var previewResponse = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await browser.Move(new Api.MoveOrCopyRequest { From = source, To = target, PreviewOnly = true }));
                var preview = Assert.IsType<Api.FileMovePreviewDto>(previewResponse.Value);
                var response = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await browser.Move(new Api.MoveOrCopyRequest { From = source, To = target, PlanFingerprint = preview.Fingerprint }));
                Assert.True(Assert.IsType<FixResult>(response.Value).Success);
                Assert.False(File.Exists(companion));
                Assert.Equal("subtitle payload", File.ReadAllText(Path.Combine(targetDirectory, "Cross.en.srt")));
            }
            else
            {
                var result = entryPoint == "group"
                    ? await new MediaGrouperFixer(NewService(guard, protection, manager)).FixAsync(UngroupedIssue(source, target, "MoveFile"), null, default)
                    : await new MediaSorterFixer(NewService(guard, protection, manager)).FixAsync(MisplacedIssue(source, target), null, default);
                Assert.True(result.Success, result.Message);
                Assert.Equal(0, result.BytesFreed);
                Assert.False(File.Exists(companion));
                Assert.Equal("subtitle payload", File.ReadAllText(Path.Combine(targetDirectory, "Cross.en.srt")));
            }
            Assert.False(File.Exists(source));
            Assert.Equal("primary payload", File.ReadAllText(target));
            Assert.Empty(Directory.GetFiles(targetDirectory, "*.mediadash-*-*"));
        }
        finally { Directory.Delete(targetDirectory, true); }
    }

    [Fact]
    public async Task CancellationBeforeMutationLeavesAllPathsUntouched_sorter()
    {
        var source = Write("cancelled.mkv", "payload");
        var target = Path.Combine(_root, "Movies", "cancelled.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            NewSorter().FixAsync(MisplacedIssue(source, target), null, cts.Token));

        Assert.True(File.Exists(source));
        Assert.False(File.Exists(target));
        Assert.Equal("payload", File.ReadAllText(source));
    }

    [Fact]
    public async Task CancellationBeforeMutationLeavesAllPathsUntouched_grouper()
    {
        var source = Write("Movie.mkv", "payload");
        var target = Path.Combine(_root, "Movie", "Movie.mkv");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            NewGrouper().FixAsync(UngroupedIssue(source, target, action: "MoveFile"), null, cts.Token));

        Assert.True(File.Exists(source));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task ProtectedDescendantBlocksQueuedFolderMove()
    {
        Write(Path.Combine("Show", "Season 01", "E01.mkv"), "ep");
        var protectedChild = Write(Path.Combine("Show", "Season 02", "E02.mkv"), "protected");

        _db.Protection.Add(new ProtectionRule
        {
            Kind = "folder",
            Target = protectedChild,
            ScannersMask = "*",
            FixersMask = "*"
        });
        var protection = new ProtectionService(_db.Protection, libraryManager: null);

        var source = Path.Combine(_root, "Show");
        var target = Path.Combine(_root, "Grouped", "Show");
        var result = await NewGrouper(protection).FixAsync(UngroupedIssue(source, target, action: "MoveFolder"), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(FixOutcome.Refused, result.Outcome);
        Assert.True(Directory.Exists(source), "protected-descendant refusal must leave the source folder untouched");
        Assert.True(File.Exists(protectedChild));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task DisabledDestinationIsRefused_sorter()
    {
        // Flip to a different library layout: only _root/enabled is enabled, _root/disabled still
        // sits inside the "any library" set (via _guard being configured with _root alone) but we
        // simulate a user-disabled library by narrowing EnabledLibraries. The helper
        // GetEnabledLibraryLocations returns [] when EnabledLibraries filters out every virtual
        // folder — exercising the "disabled destination" path.
        var manager = System.Reflection.DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)manager).Call = (method, args) => method.Name switch
        {
            "GetVirtualFolders" => new List<VirtualFolderInfo>
                {
                    new() { ItemId = "enabled-id", Locations = [Path.Combine(_root, "enabled")] },
                    new() { ItemId = "disabled-id", Locations = [Path.Combine(_root, "disabled")] }
                },
            // GetId prefers ItemId when non-empty (both entries above set it), so BuildIdLookup
            // is never consulted for this fixture — but LibraryGuard calls it eagerly. Return an
            // empty list so the lookup build short-circuits without touching the real library DB.
            "GetItemList" => (object)Array.Empty<MediaBrowser.Controller.Entities.BaseItem>(),
            "GetItemById" => _items.Find(item => args![0] is Guid id && item.Id == id),
            _ => null
        };
        var guard = new LibraryGuard(manager);
        _config.EnabledLibraries = ["enabled-id"];
        Directory.CreateDirectory(Path.Combine(_root, "enabled"));
        Directory.CreateDirectory(Path.Combine(_root, "disabled", "Movies"));

        var source = Write(Path.Combine("enabled", "wayward.mkv"), "payload");
        var target = Path.Combine(_root, "disabled", "Movies", "wayward.mkv");
        var fixer = new MediaSorterFixer(NewService(guard, manager: manager));

        var result = await fixer.FixAsync(MisplacedIssue(source, target), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(FixOutcome.Refused, result.Outcome);
        Assert.Equal(FixReason.OutsideLibrary, result.Reason);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task DetailsSourceMustMatchApprovedPath_grouper()
    {
        var scanned = Write("Original.mkv", "approved");
        var relocated = Write("Different.mkv", "replacement");
        var target = Path.Combine(_root, "Grouped", "Different.mkv");

        // Simulate the mid-queue hazard: the Issue.Path got rewritten (via RelocateIssuePaths or
        // a sibling operation) between approval and run, but DetailsJson still points at the
        // originally approved source. Acting on the recorded source would move the wrong file.
        var issue = UngroupedIssue(relocated, target, action: "MoveFile", detailsSource: scanned);

        var result = await NewGrouper().FixAsync(issue, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(FixOutcome.Refused, result.Outcome);
        Assert.Equal(FixReason.SourceChanged, result.Reason);
        Assert.True(File.Exists(scanned));
        Assert.True(File.Exists(relocated));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task DetailsSourceMustMatchApprovedPath_sorter()
    {
        var scanned = Write("Original.mkv", "approved");
        var relocated = Write("Different.mkv", "replacement");
        var target = Path.Combine(_root, "Movies", "Original.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // issue.Path has drifted from the originally scanned path; approvedSource still records it.
        var issue = MisplacedIssue(relocated, target, approvedSource: scanned);

        var result = await NewSorter().FixAsync(issue, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(FixOutcome.Refused, result.Outcome);
        Assert.Equal(FixReason.SourceChanged, result.Reason);
        Assert.True(File.Exists(scanned));
        Assert.True(File.Exists(relocated));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task UnknownActionIsRefused_grouper()
    {
        var source = Write("Weird.mkv", "payload");
        var target = Path.Combine(_root, "Elsewhere", "Weird.mkv");
        var issue = UngroupedIssue(source, target, action: "LaunchMissiles");

        var result = await NewGrouper().FixAsync(issue, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(FixOutcome.Refused, result.Outcome);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task LegacyIssueWithoutApprovedSourceIsRefused_sorter()
    {
        var source = Write("Legacy.mkv", "payload");
        var target = Path.Combine(_root, "Movies", "Legacy.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var legacy = new Issue
        {
            Type = IssueType.Misplaced,
            Path = source,
            // No approvedSource field — this is what a pre-containment scan would have emitted.
            DetailsJson = JsonSerializer.Serialize(new { targetPath = target })
        };

        var result = await NewSorter().FixAsync(legacy, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(FixOutcome.Refused, result.Outcome);
        Assert.Equal(FixReason.SourceChanged, result.Reason);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(target));
    }
}
