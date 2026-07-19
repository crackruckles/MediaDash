using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// A fixer resolves queued issues of one or more types.
/// </summary>
public interface IFixer
{
    /// <summary>
    /// Checks whether this fixer handles the given issue type.
    /// </summary>
    /// <param name="type">The issue type.</param>
    /// <returns>True when this fixer applies.</returns>
    bool CanFix(IssueType type);

    /// <summary>
    /// Applies the fix for one issue. Honors the global dry-run setting.
    /// </summary>
    /// <param name="issue">The issue to fix.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome.</returns>
    Task<FixResult> FixAsync(Issue issue, CancellationToken cancellationToken);
}
