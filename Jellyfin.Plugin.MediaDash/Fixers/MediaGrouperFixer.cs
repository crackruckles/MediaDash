using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>Executes complete approved grouping plans through the durable mover.</summary>
public sealed class MediaGrouperFixer : IFixer
{
    private readonly MediaMoveService _moves;

    /// <summary>Initializes a new instance of the <see cref="MediaGrouperFixer"/> class.</summary>
    /// <param name="moves">Durable complete-item execution and recovery.</param>
    public MediaGrouperFixer(MediaMoveService moves) => _moves = moves;

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.Ungrouped;

    /// <inheritdoc />
    public Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
        => _moves.ExecuteAsync(issue, progress, cancellationToken);
}
