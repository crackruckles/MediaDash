using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// Owns the TVDb identity tables: one <c>media_identities</c> row per series, episode and movie,
/// the <c>media_masters</c> folder each TVDb title is grouped into, and a <c>tvdb_lookups</c> cache
/// so the TVDb API is asked about any one title at most once per cache period.
/// </summary>
public sealed class MediaIdentityStore
{
    private readonly MediaDashConnectionFactory _connectionFactory;

    /// <summary>Initializes a new instance of the <see cref="MediaIdentityStore"/> class.</summary>
    /// <param name="connectionFactory">Shared connection factory.</param>
    public MediaIdentityStore(MediaDashConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <summary>Inserts or replaces identity rows in one transaction.</summary>
    /// <param name="rows">Identity rows.</param>
    public void Upsert(IReadOnlyCollection<MediaIdentity> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return;
        }

        using var connection = _connectionFactory.Open();
        using var transaction = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT OR REPLACE INTO media_identities
                (item_id, kind, path, name, year, series_item_id, tvdb_id, tvdb_source, episode_tvdb_id, imdb_id, tmdb_id, updated_utc_ticks)
            VALUES (@id, @kind, @path, @name, @year, @series, @tvdb, @source, @episode, @imdb, @tmdb, @now)
            """;
        var id = cmd.Parameters.Add("@id", SqliteType.Text);
        var kind = cmd.Parameters.Add("@kind", SqliteType.Text);
        var path = cmd.Parameters.Add("@path", SqliteType.Text);
        var name = cmd.Parameters.Add("@name", SqliteType.Text);
        var year = cmd.Parameters.Add("@year", SqliteType.Integer);
        var series = cmd.Parameters.Add("@series", SqliteType.Text);
        var tvdb = cmd.Parameters.Add("@tvdb", SqliteType.Integer);
        var source = cmd.Parameters.Add("@source", SqliteType.Text);
        var episode = cmd.Parameters.Add("@episode", SqliteType.Integer);
        var imdb = cmd.Parameters.Add("@imdb", SqliteType.Text);
        var tmdb = cmd.Parameters.Add("@tmdb", SqliteType.Text);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.Ticks);
        foreach (var row in rows)
        {
            id.Value = row.ItemId.ToString("N", CultureInfo.InvariantCulture);
            kind.Value = row.Kind;
            path.Value = row.Path;
            name.Value = row.Name;
            year.Value = (object?)row.Year ?? DBNull.Value;
            series.Value = row.SeriesItemId is { } s ? s.ToString("N", CultureInfo.InvariantCulture) : DBNull.Value;
            tvdb.Value = (object?)row.TvdbId ?? DBNull.Value;
            source.Value = row.TvdbSource;
            episode.Value = (object?)row.EpisodeTvdbId ?? DBNull.Value;
            imdb.Value = (object?)row.ImdbId ?? DBNull.Value;
            tmdb.Value = (object?)row.TmdbId ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Removes identity rows whose path no longer exists.</summary>
    /// <param name="exists">Returns true when a recorded path still exists.</param>
    public void PruneMissing(Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        using var connection = _connectionFactory.Open();
        var stale = new List<string>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT item_id, path FROM media_identities";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                if (!exists(reader.GetString(1)))
                {
                    stale.Add(reader.GetString(0));
                }
            }
        }

        if (stale.Count == 0)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM media_identities WHERE item_id = @id";
        var id = delete.Parameters.Add("@id", SqliteType.Text);
        foreach (var value in stale)
        {
            id.Value = value;
            delete.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Returns the identity row for an item, if recorded.</summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <returns>The row or null.</returns>
    public MediaIdentity? Get(Guid itemId)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT item_id, kind, path, name, year, series_item_id, tvdb_id, tvdb_source, episode_tvdb_id, imdb_id, tmdb_id FROM media_identities WHERE item_id = @id";
        cmd.Parameters.AddWithValue("@id", itemId.ToString("N", CultureInfo.InvariantCulture));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new MediaIdentity(
            Guid.ParseExact(reader.GetString(0), "N"),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.IsDBNull(5) ? null : Guid.ParseExact(reader.GetString(5), "N"),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetInt32(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    /// <summary>Returns the TVDb id recorded for every item of one kind that has one.</summary>
    /// <param name="kind"><c>series</c>, <c>episode</c> or <c>movie</c>.</param>
    /// <returns>Item id to TVDb id (the series id for episodes) and where that id came from.</returns>
    public IReadOnlyDictionary<Guid, (int TvdbId, string Source)> GetTvdbIds(string kind)
    {
        var result = new Dictionary<Guid, (int TvdbId, string Source)>();
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT item_id, tvdb_id, tvdb_source FROM media_identities WHERE kind = @kind AND tvdb_id IS NOT NULL";
        cmd.Parameters.AddWithValue("@kind", kind);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (Guid.TryParseExact(reader.GetString(0), "N", out var itemId))
            {
                result[itemId] = (reader.GetInt32(1), reader.GetString(2));
            }
        }

        return result;
    }

    /// <summary>Reads a cached TVDb episode list.</summary>
    /// <param name="seriesTvdbId">TVDb series id.</param>
    /// <param name="maxAge">How old a cached list may be.</param>
    /// <param name="episodes">The cached episodes.</param>
    /// <returns>True when a fresh enough list is cached.</returns>
    public bool TryGetEpisodeGuide(int seriesTvdbId, TimeSpan maxAge, out IReadOnlyList<Metadata.TvdbEpisode> episodes)
    {
        episodes = [];
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT episodes_json, fetched_utc_ticks FROM tvdb_episode_guides WHERE series_tvdb_id = @id";
        cmd.Parameters.AddWithValue("@id", seriesTvdbId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || DateTime.UtcNow - new DateTime(reader.GetInt64(1), DateTimeKind.Utc) >= maxAge)
        {
            return false;
        }

        try
        {
            episodes = System.Text.Json.JsonSerializer.Deserialize<List<Metadata.TvdbEpisode>>(reader.GetString(0)) ?? [];
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>Caches a TVDb episode list.</summary>
    /// <param name="seriesTvdbId">TVDb series id.</param>
    /// <param name="episodes">The episodes TVDb listed.</param>
    public void SaveEpisodeGuide(int seriesTvdbId, IReadOnlyList<Metadata.TvdbEpisode> episodes)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO tvdb_episode_guides (series_tvdb_id, episodes_json, fetched_utc_ticks) VALUES (@id, @json, @now)";
        cmd.Parameters.AddWithValue("@id", seriesTvdbId);
        cmd.Parameters.AddWithValue("@json", System.Text.Json.JsonSerializer.Serialize(episodes));
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.Ticks);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Returns the recorded master folder for a TVDb title.</summary>
    /// <param name="kind">Title kind (series or movie).</param>
    /// <param name="tvdbId">TVDb id.</param>
    /// <returns>The master folder path or null.</returns>
    public string? GetMaster(string kind, int tvdbId)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT master_path FROM media_masters WHERE kind = @kind AND tvdb_id = @tvdb";
        cmd.Parameters.AddWithValue("@kind", kind);
        cmd.Parameters.AddWithValue("@tvdb", tvdbId);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Records the master folder for a TVDb title.</summary>
    /// <param name="kind">Title kind.</param>
    /// <param name="tvdbId">TVDb id.</param>
    /// <param name="masterPath">Folder every copy of the title is grouped into.</param>
    /// <param name="name">Title name.</param>
    /// <param name="year">Title year.</param>
    public void SetMaster(string kind, int tvdbId, string masterPath, string name, int? year)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO media_masters (kind, tvdb_id, master_path, name, year, updated_utc_ticks)
            VALUES (@kind, @tvdb, @path, @name, @year, @now)
            """;
        cmd.Parameters.AddWithValue("@kind", kind);
        cmd.Parameters.AddWithValue("@tvdb", tvdbId);
        cmd.Parameters.AddWithValue("@path", masterPath);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@year", (object?)year ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.Ticks);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns every recorded series folder whose TVDb title has a different master folder: the
    /// folders grouping has merged (or will merge) away.
    /// </summary>
    /// <returns>Candidate leftover folders with their masters.</returns>
    public IReadOnlyList<GroupedLeftover> GetGroupedLeftovers()
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT i.path, i.tvdb_id, m.master_path, m.year
            FROM media_identities i
            JOIN media_masters m ON m.kind = 'series' AND m.tvdb_id = i.tvdb_id
            WHERE i.kind = 'series' AND i.path <> m.master_path
            """;
        var result = new List<GroupedLeftover>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new GroupedLeftover(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt32(3)));
        }

        return result;
    }

    /// <summary>Forgets the master folder of a TVDb title.</summary>
    /// <param name="kind">Title kind.</param>
    /// <param name="tvdbId">TVDb id.</param>
    public void RemoveMaster(string kind, int tvdbId)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM media_masters WHERE kind = @kind AND tvdb_id = @tvdb";
        cmd.Parameters.AddWithValue("@kind", kind);
        cmd.Parameters.AddWithValue("@tvdb", tvdbId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Reads a cached TVDb lookup.</summary>
    /// <param name="key">Normalised lookup key.</param>
    /// <param name="maxNegativeAge">How long a "not found" answer stays valid.</param>
    /// <param name="tvdbId">The cached id, or null when TVDb had no confident match.</param>
    /// <returns>True when a usable cached answer exists.</returns>
    public bool TryGetLookup(string key, TimeSpan maxNegativeAge, out int? tvdbId)
    {
        tvdbId = null;
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT tvdb_id, fetched_utc_ticks FROM tvdb_lookups WHERE query_key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        if (!reader.IsDBNull(0))
        {
            tvdbId = reader.GetInt32(0);
            return true;
        }

        return DateTime.UtcNow - new DateTime(reader.GetInt64(1), DateTimeKind.Utc) < maxNegativeAge;
    }

    /// <summary>Caches a TVDb lookup answer.</summary>
    /// <param name="key">Normalised lookup key.</param>
    /// <param name="tvdbId">The id found, or null for "no confident match".</param>
    public void SaveLookup(string key, int? tvdbId)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO tvdb_lookups (query_key, tvdb_id, fetched_utc_ticks) VALUES (@key, @tvdb, @now)";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@tvdb", (object?)tvdbId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.Ticks);
        cmd.ExecuteNonQuery();
    }
}
