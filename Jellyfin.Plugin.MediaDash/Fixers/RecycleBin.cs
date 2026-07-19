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
    /// Moves a file into the recycle bin.
    /// </summary>
    /// <param name="filePath">The file to recycle.</param>
    /// <returns>The file's location inside the bin.</returns>
    public string MoveToBin(string filePath)
    {
        var folder = Path.Combine(Root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Path.GetFileName(filePath));
        MoveAcrossVolumes(filePath, target);
        _logger.LogInformation("Recycled {Path} -> {Target}", filePath, target);
        return target;
    }

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
