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
    /// Scans the given library items and returns all detected issues.
    /// </summary>
    /// <param name="items">The media items to inspect.</param>
    /// <param name="progress">Progress reporter (0-100 within this scanner's work).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The detected issues.</returns>
    Task<IReadOnlyList<Issue>> ScanAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken cancellationToken);
}
