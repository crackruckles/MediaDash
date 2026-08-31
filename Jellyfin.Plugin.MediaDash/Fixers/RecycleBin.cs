using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using MediaBrowser.Common.Configuration;
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
    /// <param name="db">The plugin database containing authoritative recycle history.</param>
    public RecycleBin(IApplicationPaths applicationPaths, ILogger<RecycleBin> logger, MediaDashDb db)
    {
        _defaultRoot = Path.Combine(applicationPaths.DataPath, "mediadash", "recycle");
        _logger = logger;
        // Deferred: plugin startup shouldn't block on a filesystem walk + marker writes when the
        // recycle root sits on a slow or unavailable network share. The migration is idempotent
        // and cheap on the default path (early-returns before any I/O), so paying it on the first
        // real bin operation instead of in the constructor is transparent to callers.
        _legacyAdopted = new Lazy<bool>(() =>
        {
            AdoptLegacyCustomBatches(db);
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
        _ = _legacyAdopted.Value;
        // Timestamp alone collides when two files with the same basename are recycled inside the same
        // millisecond (e.g. concurrent Delete + fix run). Suffix with a short GUID so folder names stay
        // unique. ListContents sorts by folder-name descending, and the timestamp prefix still
        // controls ordering because the GUID is short and comes after.
        var folder = Path.Combine(
            Root,
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
    /// Restores a recycled file to its original location.
    /// </summary>
    /// <param name="recyclePath">The file's location inside the bin.</param>
    /// <param name="originalPath">The original path to restore to.</param>
    public void Restore(string recyclePath, string originalPath)
    {
        _ = _legacyAdopted.Value;
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
        _ = _legacyAdopted.Value;
        if (!Directory.Exists(Root))
        {
            return (0, 0);
        }

        var count = 0;
        long size = 0;
        // IgnoreInaccessible so one unreadable subfolder doesn't abort the whole size scan mid-walk.
        var enumOpts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
        foreach (var batch in Directory.GetDirectories(Root).Where(IsOwnedBatchDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(batch, "*", enumOpts))
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
    public IReadOnlyList<(string FileName, string BinPath, long SizeBytes, DateTime RecycledAtUtc, string? OriginalPath)> ListContents(int limit = 500)
    {
        _ = _legacyAdopted.Value;
        var result = new List<(string, string, long, DateTime, string?)>();
        if (!Directory.Exists(Root))
        {
            return result;
        }

        foreach (var dir in Directory.GetDirectories(Root).Where(IsOwnedBatchDirectory).OrderByDescending(d => d, StringComparer.Ordinal))
        {
            var manifestOrigins = ReadOriginManifest(dir);
            foreach (var file in Directory.EnumerateFiles(dir))
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

    // Appends the source path to a batch's origin manifest so restore knows where to put the file
    // back regardless of HistoryEntry state. Missing manifest is not fatal — it degrades restore to
    // "no origin metadata"; logs a warning so a filesystem that refuses small text writes still
    // surfaces the failure. Instance method so it can log through _logger, but the read counterpart
    // is static.
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
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied writing recycle-bin origin manifest for '{Path}'.", sourcePath);
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
            var dirs = Directory.GetDirectories(Root).Where(IsOwnedBatchDirectory).ToArray();
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
        _ = _legacyAdopted.Value;
        if (!Directory.Exists(Root))
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        foreach (var dir in Directory.GetDirectories(Root).Where(IsOwnedBatchDirectory))
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

    private bool IsOwnedBatchDirectory(string path) => IsOwnedBatchDirectory(path, _defaultRoot);

    /// <summary>
    /// Derives every OTHER bin root the user has historically recycled to, based on
    /// <see cref="HistoryEntry.RecyclePath"/>. Filters out the current <see cref="Root"/>. Each
    /// entry reports counts of MediaDash-shaped batch folders still on disk and their total size —
    /// so the UI can offer a "consolidate into current bin" action.
    /// </summary>
    /// <param name="db">The plugin database (source of historical recycle paths).</param>
    /// <returns>List of foreign bin roots with file counts + sizes.</returns>
    public IReadOnlyList<(string RootPath, int BatchCount, long SizeBytes)> DiscoverOtherBinRoots(MediaDashDb db)
    {
        return DiscoverOtherBinRoots(db.GetRecyclePaths(), Root);
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
    /// safe via <see cref="Api.FileBrowserController.CrossDeviceMove(string, string, bool)"/>.
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

    private void AdoptLegacyCustomBatches(MediaDashDb db)
    {
        var root = Root;
        if (PathsEqual(root, _defaultRoot) || !Directory.Exists(root))
        {
            return;
        }

        try
        {
            foreach (var recyclePath in db.GetRecyclePaths())
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
                    Api.Diagnostics.Record(
                        "RecycleBin.LegacyMigrationFailed",
                        "Could not adopt legacy recycle batch '" + candidate + "': " + ex.Message + ". Create an empty '" + OwnershipMarkerFileName + "' file inside that batch to adopt it manually.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not inspect custom recycle root {Root} for legacy batches", root);
            Api.Diagnostics.Record("RecycleBin.LegacyMigrationFailed", "Could not inspect custom recycle root '" + root + "' for legacy batches: " + ex.Message);
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
