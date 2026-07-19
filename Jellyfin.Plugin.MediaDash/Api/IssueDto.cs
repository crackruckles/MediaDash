using System;
using Jellyfin.Plugin.MediaDash.Data;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// An issue as shown in the dashboard.
/// </summary>
public sealed class IssueDto
{
    /// <summary>
    /// Gets or sets the issue id.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the issue type name.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the full file path.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file name without directory.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the plain-language description of the suggested fix.
    /// </summary>
    public string SuggestedFix { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets scanner-specific details as JSON.
    /// </summary>
    public string DetailsJson { get; set; } = "{}";

    /// <summary>
    /// Gets or sets the estimated bytes reclaimed by the fix.
    /// </summary>
    public long SizeSavings { get; set; }

    /// <summary>
    /// Gets or sets the issue status name.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the issue was detected (UTC).
    /// </summary>
    public DateTime DetectedAtUtc { get; set; }

    /// <summary>
    /// Maps a database issue to the DTO.
    /// </summary>
    /// <param name="issue">The issue.</param>
    /// <returns>The DTO.</returns>
    public static IssueDto FromIssue(Issue issue)
    {
        return new IssueDto
        {
            Id = issue.Id,
            Type = issue.Type.ToString(),
            Path = issue.Path,
            FileName = System.IO.Path.GetFileName(issue.Path),
            SuggestedFix = issue.SuggestedFix,
            DetailsJson = issue.DetailsJson,
            SizeSavings = issue.SizeSavings,
            Status = issue.Status.ToString(),
            DetectedAtUtc = issue.DetectedAtUtc
        };
    }
}
