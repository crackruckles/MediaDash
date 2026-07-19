using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Base for scanners that evaluate each library file individually from its ffprobe data.
/// </summary>
public abstract class ProbingScannerBase : IScanner
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProbingScannerBase"/> class.
    /// </summary>
    /// <param name="ffprobe">The probe service.</param>
    /// <param name="logger">The logger.</param>
    protected ProbingScannerBase(FfprobeService ffprobe, ILogger logger)
    {
        Ffprobe = ffprobe;
        Logger = logger;
    }

    /// <inheritdoc />
    public abstract IssueType Type { get; }

    /// <summary>
    /// Gets the probe service.
    /// </summary>
    protected FfprobeService Ffprobe { get; }

    /// <summary>
    /// Gets the logger.
    /// </summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// Gets the current plugin configuration.
    /// </summary>
    protected static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <inheritdoc />
    public async Task<IReadOnlyList<Issue>> ScanAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var issues = new List<Issue>();
        if (!IsConfigured())
        {
            Logger.LogInformation("Scanner {Type} skipped: not configured yet", Type);
            return issues;
        }

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[i];
            foreach (var path in MediaFileHelper.GetFilePaths(item))
            {
                var probe = await Ffprobe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
                var issue = await EvaluateAsync(item, path, probe, cancellationToken).ConfigureAwait(false);
                if (issue is not null)
                {
                    issue.Type = Type;
                    issue.ItemId = item.Id;
                    issue.Path = path;
                    issue.Status = IssueStatus.Detected;
                    issue.DetectedAtUtc = DateTime.UtcNow;
                    issues.Add(issue);
                }
            }

            progress.Report((i + 1) * 100.0 / items.Count);
        }

        return issues;
    }

    /// <summary>
    /// Checks whether the scanner has the configuration it needs to run at all.
    /// </summary>
    /// <returns>True when the scanner should run.</returns>
    protected virtual bool IsConfigured() => true;

    /// <summary>
    /// Evaluates a single file. Return null when the file is fine.
    /// </summary>
    /// <param name="item">The library item the file belongs to.</param>
    /// <param name="path">The file path being evaluated.</param>
    /// <param name="probe">The ffprobe result; null when the file is missing or ffprobe could not run.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An issue with the scanner-specific fields set, or null.</returns>
    protected abstract Task<Issue?> EvaluateAsync(BaseItem item, string path, FfprobeData? probe, CancellationToken cancellationToken);
}
