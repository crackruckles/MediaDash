using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class NativeFolderIndexTests
{
    [Fact]
    public async Task IndexOnlyUsesTheCanonicalFolderAndNeverRefreshesChildMetadata()
    {
        var folder = new RecordingFolder();
        var directories = Directories();
        using var cancellation = new CancellationTokenSource();
        await NativeFolderIndex.IndexOnlyAsync(folder, directories, new Progress<double>(), cancellation.Token);
        Assert.Equal(1, folder.Calls);
        Assert.False(folder.Recursive);
        Assert.False(folder.RefreshChildren);
        Assert.False(folder.AllowRemoveRoot);
        Assert.Same(directories.Native, folder.Options!.DirectoryService);
        Assert.Equal(MetadataRefreshMode.None, folder.Options.MetadataRefreshMode);
        Assert.Equal(MetadataRefreshMode.None, folder.Options.ImageRefreshMode);
        Assert.False(folder.Options.ForceSave);
        Assert.Equal(cancellation.Token, folder.Token);
    }

    [Fact]
    public async Task CancellationBeforeIndexingDoesNotInvokeNativeValidation()
    {
        var folder = new RecordingFolder();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeFolderIndex.IndexOnlyAsync(folder,
            Directories(), new Progress<double>(), cancellation.Token));
        Assert.Equal(0, folder.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeReadErrorsRemainVisibleWithoutAReflectionWrapper(bool asynchronous)
    {
        var error = new IOException("owned native enumeration failed");
        var folder = new RecordingFolder { Error = error, Asynchronous = asynchronous };
        var actual = await Assert.ThrowsAsync<IOException>(() => NativeFolderIndex.IndexOnlyAsync(folder,
            Directories(), new Progress<double>(), CancellationToken.None));
        Assert.Same(error, actual);
        Assert.Equal(1, folder.Calls);
    }

    [Fact]
    public void UnsupportedNativeApiIsAnExplicitFailure()
    {
        var error = Assert.Throws<NotSupportedException>(() => NativeFolderIndex.ResolveIndexMethod(typeof(string)));
        Assert.Contains("indexing", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ACompleteListingForAnotherFolderCannotAuthorizeIndexing()
    {
        var folder = new RecordingFolder { Path = Path.Combine(Path.GetTempPath(), "different-native-folder") };
        var fs = DispatchProxy.Create<MediaBrowser.Model.IO.IFileSystem, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)fs).Call = (method, _) => method.Name == "GetFileSystemEntries" ? Array.Empty<MediaBrowser.Model.IO.FileSystemMetadata>() : null;
        var complete = NativeFolderDirectoryService.Create(Path.Combine(Path.GetTempPath(), "owned-native-index-listing"), fs);
        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeFolderIndex.IndexOnlyAsync(folder, complete, new Progress<double>(), CancellationToken.None));
        Assert.Equal(0, folder.Calls);
    }

    private sealed class RecordingFolder : Folder
    {
        public RecordingFolder() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "owned-native-index-listing");

        internal int Calls { get; private set; }
        internal bool Recursive { get; private set; }
        internal bool RefreshChildren { get; private set; }
        internal bool AllowRemoveRoot { get; private set; }
        internal MetadataRefreshOptions? Options { get; private set; }
        internal CancellationToken Token { get; private set; }
        internal IOException? Error { get; init; }
        internal bool Asynchronous { get; init; }

        protected override Task ValidateChildrenInternal(IProgress<double> progress, bool recursive, bool refreshChildMetadata, bool allowRemoveRoot,
            MetadataRefreshOptions refreshOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            Calls++;
            Recursive = recursive;
            RefreshChildren = refreshChildMetadata;
            AllowRemoveRoot = allowRemoveRoot;
            Options = refreshOptions;
            Token = cancellationToken;
            if (Error is null) return Task.CompletedTask;
            if (Asynchronous) return Task.FromException(Error);
            throw Error;
        }
    }

    private static NativeFolderDirectoryService Directories()
    {
        var fs = DispatchProxy.Create<IFileSystem, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)fs).Call = (method, _) => method.Name == "GetFileSystemEntries" ? Array.Empty<FileSystemMetadata>() : null;
        return NativeFolderDirectoryService.Create(Path.Combine(Path.GetTempPath(), "owned-native-index-listing"), fs);
    }
}
