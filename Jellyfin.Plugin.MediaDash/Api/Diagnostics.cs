using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Bounded ring buffer of recent plugin errors, surfaced by the Errors tab.
/// Callers push here from catch blocks so failures that would otherwise be invisible
/// (silent nulls from a system-stats sample, per-file scan failures, cancelled fixes)
/// show up in one place the admin can look at.
///
/// The in-memory buffer is a write-through cache over a persisted <c>diagnostics</c> SQLite table —
/// once <see cref="Attach"/> hands the class a database reference at plugin startup, every
/// <see cref="Record"/> flushes to disk so errors survive Jellyfin restarts and plugin updates.
/// Before <see cref="Attach"/> runs (early in plugin construction, before the DB is ready),
/// writes stay memory-only.
/// </summary>
public static class Diagnostics
{
    // In-memory buffer stays small so serialisation of the /Errors payload is cheap and DOM
    // rendering on the Errors tab stays snappy. Anything older than this is still on disk —
    // /Errors?full=true reads directly from the persisted table up to MaxPersisted.
    private const int MaxEntries = 100;
    private const int MaxPersisted = 5000;

    private static readonly object Lock = new();
    private static readonly LinkedList<DiagnosticEntry> Entries = new();
    private static Data.MediaDashDb? _db;

    /// <summary>
    /// Attach the shared database and rehydrate the ring buffer from persisted rows.
    /// Called once from <see cref="PluginServiceRegistrator"/> after the DB service is available.
    /// Safe to call multiple times — subsequent attachments swap the reference and reload.
    /// </summary>
    /// <param name="db">The plugin database. Rows in the <c>diagnostics</c> table become the initial buffer contents.</param>
    public static void Attach(Data.MediaDashDb db)
    {
        lock (Lock)
        {
            _db = db;
            Entries.Clear();
            try
            {
                foreach (var row in db.LoadDiagnostics(MaxEntries))
                {
                    Entries.AddLast(new DiagnosticEntry
                    {
                        AtUtc = row.AtUtc,
                        LastAtUtc = row.LastAtUtc,
                        Source = row.Source,
                        Message = row.Message,
                        MessageHash = row.MessageHash,
                        Count = row.Count
                    });
                }
            }
            catch (Exception)
            {
                // If the load fails (fresh install, disk hiccup, schema not yet migrated) start
                // with an empty buffer — subsequent Record calls will populate it.
            }
        }
    }

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
        DateTime firstSeen;
        int newCount;
        lock (Lock)
        {
            // Dedup: any existing entry with the same source + full message gets its Count bumped
            // and moved to the head. Previous "head-only dedup" merged repeats only when they hit
            // back to back; scanning all entries mirrors the SQLite ON CONFLICT semantics.
            LinkedListNode<DiagnosticEntry>? match = null;
            for (var node = Entries.First; node is not null; node = node.Next)
            {
                if (string.Equals(node.Value.Source, source, StringComparison.Ordinal)
                    && node.Value.MessageHash == messageHash
                    && string.Equals(node.Value.Message, trimmed, StringComparison.Ordinal))
                {
                    match = node;
                    break;
                }
            }

            if (match is not null)
            {
                firstSeen = match.Value.AtUtc;
                newCount = match.Value.Count + 1;
                Entries.Remove(match);
                Entries.AddFirst(new DiagnosticEntry
                {
                    AtUtc = firstSeen,
                    LastAtUtc = now,
                    Source = source,
                    Message = trimmed,
                    MessageHash = messageHash,
                    Count = newCount
                });
            }
            else
            {
                firstSeen = now;
                newCount = 1;
                Entries.AddFirst(new DiagnosticEntry
                {
                    AtUtc = firstSeen,
                    LastAtUtc = now,
                    Source = source,
                    Message = trimmed,
                    MessageHash = messageHash,
                    Count = newCount
                });
                while (Entries.Count > MaxEntries)
                {
                    Entries.RemoveLast();
                }
            }
        }

        // Persist outside the buffer lock — SQLite has its own locking and we don't want to hold
        // the in-memory mutex across the disk write. If the persist call fails the buffer stays
        // authoritative for this session (and re-persists on the next Record).
        var db = _db;
        if (db is not null)
        {
            try
            {
                db.SaveDiagnostic(source, messageHash, trimmed, firstSeen, now, MaxPersisted);
            }
            catch (Exception)
            {
                // Persistence is best-effort. The Errors tab still shows the in-memory copy.
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

    /// <summary>
    /// Reads the persisted diagnostics table directly (bypassing the in-memory ring buffer) so the
    /// Errors tab's "Load older" button can surface entries beyond the newest <see cref="MaxEntries"/>.
    /// Returns the newest <see cref="MaxPersisted"/> at most. Falls back to the in-memory buffer if
    /// the DB isn't attached yet or the read fails.
    /// </summary>
    /// <returns>All persisted entries, newest first.</returns>
    public static IReadOnlyList<DiagnosticEntry> RecentAll()
    {
        var db = _db;
        if (db is null)
        {
            return Recent();
        }

        try
        {
            var rows = db.LoadDiagnostics(MaxPersisted);
            var list = new List<DiagnosticEntry>(rows.Count);
            foreach (var row in rows)
            {
                list.Add(new DiagnosticEntry
                {
                    AtUtc = row.AtUtc,
                    LastAtUtc = row.LastAtUtc,
                    Source = row.Source,
                    Message = row.Message,
                    MessageHash = row.MessageHash,
                    Count = row.Count
                });
            }

            return list;
        }
        catch (Exception)
        {
            return Recent();
        }
    }

    /// <summary>
    /// Total number of persisted diagnostic entries. Used by the Errors tab to decide whether to
    /// surface a "Load older" button (persisted total exceeds the in-memory buffer size).
    /// </summary>
    /// <returns>Persisted row count, or the in-memory buffer size when the DB isn't attached.</returns>
    public static int PersistedCount()
    {
        var db = _db;
        if (db is null)
        {
            lock (Lock)
            {
                return Entries.Count;
            }
        }

        try
        {
            return db.CountDiagnostics();
        }
        catch (Exception)
        {
            lock (Lock)
            {
                return Entries.Count;
            }
        }
    }

    /// <summary>Empties the buffer and the persisted table.</summary>
    public static void Clear()
    {
        lock (Lock)
        {
            Entries.Clear();
        }

        var db = _db;
        if (db is not null)
        {
            try
            {
                db.ClearDiagnostics();
            }
            catch (Exception)
            {
                // Same policy as Record — persistence is best-effort. Next Record repopulates the DB.
            }
        }
    }
}
