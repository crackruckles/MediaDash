# 05 · Scheduled Tasks

Everything in `Jellyfin.Plugin.MediaDash/ScheduledTasks/`.

Return to [INDEX](INDEX.md).

---

## Session prep

- [ ] **P.1** `$env:TOKEN` set.
- [ ] **P.2** Global dry-run **ON** for observation blocks; **OFF** where
      a real run is required.
- [ ] **P.3** Fresh state: `POST /MediaDash/Reset`.

Helper — invoke a scheduled task via Jellyfin's task API:
```powershell
function Start-Task([string]$key) {
  $tasks = curl.exe -s -H "X-Emby-Token: $env:TOKEN" http://localhost:8099/ScheduledTasks | ConvertFrom-Json
  $id = ($tasks | Where-Object { $_.Key -eq $key }).Id
  curl.exe -X POST -H "X-Emby-Token: $env:TOKEN" "http://localhost:8099/ScheduledTasks/Running/$id"
}
```

---

## 05-A · ScanTask  (relies on `ScanTaskProbeTests`)

Registered scheduled task that runs a full scan.

### Discovery
- [ ] **A.1** `/ScheduledTasks` list contains the MediaDash Scan task.
      Keys typically `MediaDash.Scan` or similar — verify:
      ```powershell
      curl.exe -s -H "X-Emby-Token: $env:TOKEN" http://localhost:8099/ScheduledTasks `
        | ConvertFrom-Json | Where-Object { $_.Category -match "MediaDash" }
      ```

### Run
- [ ] **A.2** `Start-Task "MediaDash.Scan"` (use the actual key from A.1).
      Jellyfin task queue shows task Running.
- [ ] **A.3** During the run, `/MediaDash/Status` shows
      `scanRunning=true`.
- [ ] **A.4** Task progress updates (integer 0-100) visible in
      Dashboard → Scheduled Tasks.
- [ ] **A.5** Log shows `ScanTask: started` and `ScanTask: complete` lines.

### Cancellation
- [ ] **A.6** Cancel via Jellyfin dashboard → task stops within 5 s;
      `Status.scanRunning=false`.

### Failure
- [ ] **A.7** Cause a hard failure (revoke read access to `$LIB` mid-run
      by moving files) → task ends with error, next run resumes cleanly.

### Cross-version (10.11 ↔ 12.0)
- [ ] **A.8** No `MissingMethodException` in log after task run.

---

## 05-B · FixTask  (relies on `FixTaskDiskFullTests`,
`FixTaskHistoryFanoutTests`, `FixTaskStaleFailureTests`,
`FixTaskSubtitleQuotaTests`)

Registered scheduled task that runs the fix queue.

### Discovery
- [ ] **B.1** Task visible in `/ScheduledTasks` with MediaDash category.

### Run
- [ ] **B.2** With approved issues, `Start-Task "MediaDash.Fix"` starts a
      run.
- [ ] **B.3** `Status.fixRunning=true` during run.
- [ ] **B.4** History fanout: one History row per issue fixed (verify
      count matches approved count).

### Disk-full behaviour
- [ ] **B.5** Fill target drive to < 2× source size. Trigger fix →
      transcode fixer refuses; other fix types still proceed (see
      02-M.6).
- [ ] **B.6** History row for the refused transcode has `error =
      InsufficientSpace` and non-fatal exit — task completes.

### Stale-failure behaviour
- [ ] **B.7** Reproduce a stale fix (issue since removed): task marks it
      stale, moves to next.

### Subtitle quota
- [ ] **B.8** Quota exhausted mid-run → remaining subtitle issues
      deferred with reason.

### Cancellation
- [ ] **B.9** Cancel task → ffmpeg (if running) killed within 5 s. No
      partial output left in place (verify no `.mediadash-tmp` files).

### Concurrency
- [ ] **B.10** ScanTask + FixTask cannot run simultaneously (starting one
      queues the other).

---

## 05-C · IdleCheck  (relies on `IdleCheckTests`)

Pauses fix run when a user is actively playing media.

### Positive
- [ ] **C.1** Start a fix run. In a Jellyfin Web client, start playing a
      video. Within `IdleCheckPollSeconds`, fix run pauses (log:
      `IdleCheck: paused, active session`).
- [ ] **C.2** Stop playback. Fix run resumes within one poll interval.

### Ignore-activity override
- [ ] **C.3** `POST /Fix/IgnoreActivity` before fix. During run, start
      playback. Fix run does NOT pause.

### Multi-user
- [ ] **C.4** Two users playing simultaneously — same behaviour (single
      pause).

### Idle detection edge
- [ ] **C.5** User paused (playback state = paused for > 60 s) counts as
      idle if config `TreatPausedAsIdle = true`; otherwise still active.

---

## 05-D · ScheduleMigrator

Runs once at plugin startup to migrate legacy schedule configs.

### Positive
- [ ] **D.1** Rewind config to pre-migration state (edit
      `PluginConfiguration.xml` in Jellyfin data dir to a legacy shape:
      remove new cron fields, keep old interval-based fields).
- [ ] **D.2** Restart Jellyfin. Log shows `ScheduleMigrator: migrated`.
- [ ] **D.3** After restart, config has new cron fields populated.
- [ ] **D.4** Task schedules in `/ScheduledTasks` reflect the new cron.

### Idempotence
- [ ] **D.5** Second restart → `ScheduleMigrator: already migrated`, no
      changes.

### Failure
- [ ] **D.6** Malformed legacy XML → migrator logs error, falls back to
      defaults, plugin still loads.

---

## End-of-chapter cleanup

- [ ] **Z.1** Cancel any running task.
- [ ] **Z.2** `Reset`.
- [ ] **Z.3** Re-enable dry-run globally.
- [ ] **Z.4** Update INDEX progress.
