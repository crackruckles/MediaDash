using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Plugin-managed trash folder: removed files are held here until retention expires so mistakes are recoverable.
/// </summary>
public sealed class RecycleBin
{
    // OS-reserved roots the recycle bin must not sit under — an admin who accidentally (or
    // maliciously) sets RecycleBinPath = "/etc" would otherwise land recycled files at
    // /etc/<timestamp>/<original-name>. Refusing these here is defense in depth; the setting is
    // admin-only, but restored config XML from an untrusted source is a real threat model.
    private static readonly string[] LinuxReservedRoots = ["/etc", "/bin", "/sbin", "/usr", "/boot", "/lib", "/lib64", "/proc", "/sys", "/dev", "/root"];

    private readonly string _defaultRoot;
    private readonly ILogger<RecycleBin> _logger;
    private int _emptyingTotal;
    private int _emptyingDone;
    private int _emptyingGate; // 0 = idle, 1 = running (CompareExchange gate)
    private volatile string? _lastEmptyError;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecycleBin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="logger">The logger.</param>
    public RecycleBin(IApplicationPaths applicationPaths, ILogger<RecycleBin> logger)
    {
        _defaultRoot = Path.Combine(applicationPaths.DataPath, "mediadash", "recycle");
        _logger = logger;
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

            if (IsSystemReservedPath(configured))
            {
                _logger.LogWarning("Configured RecycleBinPath '{Path}' sits under an OS-reserved root; falling back to the default location.", configured);
                Api.Diagnostics.Record("RecycleBin.ReservedPath", "Configured recycle bin path '" + configured + "' sits under an OS-reserved root and was rejected. Falling back to the plugin's default location. Update Settings → Recycle bin to a path on a data volume (a sibling of your library folder works best — moves become instant renames instead of cross-volume copies).");
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
        // Timestamp alone collides when two files with the same basename are recycled inside the same
        // millisecond (e.g. concurrent Delete + fix run). Suffix with a short GUID so folder names stay
        // unique. ListContents sorts by folder-name descending, and the timestamp prefix still
        // controls ordering because the GUID is short and comes after.
        var folder = Path.Combine(
            Root,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        // Trim trailing separator before GetFileName — otherwise a caller passing "/foo/bar/" reduces
        // target to `folder` itself, and Directory.Move(source, target) becomes "move to self" and throws.
        var basename = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (string.IsNullOrEmpty(basename))
        {
            throw new IOException("Cannot recycle '" + path + "': no basename to move into the bin.");
        }

        var target = Path.Combine(folder, basename);

        // Cross-volume moves are copy+delete under the hood — they need the file's size to fit on the recycle
        // bin volume. Pre-check when we can, so users get a clear "put the bin next to the media" message
        // instead of "No space left on device".
        if (System.IO.File.Exists(path))
        {
            var srcDrive = FindDriveForPath(path);
            var dstDrive = FindDriveForPath(folder);
            if (srcDrive is not null && dstDrive is not null
                && !string.Equals(srcDrive.RootDirectory.FullName, dstDrive.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
            {
                var size = new FileInfo(path).Length;
                const long headroom = 100L * 1024 * 1024;
                if (dstDrive.AvailableFreeSpace < size + headroom)
                {
                    var freeMb = dstDrive.AvailableFreeSpace / 1024 / 1024;
                    var neededMb = size / 1024 / 1024;
                    throw new IOException(
                        $"Not enough free space on the recycle bin volume ('{dstDrive.RootDirectory.FullName}' has {freeMb} MB free, need about {neededMb} MB). "
                        + "Fix: Settings → Recycle bin → change 'Recycle bin folder' to a path on the same volume as your media (e.g. '/mnt/media/.mediadash-recycle'). Moves then become instant renames and don't use extra space.");
                }
            }
        }

        if (Directory.Exists(path))
        {
            try
            {
                Directory.Move(path, target);
            }
            catch (IOException)
            {
                // Cross-volume directory move (EXDEV) — Directory.Move can't span filesystems. Fall back
                // to the same verified copy → rename → delete-source pattern the file path uses. Reported
                // by users whose Jellyfin data (e.g. /var/lib/jellyfin/data/collections) sits on a
                // different volume than the recycle bin.
                Api.FileBrowserController.CrossDeviceMove(path, target, sourceIsDir: true);
            }
        }
        else
        {
            MoveAcrossVolumes(path, target);
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
    /// Restores a recycled file to its original location.
    /// </summary>
    /// <param name="recyclePath">The file's location inside the bin.</param>
    /// <param name="originalPath">The original path to restore to.</param>
    public void Restore(string recyclePath, string originalPath)
    {
        if (File.Exists(originalPath))
        {
            throw new IOException($"Cannot restore: a file already exists at {originalPath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
        MoveAcrossVolumes(recyclePath, originalPath);
        _logger.LogInformation("Restored {Recycle} -> {Original}", recyclePath, originalPath);
    }

    /// <summary>
    /// Gets the current number of files and total bytes held in the bin.
    /// </summary>
    /// <returns>File count and total size.</returns>
    public (int FileCount, long SizeBytes) GetContents()
    {
        if (!Directory.Exists(Root))
        {
            return (0, 0);
        }

        var count = 0;
        long size = 0;
        // IgnoreInaccessible so one unreadable subfolder doesn't abort the whole size scan mid-walk.
        var enumOpts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
        foreach (var batch in Directory.GetDirectories(Root).Where(IsMediaDashBatchDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(batch, "*", enumOpts))
            {
                count++;
                try
                {
                    size += new FileInfo(file).Length;
                }
                catch (IOException ex)
                {
                    Api.Diagnostics.Record("RecycleBin.SizeScan", "Could not stat recycled file '" + file + "': " + ex.Message + ". Total-size total will be short by one entry.");
                }
                catch (UnauthorizedAccessException ex)
                {
                    Api.Diagnostics.Record("RecycleBin.SizeScan", "Access denied stat'ing recycled file '" + file + "': " + ex.Message + ". Total-size total will be short by one entry.");
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
    public IReadOnlyList<(string FileName, string BinPath, long SizeBytes, DateTime RecycledAtUtc)> ListContents(int limit = 500)
    {
        var result = new List<(string, string, long, DateTime)>();
        if (!Directory.Exists(Root))
        {
            return result;
        }

        foreach (var dir in Directory.GetDirectories(Root).Where(IsMediaDashBatchDirectory).OrderByDescending(d => d, StringComparer.Ordinal))
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                try
                {
                    var info = new FileInfo(file);
                    // Parse the timestamp from the folder name (yyyyMMdd-HHmmss-fff-<guid>) so the
                    // listing is correct even on filesystems (FAT/exFAT/some SMB) whose creation time
                    // is stored as local time and returned to us in the wrong TZ.
                    var folderName = Path.GetFileName(dir);
                    var recycledAt = TryParseRecycleTimestamp(folderName) ?? Directory.GetCreationTimeUtc(dir);
                    result.Add((info.Name, file, info.Length, recycledAt));
                }
                catch (IOException ex)
                {
                    Api.Diagnostics.Record("RecycleBin.ListScan", "Could not read recycled file '" + file + "': " + ex.Message + ". It will not appear in the recycle bin listing.");
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    Api.Diagnostics.Record("RecycleBin.ListScan", "Access denied on recycled file '" + file + "': " + ex.Message + ". It will not appear in the recycle bin listing.");
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

    /// <summary>
    /// Finds the "optimized twin" of a recycled original: the smaller sibling bin file with the same
    /// basename, recycled within a small time window of <paramref name="fixedAtUtc"/>. Used by the
    /// redownload-warning restore flow to recover the remuxed copy that the pre-0.9.9 SubtitleLanguage
    /// bug wrongly moved to the bin alongside the original. Returns null when no unambiguous twin can
    /// be identified.
    /// </summary>
    /// <param name="originalRecyclePath">Bin path of the original file (from the history row).</param>
    /// <param name="fixedAtUtc">When the buggy fix ran.</param>
    /// <returns>The twin's bin path and size, or null.</returns>
    public (string BinPath, long SizeBytes)? FindOptimizedTwin(string originalRecyclePath, DateTime fixedAtUtc)
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

        return SelectOptimizedTwin(ListContents(limit: 5000), originalRecyclePath, originalSize, fixedAtUtc);
    }

    /// <summary>
    /// Pure-logic core of <see cref="FindOptimizedTwin"/>: picks the single smaller sibling in
    /// <paramref name="entries"/> that shares the original's basename and was recycled within 5
    /// minutes of the buggy fix. Returns null when no candidate matches or when the choice is
    /// ambiguous. Exposed internal for direct unit testing.
    /// </summary>
    /// <param name="entries">Bin listing to search.</param>
    /// <param name="originalRecyclePath">Bin path of the original file.</param>
    /// <param name="originalSize">Size in bytes of the original.</param>
    /// <param name="fixedAtUtc">When the buggy fix ran.</param>
    /// <returns>The twin's bin path and size, or null.</returns>
    internal static (string BinPath, long SizeBytes)? SelectOptimizedTwin(
        IReadOnlyList<(string FileName, string BinPath, long SizeBytes, DateTime RecycledAtUtc)> entries,
        string originalRecyclePath,
        long originalSize,
        DateTime fixedAtUtc)
    {
        var basename = Path.GetFileName(originalRecyclePath);
        var window = TimeSpan.FromMinutes(5);
        var candidates = new List<(string BinPath, long SizeBytes)>();

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

            candidates.Add((entry.BinPath, entry.SizeBytes));
        }

        // Multiple candidates in the same window mean we can't be sure which is "the" twin.
        // Better to make the user pick from the recycle bin UI than restore the wrong file.
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// Permanently deletes everything in the bin, regardless of retention. Reports batch-level progress
    /// via <see cref="GetEmptyingProgress"/> so the UI can show a bar while a large bin is being cleared.
    /// </summary>
    public void EmptyAll()
    {
        // Atomic single-runner gate — two concurrent POSTs racing the "already running" check on the
        // controller both saw IsRunning=false and could start twice; CompareExchange makes the second
        // arrival return early without touching state.
        if (System.Threading.Interlocked.CompareExchange(ref _emptyingGate, 1, 0) != 0)
        {
            return;
        }

        _lastEmptyError = null;
        if (!Directory.Exists(Root))
        {
            System.Threading.Volatile.Write(ref _emptyingGate, 0);
            return;
        }

        try
        {
            var dirs = Directory.GetDirectories(Root).Where(IsMediaDashBatchDirectory).ToArray();
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
                    _lastEmptyError ??= "Permission denied deleting '" + dir + "'. Grant the user Jellyfin runs as read+write on the recycle bin folder. (" + ex.Message + ")";
                    _logger.LogWarning(ex, "Permission denied deleting {Dir}", dir);
                    Api.Diagnostics.Record("RecycleBin.PermissionDenied", _lastEmptyError);
                }
                catch (IOException ex)
                {
                    _lastEmptyError ??= "Could not delete '" + dir + "': " + ex.Message + ". A file may be open in another program.";
                    _logger.LogWarning(ex, "I/O error deleting {Dir}", dir);
                    Api.Diagnostics.Record("RecycleBin.IOError", _lastEmptyError);
                }

                System.Threading.Interlocked.Increment(ref _emptyingDone);
            }

            _logger.LogInformation("Recycle bin emptied by user request ({Done}/{Total} batches)", _emptyingDone, _emptyingTotal);
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
        if (!Directory.Exists(Root))
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        foreach (var dir in Directory.GetDirectories(Root).Where(IsMediaDashBatchDirectory))
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
                Api.Diagnostics.Record("RecycleBin.PurgeFailed", "Could not purge expired recycle bin folder '" + dir + "': " + ex.Message + ". Retention will retry it next cycle. A file may be open in another program.");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Access denied purging recycle bin folder {Dir}", dir);
                Api.Diagnostics.Record("RecycleBin.PurgeFailed", "Access denied purging expired recycle bin folder '" + dir + "': " + ex.Message + ". Grant Jellyfin's user delete permission on the recycle bin folder.");
            }
        }
    }

    private static DateTime? TryParseRecycleTimestamp(string folderName)
    {
        // Folder names are yyyyMMdd-HHmmss-fff-<8charGuid>. The first 18 chars are the timestamp.
        if (folderName.Length < 18)
        {
            return null;
        }

        return DateTime.TryParseExact(
            folderName[..18],
            "yyyyMMdd-HHmmss-fff",
            CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Returns whether a directory name matches the exact timestamp + GUID format created by
    /// <see cref="MoveToBin"/>. Cleanup operations must ignore every other child of a configured
    /// recycle root so a broad or mistaken custom path can never make unrelated folders purgeable.
    /// </summary>
    /// <param name="path">A directory path or leaf name.</param>
    /// <returns>True only for a MediaDash-owned recycle batch name.</returns>
    internal static bool IsMediaDashBatchDirectory(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (string.IsNullOrEmpty(name) || name.Length != 27 || name[18] != '-' || TryParseRecycleTimestamp(name) is null)
        {
            return false;
        }

        for (var i = 19; i < name.Length; i++)
        {
            if (!Uri.IsHexDigit(name[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static void MoveAcrossVolumes(string source, string target)
    {
        try
        {
            File.Move(source, target);
        }
        catch (IOException)
        {
            // Cross-volume: verified copy → rename → delete-source. Never delete the source before the
            // copy has been proven complete; a truncated write on the target volume would otherwise lose
            // the user's file.
            Api.FileBrowserController.CrossDeviceMove(source, target, sourceIsDir: false);
        }
    }
}
