using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Model.IO;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class NativeFolderDirectoryServiceTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "owned-native-index-listing");

    [Fact]
    public void CompleteListingKeepsUnrelatedNamesAndReusesTheNativeCache()
    {
        var file = new FileSystemMetadata { FullName = Path.Combine(_root, "unrelated.mkv"), Name = "unrelated.mkv", Exists = true };
        var directory = new FileSystemMetadata { FullName = Path.Combine(_root, "unrelated-folder"), Name = "unrelated-folder", Exists = true, IsDirectory = true };
        var reads = 0;
        var fs = FileSystem(path => { Assert.Equal(_root, path); reads++; return new[] { file, directory }; });
        var complete = NativeFolderDirectoryService.Create(_root, fs).Native;
        Assert.Equal(2, complete.GetFileSystemEntries(_root).Length);
        Assert.Same(file, Assert.Single(complete.GetFiles(_root)));
        Assert.Same(directory, Assert.Single(complete.GetDirectories(_root)));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void FailedPartialEnumerationCannotBecomeAnEmptyNativeListing()
    {
        var error = new IOException("owned network listing interrupted");
        IEnumerable<FileSystemMetadata> Partial()
        {
            yield return new FileSystemMetadata { FullName = Path.Combine(_root, "keep.mkv"), Name = "keep.mkv" };
            throw error;
        }

        var actual = Assert.Throws<InvalidOperationException>(() => NativeFolderDirectoryService.Create(_root, FileSystem(_ => Partial())));
        Assert.Same(error, actual.InnerException);
    }

    [Fact]
    public void DescendantReadFailureRemainsExplicitAfterTheRootWasRead()
    {
        var error = new UnauthorizedAccessException("owned denied child");
        var complete = NativeFolderDirectoryService.Create(_root, FileSystem(path => path == _root ? [] : throw error)).Native;
        var actual = Assert.Throws<InvalidOperationException>(() => complete.GetDirectories(Path.Combine(_root, "child")));
        Assert.Same(error, actual.InnerException);
    }

    private static IFileSystem FileSystem(Func<string, IEnumerable<FileSystemMetadata>> entries)
    {
        var fs = DispatchProxy.Create<IFileSystem, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)fs).Call = (method, args) => method.Name == "GetFileSystemEntries" ? entries((string)args![0]!) : null;
        return fs;
    }
}
