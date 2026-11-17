using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Scanners;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("Diagnostics")]
public sealed class BugReportScanEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-scan-endpoint-" + Guid.NewGuid().ToString("N"));
    private readonly Plugin? _previous = Plugin.Instance;
    private readonly RunState? _previousState = Plugin.AttachedRunState;
    private readonly PluginConfiguration _config = new() { DryRun = true, AnalyticsEnabled = false };
    private readonly MediaDashDb _db;
    private readonly ILibraryManager _library;
    private readonly IServerApplicationPaths _paths;
    private readonly List<VirtualFolderInfo> _folders = [];
    private readonly MediaDashController _controller;

    public BugReportScanEndpointTests()
    {
        Directory.CreateDirectory(_root);
        _db = new MediaDashDb(Path.Combine(_root, "state.db"));
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, _config);
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, plugin);
        _library = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)_library).Call = (method, _) => method.Name switch
        {
            "GetVirtualFolders" => _folders,
            "GetItemList" => new List<BaseItem>(),
            _ => null
        };
        _paths = DispatchProxy.Create<IServerApplicationPaths, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)_paths).Call = (method, _) => method.Name == "get_InternalMetadataPath" ? Path.Combine(_root, "metadata") : _root;
        var host = DispatchProxy.Create<IServerApplicationHost, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)host).Call = (method, _) => method.Name == "get_ApplicationVersion" ? new Version(12, 0, 0) : null;
        var tasks = DispatchProxy.Create<ITaskManager, LibraryScanCancellationTests.ManagerProxy>();
        var protection = new ProtectionService(_db.Protection, _library);
        var scanner = new SuspiciousFileScanner(_library, NullLogger<SuspiciousFileScanner>.Instance, protection);
        _controller = new MediaDashController(_db, _db.Issues, _db.History, tasks, null!, null!, _library, host, [], [scanner], new PostUpgradeCleanup(_paths, _library, NullLogger<PostUpgradeCleanup>.Instance), new LibraryGuard(_library), new RunState(), null!, null!, protection)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
    }

    public void Dispose()
    {
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, _previous);
        Plugin.AttachedRunState = _previousState;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }

    private void PriorFinding(string path) => _db.ReplaceDetectedIssues(IssueType.MalwareRisk, [new Issue { Type = IssueType.MalwareRisk, Path = path, DetectedAtUtc = DateTime.UtcNow }]);

    [Fact]
    public async Task ScanSuspicious_UnreachableRoot_PreservesPriorDetectedRows()
    {
        var missingRoot = Path.Combine(_root, "offline-share");
        _folders.Add(new VirtualFolderInfo { Locations = [missingRoot] });
        var prior = Path.Combine(missingRoot, "prior.exe");
        PriorFinding(prior);
        Assert.IsType<OkObjectResult>((await _controller.ScanSuspicious()).Result);
        Assert.Equal(prior, Assert.Single(_db.GetIssues(IssueType.MalwareRisk)).Path);
    }

    [Fact]
    public async Task ScanSuspicious_FullyWalkedRoot_ClearsDeletedPriorFinding()
    {
        var media = Path.Combine(_root, "media");
        Directory.CreateDirectory(media);
        _folders.Add(new VirtualFolderInfo { Locations = [media] });
        PriorFinding(Path.Combine(media, "removed.exe"));
        Assert.IsType<OkObjectResult>((await _controller.ScanSuspicious()).Result);
        Assert.Empty(_db.GetIssues(IssueType.MalwareRisk));
    }

    [Fact]
    public async Task ScanSuspicious_InaccessibleDescendant_PreservesPriorFinding()
    {
        if (!OperatingSystem.IsWindows()) return;
        var media = Path.Combine(_root, "media");
        var denied = Directory.CreateDirectory(Path.Combine(media, "denied"));
        _folders.Add(new VirtualFolderInfo { Locations = [media] });
        var prior = Path.Combine(denied.FullName, "prior.exe");
        PriorFinding(prior);
        var original = denied.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var changed = denied.GetAccessControl();
        changed.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
        try
        {
            denied.SetAccessControl(changed);
            Assert.Throws<UnauthorizedAccessException>(() => Directory.EnumerateFiles(denied.FullName).ToArray());
            Assert.IsType<OkObjectResult>((await _controller.ScanSuspicious()).Result);
            Assert.Equal(prior, Assert.Single(_db.GetIssues(IssueType.MalwareRisk)).Path);
        }
        finally
        {
            var restore = new DirectorySecurity();
            restore.SetSecurityDescriptorSddlForm(original, AccessControlSections.Access);
            denied.SetAccessControl(restore);
        }
    }

    [Fact]
    public async Task PostUpgradeDryRun_PreservesCandidateAndCompletionFlag()
    {
        var orphan = Path.Combine(_root, "trickplay", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);
        var payload = Path.Combine(orphan, "tile.jpg");
        File.WriteAllText(payload, "owned image fixture");
        Assert.IsType<OkObjectResult>((await _controller.RunPostUpgradeCleanup()).Result);
        Assert.Equal("owned image fixture", File.ReadAllText(payload));
        Assert.False(_config.PostV12CleanupCompleted);
        var history = Assert.Single(_db.History.GetHistory());
        Assert.True(history.WasDryRun);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileBrowserDelete_RefusesRootAndParentContainingNestedRoot(bool parentOfNestedRoot)
    {
        _config.DryRun = false;
        var media = Path.Combine(_root, "media");
        var parent = Path.Combine(media, "parent");
        var nested = Path.Combine(parent, "nested");
        Directory.CreateDirectory(nested);
        _folders.Add(new VirtualFolderInfo { Locations = [media, nested] });
        var protection = new ProtectionService(_db.Protection, _library);
        var controller = new FileBrowserController(new LibraryGuard(_library), null!, null!, _library, _paths, _db.History, protection, NullLogger<FileBrowserController>.Instance);
        var result = Assert.IsType<ObjectResult>(controller.Delete(new DeleteRequest { Path = parentOfNestedRoot ? parent : media }));
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.True(Directory.Exists(nested));
        Assert.Empty(_db.History.GetHistory());
    }
}
