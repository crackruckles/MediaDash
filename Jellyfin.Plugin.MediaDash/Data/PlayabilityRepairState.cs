using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.MediaDash.Data;

// Failed repair follows the source size and modification time across rescans.
internal static class PlayabilityRepairState
{
    internal static bool IsCurrent(Issue issue)
    {
        if (issue.Type != IssueType.Playability)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(issue.DetailsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("repair_failed", out var failed) || failed.ValueKind != JsonValueKind.Object
                || !failed.TryGetProperty("length", out var length) || !length.TryGetInt64(out var size) || size < 0
                || !failed.TryGetProperty("mtime_ticks", out var mtime) || !mtime.TryGetInt64(out var ticks) || ticks <= 0)
            {
                return false;
            }

            var file = new FileInfo(issue.Path);
            return file.Exists && file.Length == size && file.LastWriteTimeUtc.Ticks == ticks;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static void Apply(Issue issue, long length, long mtimeTicks)
    {
        var details = JsonNode.Parse(issue.DetailsJson) as JsonObject ?? new JsonObject();
        details["repair_failed"] = new JsonObject { ["length"] = length, ["mtime_ticks"] = mtimeTicks };
        var warnings = details["warnings"] as JsonArray;
        if (warnings is null)
        {
            warnings = new JsonArray();
            details["warnings"] = warnings;
        }

        // Apply is also used for fresh scanner payloads; don't duplicate an existing warning.
        if (!issue.HasBlockingWarnings)
        {
            warnings.Add(new JsonObject
            {
                ["code"] = "repair-failed-delete",
                ["message"] = "Repair failed. The file is unchanged. Delete removes it using your disposal setting; Ignore keeps it.",
                ["blocking"] = true
            });
        }

        issue.DetailsJson = details.ToJsonString();
        issue.SuggestedFix = "Repair failed. Delete this file, or Ignore to keep it. Deletion requires your approval.";
    }
}
