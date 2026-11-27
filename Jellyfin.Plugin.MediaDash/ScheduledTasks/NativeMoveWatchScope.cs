using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>Pairs native watcher suppression around only the known paths of a planned move.</summary>
internal sealed class NativeMoveWatchScope : IDisposable
{
    private readonly ILibraryMonitor _monitor;
    private readonly List<string> _begun = [];
    private bool _disposed;

    private NativeMoveWatchScope(ILibraryMonitor monitor) => _monitor = monitor;

    internal static NativeMoveWatchScope? Begin(ILibraryMonitor? monitor, IEnumerable<string> paths)
    {
        if (monitor is null)
        {
            return null;
        }

        var watched = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (!Path.IsPathFullyQualified(path))
            {
                throw new IOException("A planned watcher path is not absolute. No move may begin.");
            }

            watched.Add(Path.GetFullPath(path));
            var parent = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                watched.Add(parent);
                parent = Path.GetDirectoryName(parent);
            }
        }

        var scope = new NativeMoveWatchScope(monitor);
        try
        {
            foreach (var path in watched)
            {
                monitor.ReportFileSystemChangeBeginning(path);
                scope._begun.Add(path);
            }

            return scope;
        }
        catch (Exception error)
        {
            scope.Dispose();
            throw new IOException("Native watcher coordination failed before the move began.", error);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var path in _begun)
        {
            try
            {
                // Native Jellyfin retains its normal delayed suppression window. Explicit
                // bounded indexing owns notification; never request a recursive fallback.
                _monitor.ReportFileSystemChangeComplete(path, refreshPath: false);
            }
            catch (Exception error)
            {
                Api.Diagnostics.Record("Move.NativeWatcher", "Jellyfin may not have noticed the changes in '" + path + "' after a move. If it still shows the old location, run 'Scan Media Library' in the Jellyfin dashboard. Details: " + error.Message);
            }
        }
    }
}
