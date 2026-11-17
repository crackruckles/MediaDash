using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>Compatibility boundary for immediate native membership validation without child metadata refresh.</summary>
internal static class NativeFolderIndex
{
    internal static async Task IndexOnlyAsync(Folder folder, NativeFolderDirectoryService directoryService, IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(folder.Path) || !Path.IsPathFullyQualified(folder.Path)
            || !string.Equals(Path.GetFullPath(folder.Path), Path.GetFullPath(directoryService.Root), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The complete directory listing does not belong to the native folder being indexed.");
        }

        var method = ResolveIndexMethod(typeof(Folder));
        var options = new MetadataRefreshOptions(directoryService.Native)
        {
            MetadataRefreshMode = MetadataRefreshMode.None,
            ImageRefreshMode = MetadataRefreshMode.None
        };
        Task work;
        try
        {
            // The public entry always enables child metadata, including unrelated metadata
            // containers. Retain native virtual dispatch, but explicitly disable both traversal
            // and child refresh. Callers must supply a complete, readable directory listing.
            work = method.Invoke(folder, [progress, false, false, false, options, directoryService.Native, cancellationToken]) as Task
                ?? throw new NotSupportedException("The native indexing call did not return its expected task.");
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }

        await work.ConfigureAwait(false);
    }

    internal static MethodInfo ResolveIndexMethod(Type folderType)
    {
        var method = typeof(Folder).IsAssignableFrom(folderType) ? folderType.GetMethod(
            "ValidateChildrenInternal",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [typeof(IProgress<double>), typeof(bool), typeof(bool), typeof(bool), typeof(MetadataRefreshOptions), typeof(IDirectoryService), typeof(CancellationToken)],
            null) : null;
        return method is not null && method.ReturnType == typeof(Task) ? method
            : throw new NotSupportedException("This Jellyfin version does not expose the supported indexing-only call. Run its library scan instead.");
    }
}
