using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Walks each library folder tree, finds directories that hold audio files whose embedded artwork
/// is duplicated across tracks but for which no folder-level <c>cover.jpg</c> / <c>folder.jpg</c>
/// exists. Emits one <see cref="IssueType.EmbeddedCoverArt"/> per candidate folder — the fixer
/// extracts a shared cover file and (optionally) strips the redundant per-file copies.
/// </summary>
public sealed class EmbeddedCoverArtScanner : IScanner
{
    // Audio formats that reliably carry embedded cover art through ffmpeg. Superset of the older
    // 10-format list, now covering AIFF/APE/DSF/Matroska-audio too. Kept curated (not
    // MediaFormats.Audio) because module trackers, chiptune, cue sheets, playlists, and DRM
    // audiobook formats either have no cover-art concept or need dedicated tooling — a shared
    // "cover.jpg" extraction pass would run and produce nothing useful.
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".m4b", ".aac", ".opus", ".ogg", ".oga", ".wav", ".wma",
        ".aiff", ".aif", ".aifc", ".ape", ".dsf", ".dff", ".mka", ".wv", ".mpc", ".mp2"
    };

    // Jellyfin considers any of these as folder-level artwork; skip folders that already have one.
    private static readonly string[] FolderCoverBaseNames = { "cover", "folder", "album", "front" };
    private static readonly string[] FolderCoverExtensions = { ".jpg", ".jpeg", ".png", ".webp" };

    private readonly ILibraryManager _libraryManager;
    private readonly FfprobeService _ffprobe;
    private readonly ILogger<EmbeddedCoverArtScanner> _logger;

    /// <summary>Initializes a new instance of the <see cref="EmbeddedCoverArtScanner"/> class.</summary>
    /// <param name="libraryManager">Library manager — used to walk configured library roots.</param>
    /// <param name="ffprobe">The probe service — cheap because probe results are cached in the plugin DB.</param>
    /// <param name="logger">The logger.</param>
    public EmbeddedCoverArtScanner(ILibraryManager libraryManager, FfprobeService ffprobe, ILogger<EmbeddedCoverArtScanner> logger)
    {
        _libraryManager = libraryManager;
        _ffprobe = ffprobe;
        _logger = logger;
    }

    /// <inheritdoc />
    public IssueType Type => IssueType.EmbeddedCoverArt;

    /// <inheritdoc />
    // Findings are folder paths, not BaseItem paths — bypass the scoped-delete pass that would
    // otherwise wipe them because folder rows don't match a currently-scoped video file.
    public bool AlwaysUnscoped => true;

    /// <inheritdoc />
    public async Task<IReadOnlyList<Issue>> ScanAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (config.EmbeddedCoverFixMode == Configuration.FixMode.Off)
        {
            progress.Report(100);
            return Array.Empty<Issue>();
        }

        // Only walk libraries the user opted into via Settings → Libraries — otherwise we'd
        // rewrite audio files (or delete embedded covers) in a music library the user chose NOT
        // to enable in MediaDash. Same class of bug as the 2026-08-23 field report on
        // OrphanCleanupScanner. See VirtualFolderIdentity.GetEnabledFolders.
        var libraryLocations = VirtualFolderIdentity.GetEnabledFolders(_libraryManager, config.EnabledLibraries)
            .Where(f => IsAudioLibrary(f.CollectionType?.ToString()))
            .SelectMany(f => f.Locations ?? Array.Empty<string>())
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (libraryLocations.Count == 0)
        {
            _logger.LogInformation("EmbeddedCoverArtScanner: no music / audiobook libraries configured; skipping.");
            progress.Report(100);
            return Array.Empty<Issue>();
        }

        // First pass: collect every candidate folder (has audio, no folder cover) so we can report
        // progress meaningfully. Second pass probes the first audio file in each.
        var candidates = new List<string>();
        foreach (var root in libraryLocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CollectCandidates(root, candidates, cancellationToken);
        }

        if (candidates.Count == 0)
        {
            progress.Report(100);
            _logger.LogInformation("EmbeddedCoverArtScanner: no candidate folders — everything already has a shared cover.");
            return Array.Empty<Issue>();
        }

        var issues = new List<Issue>();
        for (var i = 0; i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = candidates[i];
            progress.Report(i * 100.0 / candidates.Count);

            var audioFiles = SafeListAudioFiles(folder);
            if (audioFiles.Count == 0)
            {
                continue;
            }

            var probeTarget = audioFiles[0];
            FfprobeData? probe;
            try
            {
                probe = await _ffprobe.ProbeAsync(probeTarget, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EmbeddedCoverArtScanner: probe failed for {Path}", probeTarget);
                continue;
            }

            if (!HasEmbeddedCover(probe))
            {
                continue;
            }

            var savings = EstimateSavings(audioFiles, probe);
            issues.Add(new Issue
            {
                Type = IssueType.EmbeddedCoverArt,
                Path = folder,
                Status = IssueStatus.Detected,
                DetectedAtUtc = DateTime.UtcNow,
                SizeSavings = savings,
                SuggestedFix = config.EmbeddedCoverStripFromAudio
                    ? "Extract the shared cover to " + config.EmbeddedCoverFilename + " and strip the duplicate embedded copies from each audio file."
                    : "Extract the shared cover to " + config.EmbeddedCoverFilename + " so Jellyfin uses it instead of re-decoding per file.",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    audioFileCount = audioFiles.Count,
                    sampleProbedFile = Path.GetFileName(probeTarget),
                    coverBytesEmbedded = EstimateCoverBytes(probe)
                })
            });
        }

        progress.Report(100);
        _logger.LogInformation("EmbeddedCoverArtScanner: {Count} folder(s) with duplicated embedded artwork.", issues.Count);
        return issues;
    }

    // Recurse each library root looking for directories that:
    //   (a) contain at least one audio file directly (not descendants), AND
    //   (b) do NOT already have a folder-level cover file at that level.
    // Descend into subdirectories regardless (albums can be nested under artist folders).
    private void CollectCandidates(string dir, List<string> candidates, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<string> files;
        IEnumerable<string> subdirs;
        try
        {
            files = Directory.EnumerateFiles(dir);
            subdirs = Directory.EnumerateDirectories(dir);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        var hasAudio = false;
        var hasFolderCover = false;
        foreach (var f in files)
        {
            if (!hasAudio && AudioExtensions.Contains(Path.GetExtension(f)))
            {
                hasAudio = true;
            }

            if (!hasFolderCover && IsFolderCover(Path.GetFileName(f)))
            {
                hasFolderCover = true;
            }

            if (hasAudio && hasFolderCover)
            {
                break;
            }
        }

        if (hasAudio && !hasFolderCover)
        {
            candidates.Add(dir);
        }

        foreach (var sub in subdirs)
        {
            CollectCandidates(sub, candidates, cancellationToken);
        }
    }

    private static bool IsAudioLibrary(string? collectionType)
    {
        // Jellyfin CollectionType values that carry audio content Jellyfin will look for cover art on.
        return string.Equals(collectionType, "music", StringComparison.OrdinalIgnoreCase)
            || string.Equals(collectionType, "musicvideos", StringComparison.OrdinalIgnoreCase)
            || string.Equals(collectionType, "audiobooks", StringComparison.OrdinalIgnoreCase)
            || string.Equals(collectionType, "books", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsFolderCover(string filename)
    {
        var ext = Path.GetExtension(filename);
        if (string.IsNullOrEmpty(ext) || Array.IndexOf(FolderCoverExtensions, ext.ToLowerInvariant()) < 0)
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(filename);
        foreach (var basename in FolderCoverBaseNames)
        {
            if (name.Equals(basename, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasEmbeddedCover(FfprobeData? probe)
    {
        if (probe?.Streams is null)
        {
            return false;
        }

        foreach (var stream in probe.Streams)
        {
            // Cover art in mp3/flac/m4a/m4b is stored as a video stream carrying an image codec.
            if (!string.Equals(stream.CodecType, "video", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var codec = stream.CodecName ?? string.Empty;
            if (codec.Equals("mjpeg", StringComparison.OrdinalIgnoreCase)
                || codec.Equals("png", StringComparison.OrdinalIgnoreCase)
                || codec.Equals("jpeg", StringComparison.OrdinalIgnoreCase)
                || codec.Equals("webp", StringComparison.OrdinalIgnoreCase)
                || codec.Equals("bmp", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> SafeListAudioFiles(string folder)
    {
        try
        {
            var list = new List<string>();
            foreach (var f in Directory.EnumerateFiles(folder))
            {
                if (AudioExtensions.Contains(Path.GetExtension(f)))
                {
                    list.Add(f);
                }
            }

            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }
        catch (IOException)
        {
            return new List<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    // Rough estimate: cover bytes per file × (file count - 1). Undercounts if covers differ per track.
    private static long EstimateSavings(List<string> audioFiles, FfprobeData? probe)
    {
        var coverBytes = EstimateCoverBytes(probe);
        return audioFiles.Count > 1 ? coverBytes * (audioFiles.Count - 1) : 0;
    }

    private static long EstimateCoverBytes(FfprobeData? probe)
    {
        if (probe?.Streams is null)
        {
            return 0;
        }

        foreach (var s in probe.Streams)
        {
            if (!string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (long.TryParse(s.BitRate, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var bps)
                && bps > 0)
            {
                // Cover streams report their byte count under BitRate for still-image streams. Fallback = ~500 KB.
                return bps / 8;
            }
        }

        return 500_000;
    }
}
