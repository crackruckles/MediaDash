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
    private readonly string _defaultRoot;
    private readonly ILogger<RecycleBin> _logger;

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
            return string.IsNullOrWhiteSpace(configured) ? _defaultRoot : configured;
        }
    }

    /// <summary>
    /// Moves a file or directory into the recycle bin.
    /// </summary>
    /// <param name="path">The file or directory to recycle.</param>
    /// <returns>The item's location inside the bin.</returns>
    public string MoveToBin(string path)
    {
        var folder = Path.Combine(Root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Path.GetFileName(path));

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
            Directory.Move(path, target);
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
                .Where(d => full.StartsWith(d.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
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
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            count++;
            size += new FileInfo(file).Length;
        }

        return (count, size);
    }

    /// <summary>
    /// Lists the files currently held in the bin, newest first.
    /// </summary>
    /// <param name="limit">Maximum entries returned.</param>
    /// <returns>File name, size and when it was recycled.</returns>
    public IReadOnlyList<(string FileName, long SizeBytes, DateTime RecycledAtUtc)> ListContents(int limit = 500)
    {
        var result = new List<(string, long, DateTime)>();
        if (!Directory.Exists(Root))
        {
            return result;
        }

        foreach (var dir in Directory.GetDirectories(Root).OrderByDescending(d => d, StringComparer.Ordinal))
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var info = new FileInfo(file);
                result.Add((info.Name, info.Length, Directory.GetCreationTimeUtc(dir)));
                if (result.Count >= limit)
                {
                    return result;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Permanently deletes everything in the bin, regardless of retention.
    /// </summary>
    public void EmptyAll()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(Root))
        {
            Directory.Delete(dir, recursive: true);
        }

        _logger.LogInformation("Recycle bin emptied by user request");
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
        foreach (var dir in Directory.GetDirectories(Root))
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
            }
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
            // Cross-volume move: copy then delete.
            File.Copy(source, target, overwrite: false);
            File.Delete(source);
        }
    }
}
