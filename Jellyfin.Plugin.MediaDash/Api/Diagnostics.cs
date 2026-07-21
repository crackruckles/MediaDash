using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Bounded ring buffer of recent plugin errors, surfaced by the Errors tab.
/// Callers push here from catch blocks so failures that would otherwise be invisible
/// (silent nulls from a system-stats sample, per-file scan failures, cancelled fixes)
/// show up in one place the admin can look at.
/// </summary>
public static class Diagnostics
{
    private const int MaxEntries = 100;

    private static readonly object Lock = new();
    private static readonly LinkedList<DiagnosticEntry> Entries = new();

    /// <summary>Records a diagnostic event. Message is truncated to avoid huge stack traces.</summary>
    /// <param name="source">Short source label (e.g., "SystemStats.Linux", "PlayabilityScanner").</param>
    /// <param name="message">Human-readable description of what went wrong.</param>
    public static void Record(string source, string message)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var trimmed = message.Length > 800 ? message[..800] + "…" : message;
        lock (Lock)
        {
            Entries.AddFirst(new DiagnosticEntry
            {
                AtUtc = DateTime.UtcNow,
                Source = source,
                Message = trimmed
            });
            while (Entries.Count > MaxEntries)
            {
                Entries.RemoveLast();
            }
        }
    }

    /// <summary>Returns the currently-buffered entries, newest first.</summary>
    /// <returns>The entries.</returns>
    public static IReadOnlyList<DiagnosticEntry> Recent()
    {
        lock (Lock)
        {
            return Entries.ToList();
        }
    }

    /// <summary>Empties the buffer.</summary>
    public static void Clear()
    {
        lock (Lock)
        {
            Entries.Clear();
        }
    }
}
