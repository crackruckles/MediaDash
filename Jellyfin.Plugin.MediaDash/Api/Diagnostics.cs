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
        // Dedup key hashes the *pre-truncation* message so two different long errors that share the
        // truncation prefix don't get collapsed into one entry with a misleading Count.
        var messageHash = message.GetHashCode(StringComparison.Ordinal);
        var now = DateTime.UtcNow;
        lock (Lock)
        {
            // Dedup: if the most recent entry has the same source + full message, replace it with a new
            // instance whose Count is bumped and LastAtUtc is updated. Replacing (rather than mutating
            // the existing entry) avoids torn reads for concurrent Recent() callers whose returned list
            // still holds references to the previous instance.
            var head = Entries.First;
            if (head is not null
                && string.Equals(head.Value.Source, source, StringComparison.Ordinal)
                && head.Value.MessageHash == messageHash
                && string.Equals(head.Value.Message, trimmed, StringComparison.Ordinal))
            {
                Entries.RemoveFirst();
                Entries.AddFirst(new DiagnosticEntry
                {
                    AtUtc = head.Value.AtUtc,
                    LastAtUtc = now,
                    Source = source,
                    Message = trimmed,
                    MessageHash = messageHash,
                    Count = head.Value.Count + 1
                });
                return;
            }

            Entries.AddFirst(new DiagnosticEntry
            {
                AtUtc = now,
                LastAtUtc = now,
                Source = source,
                Message = trimmed,
                MessageHash = messageHash,
                Count = 1
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
