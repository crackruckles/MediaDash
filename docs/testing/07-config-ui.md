# 07 · Configuration UI (`configPage.html`)

The single embedded Jellyfin config page has seven tabs. Each is tested as
a standalone component: layout, controls, backend calls, error states,
i18n reactivity.

Also covers `PluginConfiguration` XML (settings persistence) and every
enum in `Configuration/`: `DisposalMethod`, `EncodePreset`, `FixMode`,
`MediaSortSource`.

Return to [INDEX](INDEX.md).

---

## Session prep

- [ ] **P.1** Open Jellyfin dashboard → Plugins → MediaDash. Confirm all
      7 tabs visible: **Overview · Issues · Files · Recycle · History ·
      Errors · Settings**.
- [ ] **P.2** Browser DevTools open (F12) — Network + Console tabs
      pinned. Every UI test asserts network calls too.
- [ ] **P.3** Screenshot the initial state of each tab (dated
      `<tab>-YYYY-MM-DD.png`) — repeat after any settings-persistence test
      to prove idempotence.
- [ ] **P.4** Confirm zero console errors on first load
      (`console.error` count = 0).

---

## 07-A · Overview tab (`data-tab="overview"`)

Landing tab. Summary counts, quick actions, health banners.

### Load
- [ ] **A.1** Tab shows counts per issue type. Match against
      `GET /MediaDash/Issues` type breakdown.
- [ ] **A.2** "Scan now" button (`#mdScanNow`) present.
- [ ] **A.3** "Fix now" button (`#mdFixNow`) present.
- [ ] **A.4** With active scan, button label swaps to "Cancel".
- [ ] **A.5** Environment card shows OS, ffmpeg version, plugin version
      matching `/Environment`.

### Actions
- [ ] **A.6** Click Scan now → `POST /MediaDash/Scan`, spinner shows, log
      updates.
- [ ] **A.7** Click again during scan → `POST /MediaDash/Scan/Cancel`.
- [ ] **A.8** Click Fix now (`#mdFixNow`) → `POST /MediaDash/Fix`.
- [ ] **A.9** Fix now while running → cancel confirmation dialog before
      calling `POST /Fix/Cancel`.

### Banners
- [ ] **A.10** With dry-run ON, "Go to dry-run settings" banner
      (`#mdGoDryRun`) visible; click → jumps to Settings tab.
- [ ] **A.11** With Post-Upgrade cleanup offered:
      - Run button (`#mdPostUpgradeRun`) triggers
        `POST /PostUpgradeCleanup/Run`.
      - Dismiss button (`#mdPostUpgradeDismiss`) triggers
        `?dismissOnly=true`.
      - After ack/run, banner disappears and does not return on refresh.
- [ ] **A.12** With items in bin, "Go to bin review" banner
      (`#mdGoBinReview`) visible; click jumps to Recycle tab.
- [ ] **A.13** With `Fix/IgnoreActivity` toggle, button
      (`#mdFixIgnoreActivity`) is enabled; click sets the ignore flag for
      current fix run only.

### Empty / error states
- [ ] **A.14** Fresh install, zero data — Overview shows "No issues
      found" copy, no counts.
- [ ] **A.15** Kill backend mid-load → tab shows friendly error, no
      infinite spinner.

---

## 07-B · Issues tab (`data-tab="issues"`)

List, filter, and act on individual issues.

### Load
- [ ] **B.1** Table renders with columns: Path, Type, Library, Detected,
      Status, Actions.
- [ ] **B.2** Type filter dropdown lists all 18 issue types.
- [ ] **B.3** Status filter includes Open/Approved/Dismissed/Fixed/
      Reverted.
- [ ] **B.4** Pagination visible when > page size.

### Actions per row
- [ ] **B.5** Approve button → POST Approve, row status updates
      in place.
- [ ] **B.6** Dismiss → POST Dismiss.
- [ ] **B.7** Revert (on Fixed rows) → POST Revert; row status updates.
- [ ] **B.8** Path is clickable → opens Files tab pre-navigated to that
      folder.

