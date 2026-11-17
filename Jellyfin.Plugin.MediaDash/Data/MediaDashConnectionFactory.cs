using System;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// Single owner of SQLite connection creation for MediaDash. Every store family that lands in
/// 05-C (IssueStore, HistoryStore, ProbeCacheStore, DiagnosticStore, RestoreOperationStore) opens
/// its connections through this factory so PRAGMAs / busy-timeout / pooling behaviour stays
/// consistent across every code path. Extracted from <c>MediaDashDb.Open()</c> in 05-C.1.
/// </summary>
/// <remarks>
/// Kept <c>public sealed</c> so it can be DI-injected into future store services without
/// InternalsVisibleTo gymnastics. The single <see cref="MediaDashDb"/> instance constructs one
/// factory in its ctor and delegates every open through it; later 05-C sub-substeps will migrate
/// consumers to take the factory directly and drop the delegation.
/// </remarks>
public sealed class MediaDashConnectionFactory
{
    [ThreadStatic]
    private static bool _recordingOpenFailure;

    private readonly string _connectionString;

    /// <summary>Initializes a new instance of the <see cref="MediaDashConnectionFactory"/> class.</summary>
    /// <param name="connectionString">Fully-formed SQLite connection string (typically
    /// <c>SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()</c>).</param>
    public MediaDashConnectionFactory(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    /// <summary>
    /// Opens a fresh <see cref="SqliteConnection"/>. Records a diagnostic and rethrows on any
    /// SQLite-level open failure — a completely-unopenable database is not recoverable at the
    /// caller level; the diagnostic gives the admin a breadcrumb pointing at whatever else is
    /// holding the file (backup software, antivirus, WAL checkpoint).
    /// </summary>
    /// <returns>An opened connection the caller owns and must dispose.</returns>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch (SqliteException ex)
        {
            // Backup tool / virus scanner / WAL checkpoint holding a Windows file lock produces a
            // hard failure with no breadcrumb otherwise. Record so admins can see it in the Errors
            // tab and correlate with the resulting API 500. Rethrow — callers cannot recover from a
            // completely-unopenable database.
            connection.Dispose();
            // Persisting a diagnostic opens this same database; only the outer failure records it.
            if (!_recordingOpenFailure)
            {
                _recordingOpenFailure = true;
                try
                {
                    Api.Diagnostics.Record("MediaDashDb.Open", "Could not open SQLite database: " + ex.Message + ". Something else may be holding a lock on the file (backup software, antivirus, running scan).");
                }
                finally
                {
                    _recordingOpenFailure = false;
                }
            }

            throw;
        }
    }
}
