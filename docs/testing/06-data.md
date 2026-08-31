# 06 · Data layer

`MediaDashDb` (SQLite) plus entities: `HistoryEntry`, `Issue`,
`IssueStatus`, `IssueSummary`, `IssueType`, `FormatProbeResult`,
`MonthAggregate`.

Return to [INDEX](INDEX.md).

---

## Session prep

- [ ] **P.1** `$env:TOKEN` set.
- [ ] **P.2** Locate DB file:
      `%LOCALAPPDATA%\jellyfin\plugins\configurations\MediaDash*\mediadash.db`
      (path shown in `/Environment`). Record here: `_____________`.
- [ ] **P.3** Install `sqlite3.exe` if not present, or use `dotnet-ef`:
      ```powershell
      # quick client
      winget install --silent SQLite.SQLite
      ```
- [ ] **P.4** Backup current DB before running destructive tests:
      ```powershell
      Copy-Item <dbpath> "$env:TEMP\mediadash-e2e-backup.db"
      ```

Helper:
```powershell
function Q($sql) { & sqlite3.exe <dbpath> $sql }
```

---

## 06-A · MediaDashDb  (relies on `MediaDashDbTests`)

### Bootstrap
- [ ] **A.1** Delete DB. Restart Jellyfin. DB re-created, schema at
      current migration.
- [ ] **A.2** Schema — verify tables exist:
      ```
      Q ".tables"
      ```
      Expect `Issues`, `History`, `FormatProbeResults`, `MonthAggregates`,
      `KeyValue` (or similar).

### Migrations
- [ ] **A.3** Restore an old DB from `_stage_v0*/` (bundled staging
      folders keep historical DBs). Restart. Log shows migration steps
      executed; no data lost from existing tables (row counts before ==
      after for unchanged tables).
- [ ] **A.4** Rollback path — plugin refuses to load if DB is at a
      version newer than plugin knows. Log message clear.

### Transaction safety
- [ ] **A.5** Kill Jellyfin during a fix run (Task Manager). Restart. DB
      not corrupted; last transaction rolled back (issues not partially
      approved).
- [ ] **A.6** WAL file cleaned up on graceful shutdown.

### Concurrency
- [ ] **A.7** Simultaneous scan + fix (impossible in practice but exercise
      via two long API calls) — no `SQLITE_BUSY` errors in log.

### Cleanup — restore your backup if needed
- [ ] **A.8** `Copy-Item "$env:TEMP\mediadash-e2e-backup.db" <dbpath>
      -Force` — done after chapter.

---

## 06-B · Issue entity

### CRUD
- [ ] **B.1** After a scan, `SELECT count(*) FROM Issues` matches
      `GET /MediaDash/Issues` array length.
- [ ] **B.2** Each row has `Id`, `Type`, `Path`, `LibraryId`,
      `DetectedUtc`, `Status`, `Metadata` (JSON blob).
- [ ] **B.3** `Metadata` JSON round-trips via API — see 03-B.6.

### IssueStatus enum
- [ ] **B.4** Values: `Open`, `Approved`, `Dismissed`, `Fixed`, `Reverted`
      (verify from `IssueStatus.cs`).
- [ ] **B.5** Each status reachable via API and readable back correctly.

### Uniqueness
- [ ] **B.6** Re-running a scan does NOT duplicate open issues for
      unchanged files (identity by type + path).
- [ ] **B.7** Fixed issue removed from disk that reappears (e.g. re-
      downloaded) creates a new Issue with different `Id` and separate
      `DetectedUtc`.

---

## 06-C · IssueSummary

Rolls up counts by type / library. Used in `LibraryStats`.

- [ ] **C.1** After a scan producing multiple issue types, summary matches
      `SELECT Type, count(*) FROM Issues GROUP BY Type`.
- [ ] **C.2** Summary updates immediately on API approve (not stale).

---

## 06-D · HistoryEntry

- [ ] **D.1** After a fix run, `SELECT count(*) FROM History` matches
      fanout expectation (one row per fixed issue).
- [ ] **D.2** Fields: `Id`, `TimestampUtc`, `IssueType`, `Path`, `Result`
      (enum: Success/Failure/DryRun), `DryRun`, `BytesFreed`,
      `ErrorMessage`, `BinPath` (nullable), `MetadataJson`.
- [ ] **D.3** `BytesFreed` non-null only for space-affecting fixers.

---

## 06-E · FormatProbeResult

- [ ] **E.1** After scan, one row per media file: `Path`, `DurationSec`,
      `Streams` (JSON), `Format` (JSON), `LastProbedUtc`.
- [ ] **E.2** Re-scan without file changes → row `LastProbedUtc` updated
      but content stable (idempotent).
- [ ] **E.3** File deleted + rescan → row removed by cleanup pass.

---

## 06-F · MonthAggregate

Rollup used by `History/Stats`.

- [ ] **F.1** After several fix runs across months (or backdated
      history via SQL), `MonthAggregate` returns per-month totals matching
      `SELECT strftime('%Y-%m', TimestampUtc), sum(BytesFreed) FROM
      History GROUP BY 1`.
- [ ] **F.2** Empty months not returned as null rows.

---

## 06-G · IssueType coverage

Every enum value round-trips through scan → DB → API. Reuse fixtures
across chapters:

- [ ] **G.1** `Duplicate` (01-C).
- [ ] **G.2** `Playability` (01-J).
- [ ] **G.3** `Quality` (01-K).
- [ ] **G.4** `SubtitleLanguage` (01-N).
- [ ] **G.5** `AudioLanguage` (01-B).
- [ ] **G.6** `Misplaced` (01-F).
- [ ] **G.7** `MissingSubtitles` (01-G).
- [ ] **G.8** `Stale` (01-L).
- [ ] **G.9** `CorruptArtwork` (01-A).
- [ ] **G.10** `MalwareRisk` (01-O).
- [ ] **G.11** `Ungrouped` (01-E).
- [ ] **G.12** `LargeTrickplay` (01-Q).
- [ ] **G.13** `SubtitleFonts` (01-M).
- [ ] **G.14** `OrphanedDebris` (01-I).
- [ ] **G.15** `CorruptNfo` (01-H).
- [ ] **G.16** `HeavyTranscode` (01-P).
- [ ] **G.17** `FailedTranscode` (01-P).
- [ ] **G.18** `EmbeddedCoverArt` (01-D).

Every value seen in the DB `Issues.Type` column at least once during
this chapter's runs. Verify:
```
Q "SELECT DISTINCT Type FROM Issues ORDER BY Type;"
```

---

## End-of-chapter cleanup

- [ ] **Z.1** Restore DB backup if you ran section 06-A.
- [ ] **Z.2** `Reset`.
- [ ] **Z.3** Update INDEX progress.
