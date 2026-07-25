using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// Per-type success counts and total bytes freed across a single month of fix history.
/// </summary>
/// <param name="ByType">Count of successful, non-dry-run fixes grouped by <see cref="IssueType"/>.</param>
/// <param name="BytesFreed">Sum of bytes freed by those fixes.</param>
public sealed record MonthAggregate(IReadOnlyDictionary<IssueType, int> ByType, long BytesFreed);
