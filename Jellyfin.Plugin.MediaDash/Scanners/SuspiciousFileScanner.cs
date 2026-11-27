using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Walks the library folder trees looking for executables and scripts. A media library should
/// contain video, audio, subtitle and artwork files — nothing else. An .exe sitting next to a
/// movie is almost always malware bundled with a pirated rip, so surface it as a deletion candidate.
/// This scanner ignores the <see cref="BaseItem"/> list entirely because these files are not
/// indexed by Jellyfin — they live in the folders but never become library items.
/// </summary>
public sealed class SuspiciousFileScanner : IScanner
{
    // ponytail: hand-curated list of extensions that never belong in a media library. Grow it if the
    // wild throws a new sample at us, but resist expanding to genuinely ambiguous extensions (.js, .iso).
    private static readonly HashSet<string> SuspiciousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".com", ".scr", ".pif", ".cpl",
        ".bat", ".cmd", ".ps1", ".vbs", ".wsf", ".hta", ".jse",
        ".jar", ".dll", ".lnk", ".app", ".sh", ".run"
    };

    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SuspiciousFileScanner> _logger;
    private readonly ProtectionService? _protection;

    /// <summary>
    /// Initializes a new instance of the <see cref="SuspiciousFileScanner"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="protection">Rules excluding protected files from analysis.</param>
    /// <param name="logger">The logger.</param>
    public SuspiciousFileScanner(ILibraryManager libraryManager, ILogger<SuspiciousFileScanner> logger, ProtectionService? protection = null)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _protection = protection;
    }

    internal List<string> CompletedRoots { get; } = new();

    /// <inheritdoc />
    public IssueType Type => IssueType.MalwareRisk;

    /// <inheritdoc />
    // MD-007: this scanner emits executable/script paths that never match ScanTask's media-only
    // scannedPaths list, so the default scoped-delete branch used to leave prior Detected rows in
    // place and every scoped scan appended another duplicate. Opting out of scoped replacement is
    // safe because RunScanAsync already walks every currently-enabled library root each run —
    // the returned set IS the authoritative snapshot for MalwareRisk under the current scope.
    public bool AlwaysUnscoped => true;

    /// <summary>
    /// Returns true when a file path's extension is on the suspicious list. Extracted for direct unit-testing.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>True when the extension is one MediaDash flags.</returns>
    internal static bool IsSuspicious(string path)
    {
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && SuspiciousExtensions.Contains(ext);
    }

    /// <summary>
    /// Builds the finding for a disc image that is not a video disc: one holding programs or scripts
    /// (a malware wrapper posing as an episode or film), or one holding no video at all. Real DVD and
    /// Blu-ray images, and images MediaDash cannot read, are never flagged.
    /// </summary>
    /// <param name="path">The .iso or .img file.</param>
    /// <returns>The finding, or null when the image looks like a real disc or cannot be read.</returns>
    internal static Issue? DiscImageIssue(string path)
    {
        var contents = DiscImageInspector.Inspect(path);
        if (!contents.HoldsPrograms && !contents.HoldsNoVideo)
        {
            return null;
        }

        long size = 0;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (IOException)
        {
        }

        var programs = contents.Programs.Take(5).ToList();
        return new Issue
        {
            Type = IssueType.MalwareRisk,
            ItemId = Guid.Empty,
            Path = path,
            Status = IssueStatus.Detected,
            DetectedAtUtc = DateTime.UtcNow,
            SizeSavings = size,
            DetailsJson = contents.HoldsPrograms
                ? JsonSerializer.Serialize(new { extension = Path.GetExtension(path).ToLowerInvariant(), discImage = "programs", programs })
                : JsonSerializer.Serialize(new
                {
                    extension = Path.GetExtension(path).ToLowerInvariant(),
                    discImage = "noVideo",
                    fileCount = contents.FileCount,
                    warnings = new[]
                    {
                        new IssueWarning
                        {
                            Code = "disc-image-no-video",
                            Blocking = true,
                            Message = "This disc image holds no DVD or Blu-ray video, so it is probably software or a game rather than media. Approve only if you did not put it here on purpose."
                        }
                    }
                }),
            SuggestedFix = contents.HoldsPrograms
                ? "This disc image is not a video: it holds the program file(s) " + string.Join(", ", programs.Select(Path.GetFileName))
                    + ". Disc images posing as episodes or films are a common way malware spreads. Delete it unless you put it there on purpose; a real copy of the video is a normal video file."
                : "This disc image holds no DVD or Blu-ray video, so Jellyfin cannot play it. Delete it unless it is software or a game you keep here on purpose."
        };
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Issue>> ScanAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance!.Configuration.SuspiciousFileFixMode == FixMode.Off)
        {
            progress.Report(100);
            return Task.FromResult<IReadOnlyList<Issue>>([]);
        }

        return RunScanAsync(progress, cancellationToken);
    }

    /// <summary>
    /// Walks the library roots for suspicious files. Bypasses the <see cref="FixMode.Off"/> gate that
    /// <see cref="ScanAsync"/> applies, so the Maintenance "Start virus scan" button can force a
    /// one-shot scan even when the scheduled task is turned off.
    /// </summary>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="completedRoots">Receives roots whose traversal completed successfully.</param>
    /// <returns>Detected suspicious-file issues.</returns>
    internal Task<IReadOnlyList<Issue>> RunScanAsync(IProgress<double> progress, CancellationToken cancellationToken, List<string>? completedRoots = null)
    {
        CompletedRoots.Clear();
        var config = Plugin.Instance!.Configuration;
        var enabled = config.EnabledLibraries ?? [];
        var idLookup = enabled.Length == 0 ? null : VirtualFolderIdentity.BuildIdLookup(_libraryManager);
        var roots = _libraryManager.GetVirtualFolders()
            .Where(f => enabled.Length == 0 || enabled.Contains(VirtualFolderIdentity.GetId(f, idLookup), StringComparer.OrdinalIgnoreCase))
            .SelectMany(f => f.Locations ?? [])
            .Where(Directory.Exists)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToList();

        var issues = new List<Issue>();
        for (var i = 0; i < roots.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = roots[i];
            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*", WalkOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_protection?.IsScannerBlocked(path, Type) == true)
                    {
                        continue;
                    }

                    if (DiscImageInspector.IsDiscImage(path))
                    {
                        if (DiscImageIssue(path) is { } discIssue)
                        {
                            issues.Add(discIssue);
                        }

                        continue;
                    }

                    if (!IsSuspicious(path))
                    {
                        continue;
                    }

                    long size = 0;
                    try
                    {
                        size = new FileInfo(path).Length;
                    }
                    catch (IOException)
                    {
                    }

                    issues.Add(new Issue
                    {
                        Type = Type,
                        ItemId = Guid.Empty,
                        Path = path,
                        Status = IssueStatus.Detected,
                        DetectedAtUtc = DateTime.UtcNow,
                        SizeSavings = size,
                        DetailsJson = JsonSerializer.Serialize(new
                        {
                            extension = Path.GetExtension(path).ToLowerInvariant()
                        }),
                        SuggestedFix = "Executable file inside a media folder. Almost always malware from a pirated release — delete unless you put it there on purpose."
                    });
                }

                CompletedRoots.Add(root);
                completedRoots?.Add(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "SuspiciousFileScanner: could not walk {Root}", root);
                Api.Diagnostics.Record("SuspiciousFileScanner.WalkFailed", "The suspicious file check could not read '" + root + "'. Give the user Jellyfin runs as read access to that folder. Other libraries were still checked. Details: " + ex.Message);
            }

            progress.Report((i + 1) * 100.0 / Math.Max(roots.Count, 1));
        }

        progress.Report(100);
        return Task.FromResult<IReadOnlyList<Issue>>(issues);
    }
}