### Bulk
- [ ] **B.9** "Approve all of type X" button → POST ApproveAll?type=X.
- [ ] **B.10** Multi-select checkboxes → "Approve selected" calls
      `POST /Issues/Bulk` with `action=Approve`.
- [ ] **B.11** Bulk over 1000 rows still responsive (progress toast).

### Empty / error
- [ ] **B.12** Zero issues → "Everything looks clean" empty state.
- [ ] **B.13** Filter matches nothing → distinct empty state ("No results
      for this filter").

---

## 07-C · Files tab (`data-tab="files"`)

File browser calling `FileBrowserController`.

### Load
- [ ] **C.1** Root shows drive list.
- [ ] **C.2** Breadcrumb shows current path; each segment clickable.
- [ ] **C.3** File rows show size, modified date, type icon.

### Navigation
- [ ] **C.4** Click folder → GET `/Files/List?path=...`.
- [ ] **C.5** Back button navigates up.
- [ ] **C.6** Attempt to navigate outside allowed roots → tab shows
      "access denied" message.

### Operations (each with confirmation)
- [ ] **C.7** Mkdir → prompt for name → POST Mkdir → row appears.
- [ ] **C.8** Rename → inline edit → POST Rename.
- [ ] **C.9** Delete → confirmation ("This will move X items to bin.
      Continue?") → POST Delete with `moveToBin=true`.
- [ ] **C.10** Delete with Shift held → confirmation warns permanent
      delete → POST Delete with `moveToBin=false`.
- [ ] **C.11** Copy / Move → target picker dialog → POST Copy/Move.
- [ ] **C.12** Upload — drag-drop and file picker both work; progress
      bar; success toast.
- [ ] **C.13** Download — right-click / action menu → GET Download.

### Safety UI copy
- [ ] **C.14** Every destructive button spells the consequence (per
      CLAUDE.md UI standard). E.g. "Delete permanently" not just
      "Delete".

---

## 07-D · Recycle tab (`data-tab="recycle"`)

Recycle bin browser + consolidate + adopt.

### Load
- [ ] **D.1** Header shows totals from `GET /RecycleBin`.
- [ ] **D.2** Table lists items with columns: original path, size,
      reason, binned date.
- [ ] **D.3** Disk info block shows free/total/bin bytes.

### Actions
- [ ] **D.4** Restore selected → POST RecycleBin/Items/Restore.
- [ ] **D.5** Restore all → confirmation → POST for each.
- [ ] **D.6** Empty bin → confirmation → POST Empty; totals update to 0.
- [ ] **D.7** Consolidate — with `GET /RecycleBin/OtherBins` returning
      other roots, "Consolidate" button visible; click → dialog listing
      other roots; confirm → POST Consolidate.
- [ ] **D.8** Adopt orphaned bin — via a "manage bins" section; POST
      AdoptBatch.

### Suggested cap
- [ ] **D.9** With low free space, banner recommends a pause cap in GB
      (from `RecycleBinDiskInfo.suggestedCapGb`).

### Safety UI copy
- [ ] **D.10** Empty bin button copy: "Permanently delete N items. This
      cannot be undone." (or similar).

---

## 07-E · History tab (`data-tab="history"`)

Fix history + monthly stats.

### Load
- [ ] **E.1** Chart shows monthly bytes-freed (from `/History/Stats`).
- [ ] **E.2** Table lists rows newest-first.
- [ ] **E.3** Filter by issue type, result (Success/Failure/DryRun),
      library.

### Actions
- [ ] **E.4** Restore from row → POST History/{id}/Restore.
- [ ] **E.5** Clear history → confirmation → POST History/Clear (verify
      bin not affected).
- [ ] **E.6** Redownload warning row: click Acknowledge → POST
      RedownloadWarnings/{historyId}/Acknowledge; click "Restore
      optimized" → RestoreOptimized endpoint.

### Empty
- [ ] **E.7** No history yet → "Nothing has been fixed yet" empty state.

---

## 07-F · Errors tab (`data-tab="errors"`)

Diagnostics from `/Errors`.

### Load
- [ ] **F.1** List of recent plugin errors with timestamps, messages,
      optional stack.
- [ ] **F.2** Count badge in tab title matches `/Errors/Count`.

### Full-detail toggle
- [ ] **F.3** "Show full" toggle switches `?full=true` → stack traces
      visible.

### Actions
- [ ] **F.4** Clear button → POST Errors/Clear; list empties.
- [ ] **F.5** Copy-to-clipboard on a row → puts JSON into clipboard
      (verify with paste in DevTools).

### Empty
- [ ] **F.6** Zero errors → "All quiet on the plugin front" empty state.

---

## 07-G · Settings tab (`data-tab="settings"`)

Everything writing to `PluginConfiguration.xml`.

### Structural
- [ ] **G.1** Sections visible: Libraries · Safety · Schedule · Scan
      preferences · Fixer settings · Recycle bin · Advanced.
- [ ] **G.2** Save button disabled until any field changed.
- [ ] **G.3** Save → POST config → 200; button re-disables.
- [ ] **G.4** Refresh page → all values persist (verify XML file mtime
      updated on save).

### Per-enum coverage
- [ ] **G.5** `DisposalMethod` dropdown per fix type (RecycleBin,
      Permanent) — each option selectable; save; XML has correct value.
- [ ] **G.6** `EncodePreset` dropdown lists every preset from the C# enum;
      changing it in a transcode dry-run reflects in ffmpeg command.
- [ ] **G.7** `FixMode` — Off, Approved, Auto; each mode changes fix run
      behaviour (Auto approves as it fixes — verify with a small run).
- [ ] **G.8** `MediaSortSource` — Folder, Filename, Ffprobe; changing it
      affects `MediaSorterScanner` results (01-F.6).

### First-run 3-question setup
- [ ] **G.9** Fresh install (or `Reset` + delete config) → first opening
      of Settings prompts 3 questions per CLAUDE.md UI standard (wanted
      languages, primary library kinds, dry-run intro).
- [ ] **G.10** Answers persist and pre-populate matching settings.

### Safety copy (CLAUDE.md acceptance criteria)
- [ ] **G.11** Every destructive default toggled off by default (dry-run
      on, permanent disposal off).
- [ ] **G.12** No jargon in primary labels (plain-English test — grep
      settings for words like "regex", "ffprobe", "encoder" in primary
      labels; each must be behind an "Advanced" collapsible).

### Validation
- [ ] **G.13** Invalid cron expression → save blocked, inline error.
- [ ] **G.14** Non-existent path in library allowlist → save blocked with
      "path not found".
- [ ] **G.15** Negative number in quota → clamped to 0.

### Recycle bin config
- [ ] **G.16** Configure per-fix-type disposal; each per-fixer chapter
      test (02-A.11, 02-K.3) reads this correctly.

---

## 07-H · Cross-tab UX

- [ ] **H.1** Tab switching does not re-fetch already-loaded data unless
      "Refresh" clicked.
- [ ] **H.2** Deep-linkable tabs — URL hash `#tab=issues` opens that tab
      directly.
- [ ] **H.3** Keyboard focus preserved on tab change; screen-reader
      announces tab name.
- [ ] **H.4** Reload during scan → progress banner reflects current
      state on load.
- [ ] **H.5** No console errors when navigating through all tabs.
- [ ] **H.6** Layout survives 320px viewport width (mobile).
- [ ] **H.7** Layout survives 4K viewport width.

---

## 07-I · PluginConfiguration.xml (persistence)

- [ ] **I.1** Locate:
      `%LOCALAPPDATA%\jellyfin\plugins\configurations\Jellyfin.Plugin.MediaDash.xml`.
- [ ] **I.2** Every field in `PluginConfiguration.cs` reachable from
      Settings tab.
- [ ] **I.3** Manual edit (with Jellyfin stopped) survives restart.
- [ ] **I.4** Malformed XML → plugin logs error, loads defaults, does not
      crash.
- [ ] **I.5** Old-schema config (from an earlier `_stage_v0X/`) migrated
      cleanly on load (deferred to `ScheduleMigrator` for schedule
      fields — 05-D).

---

## End-of-chapter cleanup

- [ ] **Z.1** Reset settings back to your normal dev config.
- [ ] **Z.2** `Reset` plugin state.
- [ ] **Z.3** Update INDEX progress.
