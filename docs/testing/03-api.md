# 03 · HTTP API

Every endpoint in `MediaDashController` (`/MediaDash/*`) and
`FileBrowserController` (`/MediaDash/Files/*`), plus one-round-trip
verification of each DTO.

All endpoints require elevated user session (`[RequiresElevation]`).
Token comes from `00-setup.md` §3.

Return to [INDEX](INDEX.md).

---

## Session prep

- [x] **P.1** `$env:TOKEN` set.
- [!] **P.2** Save the reusable header: `X-Emby-Token` shape returns 401
      (F-008 still true). Use `$HAuth = @{ Authorization = $env:JFAUTH; "Content-Type" = "application/json" }` with the full MediaBrowser header instead.
- [!] **P.3** Route base: both `/MediaDash/Status` and `/mediadash/status`
      return **200** with `Authorization` header, **401** with raw
      `X-Emby-Token` header (F-008). Case-insensitivity confirmed.

---

## 03-A · MediaDashController · Status & schedule endpoints

### GET /MediaDash/Status → `StatusResponse`
- [!] **A.1** Actual keys entirely differ from doc — see **F-039**.
      Real keys: `IsScanning, IsFixing, OpenIssueTotal, FailedHistoryTotal,
      FreeDiskBytes, TotalDiskBytes, TotalPotentialSavings,
      LifetimeBytesReclaimed, LifetimeCounts, Counts, PendingFixCount,
      Drives, System, RecycleBinPath, DataDirectory, RecycleBinCrossVolume,
      RecycleBinBytes, RecycleBinFileCount, RecycleBinRetentionDays,
      LastFixRun, RedownloadWarnings`. No `lastScanUtc`, `nextScheduled*`,
      `queueLength`, `configVersion`, `dryRun` anywhere on Status.
- [x] **A.2** Types on the fields that ARE present are correct
      (bools, ints/int64s, ISO8601 on `LastFixRun.FinishedAtUtc`).
- [x] **A.3** During a running scan: renamed key `IsScanning=true` observed
      immediately after `POST /Scan`. (`queueLength` N/A — F-039.)
- [x] **A.4** No auth → 401. Non-admin token → 403. Verified against a
      freshly-created `e2enonadmin` (existing `crack` user is admin;
      password unknown — noted).

### POST /MediaDash/Scan
- [x] **A.5** Empty POST → 204. `IsScanning` observed True immediately.
- [x] **A.6** Three back-to-back 204 responses; scan actually starts once
      (fixture library scans in under ~200ms so log-line dedup not
      inspected).

### POST /MediaDash/Scan/Suspicious → object
- [!] **A.7** Returns `{"Detected":0,"ElapsedMs":1}` — key `Detected`, not
      `count`, plus extra `ElapsedMs`. See **F-040**.
- [x] **A.8** Sync, 1–2 ms.

### POST /MediaDash/Scan/Cancel
- [x] **A.9** Cancel during scan → `IsScanning=false` in ~40 ms.
- [x] **A.10** Cancel when idle → 204, no-op.

### POST /MediaDash/Fix
- [x] **A.11** Approved issue → 204. History entry created with
      `Success=true, WasDryRun=true, Action="Preview only — no files were
      changed. Would have..."`. Note: field names on HistoryDto differ from
      docs — see **F-041**.
- [!] **A.12** No approved → 204, but NO `nothingToDo=true` history entry
      is written (no such field exists on HistoryDto — F-041).

