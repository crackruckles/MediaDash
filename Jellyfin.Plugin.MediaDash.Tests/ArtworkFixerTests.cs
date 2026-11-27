using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("Diagnostics")]
public sealed class ArtworkFixerTests : IDisposable
{
    private readonly Plugin? _previous = Plugin.Instance;
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-art-" + Guid.NewGuid().ToString("N"));

    public ArtworkFixerTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, _previous);
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    [Fact]
    public void DeleteArtwork_RemovesFile()
    {
        var tmp = Path.GetTempFileName() + ".jpg";
        File.WriteAllBytes(tmp, new byte[] { 0x00 });
        try
        {
            Assert.True(File.Exists(tmp));
            var result = ArtworkFixer.DeleteArtworkFile(tmp);
            Assert.True(result);
            Assert.False(File.Exists(tmp));
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    [Fact]
    public void DeleteArtwork_MissingFile_ReturnsFalse()
    {
        var reallyGone = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".jpg");
        Assert.False(ArtworkFixer.DeleteArtworkFile(reallyGone));
    }

    [Fact]
    public async Task FixAsync_ValidSvg_IsPreserved_NotDeleted()
    {
        // #88: a stale queued CorruptArtwork row pointing at a valid SVG must not delete it.
        // The fixer revalidates against the live scanner evaluator before any mutation.
        var fixer = Build(dryRun: false, out var metadataRoot);
        var svgPath = Path.Combine(metadataRoot, "logo.svg");
        File.WriteAllText(
            svgPath,
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\" fill=\"red\"/></svg>");

        var issue = new Issue { Type = IssueType.CorruptArtwork, Path = svgPath, ItemId = Guid.Empty, DetailsJson = "{}" };
        var result = await fixer.FixAsync(issue, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(File.Exists(svgPath));
    }

    [Fact]
    public async Task FixAsync_StaleFindingOnReplacedHealthyPng_IsPreserved()
    {
        // Audit check 6: scan flagged the file, user replaced it with a healthy PNG, then
        // the stale queued row ran. The old fixer deleted the healthy replacement; the new
        // fixer must revalidate and refuse.
        var fixer = Build(dryRun: false, out var metadataRoot);
        var path = Path.Combine(metadataRoot, "poster.png");
        File.WriteAllBytes(path, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII="));

        var issue = new Issue { Type = IssueType.CorruptArtwork, Path = path, ItemId = Guid.Empty, DetailsJson = "{}" };
        var result = await fixer.FixAsync(issue, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task FixAsync_GenuinelyEmptyFile_IsRecycled()
    {
        // Positive control: an actually-broken (zero-byte) raster still gets removed. The
        // revalidation gate must not block genuine corruption.
        var fixer = Build(dryRun: false, out var metadataRoot);
        var path = Path.Combine(metadataRoot, "broken.png");
        File.WriteAllBytes(path, Array.Empty<byte>());

        var issue = new Issue { Type = IssueType.CorruptArtwork, Path = path, ItemId = _ownerId, DetailsJson = "{}" };
        var result = await fixer.FixAsync(issue, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(result.RecyclePath));
    }

    [Fact]
    public async Task FixAsync_DryRun_LeavesFileAlone()
    {
        var fixer = Build(dryRun: true, out var metadataRoot);
        var path = Path.Combine(metadataRoot, "broken.png");
        File.WriteAllBytes(path, Array.Empty<byte>());

        var issue = new Issue { Type = IssueType.CorruptArtwork, Path = path, ItemId = _ownerId, DetailsJson = "{}" };
        var result = await fixer.FixAsync(issue, null, CancellationToken.None);

        Assert.True(result.WasDryRun);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task FixAsync_Cancelled_LeavesFileAlone()
    {
        var fixer = Build(dryRun: false, out var metadataRoot);
        var path = Path.Combine(metadataRoot, "broken.png");
        File.WriteAllBytes(path, Array.Empty<byte>());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixer.FixAsync(
            new Issue { Type = IssueType.CorruptArtwork, Path = path }, null, new CancellationToken(true)));
        Assert.True(File.Exists(path));
    }

    private ArtworkFixer Build(bool dryRun, out string metadataRoot)
    {
        metadataRoot = Path.Combine(_root, "metadata");
        Directory.CreateDirectory(metadataRoot);

        var config = new PluginConfiguration { DryRun = dryRun };
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, config);
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, plugin);

        var appPaths = System.Reflection.DispatchProxy.Create<IServerApplicationPaths, OriginalLanguageFlowTests.CallProxy>();
        var rootForClosure = metadataRoot;
        ((OriginalLanguageFlowTests.CallProxy)(object)appPaths).Call = (method, _) => method.Name switch
        {
            "get_InternalMetadataPath" => rootForClosure,
            "get_DataPath" => _root,
            _ => null
        };
        var monitor = System.Reflection.DispatchProxy.Create<ILibraryMonitor, OriginalLanguageFlowTests.CallProxy>();
        var library = System.Reflection.DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        var media = Path.Combine(_root, "media");
        Directory.CreateDirectory(media);
        var owner = new Video { Id = _ownerId, Path = Path.Combine(media, "movie.mkv"),
            ImageInfos = [new ItemImageInfo { Path = Path.Combine(metadataRoot, "broken.png"), Type = ImageType.Primary }] };
        ((OriginalLanguageFlowTests.CallProxy)(object)library).Call = (method, _) => method.Name switch
        {
            "GetVirtualFolders" => new System.Collections.Generic.List<VirtualFolderInfo> { new() { Locations = [media] } },
            "GetItemById" => owner,
            "UpdateItemAsync" => Task.CompletedTask,
            _ => null
        };
        return new ArtworkFixer(appPaths, monitor, library, NullLogger<ArtworkFixer>.Instance,
            new RecycleBin(appPaths, NullLogger<RecycleBin>.Instance, null!));
    }
}
