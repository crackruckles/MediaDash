using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Plugin-managed trash folder: removed files are held here until retention expires so mistakes are recoverable.
/// </summary>
public sealed class RecycleBin
{
    /// <summary>Marker written into every newly-created MediaDash recycle batch.</summary>
    internal const string OwnershipMarkerFileName = ".mediadash-owned-v1";

    // Per-batch sidecar that remembers where each recycled file came from. UTF-8, one absolute path
    // per line, one line per file in the batch — the order matches Directory.EnumerateFiles's listing.
    // Restore looks the original path up here so every bin file can be put back exactly where it was,
    // regardless of whether a HistoryEntry row exists. Missing (pre-manifest batches) or unreadable
    // manifests degrade gracefully to the previous "no history" state.
    internal const string OriginManifestFileName = ".mediadash-origin";

    // OS-reserved roots the recycle bin must not sit under — an admin who accidentally (or
    // maliciously) sets RecycleBinPath = "/etc" would otherwise land recycled files at
    // /etc/<timestamp>/<original-name>. Refusing these here is defense in depth; the setting is
    // admin-only, but restored config XML from an untrusted source is a real threat model.
    private static readonly string[] LinuxReservedRoots = ["/etc", "/bin", "/sbin", "/usr", "/boot", "/lib", "/lib64", "/proc", "/sys", "/dev", "/root"];

    private readonly string _defaultRoot;
    private readonly string _metadataRoot;
    private readonly MountedRecycleRoots _mountedRoots;
    private readonly ILibraryManager? _libraryManager;
    private readonly ILogger<RecycleBin> _logger;
    private readonly Lazy<bool> _legacyAdopted;
    private int _emptyingTotal;
    private int _emptyingDone;
    private int _emptyingGate; // 0 = idle, 1 = running (CompareExchange gate)
    private volatile string? _lastEmptyError;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecycleBin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="history">History-table store (05-C.3), queried for the authoritative set of recycle paths (adoption + discovery).</param>
    /// <param name="libraryManager">Library locations used if the mount root is not writable.</param>
    public RecycleBin(IApplicationPaths applicationPaths, ILogger<RecycleBin> logger, HistoryStore history, ILibraryManager? libraryManager = null)
    {
        _defaultRoot = Path.Combine(applicationPaths.DataPath, "mediadash", "recycle");
        _metadataRoot = Path.Combine(applicationPaths.DataPath, "mediadash", "metadata-recycle");
        _mountedRoots = new MountedRecycleRoots(Path.Combine(applicationPaths.DataPath, "mediadash", "mounted-recycle-roots"));
        _libraryManager = libraryManager;
        _logger = logger;
        // Deferred: plugin startup shouldn't block on a filesystem walk + marker writes when the
        // recycle root sits on a slow or unavailable network share. The migration is idempotent
        // and cheap on the default path (early-returns before any I/O), so paying it on the first
        // real bin operation instead of in the constructor is transparent to callers.
        _legacyAdopted = new Lazy<bool>(() =>
        {
            AdoptLegacyCustomBatches(history);
            return true;
        });
    }

    private string Root
    {
        get
        {
            var configured = Plugin.Instance!.Configuration.RecycleBinPath;
            if (string.IsNullOrWhiteSpace(configured))
            {
                return _defaultRoot;
            }

            if (IsManagedMetadataBinPath(configured) || MountedRecycleRoots.Contains(configured))
            {
                _logger.LogWarning("An automatically managed recycle folder cannot be configured as the media recycle bin.");
                return _defaultRoot;
            }

            if (IsSystemReservedPath(configured))
            {
                _logger.LogWarning("Configured RecycleBinPath '{Path}' sits under an OS-reserved root; falling back to the default location.", configured);
                Api.Diagnostics.Record("RecycleBin.ReservedPath", "The recycle bin folder set in Settings ('" + configured + "') is inside a system folder, so MediaDash is using its default location instead. Choose a folder on your media drive in Settings → Recycle bin; next to your library folder is best, because moves there are instant.");
                return _defaultRoot;
            }

            // Everything else is accepted. Users typically put the bin as a sibling of their media
            // (e.g. `/mnt/media/.mediadash-recycle` next to `/mnt/media/library-folder/`) so recycles
            // are same-filesystem renames rather than cross-volume copies — that path is NOT under a
            // library root but IS the recommended layout. The OS-reserved gate above is the only
            // hard block; anything else the admin chooses is honoured.
            return configured;
        }
    }

    internal static bool IsSystemReservedPath(string path)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (OperatingSystem.IsWindows())
            {
                var windir = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                var programFiles = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
                var programFilesX86 = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
                foreach (var reserved in new[] { windir, programFiles, programFilesX86 })
                {
                    if (!string.IsNullOrEmpty(reserved) && LibraryGuard.IsUnder(full, reserved))
                    {
                        return true;
                    }
                }

                return false;
            }

            foreach (var reserved in LinuxReservedRoots)
            {
                if (LibraryGuard.IsUnder(full, reserved))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Gets progress information for an in-flight <see cref="EmptyAll"/> run.</summary>
    /// <returns>Whether an empty is running, and how many top-level batches have been deleted out of the total.</returns>
    public (bool IsRunning, int Done, int Total, string? LastError) GetEmptyingProgress()
    {
        return (System.Threading.Volatile.Read(ref _emptyingGate) == 1, System.Threading.Volatile.Read(ref _emptyingDone), System.Threading.Volatile.Read(ref _emptyingTotal), _lastEmptyError);
    }

    /// <summary>
    /// Moves a file or directory into the recycle bin.
    /// </summary>
    /// <param name="path">The file or directory to recycle.</param>
    /// <returns>The item's location inside the bin.</returns>
    public string MoveToBin(string path)
    {
        try
        {
            return MoveIntoRoot(path, Root);
        }
        catch (IOException ex) when (ex.Message == MediaMoveSafety.UnsafeSourceDeletionMessage)
        {
            var localRoot = _mountedRoots.GetOrCreate(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), GetLibraryLocations);
            if (localRoot is null)
            {
                throw;
            }

            return MoveIntoRoot(path, localRoot);
        }
    }

