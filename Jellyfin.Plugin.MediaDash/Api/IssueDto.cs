using System;
using System.Collections.Generic;
using System.Text.Json;
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
    /// Gets or sets the Jellyfin library item id — used by the UI to link back to the item's detail page.
    /// </summary>
    public Guid ItemId { get; set; }

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
    /// Gets or sets a value indicating whether the user previously restored a file at this
    /// (path, type). When true, MediaDash keeps the row Detected instead of auto-queuing it —
    /// the UI renders a "You restored this before" badge so the user understands why the row
    /// isn't scheduled for a fix run and can Approve or Dismiss it manually.
    /// </summary>
    public bool WasPreviouslyRestored { get; set; }

    /// <summary>
    /// Gets or sets the data-loss / consent warnings the scanner attached to this issue.
    /// Empty when the fix incurs no data loss (the common case). The UI renders each warning
    /// under the row and, for blocking warnings, hoists the "⚠ needs review" chip so the user
    /// knows an Approve click is required before the fix runs. Parsed from
    /// <c>DetailsJson.warnings[]</c> — malformed roots yield an empty list, never throw
    /// (CLAUDE.md safety invariant #6).
    /// </summary>
    public IReadOnlyList<IssueWarning> Warnings { get; set; } = Array.Empty<IssueWarning>();

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
            ItemId = issue.ItemId,
            Type = issue.Type.ToString(),
            Path = issue.Path,
            FileName = System.IO.Path.GetFileName(issue.Path),
            SuggestedFix = issue.SuggestedFix,
            DetailsJson = issue.DetailsJson,
            SizeSavings = issue.SizeSavings,
            Status = issue.Status.ToString(),
            DetectedAtUtc = issue.DetectedAtUtc,
            Warnings = ParseWarnings(issue.DetailsJson)
        };
    }

    /// <summary>
    /// Parses the <c>warnings[]</c> array out of a raw DetailsJson blob. Never throws:
    /// malformed roots, missing keys, wrong types, and non-object entries all yield an empty
    /// list. Kept internal and static so unit tests can pin the guardrail directly.
    /// </summary>
    /// <param name="detailsJson">The raw DetailsJson from the DB.</param>
    /// <returns>The list of warnings; empty when none apply.</returns>
    internal static IReadOnlyList<IssueWarning> ParseWarnings(string detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return Array.Empty<IssueWarning>();
        }

        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            // F-016 pattern: TryGetProperty on non-object throws IOE, not JsonException.
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("warnings", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<IssueWarning>();
            }

            var list = new List<IssueWarning>();
            foreach (var w in arr.EnumerateArray())
            {
                if (w.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                list.Add(new IssueWarning
                {
                    Code = ReadString(w, "code") ?? string.Empty,
                    Message = ReadString(w, "message") ?? string.Empty,
                    Blocking = w.TryGetProperty("blocking", out var b) && b.ValueKind == JsonValueKind.True
                });
            }

            return list;
        }
        catch (JsonException)
        {
            return Array.Empty<IssueWarning>();
        }
    }

    private static string? ReadString(JsonElement obj, string key)
    {
        return obj.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }
}
