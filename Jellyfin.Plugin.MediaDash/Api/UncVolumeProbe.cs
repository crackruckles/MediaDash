using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Reads free/total space for network shares without letting a slow or offline NAS stall the Overview.
/// Each measurement runs on the thread pool and is waited on for at most <c>timeout</c>; results are
/// cached for <c>ttl</c> so the dashboard's 3-15 s polling doesn't hit the share every time; and at
/// most one measurement per share is ever in flight, so a hung share can't pile up stuck threads.
/// While a share is unresponsive the last good reading is served, and if there never was one the
/// share is simply left out of the card.
/// </summary>
internal sealed class UncVolumeProbe
{
    private readonly Func<string, Reading> _measure;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _ttl;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="UncVolumeProbe"/> class.</summary>
    /// <param name="measure">Measures one share root. Must not assume it returns quickly.</param>
    /// <param name="timeout">How long a caller waits for a fresh measurement before falling back.</param>
    /// <param name="ttl">How long a reading is reused before the share is measured again.</param>
    /// <param name="utcNow">Clock override for tests; defaults to <see cref="DateTime.UtcNow"/>.</param>
    public UncVolumeProbe(Func<string, Reading> measure, TimeSpan timeout, TimeSpan ttl, Func<DateTime>? utcNow = null)
    {
        _measure = measure;
        _timeout = timeout;
        _ttl = ttl;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Gets free/total bytes for <paramref name="root"/>, from cache when fresh.</summary>
    /// <param name="root">Share root such as <c>\\sunnynas\Shows</c>.</param>
    /// <param name="freeBytes">Bytes available, or 0 when this returns false.</param>
    /// <param name="totalBytes">Total bytes, or 0 when this returns false.</param>
    /// <returns>True when a usable reading (fresh, cached, or last-known-good while the share is slow) exists.</returns>
    public bool TryGet(string root, out long freeBytes, out long totalBytes)
    {
        Task<Reading>? waitOn = null;

        lock (_gate)
        {
            if (!_entries.TryGetValue(root, out var entry))
            {
                entry = new Entry();
                _entries[root] = entry;
            }

            Harvest(entry);

            if (entry.Last is { } cached && _utcNow() - entry.LastAtUtc < _ttl)
            {
                return Report(cached, out freeBytes, out totalBytes);
            }

            if (entry.Pending is null)
            {
                entry.Pending = Task.Run(() => _measure(root));
                entry.PendingSinceUtc = _utcNow();
                waitOn = entry.Pending;
            }
            else if (_utcNow() - entry.PendingSinceUtc < _timeout)
            {
                // A measurement is already running and hasn't used up its budget yet: share the wait.
                waitOn = entry.Pending;
            }
        }

        // Pending for longer than the timeout means the share is hung: don't make the dashboard wait again.
        var finished = false;
        if (waitOn is not null)
        {
            try
            {
                finished = waitOn.Wait(_timeout);
            }
            catch (AggregateException)
            {
                finished = true;
            }
        }

        lock (_gate)
        {
            var entry = _entries[root];
            Harvest(entry);

            if (finished && entry.Last is { } latest)
            {
                return Report(latest, out freeBytes, out totalBytes);
            }

            // Share is slow or unresponsive: the last good reading beats an empty card.
            return Report(entry.LastGood ?? default, out freeBytes, out totalBytes);
        }
    }

    private static bool Report(Reading reading, out long freeBytes, out long totalBytes)
    {
        freeBytes = reading.Ok ? reading.FreeBytes : 0;
        totalBytes = reading.Ok ? reading.TotalBytes : 0;
        return reading.Ok;
    }

    // Caller holds _gate. Folds a finished background measurement into the entry.
    private void Harvest(Entry entry)
    {
        if (entry.Pending is not { IsCompleted: true } done)
        {
            return;
        }

        Reading reading;
        try
        {
            reading = done.Result;
        }
        catch (AggregateException)
        {
            reading = default;
        }

        entry.Pending = null;
        entry.Last = reading;
        entry.LastAtUtc = _utcNow();
        if (reading.Ok)
        {
            entry.LastGood = reading;
        }
    }

    /// <summary>One measurement of a share.</summary>
    /// <param name="Ok">True when the share was measured.</param>
    /// <param name="FreeBytes">Bytes available to the caller.</param>
    /// <param name="TotalBytes">Total bytes on the share.</param>
    internal readonly record struct Reading(bool Ok, long FreeBytes, long TotalBytes);

    private sealed class Entry
    {
        public Task<Reading>? Pending { get; set; }

        public DateTime PendingSinceUtc { get; set; }

        public Reading? Last { get; set; }

        public DateTime LastAtUtc { get; set; }

        public Reading? LastGood { get; set; }
    }
}
