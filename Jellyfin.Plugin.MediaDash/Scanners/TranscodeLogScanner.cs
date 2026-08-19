using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Walks Jellyfin's own <c>FFmpeg.Transcode-*.log</c> files in the log directory and emits
/// <see cref="IssueType.HeavyTranscode"/> for source files that had to be transcoded on the fly
/// within the lookback window, or <see cref="IssueType.FailedTranscode"/> when a session ended
/// with a failure marker instead of a clean shutdown. Both types share the transcode fixer,
/// which re-encodes the source once to a codec/bitrate the offending client can direct-play.
/// </summary>
public sealed class TranscodeLogScanner : IScanner
{
    // Peek only the head/tail of each log — headers are always front-loaded JSON, and ffmpeg's
    // exit markers land in the last few KB. Keeps this scanner cheap even when there are
    // thousands of log files.
    private const int HeaderPeekBytes = 8 * 1024;
    private const int TailPeekBytes = 4 * 1024;
    private const string LogFilePattern = "FFmpeg.Transcode-*.log";

    // ffmpeg failure markers in the tail. `Conversion failed!` is the definitive one; the others
    // catch cases where the process died before printing that (decoder open failures, etc.).
    private static readonly string[] FailureMarkers =
    {
        "Conversion failed",
        "Error while opening",
        "Error while decoding",
        "Invalid data found when processing input",
        "No such file or directory"
    };

    private readonly IApplicationPaths _appPaths;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<TranscodeLogScanner> _logger;

    /// <summary>Initializes a new instance of the <see cref="TranscodeLogScanner"/> class.</summary>
    /// <param name="appPaths">Application paths — used to locate <c>LogDirectoryPath</c>.</param>
    /// <param name="libraryManager">Library manager — used to correlate a source file to its Jellyfin item id.</param>
    /// <param name="logger">Logger.</param>
    public TranscodeLogScanner(IApplicationPaths appPaths, ILibraryManager libraryManager, ILogger<TranscodeLogScanner> logger)
    {
        _appPaths = appPaths;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    // Two IssueType outputs, but IScanner.Type is a single value. Report Heavy as the primary;
    // Failed rows carry their own type through the emitted Issues.
    public IssueType Type => IssueType.HeavyTranscode;

    /// <inheritdoc />
    // Paths in our findings aren't guaranteed to match a currently-scoped BaseItem (the file could
    // sit in a library that isn't in the current scan scope). Use unscoped semantics so the DB
    // doesn't wipe rows the scoped-delete pass wouldn't rediscover.
    public bool AlwaysUnscoped => true;

    /// <inheritdoc />
    public Task<IReadOnlyList<Issue>> ScanAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var heavyOn = config.HeavyTranscodeFixMode != Configuration.FixMode.Off;
        var failedOn = config.FailedTranscodeFixMode != Configuration.FixMode.Off;
        if (!heavyOn && !failedOn)
        {
            progress.Report(100);
            return Task.FromResult<IReadOnlyList<Issue>>(Array.Empty<Issue>());
        }

        var logDir = _appPaths.LogDirectoryPath;
        if (string.IsNullOrEmpty(logDir) || !Directory.Exists(logDir))
        {
            _logger.LogInformation("TranscodeLogScanner: log directory missing at '{Dir}'; skipping.", logDir);
            progress.Report(100);
            return Task.FromResult<IReadOnlyList<Issue>>(Array.Empty<Issue>());
        }

        var lookbackDays = Math.Max(1, config.HeavyTranscodeLookbackDays);
        var cutoff = DateTime.UtcNow.AddDays(-lookbackDays);

        var candidates = SafeEnumerateLogs(logDir, cutoff, cancellationToken);

        // Aggregate one row per source path. First seen sample wins for anchoring paths.
        var perFile = new Dictionary<string, Aggregate>(StringComparer.OrdinalIgnoreCase);
        var processed = 0;
        var total = Math.Max(1, candidates.Count);

        foreach (var log in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            progress.Report(Math.Min(99, processed * 100.0 / total));

            var parsed = ReadLogSummary(log.FullName);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.SourcePath))
            {
                continue;
            }

            if (!perFile.TryGetValue(parsed.SourcePath, out var agg))
            {
                agg = new Aggregate();
                perFile[parsed.SourcePath] = agg;
            }

            agg.Count++;
            if (parsed.Failed)
            {
                agg.FailedCount++;
                if (log.LastWriteTimeUtc > agg.LastFailureUtc)
                {
                    agg.LastFailureUtc = log.LastWriteTimeUtc;
                }
            }

