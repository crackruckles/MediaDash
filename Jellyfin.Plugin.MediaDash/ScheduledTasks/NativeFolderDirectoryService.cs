using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>Complete cached native listings whose read failures cannot be mistaken for absent children.</summary>
internal sealed class NativeFolderDirectoryService
{
    private NativeFolderDirectoryService(string root, IDirectoryService native)
    {
        Root = root;
        Native = native;
    }

    internal string Root { get; }

    internal IDirectoryService Native { get; }

    internal static NativeFolderDirectoryService Create(string root, IFileSystem fileSystem)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException("The native folder listing requires an absolute path.", nameof(root));
        }

        // GetFilePaths has different overloads on Jellyfin 10 and 12. Generate the
        // interface implementation from the running host, and forward its own methods.
        var native = DispatchProxy.Create<IDirectoryService, GuardedDirectoryProxy>();
        ((GuardedDirectoryProxy)native).Inner = new DirectoryService(fileSystem);
        // Materialize the entire native listing BEFORE allowing catalog validation. Never
        // substitute a changed-path subset: omitted children would look like deletions.
        _ = native.GetFileSystemEntries(root);
        return new NativeFolderDirectoryService(root, native);
    }

    private class GuardedDirectoryProxy : DispatchProxy
    {
        internal DirectoryService Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            try
            {
                return (targetMethod ?? throw new InvalidOperationException("The native directory method is unavailable.")).Invoke(Inner, args);
            }
            catch (TargetInvocationException error) when (error.InnerException is IOException or UnauthorizedAccessException or SecurityException)
            {
                // Native Folder can catch a failed read and continue with an empty listing.
                throw new InvalidOperationException("Folder contents could not be read completely. Indexing cannot assume that its children are missing.", error.InnerException);
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }
}