    /// <summary>Checks that recycling can use a safe same-filesystem rename.</summary>
    /// <param name="path">Source to recycle.</param>
    /// <returns>True when a supported recycle root is available.</returns>
    public bool CanRecycle(string path)
    {
        try
        {
            if (!MediaMoveSafety.RequiresCopyDelete(path, Root))
            {
                return true;
            }

            var localRoot = _mountedRoots.GetOrCreate(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), GetLibraryLocations);
            return localRoot is not null && !MediaMoveSafety.RequiresCopyDelete(path, localRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private IEnumerable<string> GetLibraryLocations() =>
        _libraryManager?.GetVirtualFolders().SelectMany(folder => folder.Locations) ?? [];

    internal string MoveManagedMetadataToBin(string path)
    {
        var root = Root;
        try
        {
            return MoveIntoRoot(path, root);
        }
        catch (IOException ex) when (ex.Message == MediaMoveSafety.UnsafeSourceDeletionMessage
            && !PathsEqual(root, _metadataRoot) && !LibraryGuard.HasReparsePointAncestor(Path.GetFullPath(_metadataRoot)))
        {
            return MoveIntoRoot(path, _metadataRoot);
        }
    }

    [SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "The Files endpoint checks library containment and protection before calling MoveToBin. The destination root is the validated recycle setting or the plugin-owned metadata root.")]
    private string MoveIntoRoot(string path, string root)
    {
        MediaMoveSafety.RequireSameFilesystemMove(path, root);
        _ = _legacyAdopted.Value;
        // Timestamp alone collides when two files with the same basename are recycled inside the same
        // millisecond (e.g. concurrent Delete + fix run). Suffix with a short GUID so folder names stay
        // unique. ListContents sorts by folder-name descending, and the timestamp prefix still
        // controls ordering because the GUID is short and comes after.
        var folder = Path.Combine(
            root,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        // Presence is the signal; no reader consults the content.
        File.Create(Path.Combine(folder, OwnershipMarkerFileName)).Dispose();
        AppendOriginManifest(folder, path);

        // Trim trailing separator before GetFileName — otherwise a caller passing "/foo/bar/" reduces
        // target to `folder` itself, and Directory.Move(source, target) becomes "move to self" and throws.
        var basename = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (string.IsNullOrEmpty(basename))
        {
            throw new IOException("Cannot recycle '" + path + "': no basename to move into the bin.");
        }

        var target = Path.Combine(folder, basename);

        try
        {
            MoveAcrossVolumes(path, target, sourceIsDir: Directory.Exists(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (!LibraryGuard.HasReparsePointAncestor(Path.GetFullPath(folder)))
                {
                    File.Delete(Path.Combine(folder, OriginManifestFileName));
                    File.Delete(Path.Combine(folder, OwnershipMarkerFileName));
                    Directory.Delete(folder);
                }
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(cleanupError, "Could not clean up the unused recycle batch {Folder}.", folder);
            }

            throw;
        }

        _logger.LogInformation("Recycled {Path} -> {Target}", path, target);
        return target;
    }

    /// <summary>Gets the drive that owns a path — the deepest-matching mount point on Linux, or the drive letter on Windows.</summary>
    /// <param name="path">A file or directory path.</param>
    /// <returns>The owning drive, or null if none matches.</returns>
    public static DriveInfo? FindDriveForPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Where(d => LibraryGuard.IsUnder(full, d.RootDirectory.FullName))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Gets the effective recycle bin root that <see cref="MoveToBin"/> will use.</summary>
    /// <returns>The root path.</returns>
    public string GetEffectiveRoot() => Root;

    /// <summary>
    /// Writes the ownership marker into an unowned batch directory sitting at the top level of the
    /// current recycle root, folding it into the managed bin from that point on
    /// (<see cref="GetContents"/> / <see cref="ListContents"/> / <see cref="EmptyAll"/> /
    /// <see cref="Purge"/> all pick it up). Rejected paths return false without touching disk.
    /// The path must be a direct child of the current <see cref="Root"/> AND its name must match the
    /// canonical timestamp+GUID batch shape (<see cref="IsMediaDashBatchName"/>).
    /// </summary>
    /// <param name="batchPath">Absolute path to a batch directory.</param>
    /// <returns>True when the marker is now present (either freshly written or already there); false when the path is not eligible.</returns>
    public bool AdoptBatchByPath(string batchPath)
    {
        if (string.IsNullOrWhiteSpace(batchPath))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(batchPath));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        if (!Directory.Exists(full) || !IsMediaDashBatchName(full))
        {
            return false;
        }

        var parent = Path.GetDirectoryName(full);
        if (parent is null || !PathsEqual(parent, Root))
        {
            return false;
        }

        try
        {
            var marker = Path.Combine(full, OwnershipMarkerFileName);
            if (!File.Exists(marker))
            {
                File.Create(marker).Dispose();
            }

            _logger.LogInformation("Adopted legacy recycle batch {Batch} into the managed bin", full);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not adopt legacy recycle batch {Batch}", full);
            return false;
        }
    }

    /// <summary>
    /// Restores a recycled entry (file OR directory) to its original location. MD-012: MoveToBin
    /// accepts directories (orphan cleanup empty-folder path) but the pre-fix Restore only handled
    /// files, so directory entries showed up in the recycle-bin tab but the restore button
    /// silently 409'd against File.Exists / File.Move. Both callers (History restore endpoint and
    /// recycle-bin tab restore) now work symmetrically with MoveToBin.
    /// </summary>
    /// <param name="recyclePath">The entry's location inside the bin (file or directory).</param>
    /// <param name="originalPath">The original path to restore to.</param>
    public void Restore(string recyclePath, string originalPath)
    {
        _ = _legacyAdopted.Value;
        // Reject collisions on either kind — a file and a folder can share a target path on
        // different platforms depending on how MediaDash was upgraded, so check both.
        if (File.Exists(originalPath) || Directory.Exists(originalPath))
        {
            throw new IOException($"Cannot restore: something already exists at {originalPath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
        var sourceIsDir = Directory.Exists(recyclePath);
        MoveAcrossVolumes(recyclePath, originalPath, sourceIsDir);
        _logger.LogInformation("Restored {Recycle} -> {Original}", recyclePath, originalPath);
    }

    /// <summary>
    /// Permanently deletes a single file from the bin. Caller must have verified the path against
    /// <see cref="ListContents"/> — the whitelist is the safety boundary, symmetric with <see cref="Restore"/>.
    /// </summary>
    /// <param name="recyclePath">Full path to the recycled file inside a bin batch folder.</param>
    public void DeleteFile(string recyclePath)
    {
        _ = _legacyAdopted.Value;
        // Directory-recycled entries (orphan cleanup, Files-tab folder delete) now surface in
        // ListContents, so the DeleteBinItem endpoint can send us a directory path — File.Delete
        // refuses those with UnauthorizedAccessException. Branch on the actual kind so the same
        // whitelisted path works for both.
        if (Directory.Exists(recyclePath))
        {
            Directory.Delete(recyclePath, recursive: true);
            _logger.LogInformation("Permanently deleted bin folder {Recycle}", recyclePath);
            return;
        }

        File.Delete(recyclePath);
        _logger.LogInformation("Permanently deleted bin file {Recycle}", recyclePath);
    }

    /// <summary>
    /// Gets the current number of files and total bytes held in the bin.
    /// </summary>
    /// <returns>File count and total size.</returns>
    public (int FileCount, long SizeBytes) GetContents()
    {
        _ = _legacyAdopted.Value;
        var count = 0;
        long size = 0;
        // IgnoreInaccessible so one unreadable subfolder doesn't abort the whole size scan mid-walk.
        var enumOpts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
        foreach (var batch in EnumerateOwnedBatches())
        {
            // EnumerateFiles is lazy: the directory is opened on the first MoveNext and the recursive
            // walk can hit a removed subfolder later, both outside the per-file try below. A batch
            // deleted by a concurrent EmptyAll / Purge / restore must count as gone, not fail the
            // whole size scan — that 500'd GET /RecycleBin while the bin was being emptied.
            string[] batchFiles;
            try
            {
                batchFiles = Directory.GetFiles(batch, "*", enumOpts);
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var file in batchFiles)
            {
                if (PathsEqual(file, Path.Combine(batch, OwnershipMarkerFileName))
                    || PathsEqual(file, Path.Combine(batch, OriginManifestFileName)))
                {
                    continue;
                }

                try
                {
                    var length = new FileInfo(file).Length;
                    // count only after the size read succeeds — a vanished file no longer exists to
                    // count. Old order inflated the count by 1 whenever the FileInfo threw.
                    count++;
                    size += length;
                }
                catch (FileNotFoundException)
                {
                    // Benign race: a concurrent purge / restore / empty-all removed this file between
                    // EnumerateFiles yielding it and FileInfo reading it. Skip silently — the next scan
                    // sees the correct state. Suppressing avoids the noisy user-report class where the
                    // dashboard's status poll fires every few seconds and catches every transient miss.
                }
                catch (DirectoryNotFoundException)
                {
                    // Same benign race, whole batch folder went away mid-walk. Rest of the enumerator
                    // will throw on next MoveNext; break so we don't spin.
                    break;
                }
                catch (IOException ex)
                {
                    Api.Diagnostics.Record("RecycleBin.SizeScan", "The recycle bin size shown may be slightly low: MediaDash could not read the size of '" + file + "'. Details: " + ex.Message);
                }
                catch (UnauthorizedAccessException ex)
                {
                    Api.Diagnostics.Record("RecycleBin.SizeScan", "The recycle bin size shown may be slightly low: Jellyfin does not have permission to read '" + file + "'. Give the user Jellyfin runs as read access to the recycle bin folder. Details: " + ex.Message);
                }
            }
        }

        return (count, size);
    }

    /// <summary>
    /// Lists the files currently held in the bin, newest first.
    /// </summary>
    /// <param name="limit">Maximum entries returned.</param>
    /// <returns>File name, size and when it was recycled.</returns>
    public IReadOnlyList<(string FileName, string BinPath, long SizeBytes, DateTime RecycledAtUtc, string? OriginalPath)> ListContents(int limit = 500)
    {
        _ = _legacyAdopted.Value;
        var result = new List<(string, string, long, DateTime, string?)>();
        foreach (var dir in EnumerateOwnedBatches().OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        {
            // Batch folder can vanish between GetDirectories snapshot and enumeration (concurrent
            // Purge / EmptyAll / Consolidate / external rm). ReadOriginManifest and EnumerateFiles
            // both throw DirectoryNotFoundException at first-touch — outside the per-file try/catch
            // below — so wrap the per-batch iteration here. Field report: `GET /RecycleBin/Items`
            // 500'd with "Could not find a part of the path '.../20260819-162509-677-...'" after the
            // batch was auto-purged mid-request.
            string[] manifestOrigins;
            IEnumerable<string> files;
            try
            {
                manifestOrigins = ReadOriginManifest(dir);
                // Eager, so a folder removed before the walk starts is caught here rather than in the foreach.
                files = Directory.GetFiles(dir);
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (PathsEqual(file, Path.Combine(dir, OwnershipMarkerFileName))
                    || PathsEqual(file, Path.Combine(dir, OriginManifestFileName)))
                {
                    continue;
                }

                try
                {
                    var info = new FileInfo(file);
                    // Parse the timestamp from the folder name (yyyyMMdd-HHmmss-fff-<guid>) so the
                    // listing is correct even on filesystems (FAT/exFAT/some SMB) whose creation time
                    // is stored as local time and returned to us in the wrong TZ.
                    var folderName = Path.GetFileName(dir);
                    var recycledAt = TryParseRecycleTimestamp(folderName) ?? Directory.GetCreationTimeUtc(dir);
                    var origin = MatchManifestEntryToFile(manifestOrigins, info.Name);
                    result.Add((info.Name, file, info.Length, recycledAt, string.IsNullOrEmpty(origin) ? null : origin));
                }
                catch (FileNotFoundException)
                {
                    // Benign race with a concurrent purge / restore / adopt. Same rationale as GetContents.
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    // Whole batch folder was removed mid-enumeration; the rest of the walker will trip on it too.
                    break;
                }
                catch (IOException ex)
                {
                    Api.Diagnostics.Record("RecycleBin.ListScan", "'" + file + "' is in the recycle bin but could not be read, so it is not listed. Check that the user Jellyfin runs as can read the recycle bin folder. Details: " + ex.Message);
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    Api.Diagnostics.Record("RecycleBin.ListScan", "'" + file + "' is in the recycle bin but Jellyfin does not have permission to read it, so it is not listed. Give the user Jellyfin runs as read access to the recycle bin folder. Details: " + ex.Message);
                    continue;
                }

                if (result.Count >= limit)
                {
                    return result;
                }
            }

            // Bug-pattern audit 2026-09-19: MoveToBin handles directories (Directory.Move at line
            // 190) but the listing was files-only, so recycled trees — empty per-title folders
            // from orphan cleanup, folders deleted from the Files tab — never surfaced in the
            // Recycle bin UI. GetContents's size scan is recursive so byte totals were correct,
            // but the entry itself was invisible and the user's only recovery path was manual
            // file browsing under the bin root. Enumerate top-level directories in each batch
            // alongside the files and emit a bin entry per tree, sized via the same recursive
            // walk GetContents uses so the UI's per-row size column matches the totals.
            IEnumerable<string> subdirs;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir);
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var subdir in subdirs)
            {
                try
                {
                    var info = new DirectoryInfo(subdir);
                    var folderName = Path.GetFileName(dir);
                    var recycledAt = TryParseRecycleTimestamp(folderName) ?? Directory.GetCreationTimeUtc(dir);
                    var origin = MatchManifestEntryToFile(manifestOrigins, info.Name);
                    result.Add((info.Name, subdir, DirectorySizeBytes(subdir), recycledAt, string.IsNullOrEmpty(origin) ? null : origin));
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }
                catch (IOException ex)
                {
                    Api.Diagnostics.Record("RecycleBin.ListScan", "The recycle bin folder '" + subdir + "' could not be read, so its contents are not listed. Check that the user Jellyfin runs as can read it. Details: " + ex.Message);
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    Api.Diagnostics.Record("RecycleBin.ListScan", "Jellyfin does not have permission to read the recycle bin folder '" + subdir + "', so its contents are not listed. Give the user Jellyfin runs as read access to it. Details: " + ex.Message);
                    continue;
                }

                if (result.Count >= limit)
                {
                    return result;
                }
            }
        }

        return result;
    }

    // Recursive tree size for a recycled directory. Same IgnoreInaccessible + RecurseSubdirectories
    // options GetContents uses so the per-row size in the bin listing matches the total.
    // ponytail: single pass, no caching; if a bin with 10k+ folders shows up in the wild we can
    // memoise by (path, mtime).
    internal static long DirectorySizeBytes(string path)
    {
        long size = 0;
        var enumOpts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", enumOpts))
            {
                try
                {
                    size += new FileInfo(file).Length;
                }
                catch (FileNotFoundException)
                {
                }
                catch (IOException)
                {
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return size;
    }

    // Persist recovery provenance before moving content; a failed write must leave the source intact.
    private void AppendOriginManifest(string batchFolder, string sourcePath)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(batchFolder, OriginManifestFileName),
                Path.GetFullPath(sourcePath) + Environment.NewLine);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not write recycle-bin origin manifest for '{Path}'.", sourcePath);
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied writing recycle-bin origin manifest for '{Path}'.", sourcePath);
            throw;
        }
    }

    // Picks the manifest line whose basename matches a bin file's basename. Several files can share
    // a batch (Directory.Move of a folder, or future multi-file bundles), and manifest lines are
    // appended in MoveToBin's order — but the FS listing order is not guaranteed across platforms.
    // Match by basename rather than positional. Returns null when nothing matches. Internal for direct
    // testing so basename-collision behavior can be pinned without touching disk.
    internal static string? MatchManifestEntryToFile(IReadOnlyList<string> manifestOrigins, string fileName)
    {
        for (var i = 0; i < manifestOrigins.Count; i++)
        {
            var line = manifestOrigins[i];
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            if (string.Equals(Path.GetFileName(line), fileName, StringComparison.Ordinal))
            {
                return line;
            }
        }

        return null;
    }

    // Returns absolute paths written by MoveToBin's manifest, or an empty list when the batch
    // predates the manifest or the file is unreadable. Internal so unit tests can drive the
    // reader on a synthetic batch directory without wiring up a full RecycleBin instance.
    internal static string[] ReadOriginManifest(string batchDir)
    {
        var manifestPath = Path.Combine(batchDir, OriginManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return Array.Empty<string>();
        }

        try
        {
            return File.ReadAllLines(manifestPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToArray();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Finds the "optimized twin" of a recycled original: the smaller sibling bin file with the same
    /// basename, recycled within a small time window of <paramref name="fixedAtUtc"/>. Used by the
    /// redownload-warning restore flow to recover the remuxed copy that the pre-0.9.9 SubtitleLanguage
    /// bug wrongly moved to the bin alongside the original. Returns null when no unambiguous twin can
    /// be identified.
    /// </summary>
    /// <param name="originalRecyclePath">Bin path of the original file (from the history row).</param>
    /// <param name="fixedAtUtc">When the buggy fix ran.</param>
    /// <param name="sourceOriginalPath">
    /// Optional: the on-disk path the original file lived at before the buggy fix recycled it (from
    /// <c>HistoryEntry.Path</c>). When supplied, disambiguates two same-basename/time-window
    /// candidates by preferring the one whose manifest's OriginalPath equals this value — a much
    /// tighter match than basename alone. Pass null on legacy call sites that don't have it; the
    /// selector still works, just with the old ambiguity handling.
    /// </param>
    /// <returns>The twin's bin path and size, or null.</returns>
    public (string BinPath, long SizeBytes)? FindOptimizedTwin(string originalRecyclePath, DateTime fixedAtUtc, string? sourceOriginalPath = null)
    {
        if (string.IsNullOrEmpty(originalRecyclePath) || !File.Exists(originalRecyclePath))
        {
            return null;
        }

        long originalSize;
        try
        {
            originalSize = new FileInfo(originalRecyclePath).Length;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return SelectOptimizedTwin(ListContents(limit: 5000), originalRecyclePath, originalSize, fixedAtUtc, sourceOriginalPath);
    }

    /// <summary>
    /// Pure-logic core of <see cref="FindOptimizedTwin"/>: picks the single smaller sibling in
    /// <paramref name="entries"/> that shares the original's basename and was recycled within 5
    /// minutes of the buggy fix. Returns null when no candidate matches or when the choice is
    /// ambiguous. Exposed internal for direct unit testing.
    /// </summary>
    /// <param name="entries">Every entry currently in the bin (already enumerated by the caller).</param>
    /// <param name="originalRecyclePath">Bin path of the original file.</param>
    /// <param name="originalSize">Size in bytes of the original.</param>
    /// <param name="fixedAtUtc">When the buggy fix ran.</param>
    /// <param name="sourceOriginalPath">Optional on-disk source path used to disambiguate multiple candidates by manifest match.</param>
    /// <returns>The twin's bin path and size, or null.</returns>
    internal static (string BinPath, long SizeBytes)? SelectOptimizedTwin(
        IReadOnlyList<(string FileName, string BinPath, long SizeBytes, DateTime RecycledAtUtc, string? OriginalPath)> entries,
        string originalRecyclePath,
        long originalSize,
        DateTime fixedAtUtc,
        string? sourceOriginalPath = null)
    {
        var basename = Path.GetFileName(originalRecyclePath);
        var window = TimeSpan.FromMinutes(5);
        // Tuple carries OriginalPath so the disambiguation pass below can prefer manifest-matched
        // twins over ambiguous basename+window matches.
        var candidates = new List<(string BinPath, long SizeBytes, string? OriginalPath)>();

        foreach (var entry in entries)
        {
            if (!string.Equals(entry.FileName, basename, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(entry.BinPath, originalRecyclePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if ((entry.RecycledAtUtc - fixedAtUtc).Duration() > window)
            {
                continue;
            }

            if (entry.SizeBytes >= originalSize)
            {
                continue;
            }

            candidates.Add((entry.BinPath, entry.SizeBytes, entry.OriginalPath));
        }

        // Fast path: exactly one candidate is unambiguous.
        if (candidates.Count == 1)
        {
            return (candidates[0].BinPath, candidates[0].SizeBytes);
        }

        // Disambiguation: when we have multiple basename+time-window matches AND the caller told
        // us where the source file lived on disk, prefer the candidate whose manifest OriginalPath
        // matches — that's the strongest signal that the buggy fix recycled BOTH from the same
        // source. Pre-manifest candidates (OriginalPath=null) are excluded from this pass.
        if (candidates.Count > 1 && !string.IsNullOrEmpty(sourceOriginalPath))
        {
            var manifestMatched = candidates
                .Where(c => !string.IsNullOrEmpty(c.OriginalPath)
                            && string.Equals(c.OriginalPath, sourceOriginalPath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (manifestMatched.Count == 1)
            {
                return (manifestMatched[0].BinPath, manifestMatched[0].SizeBytes);
            }
        }

        // Multiple candidates in the same window with no unambiguous manifest match mean we can't
        // be sure which is "the" twin. Better to make the user pick from the recycle bin UI than
        // restore the wrong file.
        return null;
    }

    /// <summary>
    /// Permanently deletes everything in the bin, regardless of retention. Reports batch-level progress
    /// via <see cref="GetEmptyingProgress"/> so the UI can show a bar while a large bin is being cleared.
    /// </summary>
    public void EmptyAll()
    {
        _ = _legacyAdopted.Value;
        RunEmpty(() => EnumerateOwnedBatches().ToArray(), "Recycle bin emptied by user request ({Done}/{Total} batches)");
    }

    /// <summary>Counts the batch folders <see cref="EmptyAll"/> would delete (the current bin's owned batches).</summary>
    /// <returns>Batch folder count.</returns>
    public int CountEmptyAllBatches()
    {
        _ = _legacyAdopted.Value;
        return EnumerateOwnedBatches().Count();
    }

    /// <summary>
    /// Permanently deletes every recycle bin MediaDash has used, not only the current one: the
    /// configured bin, the default bin, the metadata bin, bins named by history rows from an earlier
    /// bin path, and batches written by older MediaDash versions (timestamp-only folder names). See
    /// <see cref="PlanEmptyEverywhere"/> for what qualifies. Same progress reporting as <see cref="EmptyAll"/>.
    /// </summary>
    /// <param name="history">History store, the evidence for older bin locations and legacy batches.</param>
    public void EmptyEverywhere(HistoryStore history)
    {
        RunEmpty(() => PlanEmptyEverywhere(history).SelectMany(location => location.Batches).ToArray(), "Every recycle bin emptied by user request ({Done}/{Total} batches)");
    }

    /// <summary>
    /// Lists every recycle location MediaDash can prove it created, with the batches an
    /// "empty everything" run would delete. A batch qualifies when it carries the ownership marker,
    /// or has a MediaDash batch name (current or older timestamp-only format) AND either sits in the
    /// plugin's own default bin or is referenced by a history row. Lookalike folders in a custom
    /// location that no history row points at are left alone.
    /// </summary>
    /// <param name="history">History store.</param>
    /// <returns>One entry per location that holds at least one qualifying batch.</returns>
    internal IReadOnlyList<EmptyPlanLocation> PlanEmptyEverywhere(HistoryStore history)
    {
        _ = _legacyAdopted.Value;
        var historyPaths = history.GetRecyclePaths();
        var roots = new List<string> { Root, _defaultRoot, _metadataRoot };
        roots.AddRange(_mountedRoots.List());
        roots.AddRange(historyPaths.Select(DeriveAnyBinRoot).Where(root => root.Length > 0));
        return BuildEmptyPlan(roots, Root, _defaultRoot, historyPaths);
    }

    /// <summary>
    /// Pure core of <see cref="PlanEmptyEverywhere"/>, exposed for unit tests.
    /// </summary>
    /// <param name="roots">Candidate recycle roots (duplicates and missing folders are ignored).</param>
    /// <param name="currentRoot">The configured recycle root.</param>
    /// <param name="defaultRoot">The plugin's dedicated default recycle root.</param>
    /// <param name="historyRecyclePaths">Every recycle path recorded in history.</param>
    /// <returns>Locations with their qualifying batches.</returns>
    internal static IReadOnlyList<EmptyPlanLocation> BuildEmptyPlan(
        IEnumerable<string> roots,
        string currentRoot,
        string defaultRoot,
        IEnumerable<string> historyRecyclePaths)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var referenced = new HashSet<string>(comparer);
        foreach (var recyclePath in historyRecyclePaths)
        {
            if (TryGetBatchOf(recyclePath) is { } batch)
            {
                referenced.Add(batch);
            }
        }

        var seen = new HashSet<string>(comparer);
        var plan = new List<EmptyPlanLocation>();
        foreach (var candidate in roots)
        {
            string root;
            try
            {
                root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                continue;
            }

            if (!seen.Add(root) || IsSystemReservedPath(root) || !Directory.Exists(root) || LibraryGuard.HasReparsePointAncestor(root))
            {
                continue;
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var isDefault = PathsEqual(root, defaultRoot);
            var batches = new List<string>();
            long size = 0;
            foreach (var child in children)
            {
                var batch = Path.TrimEndingDirectorySeparator(child);
                var qualifies = IsOwnedBatchDirectory(batch, defaultRoot)
                    || ((IsMediaDashBatchName(batch) || IsLegacyBatchName(batch)) && (isDefault || referenced.Contains(batch)));
                if (!qualifies || LibraryGuard.HasReparsePoints(batch))
                {
                    continue;
                }

                batches.Add(batch);
                size += DirectorySizeBytes(batch);
            }

            if (batches.Count > 0)
            {
                plan.Add(new EmptyPlanLocation(root, batches, size, PathsEqual(root, currentRoot)));
            }
        }

        return plan;
    }

    private void RunEmpty(Func<string[]> collectBatches, string doneMessage)
    {
        // Atomic single-runner gate — two concurrent POSTs racing the "already running" check on the
        // controller both saw IsRunning=false and could start twice; CompareExchange makes the second
        // arrival return early without touching state.
        if (System.Threading.Interlocked.CompareExchange(ref _emptyingGate, 1, 0) != 0)
        {
            return;
        }

        _lastEmptyError = null;
        try
        {
            System.Threading.Volatile.Write(ref _emptyingTotal, 0);
            System.Threading.Volatile.Write(ref _emptyingDone, 0);
            var dirs = collectBatches();
            System.Threading.Volatile.Write(ref _emptyingTotal, dirs.Length);
            System.Threading.Volatile.Write(ref _emptyingDone, 0);
            foreach (var dir in dirs)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (UnauthorizedAccessException ex)
                {
                    // Record the first offender and keep going — deleting the rest still frees space.
                    _lastEmptyError ??= "Could not empty '" + dir + "' from the recycle bin because Jellyfin does not have permission. Give the user Jellyfin runs as read and write access to the recycle bin folder, then empty it again. Details: " + ex.Message;
                    _logger.LogWarning(ex, "Permission denied deleting {Dir}", dir);
                    Api.Diagnostics.Record("RecycleBin.PermissionDenied", _lastEmptyError);
                }
                catch (IOException ex)
                {
                    _lastEmptyError ??= "Could not empty '" + dir + "' from the recycle bin. A file in it may be open in another program; close it and empty the bin again. Details: " + ex.Message;
                    _logger.LogWarning(ex, "I/O error deleting {Dir}", dir);
                    Api.Diagnostics.Record("RecycleBin.IOError", _lastEmptyError);
                }

                System.Threading.Interlocked.Increment(ref _emptyingDone);
            }

#pragma warning disable CA2254 // Both templates are fixed strings chosen by the two callers above.
            _logger.LogInformation(doneMessage, _emptyingDone, _emptyingTotal);
#pragma warning restore CA2254
        }
        finally
        {
            System.Threading.Volatile.Write(ref _emptyingGate, 0);
        }
    }

    /// <summary>
    /// Deletes recycled files older than the retention period.
    /// </summary>
    /// <param name="retentionDays">Days to keep recycled files.</param>
    public void Purge(int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return;
        }

        _ = _legacyAdopted.Value;
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        foreach (var dir in EnumerateOwnedBatches())
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                {
                    Directory.Delete(dir, recursive: true);
                    _logger.LogInformation("Purged expired recycle bin folder {Dir}", dir);
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not purge recycle bin folder {Dir}", dir);
                Api.Diagnostics.Record("RecycleBin.PurgeFailed", "An expired recycle bin folder ('" + dir + "') could not be deleted. A file in it may be open in another program; MediaDash will try again later. Details: " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Access denied purging recycle bin folder {Dir}", dir);
                Api.Diagnostics.Record("RecycleBin.PurgeFailed", "An expired recycle bin folder ('" + dir + "') could not be deleted because Jellyfin does not have permission. Give the user Jellyfin runs as delete permission on the recycle bin folder. Details: " + ex.Message);
            }
        }
    }

    private static DateTime? TryParseRecycleTimestamp(string folderName)
    {
        // Folder names are yyyyMMdd-HHmmss-fff-<8charGuid>. The first 19 chars are the timestamp.
        if (folderName.Length < 19)
        {
            return null;
        }

        return DateTime.TryParseExact(
            folderName[..19],
            "yyyyMMdd-HHmmss-fff",
            CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Returns whether a directory name matches the exact timestamp + GUID format created by
    /// <see cref="MoveToBin"/>. This validates syntax only; destructive operations additionally
    /// require <see cref="IsOwnedBatchDirectory(string, string)"/>.
    /// </summary>
    /// <param name="path">A directory path or leaf name.</param>
    /// <returns>True only for a valid MediaDash recycle batch name.</returns>
    internal static bool IsMediaDashBatchName(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (string.IsNullOrEmpty(name) || name.Length != 28 || name[19] != '-' || TryParseRecycleTimestamp(name) is null)
        {
            return false;
        }

        for (var i = 20; i < name.Length; i++)
        {
            if (!Uri.IsHexDigit(name[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns whether a directory name matches the timestamp-only batch format written by older
    /// MediaDash versions (yyyyMMdd-HHmmss-fff, no GUID suffix). Those batches carry no ownership
    /// marker, so callers must have separate evidence (history row or default root) before deleting.
    /// </summary>
    /// <param name="path">A directory path or leaf name.</param>
    /// <returns>True for a legacy batch name.</returns>
    internal static bool IsLegacyBatchName(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return name.Length == 19 && TryParseRecycleTimestamp(name) is not null;
    }

    // <bin>/<batch>/<item> -> full path of <batch> when its name is a MediaDash batch name (either format).
    private static string? TryGetBatchOf(string recyclePath)
    {
        try
        {
            var batch = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(recyclePath)));
            return batch is not null && (IsMediaDashBatchName(batch) || IsLegacyBatchName(batch))
                ? Path.TrimEndingDirectorySeparator(batch)
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    // Like DeriveBinRoot but also accepts legacy timestamp-only batch names.
    private static string DeriveAnyBinRoot(string recyclePath)
    {
        var batch = TryGetBatchOf(recyclePath);
        var root = batch is null ? null : Path.GetDirectoryName(batch);
        return string.IsNullOrEmpty(root) ? string.Empty : Path.TrimEndingDirectorySeparator(root);
    }

    /// <summary>
    /// Returns whether a recycle batch has durable MediaDash ownership evidence. Unmarked legacy
    /// batches remain eligible only when they are direct children of the plugin's dedicated default
    /// recycle root; custom roots require the marker so lookalike user folders are never deleted.
    /// </summary>
    /// <param name="path">Candidate batch directory.</param>
    /// <param name="defaultRoot">The plugin's dedicated default recycle root.</param>
    /// <returns>True when the directory is safe for MediaDash to enumerate or delete.</returns>
    internal static bool IsOwnedBatchDirectory(string path, string defaultRoot)
    {
        if (!IsMediaDashBatchName(path))
        {
            return false;
        }

        try
        {
            if (File.Exists(Path.Combine(path, OwnershipMarkerFileName)))
            {
                return true;
            }

            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
            return parent is not null && PathsEqual(parent, defaultRoot);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Adds an ownership marker to an unmarked custom-root batch only when a successful history row
    /// points at an item directly inside that batch.
    /// </summary>
    /// <param name="recyclePath">Recycle path from plugin history.</param>
    /// <param name="customRoot">Configured custom recycle root.</param>
    /// <returns>True when the path is an adopted or already-marked direct child batch.</returns>
    internal static bool TryAdoptLegacyBatch(string recyclePath, string customRoot)
    {
        try
        {
            if (!File.Exists(recyclePath) && !Directory.Exists(recyclePath))
            {
                return false;
            }

            var batch = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(recyclePath)));
            if (batch is null || !IsMediaDashBatchName(batch))
            {
                return false;
            }

            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(batch));
            if (parent is null || !PathsEqual(parent, customRoot))
            {
                return false;
            }

            var marker = Path.Combine(batch, OwnershipMarkerFileName);
            if (!File.Exists(marker))
            {
                File.Create(marker).Dispose();
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal bool IsOwnedRecoveryPath(string? path, string originalPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !(File.Exists(path) || Directory.Exists(path)))
            {
                return false;
            }

            var full = Path.GetFullPath(path);
            var batch = Path.GetDirectoryName(full);
            return batch is not null && Path.GetDirectoryName(batch) is { } parent
                && (PathsEqual(parent, Root) || PathsEqual(parent, _metadataRoot)
                    || (MountedRecycleRoots.Contains(parent) && _mountedRoots.List().Contains(parent, StringComparer.Ordinal)))
                && IsOwnedBatchDirectory(batch) && !LibraryGuard.HasReparsePoints(full)
                && PathsEqual(MatchManifestEntryToFile(ReadOriginManifest(batch), Path.GetFileName(full)) ?? string.Empty, originalPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private bool IsOwnedBatchDirectory(string path) => IsOwnedBatchDirectory(path, _defaultRoot);

    internal bool IsManagedMetadataBinPath(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && LibraryGuard.IsUnder(Path.GetFullPath(path), Path.GetFullPath(_metadataRoot));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private IEnumerable<string> EnumerateOwnedBatches()
    {
        var root = Root;
        if (Directory.Exists(root))
        {
            foreach (var batch in Directory.GetDirectories(root).Where(IsOwnedBatchDirectory))
            {
                yield return batch;
            }
        }

        if (!PathsEqual(root, _metadataRoot) && Directory.Exists(_metadataRoot)
            && !LibraryGuard.HasReparsePointAncestor(Path.GetFullPath(_metadataRoot)))
        {
            foreach (var batch in Directory.GetDirectories(_metadataRoot).Where(IsOwnedBatchDirectory))
            {
                if (!LibraryGuard.HasReparsePoints(batch))
                {
                    yield return batch;
                }
            }
        }

        foreach (var localRoot in _mountedRoots.List())
        {
            foreach (var batch in Directory.GetDirectories(localRoot).Where(IsOwnedBatchDirectory))
            {
                if (!LibraryGuard.HasReparsePoints(batch))
                {
                    yield return batch;
                }
            }
        }
    }

    /// <summary>
    /// Derives every OTHER bin root the user has historically recycled to, based on
    /// <see cref="HistoryEntry.RecyclePath"/>. Filters out the current <see cref="Root"/>. Each
    /// entry reports counts of MediaDash-shaped batch folders still on disk and their total size —
    /// so the UI can offer a "consolidate into current bin" action.
    /// </summary>
    /// <param name="history">History-table store (05-C.3), source of historical recycle paths.</param>
    /// <returns>List of foreign bin roots with file counts + sizes.</returns>
    public IReadOnlyList<(string RootPath, int BatchCount, long SizeBytes)> DiscoverOtherBinRoots(HistoryStore history)
    {
        var mountedRoots = _mountedRoots.List().ToHashSet(StringComparer.Ordinal);
        var foreignPaths = history.GetRecyclePaths().Where(path => !IsManagedMetadataBinPath(path)
            && !mountedRoots.Contains(DeriveBinRoot(path)));
        return DiscoverOtherBinRoots(foreignPaths, Root);
    }

    /// <summary>
    /// Pure logic — no <see cref="Plugin.Instance"/>, no _logger. Given a set of historical recycle
    /// paths and the currently-configured bin root, returns every other root that still holds
    /// MediaDash-shaped batches. Exposed internal for direct unit testing.
    /// </summary>
    /// <param name="recyclePaths">Distinct RecyclePath values from HistoryEntry rows.</param>
    /// <param name="currentRoot">The currently-configured bin root, to exclude from the result.</param>
    /// <returns>Discovered other roots with per-root batch and size counts.</returns>
    internal static IReadOnlyList<(string RootPath, int BatchCount, long SizeBytes)> DiscoverOtherBinRoots(
        IEnumerable<string> recyclePaths,
        string currentRoot)
    {
        var seen = new Dictionary<string, (int BatchCount, long SizeBytes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var recyclePath in recyclePaths)
        {
            var derivedRoot = DeriveBinRoot(recyclePath);
            if (string.IsNullOrEmpty(derivedRoot) || PathsEqual(derivedRoot, currentRoot))
            {
                continue;
            }

            if (!Directory.Exists(derivedRoot))
            {
                continue;
            }

            if (seen.ContainsKey(derivedRoot))
            {
                continue;
            }

            seen[derivedRoot] = MeasureBinRoot(derivedRoot);
        }

        return seen
            .Select(kv => (kv.Key, kv.Value.BatchCount, kv.Value.SizeBytes))
            .Where(t => t.BatchCount > 0)
            .ToList();
    }

    // From <bin>/<batch>/<file> back to <bin>. Returns empty when the path doesn't have the
    // expected batch-shape parent (defence against corrupted history rows).
    internal static string DeriveBinRoot(string recyclePath)
    {
        try
        {
            var batch = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(recyclePath)));
            if (batch is null || !IsMediaDashBatchName(batch))
            {
                return string.Empty;
            }

            var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(batch));
            return string.IsNullOrEmpty(root) ? string.Empty : Path.TrimEndingDirectorySeparator(root);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    private static (int BatchCount, long SizeBytes) MeasureBinRoot(string root)
    {
        var batchCount = 0;
        long total = 0;
        try
        {
            foreach (var batch in Directory.GetDirectories(root).Where(IsMediaDashBatchName))
            {
                batchCount++;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(batch, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            total += new FileInfo(file).Length;
                        }
                        catch (FileNotFoundException)
                        {
                            // Benign race with a concurrent purge; skip.
                        }
                        catch (IOException)
                        {
                        }
                        catch (UnauthorizedAccessException)
                        {
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return (batchCount, total);
    }

    /// <summary>
    /// Moves every MediaDash-shaped batch folder from <paramref name="sourceRoot"/> into the
    /// currently-configured <see cref="Root"/>. Cross-volume safe. Skips a batch when a name
    /// collision exists at the target (extremely unlikely given the timestamp+GUID shape).
    /// The manifest sidecar rides along with each batch — restore paths and history joins remain intact.
    /// </summary>
    /// <param name="sourceRoot">Absolute path of the legacy bin root to drain.</param>
    /// <returns>Counts + bytes moved and an optional warning string.</returns>
    public (int BatchesMoved, int BatchesSkipped, long BytesMoved, string? Warning) ConsolidateFromRoot(string sourceRoot)
    {
        var result = ConsolidateBetween(sourceRoot, Root);
        _logger.LogInformation(
            "Consolidated {Moved} legacy batch(es) ({Bytes} bytes) from {Source} into {Root}",
            result.BatchesMoved,
            result.BytesMoved,
            sourceRoot,
            Root);
        return result;
    }

    /// <summary>
    /// Pure-logic core of <see cref="ConsolidateFromRoot(string)"/>: moves every batch-shaped
    /// folder under <paramref name="sourceRoot"/> into <paramref name="targetRoot"/>. Cross-volume
    /// safe via <see cref="Api.FileBrowserController.CrossDeviceMove"/>.
    /// Skips a batch when the target already holds one with the same leaf. Exposed internal so
    /// unit tests can drive it against temporary directories without touching <see cref="Root"/>.
    /// </summary>
    /// <param name="sourceRoot">The legacy bin root to drain.</param>
    /// <param name="targetRoot">The currently-configured bin root.</param>
    /// <returns>Counts + bytes moved and an optional warning string.</returns>
    internal static (int BatchesMoved, int BatchesSkipped, long BytesMoved, string? Warning) ConsolidateBetween(
        string sourceRoot,
        string targetRoot)
    {
        if (PathsEqual(sourceRoot, targetRoot))
        {
            return (0, 0, 0, "Source is the same as the current bin.");
        }

        try
        {
            Directory.CreateDirectory(targetRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0, 0, "Could not create the target bin directory '" + targetRoot + "': " + ex.Message);
        }

        var moved = 0;
        var skipped = 0;
        long bytes = 0;
        string? warning = null;

        string[] batches;
        try
        {
            batches = Directory.GetDirectories(sourceRoot).Where(IsMediaDashBatchName).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0, 0, "Could not enumerate legacy bin '" + sourceRoot + "': " + ex.Message);
        }

        foreach (var batch in batches)
        {
            var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(batch));
            var target = Path.Combine(targetRoot, leaf);
            if (Directory.Exists(target) || File.Exists(target))
            {
                skipped++;
                continue;
            }

            long batchBytes = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(batch, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        batchBytes += new FileInfo(file).Length;
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            try
            {
                Directory.Move(batch, target);
            }
            catch (IOException)
            {
                try
                {
                    Api.FileBrowserController.CrossDeviceMove(batch, target, sourceIsDir: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warning = (warning ?? string.Empty) + "Batch '" + leaf + "' could not be moved: " + ex.Message + ". ";
                    skipped++;
                    continue;
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                warning = (warning ?? string.Empty) + "Batch '" + leaf + "' could not be moved: " + ex.Message + ". ";
                skipped++;
                continue;
            }

            moved++;
            bytes += batchBytes;
        }

        return (moved, skipped, bytes, warning);
    }

    private void AdoptLegacyCustomBatches(HistoryStore history)
    {
        var root = Root;
        if (PathsEqual(root, _defaultRoot) || !Directory.Exists(root))
        {
            return;
        }

        try
        {
            foreach (var recyclePath in history.GetRecyclePaths())
            {
                if (TryAdoptLegacyBatch(recyclePath, root))
                {
                    _logger.LogInformation("Verified legacy recycle batch referenced by history for {RecyclePath}", recyclePath);
                }
            }

            foreach (var candidate in Directory.GetDirectories(root).Where(IsMediaDashBatchName))
            {
                var marker = Path.Combine(candidate, OwnershipMarkerFileName);
                if (File.Exists(marker))
                {
                    continue;
                }

                // The 28-char yyyyMMdd-HHmmss-fff-<8hex> shape is strict enough that a
                // non-MediaDash folder collision is practically impossible; users landing here
                // asked for auto-adoption instead of a per-batch review workflow. Write the
                // marker so the batch is listed, purged on schedule, and restorable from the UI.
                try
                {
                    File.Create(marker).Dispose();
                    _logger.LogInformation("Auto-adopted legacy recycle batch {Batch} into the managed bin", candidate);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not auto-adopt legacy recycle batch {Batch}", candidate);
                    Api.Diagnostics.Record("RecycleBin.LegacyMigrationFailed", "A recycle bin folder from an older MediaDash version ('" + candidate + "') could not be taken over, so it will not be listed or emptied automatically. To fix this, create an empty file named '" + OwnershipMarkerFileName + "' inside it. Details: " + ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not inspect custom recycle root {Root} for legacy batches", root);
            Api.Diagnostics.Record("RecycleBin.LegacyMigrationFailed", "MediaDash could not check the recycle bin folder '" + root + "' for items left by older versions. Nothing needs doing unless old recycled items are missing from the list. Details: " + ex.Message);
        }
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
                comparison);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static void MoveAcrossVolumes(string source, string target, bool sourceIsDir = false)
    {
        // File.Move may silently copy/delete across filesystems. Never use that
        // fallback for originals or recycle payloads: retain the source on refusal.
        MediaMoveSafety.RequireSameFilesystemMove(source, target);
        MediaMoveSafety.RenameNoCopy(source, target, sourceIsDir);
    }
}
