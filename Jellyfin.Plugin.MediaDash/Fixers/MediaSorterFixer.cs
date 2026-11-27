using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>Executes complete approved library-routing plans through the durable mover.</summary>
public sealed class MediaSorterFixer : IFixer
{
    private readonly MediaMoveService _moves;

    /// <summary>Initializes a new instance of the <see cref="MediaSorterFixer"/> class.</summary>
    /// <param name="moves">Durable complete-item execution and recovery.</param>
    public MediaSorterFixer(MediaMoveService moves) => _moves = moves;

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.Misplaced;

    /// <inheritdoc />
    public Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
        => _moves.ExecuteAsync(issue, progress, cancellationToken);
}