### POST /MediaDash/Fix/Cancel
- [x] **A.13** 204. In DryRun the fix completes so fast that IsFixing was
      already false by the time cancel arrived; endpoint accepts cleanly.
      ffmpeg-kill semantics not exercised (dry-run doesn't spawn ffmpeg).

### POST /MediaDash/Fix/IgnoreActivity
- [x] **A.14** POST → 204. Effect on next fix run not verifiable via API
      surface alone.

### POST /MediaDash/Schedule/Apply
- [!] **A.15** POST accepted (204). But `/Status` has no
      `nextScheduledScanUtc` field at all (F-039), and PluginConfiguration
      exposes no `ScanSchedule` field either — see **F-042**.
- [!] **A.16** Invalid cron string → **204** returned (should be 400).
      Empty `{}` body also 204. See **F-042**.

### POST /MediaDash/Reset
- [x] **A.17** Idle: 204, subsequent `GET /Issues` returns `[]` (verified,
      count 11 → 0).
- [x] **A.18** During running scan: 409 (Conflict). Confirmed.

---

## 03-B · MediaDashController · Issues endpoints

### GET /MediaDash/Issues (+ filters)
- [x] **B.1** No filter → all issues. Confirmed: 10 issues on fresh
      scan of the fixture set (default returns only status=Detected —
      see F-047).
- [x] **B.2** `?type=Duplicate` filters correctly. Returns [] (0 rows
      as expected under F-029). `?type=MissingSubtitles` returned 5 rows,
      all of type MissingSubtitles.
- [!] **B.3** `?status=Open` (0 in enum) is wrong on two counts —
      see **F-047** (real enum: 0=Detected, 1=Queued, 3=Dismissed) and
      **F-046** (string names silently ignored, integers filter correctly).
- [!] **B.4** `?libraryId=<guid>` — silent no-op. Any guid (movies,
      books, all-zeros) returns the full unfiltered set. See **F-045**.
- [!] **B.5** `?take=5` and `?skip=5` both no-ops. Returned all 10 rows,
      same Ids across both pages. See **F-044**.
- [!] **B.6** DTO shape does NOT match doc — see **F-043**. Real keys
      (PascalCase): `Id, ItemId, Type, Path, FileName, SuggestedFix,
      DetailsJson, SizeSavings, Status, DetectedAtUtc,
      WasPreviouslyRestored`. No `libraryName`, no `metadata`.

### POST /MediaDash/Issues/{id}/Approve
- [!] **B.7** Existing id → 204. Status flips to `Queued`, NOT
      `Approved`. See F-047. Approved item disappears from default
      `/Issues` listing (default filter = Detected only).
- [x] **B.8** Unknown id → 404. Body is standard ProblemDetails.
- [x] **B.9** Same id approved twice → 204 both times, idempotent.

### POST /MediaDash/Issues/{id}/Dismiss
- [x] **B.10** Dismissed a MissingSubtitles issue on `Multi Audio
      (2022).mkv`. Post-dismiss `?status=3` shows it as `Dismissed`.
      Re-scan did NOT re-emit the same path/type. (TTL configurable
      value not exercised — assumed within window.)

### POST /MediaDash/Issues/{id}/Revert  (relies on `IssueRevertTests`)
- [-] **B.11** Skipped: requires flipping DryRun=false against a real
      fixture and running Fix, which is out of scope for a 03-B pass.
      Single-issue Revert endpoint DOES exist and returned 204 on a
      queued (unfixed) issue (probably no-op).
- [!] **B.12** Cannot verify Permanent-disposal path without B.11.
      Note: Revert against unknown id `999999999` → 404 (not 400 as
      B.12 implies for `nothing to restore`).

### POST /MediaDash/Issues/ApproveAll?type=X
- [x] **B.13** `ApproveAll?type=MissingSubtitles` → 200, body is bare
      integer `3`. status=0 count dropped by 3, status=1 rose by 3.
- [x] **B.14** No `type` param → 200, body `1` (matched the one
      Detected issue present pre-call). Approves all detected regardless
      of type.

### POST /MediaDash/Issues/Bulk  ← `BulkIssueRequest`
- [x] **B.15** `{ "ids": [id1, id2], "action": "Approve" }` → 200 with
      body `2`. Note: response is a bare integer, NOT the JSON object
      with `count` field suggested by the doc.
- [!] **B.16** `Dismiss` works (200, body `1`). `Revert` is NOT
      supported by Bulk — 400 with `"Action must be 'Approve' or
      'Dismiss'."`. See **F-048**.
- [x] **B.17** Empty `ids` → 400 with body `"Ids required."`
- [!] **B.18** Mixed valid + invalid ids → 200 with bare integer body
      (count of successes). NO `errors[]` array in response. Invalid
      ids silently swallowed. See F-048 for the response-shape mismatch.

---

## 03-C · MediaDashController · History endpoints

### GET /MediaDash/History
- [!] **C.1** Returns array. Actual keys confirm F-041 exactly:
      `Id, Type, FileName, Library, Action, BytesFreed, FixedAtUtc,
      WasDryRun, Success, CanRestore`. No `path`, no `result`, no
      `errorMessage`, no `timestampUtc`, no `dryRun`. Doc row is stale.
- [x] **C.2** Sorted newest-first by `FixedAtUtc` descending, verified
      across all 500 returned rows.
- [!] **C.3** Cap is **500**, not ~1000. `?take=` and `?limit=` are
      silently ignored (always 500). See **F-049**.

### GET /MediaDash/History/Stats
- [!] **C.4** Returns `{ TotalBytesFreed, ByLibrary[] }` only. No
      `months`, no `totalRuns`. See **F-050**.
- [!] **C.5** Cannot verify: no month bucket in the response at all,
      `MonthAggregate` shape is unreachable via this endpoint. See F-050.

### POST /MediaDash/History/Clear
- [-] **C.6** Skipped by design: destructive on the dev box (500 rows
      of accumulated history). Should be covered in a scripted 02-*
      chapter that seeds+clears its own history sandbox. Backup written
      to `$env:TEMP\history-backup-03C.json` was not created (no clear
      run needed since skipped).
- [-] **C.7** Skipped: depends on C.6 being run.

### POST /MediaDash/History/{id}/Restore
- [!] **C.8** Restore of Id=574 (Playability, `broken-book.epub`,
      `CanRestore=true`) → 200 with
      `{"RestoredTo":"C:\\dev\\mediadash-fixtures\\books\\broken-book.epub","Suffixed":false}`.
      File confirmed present on disk after call. Doc claims fields
      `restoredPath` + `warnings`; actual DTO uses `RestoredTo` +
      `Suffixed`. See **F-051**. Note: restore worked even though
      DryRun was flipped ON — bin restore is not gated by DryRun, which
      is arguably correct (bin restore isn't a fix, it's an undo).
- [x] **C.9** Unknown id → 404 with standard ProblemDetails
      (`type: rfc9110#section-15.5.5`, `title: "Not Found"`, `status: 404`).

### POST /MediaDash/RedownloadWarnings/{historyId}/Acknowledge
- [-] **C.10** Skipped: `Status.RedownloadWarnings` is `[]`. No
      warning present to acknowledge. Needs a redownload scenario
      staged first (see 03-G.19).

### POST /MediaDash/RedownloadWarnings/{historyId}/RestoreOptimized
- [-] **C.11** Skipped: same prereq as C.10 (no warnings exist).

---

## 03-D · MediaDashController · Library / diagnostics endpoints

### GET /MediaDash/LibraryAccessCheck → `LibraryAccessResult[]`
- [!] **D.1** Returned 5 entries (F-005), each `{Name, Path, CanRead,
      CanWrite}` — PascalCase, extra `Name`, **no `warning` field**.
      See **F-052**.
- [-] **D.2** Skipped: destructive to dev-box permissions. Also see
      F-052 — with no `warning` field on the DTO, the assertion can't be
      met even if I forced `CanWrite=false`.

### GET /MediaDash/RecycleBinAccessCheck → `LibraryAccessResult`
- [!] **D.3** Returns a single object `{Name, Path, CanRead, CanWrite}`
      for the plugin's bin root (`...jellyfin-v10\data\mediadash\recycle`).
      Same shape drift as D.1 — see **F-053**.

### GET /MediaDash/RecycleBin/DiskInfo → `RecycleBinDiskInfo`
- [!] **D.4** Real keys: `PathProbed, TotalBytes, FreeBytes,
      MeetsFiveGbMinimum, SuggestedPauseCapGb` (PascalCase). No
      `binBytes`; `suggestedCapGb` renamed. See **F-054**.
- [-] **D.5** Skipped: unreproducible without artificially starving the
      volume (would risk the dev box). Cannot exercise
      `ComputeSuggestedPauseCapGbTests` from the outside.

### GET /MediaDash/Genres → string[]
- [x] **D.6** Returns `["Action","Adventure","Comedy","Crime",
      "Documentary","Drama","Horror","Mystery","Thriller"]` — plain
      string[] as expected.

### GET /MediaDash/Libraries → `LibraryInfo[]`
- [!] **D.7** **5** entries (F-005). Each entry:
      `{ItemId, Name, CollectionType, Locations[] }` — no `itemCount`
      (that's on LibraryStats), no scalar `path` (array of Locations
      instead), PascalCase. See **F-055**.

### GET /MediaDash/LibraryStats → `LibraryStat[]`
- [!] **D.8** Per-library object keys:
      `{ItemId, Name, CollectionType, ItemCount, TotalBytes,
      Resolutions, Codecs, Containers}` — histograms as
      `dict<string,int>`. No `videoCount/audioCount/bookCount/
      subtitleCount/bytes` breakdown. See **F-056**.

### GET /MediaDash/Environment → `EnvInfo`
- [!] **D.9** 5 fields only: `{PluginVersion, JellyfinVersion, Os,
      Framework, SubtitleProviders}`. Confirms **F-012** (missing
      `ffmpegPath`, `GpuInfo`) and **F-010** (`PluginVersion="0.0.0.0"`).
      No new finding.

### GET /MediaDash/Errors → `DiagnosticEntry[]`
- [!] **D.10** Real entry keys: `{AtUtc, Source, Message, Count,
      LastAtUtc}`. **`?full=true` has no effect** — response
      byte-identical to bare call (1 799 bytes). Never returns stacks.
      See **F-057**.

### GET /MediaDash/Errors/Count
- [!] **D.11** Returns `{"Total":N}`, not `{count:N}` (doc drift).
      `Total` matches array length. Noted inline in F-057.

### POST /MediaDash/Errors/Clear
- [x] **D.12** Snapshotted 5 errors → `evidence/F-057/errors-before-03D.json`.
      `POST /Errors/Clear` → 204, empty body. `GET /Errors/Count` after
      → `{"Total":0}`.

### GET /MediaDash/Logo
- [!] **D.13** 200 without Authorization (AllowAnonymous works),
      `Content-Type: image/png` — but body magic bytes `FF D8 FF E0`
      are **JPEG**, not PNG (61 600 bytes). Content-Type lies. See
      **F-058**.

### GET /MediaDash/I18n/{locale}
- [!] **D.14** `en` → JSON dict, 80 top-level keys (`tagline`,
      `scanIdle`, ...). Also: `/I18n/xx-INVALID` returns **200** with
      the English body instead of 404 — silent fallback. See **F-059**.

---

## 03-E · MediaDashController · Recycle bin endpoints

### GET /MediaDash/RecycleBin → `RecycleBinInfo`
- [!] **E.1** Real shape `{FileCount, SizeBytes, IsEmptying, EmptyingDone,
      EmptyingTotal}`. No `oldestUtc`, no per-bin breakdown. `itemCount` →
      `FileCount`, `bytes` → `SizeBytes`. See **F-060**.

### GET /MediaDash/RecycleBin/Items → `RecycleBinItem[]`
- [!] **E.2** DTO shape drift — real keys (PascalCase): `FileName,
      SizeBytes, RecycledAtUtc, AutoPurgesAtUtc, HistoryId, OriginalPath,
      Provenance, Reason, IssueType, ActionText, RestoreHint`. No `id`,
      no `binPath`, `binnedUtc` → `RecycledAtUtc`, `sourceHistoryId` →
      `HistoryId`. Also: `Items[].Length=1` while `GET /RecycleBin`
      reports `FileCount:2` on this box — divergence not explained by
      the doc. See **F-061**.

### POST /MediaDash/RecycleBin/Items/Restore  ← `BinRestoreRequest`
- [!] **E.3** Restore of `broken-comic.cbz` → 200 with
      `{"RestoredTo":"C:\\dev\\mediadash-fixtures\\comics\\broken-comic.cbz","Suffixed":false}`.
      Request DTO is `{BinPath: string}` (single-item, no `ids[]`).
      Response DTO is `{RestoredTo, Suffixed}` — matches the History
      restore shape (F-051), NOT doc's `{restoredPath, warnings}`. See
      **F-063**. File confirmed present on disk after call.
- [-] **E.4** Skipped: only one bin item present on this box (F-061),
      and the request DTO is single-BinPath (no `ids[]` — see F-063), so
      "restore many" would be a client-side loop rather than one call.
      Requires seeding ≥2 bin items to exercise, out of scope for a
      read-only 03-E pass.
- [x] **E.5** Collision test passed: with `broken-comic.cbz` already at
      the target path, re-restoring the same bin item returned 200 with
      `{"RestoredTo":"C:\\...\\broken-comic-restored.cbz","Suffixed":true}`.
      Original + `-restored` copy both present, then cleaned up.

### POST /MediaDash/RecycleBin/Empty
- [-] **E.6** Skipped by design: destructive on dev box, belongs in a
      scripted 02-* chapter with seed+empty sandbox. Prior-session bin
      items must survive this pass. Note in test doc: async empty
      progress fields (`IsEmptying`, `EmptyingDone`, `EmptyingTotal`) on
      `RecycleBinInfo` suggest the endpoint is fire-and-forget, which
      the doc's "returns updated `RecycleBinInfo`" wording does not
      capture.
- [-] **E.7** Skipped: depends on E.6.

### GET /MediaDash/RecycleBin/OtherBins → `OtherBinLocation[]`
- [!] **E.8** 200 with `[]` on this box (no orphan bins to discover).
      Element shape not verifiable without staging an alternate bin root.
      See **F-062** (logged so a future tester with a multi-bin box can
      finish shape verification).

### POST /MediaDash/RecycleBin/Consolidate  ← `ConsolidateRequest`
- [-] **E.9** Skipped: prerequisite is two bin roots on disk. Only one
      exists on this box (F-062 confirms `OtherBins=[]`).

### POST /MediaDash/RecycleBin/AdoptBatch  ← `AdoptBatchRequest`
- [-] **E.10** Skipped: prerequisite is one or more orphaned bin
      folders discoverable via `OtherBins`. None present (see E.8).

---

## 03-F · FileBrowserController (`/MediaDash/Files`)

### GET /List
- [x] **F.1** `DirectoryListing` = `{Path, Parent, IsRoot, IsRecycleBin,
      IsLogsDir, Entries[]}`. `FileEntry` = `{Name, IsDirectory, SizeBytes,
      ModifiedUtc, Kind}` — extra `Kind` field, PascalCase. See **F-064**.
- [!] **F.2** Returns configured library roots (7 entries), NOT drive
      letters. See **F-064**.
- [x] **F.3** `C:\Windows` → 403 (ProblemDetails, section-15.5.4).
- [x] **F.4** `movies\..\..\..\Windows` → 403. Canonicalization solid.
- [!] **F.5** Nonexistent inside allowlist → 404, nonexistent outside → 403.
      Doc claims 400; neither vector returns 400. See **F-064**.

### POST /Mkdir  ← `MkdirRequest`
- [!] **F.6** DTO is `{Path: parent, Name: leaf}`, NOT `{path: full}`.
      Correct shape → 204, folder exists. See **F-064**.
- [!] **F.7** Duplicate → **409** (`"An entry with that name already
      exists."`), not 400. See **F-064**.
- [x] **F.8** `C:\ProgramData\Evil-*` → 403, no folder created.

### POST /Rename  ← `RenameRequest`
- [x] **F.9** DTO is `{Path, NewName}`. src.txt → dst.txt inside sandbox
      returns 204, source gone, target exists.
- [!] **F.10** Rename to existing name → **409**, not 400. Same string
      as F.7. See **F-064**.
- [-] **F.11** Skipped: only C: drive on this box.

### POST /Move  ← `MoveOrCopyRequest`
- [x] **F.12** DTO `{From, To}`. Same-drive move → 204, source gone,
      target exists.
- [-] **F.13** Skipped: only C: drive on this box.
- [x] **F.14** Move sandbox file to `C:\ProgramData\escaped-*.txt` → 403,
      no target created, source untouched.

### POST /Copy  ← `MoveOrCopyRequest`
- [x] **F.15** Copy inside sandbox → 204, both files present.
- [!] **F.16** Collision → **409** not 400. `?overwrite=true` query and
      `{Overwrite: true}` body BOTH silently ignored — server always 409s.
      No overwrite escape hatch exists on this endpoint. See **F-064**.

### POST /Delete  ← `DeleteRequest`
- [x] **F.17** DTO `{Path}` only. Delete sandbox file → 204, file gone.
- [x] **F.18** Delete `C:\Windows\System32\drivers\etc\hosts` → 403, hosts
      untouched.
- [!] **F.19** DeleteRequest has NO `moveToBin` field. `?moveToBin=true`
      and `?moveToBin=false` query variants both silently ignored — delete
      always routes to the recycle bin (verified by `RecycleBin.FileCount`
      incrementing after both). No API path to permanently delete via
      /Files/Delete. See **F-064**.

### POST /Upload
- [!] **F.20** Endpoint expects `?path=<parent>&name=<leaf>` query params
      and reads `Request.Body` raw, NOT multipart. `curl -F "file=@..."`
      writes garbage/empty content. `curl --data-binary "@file" -H
      "Content-Type: application/octet-stream" ...?path=X&name=Y` → 204 and
      correct byte roundtrip. See **F-064**.
- [-] **F.21** Skipped: cap is hard-coded 50 GB in
      `FileBrowserController.UploadMaxBytes` (not `PluginConfiguration`);
      generating a 50-GB temp file to trip 413 is impractical on the dev
      box. The Content-Length short-circuit path is reachable but not
      exercised here.
- [x] **F.22** Upload to `C:\ProgramData` → 403, target untouched.

### GET /Download
- [x] **F.23** 200 with `Content-Disposition` and matching byte count
      after fixing the F.20 upload (23-byte roundtrip verified). Empty file
      streams 0 bytes correctly.
- [x] **F.24** Traversal (`movies\..\..\..\Windows\...\hosts`) → 403.
      Outright `C:\Windows\...\hosts` → 403. Both return standard
      ProblemDetails.
- [x] **F.25** 100 MB `--range 0-104857600` of the 26.9 GB fixture → 206
      Partial in 0.55 s at ~180 MB/s. Jellyfin RSS grew 0.5 MB (338.2 →
      338.7 MB). Streaming path clean, no buffer bloat.

---

## 03-G · DTO round-trips

Verify serialization matches C# definitions. Compare API responses to the
DTO classes in `Api/`.

Grep helper (memorize field names for each DTO):
```powershell
Get-Content C:\dev\mediadash\Jellyfin.Plugin.MediaDash\Api\StatusResponse.cs
```

- [!] **G.1** `StatusResponse` — see **F-039**. Full shape re-verified
      2026-08-28 matches F-039's key list exactly.
- [!] **G.2** `IssueDto` — no `metadata` dictionary; instead
      `DetailsJson` (a string containing serialized JSON) plus
      PascalCase fields `Id, ItemId, Type, Path, FileName, SuggestedFix,
      DetailsJson, SizeSavings, Status, DetectedAtUtc,
      WasPreviouslyRestored`. See **F-043**, **F-047**.
- [!] **G.3** `HistoryDto` — see **F-041**, **F-049**, **F-050**.
      Verified: `Id, Type, FileName, Library, Action, BytesFreed,
      FixedAtUtc, WasDryRun, Success, CanRestore` (500 rows returned).
- [!] **G.4** `LibraryStat` / `TypeCount` — see **F-056**. Actual keys:
      `ItemId, Name, CollectionType, ItemCount, TotalBytes, Resolutions,
      Codecs, Containers` (Resolutions/Codecs/Containers are
      dictionaries, not `TypeCount[]` arrays).
- [!] **G.5** `LibraryInfo` — see **F-055**. Actual keys:
      `ItemId, Name, CollectionType, Locations[]`.
- [!] **G.6** `LibraryAccessResult` — see **F-052**.
- [!] **G.7** `EnvInfo` — see **F-012**. Actual keys: `PluginVersion,
      JellyfinVersion, Os, Framework, SubtitleProviders[]`. No nested
      `GpuInfo` on this DTO (GPU data lives on `Status.System.Gpus[]`).
- [x] **G.8** `Errors` endpoint returns `[]` here (empty errors). Shape
      unverifiable without a fresh error; noted separately in **F-057**
      about `?full=true` having no effect. Nothing new to file.
- [!] **G.9** `RecycleBinInfo` (**F-060**), `RecycleBinItem` (**F-061**),
      `RecycleBinDiskInfo` (**F-054**), `OtherBinLocation` (**F-062**).
      `RecycleBinDiskInfo` sub-object not seen inside `/RecycleBin`
      response on this box — bin lives on the library drive so the
      cross-volume disk block does not populate.
- [-] **G.10** `ConsolidateResult` — skipped: needs two bin roots (same
      as E.9 — F-062).
- [!] **G.11** `RestoreResult` — see **F-063**. Actual shape
      `{RestoredTo, Suffixed}`.
- [x] **G.12** `SystemStats` / `DriveUsage` present inside
      `/Status.System` and `/Status.Drives[]` respectively. System keys:
      `CpuPercent, SystemCpuPercent, RamUsedBytes, RamTotalBytes,
      SystemRamUsedBytes, SystemRamTotalBytes, GpuPercent, GpuSource,
      Gpus[], CpuCoreCount, Platform, SystemStatsAvailable`. Drive keys:
      `Root, FreeBytes, TotalBytes, IsLibraryDrive, IsRecycleBinDrive,
      SmartHealth, SmartMessage, SmartModel, SmartTemperatureCelsius,
      SmartTemperatureMaxCelsius, SmartWearPercent`. No separate
      endpoint. Shape recorded in evidence.
- [-] **G.13** `PluginState` — both `/State` and `/PluginState` return
      **404**. No embedded `PluginState` sub-object on `/Status` either.
      Endpoint appears not to exist under `/MediaDash/*`; nothing to
      verify.
- [!] **G.14** `DirectoryListing` + `FileEntry` — see **F-064**.
- [x] **G.15** `FixRunSummary` — read from `Status.LastFixRun`. Actual
      keys: `FinishedAtUtc, Attempted, Succeeded, Failed,
      TopFailureCount`. Doc block doesn't spell out expected fields;
      recording actual shape as the source of truth.
- [-] **G.16** `RedownloadWarning` — `Status.RedownloadWarnings=[]` on
      this box. No redownload scenario has been enacted; skipped, will
      require the chapter-02 redownload flow to populate.
- [!] **G.17** Unknown-field leniency: **passes for all 8 DTOs**.
      Wrong-type strictness: **passes only for `BulkIssueRequest`**.
      The four `FileBrowserController` DTOs return **403** (allowlist
      eats the null-coerced Path), `BinRestoreRequest` and
      `ConsolidateRequest` return **404**, `AdoptBatchRequest` returns
      **400** but with a business-rule message not a validation error.
      Only `BulkIssueRequest` returns the proper 400 + `errors[]` model-
      binding response. See **F-065**.
- [x] **G.18** `RecycleReasonMapper` — bin contains items with distinct
      `Reason` strings (`"Duplicate — kept a better copy"` from History-
      provenance items, `"Manual delete via Files tab"` from Files-tab
      Manifest-provenance items). Mapper differentiates by issue type;
      broader coverage would need more issue types fixed. Partial pass.
- [-] **G.19** `RedownloadDetector` — needs the chapter-02 redownload
      scenario. Skipped (see chapter-02 for the required fix →
      re-appear-with-new-copy → history flag path).

---

## 03-H · Concurrency & security

- [!] **H.1** 10 parallel `POST /Scan` calls → 3 back-to-back scan runs
      observed (not 1). All 10 returned 204; no explicit "already
      running" dedup log line was emitted. Partial coalescing only. See
      **F-066**.
- [x] **H.2** 100 concurrent `GET /Status` calls → 100× 200, zero 5xx.
      Burst wall 2.54 s (well under 30 s cap). Per-request 2.19–2.37 s
      (server serialises reads; no crashes or connection drops).
- [!] **H.3** Only 10 detected issues exist on fixture set, not 1000
      — the < 5 s bound is not comparable. Actual: bulk-approve of 10
      returned 200 body `10` in 235 ms (~23.5 ms/issue → linear extrapolation
      ~23.5 s per 1000, but the bound and the shape may not scale linearly
      through DB writes). Recommend re-running against a seeded 1000-issue
      set from a scripted chapter-02 flow.
- [x] **H.4** Non-admin token (`e2enonadmin`) → 403 on all 3 sampled
      endpoints (`GET /Status`, `GET /Issues`, `GET /RecycleBin`).
      Non-admin account created via `/Users/New`, verified `IsAdmin=false`,
      and deleted at end.
- [x] **H.5** POST without `Authorization` header → 401 (both
      no-header-at-all and Cookie-only-no-Authorization variants).
      `GET /Status` unauthenticated also 401. Token IS required in the
      Authorization header; cookies alone don't authenticate. No CSRF
      surface exposed.
- [x] **H.6** `/Environment` returns 5 fields
      (`PluginVersion, JellyfinVersion, Os, Framework, SubtitleProviders`
      — see F-012). No `%USERPROFILE%` string, no `C:\Users\crackruckles`,
      no absolute path of any kind present. Redaction step is moot on the
      current shape; will need re-checking if F-012 is closed and paths
      are added.

---

## End-of-chapter cleanup

- [-] **Z.1** `POST /MediaDash/Reset` → 204, `OpenIssueTotal=0` post-call.
      **Bin-empty step skipped by design** (destructive to dev-box state
      per F-060). `RecycleBinFileCount=8` preserved into next session.
- [x] **Z.2** Snapshotted 1 diagnostic error to
      `evidence/03-H/z2-errors-before.json`, `POST /Errors/Clear` → 204,
      `GET /Errors/Count` → `{"Total":0}`.
- [ ] **Z.3** Update INDEX progress. (handled by parent)
