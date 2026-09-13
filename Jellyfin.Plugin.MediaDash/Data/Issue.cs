using System;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// A single problem detected by a scanner, tracked through its fix lifecycle.
/// </summary>
public sealed class Issue
{
    /// <summary>
    /// Gets or sets the database row id.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the issue category.
    /// </summary>
    public IssueType Type { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin library item id the file belongs to.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the full path of the affected file.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets scanner-specific details as a JSON document.
    /// </summary>
    public string DetailsJson { get; set; } = "{}";

    /// <summary>
    /// Gets or sets a human-readable description of the suggested fix.
    /// </summary>
    public string SuggestedFix { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the estimated bytes reclaimed if the fix is applied.
    /// </summary>
    public long SizeSavings { get; set; }

    /// <summary>
    /// Gets or sets the lifecycle status.
    /// </summary>
    public IssueStatus Status { get; set; }

    /// <summary>
    /// Gets or sets when the issue was detected (UTC).
    /// </summary>
    public DateTime DetectedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the confidence in <c>[0,1]</c> that this pair actually represents a duplicate
    /// (see DuplicateScanner confidence ladder). <c>null</c> for non-Duplicate issue types and for
    /// pre-migration rows. The auto-queue gate filters Duplicate rows below the configured
    /// threshold; <c>null</c> is treated as "not gated" so non-Duplicate types still queue normally.
    /// </summary>
    public double? Confidence { get; set; }

    /// <summary>
    /// Gets a value indicating whether <see cref="DetailsJson"/> carries at least one warning
    /// with <c>blocking = true</c>. The FixTask auto-queue step uses this to keep issues as
    /// Detected even when the type's FixMode is Automatic — manual Approve is required so the
    /// user can knowingly consent to the data loss the warning describes. Never throws:
    /// malformed / missing JSON returns false, preserving the historical auto-queue behaviour
    /// for pre-migration rows that never got a warnings array.
    /// </summary>
    public bool HasBlockingWarnings => TryReadBlockingWarnings();

    private bool TryReadBlockingWarnings()
    {
        if (string.IsNullOrWhiteSpace(DetailsJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(DetailsJson);
            // F-016: TryGetProperty on a non-object root throws InvalidOperationException,
            // which `catch (JsonException)` doesn't match. Guard root type before touching
            // properties. CLAUDE.md safety invariant #6.
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!doc.RootElement.TryGetProperty("warnings", out var warnings) || warnings.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var w in warnings.EnumerateArray())
            {
                if (w.ValueKind == JsonValueKind.Object
                    && w.TryGetProperty("blocking", out var b)
                    && b.ValueKind == JsonValueKind.True)
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }
}
