using System;
using System.IO;
using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class MediaFileHelperSharingTests
{
    [Fact]
    public void OpenSharedRead_AllowsConcurrentDelete()
    {
        // The reason for MediaFileHelper.OpenSharedRead to exist: a scanner/hasher mid-read must
        // NOT block a fixer that wants to move/delete the same file. Windows' default FileShare.Read
        // does block deletes, surfacing as ERROR_SHARING_VIOLATION 32 — that was the "process cannot
        // access the file" bug. This test would fail on the old File.OpenRead call.
        var path = Path.Combine(Path.GetTempPath(), "mediadash-sharing-test-" + Guid.NewGuid() + ".bin");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        try
        {
            using (var stream = MediaFileHelper.OpenSharedRead(path))
            {
                // Concurrent delete must not throw. On Windows this marks the file for delete-on-close;
                // on Linux the unlink returns immediately regardless of open handles.
                File.Delete(path);

                // The already-open stream must remain usable — Windows keeps the handle valid
                // through delete-on-close, and Linux keeps the inode alive until close.
                var b = new byte[4];
                var read = stream.Read(b, 0, 4);
                Assert.Equal(4, read);
                Assert.Equal(1, b[0]);
            }
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
