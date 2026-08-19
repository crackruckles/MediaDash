using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// A scanner inspects library items for one category of problem and reports issues.
/// </summary>
public interface IScanner
{
    /// <summary>
    /// Gets the issue type this scanner produces.
    /// </summary>
    IssueType Type { get; }

    /// <summary>
    /// Gets a value indicating whether this scanner's results replace ALL prior detected issues of its
    /// type on every run, ignoring the per-library scoped-paths filter. Set true for scanners whose
    /// issue paths aren't video files (orphan folder scans, trickplay folder scans, subtitle sidecar
    /// scans) — the default scoped-delete only wipes rows whose path matches a video file in the
    /// currently-scoped items, which leaves stale non-video-path issues sitting in the DB forever.
    /// </summary>
    bool AlwaysUnscoped => false;

    /// <summary>
    /// Scans the given library items and returns all detected issues.
    /// </summary>
    /// <param name="items">The media items to inspect.</param>
    /// <param name="progress">Progress reporter (0-100 within this scanner's work).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The detected issues.</returns>
    Task<IReadOnlyList<Issue>> ScanAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken cancellationToken);
}