            if (log.LastWriteTimeUtc > agg.LastSeenUtc)
            {
                agg.LastSeenUtc = log.LastWriteTimeUtc;
            }
        }

        var issues = new List<Issue>();
        foreach (var (path, agg) in perFile)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            if (failedOn && agg.FailedCount > 0)
            {
                issues.Add(BuildIssue(IssueType.FailedTranscode, path, agg));
            }
            else if (heavyOn)
            {
                issues.Add(BuildIssue(IssueType.HeavyTranscode, path, agg));
            }
        }

        progress.Report(100);
        _logger.LogInformation(
            "TranscodeLogScanner: {Total} log(s) inspected → {Files} distinct file(s) → {Heavy} heavy, {Failed} failed",
            processed,
            perFile.Count,
            issues.Count(i => i.Type == IssueType.HeavyTranscode),
            issues.Count(i => i.Type == IssueType.FailedTranscode));
        return Task.FromResult<IReadOnlyList<Issue>>(issues);
    }

    private Issue BuildIssue(IssueType type, string path, Aggregate agg)
    {
        var item = _libraryManager.FindByPath(path, false);
        var suggestion = type == IssueType.FailedTranscode
            ? "Re-encode with MediaDash's own settings — targeted encode usually succeeds where the live transcode failed."
            : "Re-encode once to a client-compatible profile — future plays will be direct-play instead of transcoded on demand.";

        return new Issue
        {
            Type = type,
            ItemId = item?.Id ?? Guid.Empty,
            Path = path,
            Status = IssueStatus.Detected,
            DetectedAtUtc = DateTime.UtcNow,
            SuggestedFix = suggestion,
            DetailsJson = JsonSerializer.Serialize(new
            {
                sessions = agg.Count,
                failures = agg.FailedCount,
                lastSeenUtc = agg.LastSeenUtc,
                lastFailureUtc = agg.FailedCount > 0 ? agg.LastFailureUtc : (DateTime?)null
            })
        };
    }

    private List<FileInfo> SafeEnumerateLogs(string logDir, DateTime cutoff, CancellationToken cancellationToken)
    {
        try
        {
            var all = new DirectoryInfo(logDir).EnumerateFiles(LogFilePattern, SearchOption.TopDirectoryOnly);
            var recent = new List<FileInfo>();
            foreach (var f in all)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (f.LastWriteTimeUtc >= cutoff)
                {
                    recent.Add(f);
                }
            }

            return recent;
        }
        catch (DirectoryNotFoundException)
        {
            return new List<FileInfo>();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "TranscodeLogScanner: log directory not readable at '{Dir}'.", logDir);
            Api.Diagnostics.Record("TranscodeLogScanner.EnumerateLogs", "Can't read Jellyfin transcode log directory '" + logDir + "': " + ex.Message + ". The transcode-failure scanner is skipping this run.");
            return new List<FileInfo>();
        }
    }

    // Reads only the header and tail. Header is a single-line JSON followed by the ffmpeg cmdline;
    // tail contains ffmpeg's exit summary or failure markers. Streaming from a shared read handle
    // so a live-transcoding file doesn't lock the scan.
    private static LogSummary? ReadLogSummary(string logPath)
    {
        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var headerBytes = new byte[Math.Min(HeaderPeekBytes, (int)Math.Min(stream.Length, int.MaxValue))];
            var headerRead = stream.Read(headerBytes, 0, headerBytes.Length);
            var head = System.Text.Encoding.UTF8.GetString(headerBytes, 0, headerRead);

            var sourcePath = ExtractSourcePath(head);
            if (sourcePath is null)
            {
                return null;
            }

            var tailBytes = new byte[Math.Min(TailPeekBytes, (int)Math.Min(stream.Length, int.MaxValue))];
            var tailOffset = Math.Max(0, stream.Length - tailBytes.Length);
            stream.Seek(tailOffset, SeekOrigin.Begin);
            var tailRead = stream.Read(tailBytes, 0, tailBytes.Length);
            var tail = System.Text.Encoding.UTF8.GetString(tailBytes, 0, tailRead);

            var failed = false;
            foreach (var marker in FailureMarkers)
            {
                if (tail.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    failed = true;
                    break;
                }
            }

            return new LogSummary(sourcePath, failed);
        }
        catch (IOException)
        {
            // File may be in the middle of being written; skip this pass. Next scan will pick it up.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // First line of the log is a JSON object with a `Path` property naming the source file. We
    // parse just enough of it to extract that; the rest of the fields (streams, bitrate, size) are
    // interesting for a future v2 but the fix action only needs the path.
    private static string? ExtractSourcePath(string head)
    {
        var newline = head.IndexOf('\n', StringComparison.Ordinal);
        if (newline <= 0)
        {
            return null;
        }

        var firstLine = head[..newline].Trim();
        if (firstLine.Length == 0 || firstLine[0] != '{')
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(firstLine);
            if (doc.RootElement.TryGetProperty("Path", out var pathEl)
                && pathEl.ValueKind == JsonValueKind.String)
            {
                var p = pathEl.GetString();
                if (!string.IsNullOrWhiteSpace(p))
                {
                    return p;
                }
            }
        }
        catch (JsonException)
        {
            // Header wasn't valid JSON (older ffmpeg log format, or truncated write). Skip.
        }

        return null;
    }

    private sealed class Aggregate
    {
        public int Count { get; set; }

        public int FailedCount { get; set; }

        public DateTime LastSeenUtc { get; set; }

        public DateTime LastFailureUtc { get; set; }
    }

    private sealed record LogSummary(string SourcePath, bool Failed);
}
