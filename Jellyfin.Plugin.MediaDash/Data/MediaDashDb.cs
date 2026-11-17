using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.MediaDash.Fixers;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// SQLite persistence for scan results, the fix queue, history and the probe cache.
/// </summary>
public sealed class MediaDashDb
{
    // Kept for source-compat with any external caller that read this const from MediaDashDb before
    // 05-C.2 relocated it onto MediaDashSchema. Same value, same semantics; delete once no in-tree
    // reference points at MediaDashDb.RestoredPathAnyType.
    internal const int RestoredPathAnyType = MediaDashSchema.RestoredPathAnyType;

    private readonly MediaDashConnectionFactory _connectionFactory;
    private readonly IssueStore _issues;
    private readonly HistoryStore _history;
    private readonly ProbeCacheStore _probes;
    private readonly DiagnosticStore _diagnostics;
    private readonly RestoreOperationStore _restoreOps;
    private readonly ProtectionStore _protection;
    private readonly MediaMoveStore _moves;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaDashDb"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    public MediaDashDb(IApplicationPaths applicationPaths)
        : this(EnsureDbPath(applicationPaths))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaDashDb"/> class against an explicit database file (tests).
    /// </summary>
    /// <param name="dbPath">Full path of the SQLite database file.</param>
    internal MediaDashDb(string dbPath)
    {
        _connectionFactory = new MediaDashConnectionFactory(
            new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        _issues = new IssueStore(_connectionFactory);
        _history = new HistoryStore(_connectionFactory);
        _probes = new ProbeCacheStore(_connectionFactory);
        _diagnostics = new DiagnosticStore(_connectionFactory);
        _restoreOps = new RestoreOperationStore(_connectionFactory);
        _protection = new ProtectionStore(_connectionFactory);
        _moves = new MediaMoveStore(_connectionFactory);

        SqliteConnection connection;
        try
        {
            connection = Open();
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // The DB open covers the whole first-run path (CreateDirectory / CACHEDIR.TAG failures
            // funnel up here from EnsureDbPath, and SQLite's "unable to open" surfaces here too).
            // Message names the path so the user knows what to chown/chmod; Diagnostics can't persist
            // yet (DB isn't up), so the in-memory buffer holds it — if the plugin recovers and Attach
            // runs later, Diagnostics.Attach reloads from disk and the in-memory row is still there.
            var msg = "MediaDash could not open its SQLite database at '" + dbPath + "': " + ex.Message
                + ". Check that the Jellyfin process user owns the folder and can write to it.";
            Api.Diagnostics.Record("MediaDashDb.InitFailed", msg);
            throw new InvalidOperationException(msg, ex);
        }

        using (var db = connection)
        {
            MediaDashSchema.CreateTables(db);
            MediaDashSchema.Migrate(db);
        }

        // 05-B: any Pending restore_operations row is orphan work from a process that died mid-
        // restore. Convert to NeedsReview so a subsequent restore of the same bin entry can
        // proceed only after a human looks at the leftover state. Idempotent; a fresh install has
        // no rows to reconcile so this is a cheap no-op. 05-C.6: delegated to
        // RestoreOperationStore, which opens its own connection through the shared factory.
        _restoreOps.ReconcilePendingRestoresAtStartup();
        _moves.ReconcileAtStartup();
        InvalidateLegacyMoverApprovals();

        // Wire the persistent diagnostics buffer to this DB. Previously the buffer was in-memory
        // only; recorded errors vanished on every plugin reload / update. Attach reloads persisted
        // rows into the buffer and installs the store reference so future Record calls write through.
        Api.Diagnostics.Attach(_diagnostics);

        // Startup self-heal: obsolete diagnostics that the current build no longer emits get
        // purged from disk here so upgrades don't leave the Errors tab full of stale flags for
        // conditions the code has already fixed. See Diagnostics.PurgeObsolete for the list.
        Api.Diagnostics.PurgeObsolete();

        // Same treatment for Plugin.LastFixRun + Plugin.RedownloadWarnings — both were static
        // fields that dropped on reload, so a mid-day restart lost the fix-completion popup and
        // the redownload-warning banner until the next scan. PluginState.Attach rehydrates them
        // from the plugin_state table; the property setters on Plugin write back through.
        Api.PluginState.Attach(this);
    }

    /// <summary>
    /// Gets the effective data directory holding the SQLite DB, probe cache and recycle bin, or
    /// null when this instance was constructed against an explicit path (tests). Populated by
    /// <see cref="EnsureDbPath"/> so the Overview tab can show what path the plugin is writing to
    /// — critical for troubleshooting "why can't the DB init?" errors on Docker/rootless setups.
    /// </summary>
    public static string? DataDirectory { get; private set; }

    // Exposed for future 05-C sub-substeps: when a method moves from MediaDashDb to a dedicated
    // store, the store's ctor takes this factory and opens its own connections. Internal so it
    // stays inside the plugin assembly.
    internal MediaDashConnectionFactory ConnectionFactory => _connectionFactory;

    // 05-C.2: single owner of the `issues` table. Facade methods below forward here; consumers
    // that only touch issues (fixers, ScanTask, FixTask, FixExecutor, ConfigChangeRescanner, the
    // controller's issue endpoints) inject IssueStore directly via DI and skip this facade.
    // Exposed so PluginServiceRegistrator can hand the same instance to those consumers.
    internal IssueStore Issues => _issues;

    // 05-C.3: single owner of the `history` table (except RecordFixOutcome, which stays here
    // because it's the cross-store transaction that atomically writes history AND updates issues
    // in one SQLite tx). Same DI factory pattern as IssueStore — consumers migrate one family at
    // a time.
    internal HistoryStore History => _history;

    // 05-C.4: single owner of the four probe-cache tables (probe_cache, file_hashes,
    // format_probe_cache, decode_cache). Consumed by FfprobeService / FileHasher /
    // BookProbeService / ComicProbeService. Cache-only — no cross-store atomicity concerns.
    internal ProbeCacheStore Probes => _probes;

    // 05-C.5: single owner of the `diagnostics` table. Consumed by DiagnosticService (and, via
    // that service, the Api.Diagnostics static bridge + MediaDashController /Errors endpoints).
    // The plugin_state / GetState / SetState pair stays on MediaDashDb — extracting a 2-method
    // store with 2 non-diagnostic consumers would be speculative and worsen the dep graph.
    internal DiagnosticStore Diagnostics => _diagnostics;

    // 05-C.6: single owner of the `restore_operations` table (schema v7). Consumed by
    // RestoreService (the sole caller of the restore-workflow primitives) and by
    // MediaDashDb's ctor reconciliation. Pre-05-B `TryClaimRestore` / `ReleaseRestoreClaim`
    // deleted in the same sub-substep; `RestoreClaimTests.cs` deleted with them.
    internal RestoreOperationStore RestoreOps => _restoreOps;

    // Roadmap i-110-protected-media: single owner of the `protection_rules` table (schema v8).
    // Consumed by ProtectionService, which caches the ruleset and evaluates path/library matches
    // at scan-emit and fix-execute time.
    internal ProtectionStore Protection => _protection;

    /// <summary>Gets durable media-move operations and their claims.</summary>
    internal MediaMoveStore Moves => _moves;

    private void InvalidateLegacyMoverApprovals()
    {
        foreach (var type in new[] { IssueType.Misplaced, IssueType.Ungrouped })
        {
            foreach (var issue in _issues.GetIssues(type, IssueStatus.Queued))
            {
                if (MediaMovePlan.ReadIssue(issue) is not null)
                {
                    continue;
                }

                JsonObject details;
                try
                {
                    details = JsonNode.Parse(issue.DetailsJson) as JsonObject ?? new JsonObject { ["legacyDetails"] = issue.DetailsJson };
                }
                catch (JsonException)
                {
                    details = new JsonObject { ["legacyDetails"] = issue.DetailsJson };
                }

                const string explanation = "This old move approval needs a complete current preview. Re-scan and approve the new plan; no files were changed.";
                var warning = JsonSerializer.SerializeToNode(new IssueWarning { Code = "move-plan-upgrade", Blocking = true, Message = explanation });
                if (details["warnings"] is JsonArray warnings)
                {
                    warnings.Add(warning);
                }
                else
                {
                    details["warnings"] = new JsonArray(warning);
                }

                details["movePlanUpgradeRequired"] = true;
                issue.DetailsJson = details.ToJsonString();
                issue.SuggestedFix = explanation;
                // Reuse the existing conditional status/details transaction. Do not create a
                // separate status UPDATE path for a migration or erase unrelated approvals.
                RecordFixOutcome([], [issue.Id], [IssueStatus.Queued], IssueStatus.Detected, issue);
            }
        }
    }

    private static string EnsureDbPath(IApplicationPaths applicationPaths)
    {
        var dataDir = Path.Combine(applicationPaths.DataPath, "mediadash");
        DataDirectory = dataDir;
        Directory.CreateDirectory(dataDir);

        // Mark the dir as cache-like so backup tools (rsync --exclude-caches, restic, borg, Time Machine
        // via .nobackup patterns) skip our SQLite + probe cache + recycle bin by default. Spec:
        // https://bford.info/cachedir/ — first line must be exactly the Signature: line with no BOM.
        // Best-effort: if the tag write fails (read-only tmpfs, full disk), the plugin continues —
        // don't block DB init on a non-critical backup-exclusion hint.
        var tagPath = Path.Combine(dataDir, "CACHEDIR.TAG");
        if (!File.Exists(tagPath))
        {
            try
            {
                File.WriteAllText(
                    tagPath,
                    "Signature: 8a477f597d28d172789f06886806bc55\n# This file is a cache directory tag created by MediaDash.\n# For information about cache directory tags see https://bford.info/cachedir/\n",
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Api.Diagnostics.Record(
                    "MediaDashDb.CachedirTag",
                    "Could not write CACHEDIR.TAG at '" + tagPath + "': " + ex.Message + ". Backup tools that honour cache-directory tags won't auto-skip the MediaDash folder; the plugin itself continues normally.");
            }
        }

        return Path.Combine(dataDir, "mediadash.db");
    }

    /// <summary>
    /// Replaces detected issues of a type with fresh scan results, but only for the paths that were actually scanned —
    /// a scan scoped to one library must not wipe findings from other libraries.
    /// Issues the user dismissed are preserved and not re-inserted for the same path.
    /// </summary>
    /// <param name="type">The issue type being refreshed.</param>
    /// <param name="issues">The freshly detected issues.</param>
    /// <param name="scannedPaths">All file paths covered by this scan; null means the scan covered everything.</param>
    public void ReplaceDetectedIssues(IssueType type, IReadOnlyList<Issue> issues, IReadOnlyCollection<string>? scannedPaths = null)
        => _issues.ReplaceDetectedIssues(type, issues, scannedPaths);

    /// <summary>
    /// Wipes Detected rows for every supplied issue type. Queued and Dismissed rows are left
    /// untouched — the user's approval / suppression outlives a config edit.
    /// Called by <see cref="ScheduledTasks.ConfigChangeRescanner"/> after a config change
    /// invalidates the details baked into an old scan (issue #64).
    /// </summary>
    /// <param name="types">The affected issue types.</param>
    /// <returns>The number of rows removed.</returns>
    public int WipeDetectedForTypes(IEnumerable<IssueType> types) => _issues.WipeDetectedForTypes(types);

    /// <summary>
    /// Gets issues, optionally filtered by type and status.
    /// </summary>
    /// <param name="type">Filter by issue type, or null for all types.</param>
    /// <param name="status">Filter by status, or null for all statuses.</param>
    /// <returns>The matching issues, newest first.</returns>
    public IReadOnlyList<Issue> GetIssues(IssueType? type = null, IssueStatus? status = null)
        => _issues.GetIssues(type, status);

    /// <summary>
    /// Gets per-type counts and potential savings for open issues (Detected + Queued), plus the newest detection time.
    /// Queued items are already approved and waiting for the fix task, so their savings still count toward "space you could reclaim".
    /// </summary>
    /// <returns>One summary row per issue type that has open issues.</returns>
    public IReadOnlyList<IssueSummary> GetSummary() => _issues.GetSummary();

    /// <summary>
    /// Looks up a cached ffprobe result that is still valid for the file's current size and modification time.
    /// </summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">Current file size in bytes.</param>
    /// <param name="mtimeUtcTicks">Current last-write time in UTC ticks.</param>
    /// <returns>The cached ffprobe JSON, or null when absent or stale.</returns>
    public string? GetCachedProbe(string path, long size, long mtimeUtcTicks) => _probes.GetCachedProbe(path, size, mtimeUtcTicks);

    /// <summary>
    /// Stores an ffprobe result in the cache, replacing any previous entry for the path.
    /// </summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">File size in bytes at probe time.</param>
    /// <param name="mtimeUtcTicks">Last-write time in UTC ticks at probe time.</param>
    /// <param name="json">Raw ffprobe JSON output.</param>
    public void StoreProbe(string path, long size, long mtimeUtcTicks, string json) => _probes.StoreProbe(path, size, mtimeUtcTicks, json);

    /// <summary>
    /// Looks up a cached full-file SHA-256 hash that is still valid for the file's current size
    /// and mtime. Feeds the Tier-0 (byte-identical) branch of the duplicate confidence ladder.
    /// </summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">Current file size in bytes.</param>
    /// <param name="mtimeUtcTicks">Current last-write time in UTC ticks.</param>
    /// <returns>The cached lowercase hex hash, or null when absent or stale.</returns>
    public string? GetCachedHash(string path, long size, long mtimeUtcTicks) => _probes.GetCachedHash(path, size, mtimeUtcTicks);

    /// <summary>
    /// Stores a computed hash, replacing any previous entry for the same (path, size, mtime).
    /// </summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">File size at hash time.</param>
    /// <param name="mtimeUtcTicks">Last-write time at hash time.</param>
    /// <param name="hash">Lowercase hex SHA-256.</param>
    public void StoreHash(string path, long size, long mtimeUtcTicks, string hash) => _probes.StoreHash(path, size, mtimeUtcTicks, hash);

    /// <summary>Looks up a cached format-probe result that is still valid for the file's current size and mtime.</summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">Current file size in bytes.</param>
    /// <param name="mtimeUtcTicks">Current last-write time in UTC ticks.</param>
    /// <returns>The cached result, or null when absent or stale.</returns>
    public FormatProbeResult? GetCachedFormatProbe(string path, long size, long mtimeUtcTicks) => _probes.GetCachedFormatProbe(path, size, mtimeUtcTicks);

    /// <summary>Stores a format-probe result, replacing any previous entry for the path.</summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">File size in bytes at probe time.</param>
    /// <param name="mtimeUtcTicks">Last-write time in UTC ticks at probe time.</param>
    /// <param name="ok">True when the file's container parsed cleanly.</param>
    /// <param name="reason">Human-readable failure reason when <paramref name="ok"/> is false, else null.</param>
    public void StoreFormatProbe(string path, long size, long mtimeUtcTicks, bool ok, string? reason) => _probes.StoreFormatProbe(path, size, mtimeUtcTicks, ok, reason);

    /// <summary>
    /// Looks up a cached decode-check result that is still valid for the file's current size and modification time.
    /// </summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">Current file size in bytes.</param>
    /// <param name="mtimeUtcTicks">Current last-write time in UTC ticks.</param>
    /// <returns>Empty string when the file decoded cleanly, the error text when it did not, or null when not cached.</returns>
    public string? GetCachedDecode(string path, long size, long mtimeUtcTicks) => _probes.GetCachedDecode(path, size, mtimeUtcTicks);

    /// <summary>
    /// Stores a decode-check result, replacing any previous entry for the path.
    /// </summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">File size in bytes at check time.</param>
    /// <param name="mtimeUtcTicks">Last-write time in UTC ticks at check time.</param>
    /// <param name="error">Empty string for a clean decode, otherwise the error text.</param>
    public void StoreDecode(string path, long size, long mtimeUtcTicks, string error) => _probes.StoreDecode(path, size, mtimeUtcTicks, error);

    /// <summary>Looks up a cached interleave-check result that is still valid for the file's current size and modification time.</summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">Current file size in bytes.</param>
    /// <param name="mtimeUtcTicks">Current last-write time in UTC ticks.</param>
    /// <returns>The cached offset in seconds (0 when the file is fine), or null when not cached.</returns>
    public double? GetCachedInterleave(string path, long size, long mtimeUtcTicks) => _probes.GetCachedInterleave(path, size, mtimeUtcTicks);

    /// <summary>Stores an interleave-check result, replacing any previous entry for the path.</summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">File size in bytes at check time.</param>
    /// <param name="mtimeUtcTicks">Last-write time in UTC ticks at check time.</param>
    /// <param name="offsetSeconds">Measured offset in seconds; 0 when the file is fine.</param>
    public void StoreInterleave(string path, long size, long mtimeUtcTicks, double offsetSeconds) => _probes.StoreInterleave(path, size, mtimeUtcTicks, offsetSeconds);

    /// <summary>
    /// Atomically transitions an issue's status from one of the allowed source statuses to the
    /// target status. The source-status filter runs inside the same UPDATE so a stale caller can't
    /// resurrect a Fixed row or overwrite a concurrent decision. Enforces CLAUDE.md invariant 7 —
    /// direct <c>UPDATE issues SET status = …</c> is banned outside this helper and
    /// <see cref="BulkUpdateOpenIssueStatus"/>.
    /// </summary>
    /// <param name="issueId">The issue id.</param>
    /// <param name="fromAny">Statuses that permit this transition. Empty means "any status" is rejected.</param>
    /// <param name="to">Target status.</param>
    /// <returns>
    /// <see cref="StatusTransitionResult.Applied"/> when the row moved,
    /// <see cref="StatusTransitionResult.NotFound"/> when the id doesn't exist,
    /// <see cref="StatusTransitionResult.Conflict"/> when the row exists but its current status
    /// isn't in <paramref name="fromAny"/> (a stale approve of a Fixed row, a race with the fixer, etc.).
    /// </returns>
    public StatusTransitionResult TryTransitionIssueStatus(long issueId, IReadOnlyCollection<IssueStatus> fromAny, IssueStatus to)
        => _issues.TryTransitionIssueStatus(issueId, fromAny, to);

    /// <summary>
    /// Reads a single issue's current status. Used by the fix loop to re-verify that a queued
    /// item hasn't been dismissed since the run's initial snapshot — otherwise a user pressing
    /// "Ignore" during a long fix run has no effect on the file that's about to be touched.
    /// </summary>
    /// <param name="issueId">The issue id.</param>
    /// <returns>The current status, or null if the row is gone.</returns>
    public IssueStatus? GetIssueStatus(long issueId) => _issues.GetIssueStatus(issueId);

    /// <summary>
    /// Guarded bulk status update. Only transitions rows currently in Detected or Queued to the
    /// target status — Dismissed and Fixed rows are left alone. Prevents a stale client snapshot
    /// (from before the last poll) from un-ignoring items via bulk-approve, or un-fixing them.
    /// </summary>
    /// <param name="ids">Issue ids to update.</param>
    /// <param name="target">Target status.</param>
    /// <returns>Number of rows actually updated.</returns>
    public IReadOnlyList<long> BulkUpdateOpenIssueStatus(IReadOnlyList<long> ids, IssueStatus target)
        => _issues.BulkUpdateOpenIssueStatus(ids, target);

    /// <summary>
    /// Bulk un-ignore: moves Dismissed rows back to Detected so they re-appear on the Issues tab.
    /// Only touches rows currently in Dismissed — Fixed/Queued/Detected are left alone so a stale
    /// client snapshot can't un-fix or duplicate work.
    /// </summary>
    /// <param name="ids">Issue ids to revert.</param>
    /// <returns>Number of rows actually updated.</returns>
    public int BulkRevertDismissedIssues(IReadOnlyList<long> ids) => _issues.BulkRevertDismissedIssues(ids);

    /// <summary>
    /// Re-points every issue whose stored path is exactly <paramref name="oldPath"/>, or lives under
    /// <paramref name="oldPath"/> as a directory prefix, to the equivalent location under
    /// <paramref name="newPath"/>. Call this after a move-style fixer completes so other queued issues
    /// on the same file/folder don't fail with "no longer exists" the next time they run.
    /// </summary>
    /// <param name="oldPath">The pre-move source path (file or directory).</param>
    /// <param name="newPath">The post-move target path.</param>
    /// <returns>The number of issue rows updated.</returns>
    public int RelocateIssuePaths(string oldPath, string newPath) => _issues.RelocateIssuePaths(oldPath, newPath);

    /// <summary>
    /// Moves all detected issues of a type into the queue (used by automatic mode). When
    /// <paramref name="minConfidence"/> is supplied, only rows with confidence at that value or
    /// above (or NULL — non-Duplicate types + pre-migration rows) are promoted. NULL is treated
    /// as "not gated" so non-Duplicate types still queue normally.
    /// </summary>
    /// <param name="type">The issue type.</param>
    /// <param name="minConfidence">Optional minimum <c>Issue.Confidence</c> threshold. Only pass a non-null value for confidence-gated types (currently Duplicate).</param>
    /// <returns>
    /// The IDs of the rows this call moved from Detected to Queued. The caller (FixTask) hands this
    /// exact list to <see cref="RollbackAutoQueuedBlockingWarnings"/> so the consent rollback can
    /// distinguish auto-queued rows from manually approved ones — otherwise a manual Approve would
    /// be silently reverted by any subsequent fix run.
    /// </returns>
    public IReadOnlyList<long> QueueDetectedIssues(IssueType type, double? minConfidence = null)
        => _issues.QueueDetectedIssues(type, minConfidence);

    /// <summary>
    /// Reverts auto-queued issues whose DetailsJson carries a blocking warning back to Detected,
    /// so the fixer never touches them without a manual Approve. The consent gate lives in
    /// <see cref="Issue.HasBlockingWarnings"/> — this method is the DB-side enforcement that pairs
    /// with it: <see cref="QueueDetectedIssues"/> is a bulk SQL UPDATE that can't see the per-row
    /// warning payload, so we walk the ids it just transitioned and bounce any consent-required
    /// ones back. Only rows in <paramref name="justAutoQueuedIds"/> are considered — rows a user
    /// manually approved via <c>/Issues/{id}/Approve</c> are left alone even if they carry a
    /// blocking warning, because manual approval is a stronger signal than the auto-queue.
    /// </summary>
    /// <param name="justAutoQueuedIds">
    /// Exact set of IDs the current auto-queue pass just moved from Detected to Queued. Pass the
    /// return value of <see cref="QueueDetectedIssues"/>. Empty list = no-op.
    /// </param>
    /// <returns>The number of issues reverted from Queued to Detected.</returns>
    public int RollbackAutoQueuedBlockingWarnings(IReadOnlyCollection<long> justAutoQueuedIds)
        => _issues.RollbackAutoQueuedBlockingWarnings(justAutoQueuedIds);

    /// <summary>
    /// Gets a single issue by id.
    /// </summary>
    /// <param name="issueId">The issue id.</param>
    /// <returns>The issue, or null.</returns>
    public Issue? GetIssue(long issueId) => _issues.GetIssue(issueId);

    /// <summary>
    /// Records a fix action in the history.
    /// </summary>
    /// <param name="entry">The history entry.</param>
    public void AddHistory(HistoryEntry entry) => _history.AddHistory(entry);

    /// <summary>
    /// Narrow transactional recorder consumed by <see cref="ScheduledTasks.FixResultRecorder"/>:
    /// inserts every history row and attempts each conditional status transition inside ONE
    /// SQLite transaction. If any statement in the batch throws (constraint violation, corrupt row,
    /// I/O), the enclosing transaction is disposed without commit and every effect rolls back —
    /// callers never observe a partial history/status write for the same operation. Filesystem
    /// operations MUST NOT be performed inside this method; the caller finishes all file work
    /// before invoking the recorder.
    /// </summary>
    /// <param name="historyRows">History entries to insert, in caller-supplied order (typically primary first, then companions, then sidecars).</param>
    /// <param name="idsToTransition">Issue ids to attempt <paramref name="transitionTo"/> against. Pass an empty collection for dry-run success or non-terminal failure to skip the transition pass entirely.</param>
    /// <param name="transitionFromAny">Statuses that authorise the transition. Rows whose current status is outside this set stay put and their transition is reported as <c>false</c>.</param>
    /// <param name="transitionTo">Target status for authorised transitions.</param>
    /// <param name="reviewIssue">Failed-repair details to persist with the transition back to Detected.</param>
    /// <returns>Per-id map of whether the transition applied. A <c>false</c> value means "row exists but its current status wasn't in <paramref name="transitionFromAny"/>" or "row is gone". A row that was never in <paramref name="idsToTransition"/> is absent from the map.</returns>
    public IReadOnlyDictionary<long, bool> RecordFixOutcome(
        IReadOnlyList<HistoryEntry> historyRows,
        IReadOnlyCollection<long> idsToTransition,
        IReadOnlyCollection<IssueStatus> transitionFromAny,
        IssueStatus transitionTo,
        Issue? reviewIssue = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        if (historyRows.Count > 0)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO history (issue_id, type, path, action, bytes_freed, recycle_path, fixed_at_utc, dry_run, restored, success, saved_bytes, move_operation_id)
                VALUES (@issueId, @type, @path, @action, @bytesFreed, @recyclePath, @fixedAt, @dryRun, 0, @success, @savedBytes, @move)
                """;
            var pIssueId = insert.Parameters.Add("@issueId", SqliteType.Integer);
            var pType = insert.Parameters.Add("@type", SqliteType.Integer);
            var pPath = insert.Parameters.Add("@path", SqliteType.Text);
            var pAction = insert.Parameters.Add("@action", SqliteType.Text);
            var pBytesFreed = insert.Parameters.Add("@bytesFreed", SqliteType.Integer);
            var pRecyclePath = insert.Parameters.Add("@recyclePath", SqliteType.Text);
            var pFixedAt = insert.Parameters.Add("@fixedAt", SqliteType.Integer);
            var pDryRun = insert.Parameters.Add("@dryRun", SqliteType.Integer);
            var pSuccess = insert.Parameters.Add("@success", SqliteType.Integer);
            var pSavedBytes = insert.Parameters.Add("@savedBytes", SqliteType.Integer);
            var pMove = insert.Parameters.Add("@move", SqliteType.Text);
            foreach (var row in historyRows)
            {
                pIssueId.Value = row.IssueId;
                pType.Value = (int)row.Type;
                pPath.Value = (object?)row.Path ?? DBNull.Value;
                pAction.Value = (object?)row.Action ?? DBNull.Value;
                pBytesFreed.Value = row.BytesFreed;
                pRecyclePath.Value = (object?)row.RecyclePath ?? DBNull.Value;
                pFixedAt.Value = row.FixedAtUtc.Ticks;
                pDryRun.Value = row.WasDryRun ? 1 : 0;
                pSuccess.Value = row.Success ? 1 : 0;
                pSavedBytes.Value = row.SavedBytes;
                pMove.Value = (object?)row.MoveOperationId ?? DBNull.Value;
                insert.ExecuteNonQuery();
                if (row.MoveOperationId is not null)
                {
                    MediaMoveStore.LinkInsertedHistory(connection, transaction, row.MoveOperationId);
                }
            }
        }

        var applied = new Dictionary<long, bool>(idsToTransition.Count);
        if (idsToTransition.Count > 0 && transitionFromAny.Count > 0)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            var fromNames = new List<string>(transitionFromAny.Count);
            var i = 0;
            foreach (var s in transitionFromAny)
            {
                var name = "@from" + i.ToString(CultureInfo.InvariantCulture);
                fromNames.Add(name);
                update.Parameters.AddWithValue(name, (int)s);
                i++;
            }

#pragma warning disable CA2100, CA3001 // placeholder list is machine-composed from an int range, ids/statuses are bound as parameters — no user text reaches the SQL string
            update.CommandText = "UPDATE issues SET status = @to WHERE id = @id AND status IN (" + string.Join(",", fromNames) + ")";
#pragma warning restore CA2100, CA3001
            update.Parameters.AddWithValue("@to", (int)transitionTo);
            var pId = update.Parameters.Add("@id", SqliteType.Integer);

            foreach (var id in idsToTransition)
            {
                pId.Value = id;
                applied[id] = update.ExecuteNonQuery() > 0;
            }
        }

        if (reviewIssue is not null && applied.TryGetValue(reviewIssue.Id, out var transitioned) && transitioned)
        {
            using var details = connection.CreateCommand();
            details.Transaction = transaction;
            details.CommandText = "UPDATE issues SET details = @details, suggested_fix = @fix WHERE id = @id";
            details.Parameters.AddWithValue("@details", reviewIssue.DetailsJson);
            details.Parameters.AddWithValue("@fix", reviewIssue.SuggestedFix);
            details.Parameters.AddWithValue("@id", reviewIssue.Id);
            details.ExecuteNonQuery();
        }

        transaction.Commit();
        return applied;
    }

    /// <summary>
    /// Gets fix history, newest first.
    /// </summary>
    /// <param name="limit">Maximum rows returned.</param>
    /// <returns>The history entries.</returns>
    public IReadOnlyList<HistoryEntry> GetHistory(int limit = 500) => _history.GetHistory(limit);

    /// <summary>
    /// Gets every distinct recycle path recorded by non-dry-run history. These paths
    /// are authoritative evidence for adopting pre-marker batches after an upgrade.
    /// </summary>
    /// <returns>Recorded recycle paths.</returns>
    public IReadOnlyList<string> GetRecyclePaths() => _history.GetRecyclePaths();

    /// <summary>
    /// Marks a history row as acknowledged so the redownload-warning banner stops flagging it.
    /// No-op when the row does not exist.
    /// </summary>
    /// <param name="historyId">The history row id.</param>
    /// <returns>True when a row was updated.</returns>
    public bool AcknowledgeHistoryEntry(long historyId) => _history.AcknowledgeHistoryEntry(historyId);

    /// <summary>Gets a single history row by id.</summary>
    /// <param name="historyId">The row id.</param>
    /// <returns>The row, or null.</returns>
    public HistoryEntry? GetHistoryEntry(long historyId) => _history.GetHistoryEntry(historyId);

    /// <summary>
    /// Returns the most recent successful, non-dry-run <c>fixed_at_utc</c> for a given issue type and
    /// path, or null when there's no such row. Used by <see cref="Scanners.TrickplayOptimizeScanner"/>
    /// as a rescan cutoff so folders where the fixer intentionally left small "won't-shrink" sprites
    /// don't get re-flagged forever.
    /// </summary>
    /// <param name="type">The issue type.</param>
    /// <param name="path">The exact path recorded on the history row (trickplay folder for LargeTrickplay).</param>
    /// <returns>UTC timestamp of the newest matching row, or null when none.</returns>
    public DateTime? GetLastSuccessfulFixUtc(IssueType type, string path) => _history.GetLastSuccessfulFixUtc(type, path);

    /// <summary>
    /// Counts failed non-dry-run history rows since the given watermark (Config.HistoryHiddenBeforeUtcTicks).
    /// Powers the History tab's badge. 0 for a fresh install or when everything has been cleared.
    /// </summary>
    /// <param name="hiddenBeforeUtcTicks">The Clear-history watermark; 0 = count everything.</param>
    /// <returns>Row count.</returns>
    public int GetFailedHistoryCount(long hiddenBeforeUtcTicks) => _history.GetFailedHistoryCount(hiddenBeforeUtcTicks);

    /// <summary>
    /// Returns the lifetime total of bytes reclaimed by successful non-dry-run fixes. Same
    /// success=1 AND dry_run=0 filter as the monthly aggregate so the number the user sees on
    /// Overview matches what actually happened to their disk. Returns 0 for a fresh install.
    /// </summary>
    /// <returns>Total bytes freed across the entire history.</returns>
    public long GetLifetimeBytesFreed() => _history.GetLifetimeBytesFreed();

    /// <summary>
    /// Returns every successful non-dry-run history row as (path, bytes_freed). Used by the History
    /// tab's per-library chart so the totals reflect ALL history — not just the newest 500 rows the
    /// paginated <see cref="GetHistory(int)"/> returns. Path stays server-side; the caller resolves
    /// it to a library name via Jellyfin's library manager in one pass.
    /// </summary>
    /// <returns>One row per successful non-dry-run fix.</returns>
    public IReadOnlyList<(string Path, long BytesFreed)> GetLifetimePathTotals() => _history.GetLifetimePathTotals();

    /// <summary>
    /// Per-type breakdown of the lifetime reclaim total. Powers the donut on the right half of the
    /// Overview reclaim card so the user can see which fix type has recovered the most disk.
    /// </summary>
    /// <returns>One row per type that ever produced a successful non-dry-run fix.</returns>
    public IReadOnlyList<IssueSummary> GetLifetimeSummary() => _history.GetLifetimeSummary();

    /// <summary>
    /// Aggregates the history for a given month into per-type success counts + total bytes freed.
    /// Filters to success=1 and dry_run=0 so dry-run rehearsals never inflate the analytics numbers.
    /// </summary>
    /// <param name="monthStartUtc">First instant of the target month (UTC).</param>
    /// <param name="monthEndUtc">First instant of the following month (UTC) — exclusive upper bound.</param>
    /// <returns>A per-type count map plus the total bytes freed.</returns>
    public MonthAggregate GetMonthAggregate(DateTime monthStartUtc, DateTime monthEndUtc) => _history.GetMonthAggregate(monthStartUtc, monthEndUtc);

    /// <summary>
    /// Wipes scan state — Detected issues and probe/decode caches — so the next scan starts from
    /// scratch. UI-06: preserves Queued and Dismissed issues so the user's prior approvals and
    /// ignore decisions survive the reset. Fix history and the recycle bin are preserved so recently
    /// removed files can still be restored.
    /// </summary>
    public void ResetScanState()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        // Only wipe Detected rows — Queued (approved, awaiting fix) and Dismissed (ignored) carry
        // the user's intent and must survive a reset. The old DELETE wiped everything, silently
        // undoing their approvals and letting dismissed files re-appear on the next scan.
        using (var issuesCmd = connection.CreateCommand())
        {
            issuesCmd.Transaction = transaction;
            issuesCmd.CommandText = "DELETE FROM issues WHERE status = @detected";
            issuesCmd.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
            issuesCmd.ExecuteNonQuery();
        }

        foreach (var table in new[] { "probe_cache", "decode_cache", "audio_decode_cache", "format_probe_cache", "interleave_cache" })
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
#pragma warning disable CA2100 // table name is a compile-time literal from the enumeration above.
            cmd.CommandText = "DELETE FROM " + table;
#pragma warning restore CA2100
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Marks a history entry as restored from the recycle bin.
    /// </summary>
    /// <param name="historyId">The history entry id.</param>
    public void MarkRestored(long historyId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE history SET restored = 1 WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", historyId);
        cmd.ExecuteNonQuery();
    }

    // ─── restore_operations (schema v7) ─────────────────────────────────
    // Owner-tokened claim table introduced in 05-B for the restore workflow. entry_key uniqueness
    // is the serialization point: two concurrent callers targeting the same recycled bin entry
    // both try to INSERT with the same entry_key; the SQLite UNIQUE constraint makes exactly one
    // succeed. operation_id is the owner token — only the caller that holds it can Complete /
    // Abort / MarkNeedsReview the operation. This replaces the pre-05-B `history.restored = 1`
    // claim pattern that (a) couldn't be released by owner (any caller could flip it back), and
    // (b) couldn't distinguish "restore in progress" from "restore succeeded".

    /// <summary>
    /// Atomically claims the exclusive right to restore the bin entry identified by
    /// <paramref name="entryKey"/>. Returns a fresh operation id (the owner token) on success,
    /// or <c>null</c> when another Pending / NeedsReview / Completed row already exists for the
    /// same key.
    /// </summary>
    /// <param name="entryKey">Canonical identity of the bin entry. Callers pass the recycle path
    /// (normalised) — multiple history rows may point at the same bin entry, and this key
    /// serializes physical access.</param>
    /// <param name="historyId">Optional history row that triggered this restore. Null for
    /// manifest-only restores where no history row exists.</param>
    /// <param name="sourcePath">The bin file/directory being restored.</param>
    /// <param name="destinationPath">The resolved final target on disk (post-suffix, post-force
    /// swap).</param>
    /// <returns>The operation id to hand back to Complete/Abort/MarkNeedsReview, or null when
    /// this entry is already claimed.</returns>
    public string? TryBeginRestore(string entryKey, long? historyId, string sourcePath, string destinationPath)
        => _restoreOps.TryBeginRestore(entryKey, historyId, sourcePath, destinationPath);

    /// <summary>
    /// Marks a Pending restore as successful and removes the row so the entry_key is free to be
    /// re-claimed later. Owner-mismatched calls (unknown or wrong <paramref name="operationId"/>,
    /// or the operation is no longer Pending) affect zero rows and return false.
    /// </summary>
    /// <param name="operationId">The owner token returned by <see cref="TryBeginRestore"/>.</param>
    /// <param name="finalDestination">The actual destination path the FS work landed at (may
    /// differ from the value at claim time if a late collision forced a re-resolve).</param>
    /// <returns>True when the row was Pending under this owner and successfully removed.</returns>
    public bool CompleteRestore(string operationId, string finalDestination) => _restoreOps.CompleteRestore(operationId, finalDestination);

    /// <summary>
    /// Releases a Pending claim without recording success. Used by the caller-recoverable failure
    /// path (validation failed, filesystem work threw before mutating anything). Owner-mismatched
    /// or non-Pending rows are left untouched.
    /// </summary>
    /// <param name="operationId">The owner token returned by <see cref="TryBeginRestore"/>.</param>
    /// <returns>True when a Pending row was owned by this caller and removed.</returns>
    public bool AbortRestore(string operationId) => _restoreOps.AbortRestore(operationId);

    /// <summary>
    /// Transitions a Pending operation to NeedsReview. Used when filesystem mutation started but
    /// couldn't be verifiably completed (partial move, missing verification step, ambiguous state).
    /// The row stays in the table so <see cref="GetRestoreOperations"/> surfaces it for manual
    /// triage; another restore of the same entry is refused until an admin cleans up.
    /// </summary>
    /// <param name="operationId">The owner token returned by <see cref="TryBeginRestore"/>.</param>
    /// <param name="detail">Human-readable explanation of what left the operation in an
    /// uncertain state. Persisted verbatim.</param>
    /// <returns>True when a Pending row was owned by this caller and transitioned.</returns>
    public bool MarkRestoreNeedsReview(string operationId, string detail) => _restoreOps.MarkRestoreNeedsReview(operationId, detail);

    /// <summary>
    /// Startup sweep: any Pending row is from a process that died mid-restore (the runtime
    /// contract is that every successful path either Completes or Aborts before returning).
    /// Convert every Pending row to NeedsReview with a detail explaining process death; also
    /// DELETE any Completed rows left over from a crash between UPDATE-to-Completed and the
    /// DELETE inside <see cref="CompleteRestore"/>. Per plan §05-B: "inspect known paths,
    /// preserve uncertain data and never automatically repeat destructive moves".
    /// </summary>
    /// <returns>The count of Pending rows that were reclassified as NeedsReview.</returns>
    public int ReconcilePendingRestoresAtStartup() => _restoreOps.ReconcilePendingRestoresAtStartup();

    /// <summary>
    /// Returns every row currently in restore_operations. Used by the NeedsReview surface and
    /// by tests that verify post-crash state.
    /// </summary>
    /// <returns>All rows, newest-first by <c>started_utc_ticks</c>.</returns>
    public IReadOnlyList<RestoreOperationRow> GetRestoreOperations() => _restoreOps.GetRestoreOperations();

    /// <summary>
    /// Records that a user restored a file at <paramref name="path"/> for a given IssueType. The
    /// FixTask auto-queue step consults this table so a restored file never gets auto-fixed again —
    /// the user explicitly reversed the fix. Manual approval from the Issues tab still works.
    /// Idempotent: repeated restores for the same (path, type) update the timestamp, not the row.
    /// Use <see cref="RestoredPathAnyType"/> for manifest-only restores where the type isn't known.
    /// </summary>
    /// <param name="path">The absolute path that was restored.</param>
    /// <param name="type">The issue type that had originally recycled it, or <see cref="RestoredPathAnyType"/> when unknown.</param>
    public void MarkPathRestored(string path, IssueType type)
    {
        MarkPathRestored(path, (int)type);
    }

    /// <summary>
    /// Records a restore for a manifest-only bin entry, where the caller doesn't know the IssueType
    /// (only the origin path). Recorded under <see cref="RestoredPathAnyType"/> so the auto-queue
    /// gate blocks every type at that path — the user restored it, they don't want any scanner
    /// touching it again automatically.
    /// </summary>
    /// <param name="path">The absolute path that was restored.</param>
    public void MarkPathRestoredForAnyType(string path)
    {
        MarkPathRestored(path, RestoredPathAnyType);
    }

    internal bool CompleteMoveUndo(string originalId, string reverseId, string owner)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (_moves.CompleteUndo(connection, transaction, originalId, reverseId, owner) is not { } original
            || MediaMoveManifest.TryRead(original.ManifestJson, originalId) is not { } manifest)
        {
            return false;
        }

        foreach (var entry in manifest.Plan.Entries)
        {
            foreach (var payload in entry.Inventory)
            {
                var path = payload.RelativePath.Length == 0 ? entry.Source : Path.Combine(entry.Source, payload.RelativePath);
                using var marker = connection.CreateCommand();
                marker.Transaction = transaction;
                marker.CommandText = "INSERT INTO restored_paths(path,type,restored_at_utc) VALUES(@path,@type,@ticks) ON CONFLICT(path,type) DO UPDATE SET restored_at_utc=excluded.restored_at_utc";
                marker.Parameters.AddWithValue("@path", path);
                marker.Parameters.AddWithValue("@type", RestoredPathAnyType);
                marker.Parameters.AddWithValue("@ticks", DateTime.UtcNow.Ticks);
                marker.ExecuteNonQuery();
            }
        }

        IssueStore.InvalidateUndoneMove(
            connection,
            transaction,
            original.IssueId,
            manifest.Plan.Type == IssueType.ManualMove ? manifest.Plan.ItemPath : manifest.Plan.Entries[0].Source);
        HistoryStore.Insert(connection, transaction, new HistoryEntry
        {
            IssueId = original.IssueId, Type = manifest.Plan.Type, Path = manifest.Plan.Entries[0].Source,
            Action = "Undid the complete recorded move and restored its original locations.", Success = true,
            FixedAtUtc = DateTime.UtcNow, MoveOperationId = reverseId
        });
        transaction.Commit();
        return true;
    }

    internal bool FinalizeMove(string id, string owner, string expectedState, MediaMoveManifest manifest)
    {
        if (!manifest.CleanupComplete || !manifest.HasCompletePayloads())
        {
            return false;
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!MediaMoveStore.UpdateWithinTransaction(connection, transaction, id, owner, expectedState, "Completed", manifest.Serialize())
            || MediaMoveStore.Read(connection, transaction, id) is not { } operation)
        {
            return false;
        }

        IssueStore.InvalidateMovedPaths(connection, transaction, manifest.Plan, operation.IssueId, id);
        ProbeCacheStore.InvalidateMovedPaths(connection, transaction, manifest.Plan);
        transaction.Commit();
        return true;
    }

    private void MarkPathRestored(string path, int rawType)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO restored_paths (path, type, restored_at_utc)
            VALUES (@path, @type, @at)
            ON CONFLICT (path, type) DO UPDATE SET restored_at_utc = excluded.restored_at_utc
            """;
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@type", rawType);
        cmd.Parameters.AddWithValue("@at", DateTime.UtcNow.Ticks);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns whether the given (path, type) tuple was ever restored — either as an exact match
    /// or via the wildcard <see cref="RestoredPathAnyType"/> sentinel that manifest-only restores use.
    /// FixTask auto-queue and the Issues tab badge both check this.
    /// </summary>
    /// <param name="path">The absolute path to look up.</param>
    /// <param name="type">The issue type the caller is about to auto-queue.</param>
    /// <returns>True when a matching row exists.</returns>
    public bool WasPathRestored(string path, IssueType type)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM restored_paths WHERE path = @path AND (type = @type OR type = @any) LIMIT 1";
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@type", (int)type);
        cmd.Parameters.AddWithValue("@any", RestoredPathAnyType);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Returns the paths (of the currently-Detected issues for <paramref name="type"/>) that were
    /// previously restored, so FixTask can skip them during the auto-queue step. Batched with a
    /// single query instead of one lookup per issue.
    /// </summary>
    /// <param name="type">The issue type being auto-queued.</param>
    /// <returns>Set of paths (case-sensitive) that must NOT auto-queue.</returns>
    public IReadOnlySet<string> GetRestoredPathsBlockingAutoQueue(IssueType type)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT path FROM restored_paths WHERE type = @type OR type = @any";
        cmd.Parameters.AddWithValue("@type", (int)type);
        cmd.Parameters.AddWithValue("@any", RestoredPathAnyType);
        var result = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <summary>
    /// Persists a diagnostic entry to the diagnostics table with source+hash uniqueness. If the row
    /// exists (same source + message hash), count is incremented and last_at_utc is bumped —
    /// mirrors the in-memory ring buffer's dedup semantics but survives plugin reloads / updates.
    /// After write, the table is truncated to the newest <paramref name="maxRows"/> entries so it
    /// can't grow unbounded across long-running installs.
    /// </summary>
    /// <param name="source">Short source label.</param>
    /// <param name="messageHash">Hash of the pre-truncation message; used as dedup key.</param>
    /// <param name="message">Truncated (≤ 800 char) message stored for the Errors tab display.</param>
    /// <param name="atUtc">When this dedup group first fired.</param>
    /// <param name="lastAtUtc">When the current occurrence fired.</param>
    /// <param name="maxRows">Ring-buffer size the writer caps at (default 100).</param>
    public void SaveDiagnostic(string source, int messageHash, string message, DateTime atUtc, DateTime lastAtUtc, int maxRows = 100)
        => _diagnostics.SaveDiagnostic(source, messageHash, message, atUtc, lastAtUtc, maxRows);

    /// <summary>
    /// Returns the persisted diagnostic entries, newest first. Populates the in-memory ring buffer
    /// on plugin startup so errors survive plugin updates / Jellyfin restarts.
    /// </summary>
    /// <param name="limit">Maximum entries to return.</param>
    /// <returns>Newest diagnostic entries first.</returns>
    public IReadOnlyList<(string Source, int MessageHash, string Message, int Count, DateTime AtUtc, DateTime LastAtUtc)> LoadDiagnostics(int limit = 100)
        => _diagnostics.LoadDiagnostics(limit);

    /// <summary>
    /// Returns the total row count in the diagnostics table. Cheaper than <see cref="LoadDiagnostics"/>
    /// when the Errors tab just needs to know whether a "Load older" button should appear.
    /// </summary>
    /// <returns>Total persisted diagnostic entries.</returns>
    public int CountDiagnostics() => _diagnostics.CountDiagnostics();

    /// <summary>
    /// Reads a small string value from the singleton <c>plugin_state</c> key/value table. Returns
    /// null when the row is missing. Used to persist ephemeral state (LastFixRun,
    /// RedownloadWarnings) across plugin reloads.
    /// </summary>
    /// <param name="key">The state key.</param>
    /// <returns>The stored value, or null.</returns>
    public string? GetState(string key)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM plugin_state WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        var raw = cmd.ExecuteScalar();
        return raw is string s ? s : null;
    }

    /// <summary>
    /// Upserts a small string value into the singleton <c>plugin_state</c> key/value table.
    /// Pass null to delete the row.
    /// </summary>
    /// <param name="key">The state key.</param>
    /// <param name="value">The value to store; null clears the row.</param>
    public void SetState(string key, string? value)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        if (value is null)
        {
            cmd.CommandText = "DELETE FROM plugin_state WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", key);
        }
        else
        {
            cmd.CommandText = "INSERT INTO plugin_state (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
        }

        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Wipes the diagnostics table. Used by the Errors → Clear button and by Reset.
    /// </summary>
    public void ClearDiagnostics() => _diagnostics.ClearDiagnostics();

    /// <summary>
    /// Deletes a specific diagnostic entry identified by <paramref name="source"/> and
    /// <paramref name="messageHash"/>. Used when a one-click resolution (e.g. the Errors tab
    /// "Merge into current bin" button) makes a diagnostic obsolete — keeping the row would
    /// leave a stale flag on the Errors tab even though the underlying condition is fixed.
    /// </summary>
    /// <param name="source">Diagnostic source tag.</param>
    /// <param name="messageHash">Hash of the pre-truncation message (see <see cref="Api.Diagnostics.Record"/>).</param>
    public void DeleteDiagnostic(string source, int messageHash) => _diagnostics.DeleteDiagnostic(source, messageHash);

    /// <summary>
    /// Deletes every persisted diagnostic whose source starts with <paramref name="sourcePrefix"/>
    /// OR matches it exactly. Backs review finding P2-8: the in-memory buffer only holds ~100
    /// entries, so deleting by (source, hash) tuples derived from that buffer leaves older
    /// SQLite rows behind. When a user turns off a whole subsystem (SMART monitoring, etc.) the
    /// dashboard promises to clear every historical row for that subsystem — this method makes
    /// that promise true regardless of how many rows accumulated before the toggle.
    /// </summary>
    /// <param name="sourcePrefix">
    /// Prefix to match. Rows whose source equals the prefix, or begins with the prefix followed
    /// by <c>.</c>, are deleted. This matches both the base subsystem source (e.g. "SmartHealth")
    /// and its dotted children (e.g. "SmartHealth.MalwareRisk").
    /// </param>
    /// <returns>Number of rows deleted.</returns>
    public int DeleteDiagnosticsBySourcePrefix(string sourcePrefix) => _diagnostics.DeleteDiagnosticsBySourcePrefix(sourcePrefix);

    // Delegates to <see cref="MediaDashConnectionFactory"/>. Kept as a thin forwarder so the
    // ~60 internal callers inside MediaDashDb don't churn — they already say `using var connection = Open();`
    // Later 05-C sub-substeps that migrate methods off this facade into dedicated stores will
    // consume the factory directly and drop this helper.
    private SqliteConnection Open() => _connectionFactory.Open();

    /// <summary>
    /// Returns the Playability repair-ladder tally for the Overview panel. Rung labels are parsed
    /// from <c>history.action</c> against the known set emitted by <see cref="Fixers.PlayabilityFixer"/>;
    /// anything unknown buckets as "other". Bytes-saved sums <c>saved_bytes</c> (v9 schema column) —
    /// pre-v9 repair rows contribute 0.
    /// </summary>
    /// <param name="sinceTicks">Lower bound for the 30-day window; rows with <c>fixed_at_utc &gt;= sinceTicks</c> populate the 30d fields, all rows populate lifetime.</param>
    /// <returns>A populated summary. Front-end hides the panel on <c>CountLifetime == 0</c>.</returns>
    public Api.RepairSummary GetRepairSummary(long sinceTicks)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT action, saved_bytes, fixed_at_utc
            FROM history
            WHERE type = @type
              AND success = 1
              AND dry_run = 0
              AND (action LIKE 'repaired %' OR action LIKE 'partially repaired %')
            """;
#pragma warning disable CA2100 // (int)enum is a compile-time constant, not user input.
        cmd.Parameters.Add("@type", SqliteType.Integer).Value = (int)IssueType.Playability;
#pragma warning restore CA2100

        var summary = new Api.RepairSummary();
        var rungBuckets = new Dictionary<string, (int Count, long Bytes)>(StringComparer.Ordinal);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var action = reader.GetString(0);
            var savedBytes = reader.GetInt64(1);
            var fixedAtUtc = reader.GetInt64(2);
            var inWindow = fixedAtUtc >= sinceTicks;
            var rung = ParseRepairRungLabel(action);

            summary.CountLifetime++;
            summary.SavedBytesLifetime += savedBytes;
            if (inWindow)
            {
                summary.Count30d++;
                summary.SavedBytes30d += savedBytes;
                var existing = rungBuckets.TryGetValue(rung, out var v) ? v : (Count: 0, Bytes: 0L);
                rungBuckets[rung] = (existing.Count + 1, existing.Bytes + savedBytes);
            }
        }

        summary.Rungs30d = rungBuckets
            .Select(kv => new Api.RepairRungTotal { Rung = kv.Key, Count = kv.Value.Count, SavedBytes = kv.Value.Bytes })
            .OrderByDescending(r => r.Count)
            .ThenByDescending(r => r.SavedBytes)
            .ToList();

        return summary;
    }

    // Parses the rung key from a Playability repair action string. PlayabilityFixer emits actions
    // in the shape "repaired <basename> (<rung label>)" (or "partially repaired ..." with the same
    // parenthesised tail); the rung label is a fixed vocabulary. Anything outside the vocabulary
    // buckets to "other" so a future rung addition surfaces on the panel without silently dropping.
    // Pure and testable — no I/O, no DB access.
    //
    // Substring-match on ` (<label>` (leading space + opening paren) rather than paren-extract so
    // the nested-paren case "container changed to .mkv (Jellyfin watch history reset)" still hits
    // — a naive paren walk would extract the inner "Jellyfin watch history reset" and bucket "other".
    internal static string ParseRepairRungLabel(string action)
    {
        if (string.IsNullOrEmpty(action))
        {
            return "other";
        }

        if (action.Contains(" (quick remux", StringComparison.Ordinal))
        {
            return "remux";
        }

        if (action.Contains(" (dropped", StringComparison.Ordinal))
        {
            return "drop";
        }

        if (action.Contains(" (container changed", StringComparison.Ordinal))
        {
            return "coerce";
        }

        if (action.Contains(" (audio re-encoded", StringComparison.Ordinal))
        {
            return "audio-reencode";
        }

        if (action.Contains(" (video re-encoded", StringComparison.Ordinal))
        {
            return "reencode";
        }

        return "other";
    }
}
