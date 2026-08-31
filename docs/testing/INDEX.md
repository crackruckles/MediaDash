# MediaDash E2E Testing — Master Index

End-to-end test suite for every component of the MediaDash Jellyfin plugin,
runnable against a localhost Jellyfin instance across multiple sessions.

**Test bed:** `http://localhost:8099` (dev creds `test` / `test`).
**Format:** copy-paste PowerShell / curl blocks + UI verification steps.
**Progress:** tick `- [ ]` → `- [x]` per test; roll up in the session log below.

> Do NOT run these tests against the production Jellyfin at `192.168.1.117`.
> Every fixture, scan, and destructive action stays on `localhost:8099`.

---

## For the tester (read first)

This suite is meant to be executed by a **fresh Claude instance that knows
nothing about this project** beyond:

1. The **goal**: run these E2E checklists against the MediaDash Jellyfin
   plugin and report what breaks.
2. The **environment**: `http://localhost:8099` (dev creds `test`/`test`)
   is the only Jellyfin instance you are permitted to touch. Everything
   you need to bring the test bed up is in
   [`00-setup.md`](00-setup.md).

You do **not** get to read the plugin source. You do **not** get to read
`PLAN.md`, `CLAUDE.md`, or any other design doc. If a checklist step is
ambiguous, run the closest reasonable interpretation and note the
ambiguity in FINDINGS.md — do not guess at author intent from code.

### Where findings go

Every failing check, unexpected behaviour, unclear step, or safety-
invariant violation is appended to a **single file**:
[`FINDINGS.md`](FINDINGS.md) in this same directory.

- One entry per finding. Do not batch, do not summarize, do not fix.
- Each entry is **actionable** — a future session must be able to open
  FINDINGS.md, pick an entry, and know exactly what to reproduce and
  what to change.
- Use the entry template inside FINDINGS.md verbatim (test ID, repro
  steps, expected vs actual, evidence, severity, suggested area).
- If FINDINGS.md does not yet exist, create it using the template block
  documented at the top of this file.
- Do NOT edit plugin source. Do NOT run fixes. Do NOT open PRs. Your
  output is the FINDINGS.md file, plus updated checkbox state in the
  chapter files and this INDEX.

### When to stop

- End of a chapter → update progress table + session log below, commit
  FINDINGS.md.
- Blocker discovered that prevents further tests in the current chapter
  → add to FINDINGS.md, mark the chapter row `Blocked`, move to a
  different chapter.
- Safety-invariant violation **caused by a test you just ran** (any of the
  five listed below) → stop the session immediately, escalate as
  **Severity: critical** in FINDINGS.md, do not continue.

Pre-existing machine state is **not** a safety-invariant violation. This is
a developer's dev box, not a clean-room. If you find dry-run switched off,
fix modes set to something other than detect-only, or fixture files already
consumed by a previous run, that is the box's owner having used the plugin —
record it as **Severity: low, Category: env** and carry on with the
non-destructive tests. Only a violation you can attribute to a test in these
checklists is critical.

---

## Read order (per session)

1. Open [00-setup.md](00-setup.md) once per fresh machine / after wipes.
2. Pick one chapter from the map below.
3. Do the **Session prep** block at the top of that chapter (10 min).
4. Work down its checkboxes. Every test block ends with a **Cleanup** step
   so you can stop after any block and resume next session without residue.
5. When you close the session, add a row to the [Session log](#session-log)
   and update the [Chapter progress](#chapter-progress) table.

---

## GitHub issue workthrough + feature roadmap

- `ISSUES-WORKTHROUGH.md` — bug fixes. Maps every open GitHub issue
  against the audit findings, groups them by theme (bin, fixer,
  scanner, config UI), and gives a suggested PR order.
- `FEATURE-REQUESTS.md` — build roster. Sourced from
  `enhancement`-tagged GitHub issues plus a few audit-derived
  requests. Numbered F-REQ-N with priority + effort estimates.
- `issues/gh-issues-raw.json` — snapshot of every open + closed
  issue at capture time.
- `SINGLE-PASS-ENCODE-DESIGN.md` — design doc for the combined-fix
  encode pipeline (one ffmpeg per file instead of N). Ships as
  **PR-2** in the workthrough; blocks on F-019, F-020/F-032, F-086.
- `FIX-PLAN-POST-REPRO.md` — implementation cut sheet for every
  finding that reproduced live during the F-019-unblocked test
  runs on 2026-08-30. Grouped into PR-A through PR-E with concrete
  file:line targets and regression tests. Supersedes the abstract
  sketches in `ISSUES-WORKTHROUGH.md` for those findings.

---

## Chapter map

| # | File | Scope | Components | Approx tests |
|---|------|-------|------------|--------------|
| 00 | [00-setup.md](00-setup.md) | Jellyfin bring-up, fixtures, seed data, auth token | — | 12 |
| 01 | [01-scanners.md](01-scanners.md) | Every `IScanner` implementation + helpers | 17 scanners + 5 helpers | ~140 |
| 02 | [02-fixers.md](02-fixers.md) | Every `IFixer` + supporting classes | 15 fixers + 8 helpers | ~135 |
| 03 | [03-api.md](03-api.md) | `MediaDashController` + `FileBrowserController` + every DTO round-trip | 2 controllers, 43 endpoints, 34 DTOs | ~160 |
| 04 | [04-probing.md](04-probing.md) | ffprobe, book, comic, SMART, hasher | 7 services + 6 results | ~55 |
| 05 | [05-scheduled-tasks.md](05-scheduled-tasks.md) | `ScanTask`, `FixTask`, `IdleCheck`, `ScheduleMigrator` | 4 tasks | ~40 |
| 06 | [06-data.md](06-data.md) | `MediaDashDb` + 6 entities + migrations | 7 units | ~45 |
| 07 | [07-config-ui.md](07-config-ui.md) | 7 dashboard tabs + settings surfaces + `PluginConfiguration` | 8 tabs/panels | ~90 |
| 08 | [08-i18n.md](08-i18n.md) | `I18nCatalog` + 9 locale bundles | 10 units | ~35 |
| 09 | [09-analytics-compat.md](09-analytics-compat.md) | `AnalyticsReporter`, `SkiaSharpBridge` | 3 units | ~25 |

Grand total: ~740 individually-tickable assertions.

---

## Chapter progress

Update the two right columns as you go. `Blocked` = a test failed and is
being tracked in the [Blockers](#blockers) list below rather than skipped.

| Chapter | Total | Passed | Failed | Blocked | % done |
|---------|-------|--------|--------|---------|--------|
| 00-setup | 29 | 26 | 2 | 1 (skip) | 100% run |
| 01-scanners | 140 | ~65 | ~50 | ~30 (skip/block) | 100% run |
| 02-fixers | 135 | 0 | 0 | 0 | 0% |
| 03-api | 127 | ~68 | ~40 | ~19 | 100% run |
| 04-probing | 55 | ~20 | ~15 | ~20 (skip) | 100% run |
| 05-scheduled-tasks | 40 | 12 | 1 | 15 (skip) | ~70% run |
| 06-data | 45 | ~22 | ~5 | ~8 (skip) | ~78% run |
| 07-config-ui | 90 | 15 | 3 | 26 (skip) | ~49% run |
| 08-i18n | 35 | ~30 | 2 | 5 (skip) | 100% run |
| 09-analytics-compat | 25 | 3 | 2 | 22 (skip) | 100% run |
| 02-fixers | 135 | 8 | 2 | 46 (skip) | ~42% run |
| **Total** | **754** | **~211** | **~93** | **~183 skip/block** | **~65% run** |

---

## Session log

One row per session. Keep notes terse; long analysis goes in the failing
test's checkbox or the [Blockers](#blockers) list.

| Date | Duration | Chapter(s) | Tests passed | Tests failed | Notes |
|------|----------|------------|--------------|--------------|-------|
| 2026-08-27 | ~45m | 00-setup | 2 | 8 (2 skipped) | Pilot run of an unmodified draft. Stopped at §5 per the (then over-broad) stop rule. F-001/F-002 triaged as **not bugs** — see T-001/T-002 in FINDINGS.md. F-003..F-008 were real doc bugs in this suite, all fixed. 01-scanners not started. |
| 2026-08-28 | — | — | — | — | Triage + doc-correction pass (no tests run). `00-setup.md` rewritten against the real machine; INDEX conventions, stop rule and invariant 1 corrected; `01-scanners.md` fixture/auth paths corrected. Checkbox state reset to unrun — the pilot's results were against a draft with wrong paths and do not carry over. |
| 2026-08-28 | ~55m | 01-A (Artwork), 01-G (MissingSubs), 01-J (Playability) + prep | 17 | 8 (+8 skip/block) | Second flip of `DryRun=true` for the §G / §J passes, restored at end. §A: F-014 / F-015 (item-cache fixture design). §G: 6 file paths but 7 issues — Big Buck 1080p emitted twice → F-018 (scanner emits per-item, not per-path; Jellyfin has two items on the same file). §J: Truncated Movie flagged as expected (§J.4 partial pass, 1 vs expected 2); §J.2 garbage-payload fixture never entered item cache (F-015 pattern extends here); §J.5 field name mismatch → F-017. Filed F-013 / F-016 (maintainer notes on fixer perf + date-preservation). Library snapshot still matches pre-chapter. |
| 2026-08-28 | ~4h30m | 01-C, 01-D, 01-E, 01-F, 01-I, 01-K, 01-L, 01-M, 01-N, 01-P, 01-Q, 01-R (12 blocks via subagents, one per scanner) | ~35 | ~36 | Ran fresh subagents per remaining scanner, each with its own DryRun flip + restore. Filed **F-021 through F-038** — 18 new findings this batch. Highlights: **F-019 recurs** for every new-item fixture (Loose, S01E01, Inception, FixtureAlbum, ExtCoverage) — SQLite FK bursts in `FolderMetadataService`/`MetadataService`; **F-022, F-023, F-025, F-029** are all scanner-correctness bugs (SubtitleFonts returns 0 even against an indexed stream; FailedTranscode never emitted; Trickplay 5-item sample kills the whole walk; Duplicate misses two items sharing a path); **F-030, F-031, F-033, F-036** are correctness bugs where scanner detection contradicts the doc's "expected" semantics; **F-020, F-026, F-032, F-035, F-037** are docs-drift on `DetailsJson` shapes and config field names; **F-038** — Issue.Id is not stable across rescan, breaking UI/doc assumptions. Library snapshot matches pre-chapter (16 files intact); TRUE-original dev-box config restored from `%TEMP%\mediadash-config-original.json` (DryRun=False, TranscodeFixMode=Automatic, PlayabilityFixMode=Automatic, StaleFixMode=DetectOnly). |
| 2026-08-28 | ~30m | 01-B (AudioLang), 01-H (Nfo), 01-O (SuspiciousFile) | 13 | 4 (+1 skip) | Third DryRun-on flip, restored at end. §H NfoScanner works filesystem-based — 3 corrupt files detected (empty, wrong root, malformed XML) with clear `DetailsJson.reason` populated; H.4 healthy control not flagged. §O MalwareRisk works filesystem-based — `hello.exe` flagged, `readme.txt` control not; DetailsJson is `{"extension":".exe"}`. §B AudioLanguage blocked by F-019 — my JpnOnly/Untagged fixtures never became Jellyfin items because Jellyfin core threw a SQL exception in `MetadataService` during library validation; recorded so a follow-up session can capture the exact column error. Also filed F-020 for the H.6/J.5/O.5 field-name drift. Library snapshot still matches pre-chapter (16 files). |
| 2026-08-28 | ~40m | 06-data (P/A.2/A.8/B/C/D/E/F.2/G/Z) | ~22 | 5 (+8 skip) | Read-only DB inspection via `node --experimental-sqlite`. Backup at `$env:TEMP\mediadash-e2e-backup.db` (MD5 968282610C6C2002A33A389BE7763DA0). Filed F-067 (schema drift — no `MonthAggregates`, snake_case tables, 5 extra tables), F-068 (issues+history column names differ from doc AND API DTOs — three schemes), F-069 (`bytes_freed` NOT NULL 0-sentinel vs doc "nullable"), F-070 **high** (rescan duplicates Open issue for same type+path: id 7927 + 7937 both Ungrouped Big Buck Test), F-071 (probe cache row untouched on rescan, doc claims LastProbedUtc updates), F-072 (7 of 18 enum values uncovered on this box + type=15 unnameable via API). B.1 DB-vs-API count matched (10=10). Status enum ints observed: {0,1,2,3}=Open,Approved,Fixed,Dismissed — no Reverted. Skipped as instructed: A.1, A.3-A.7, B.7, E.3, F.1. DryRun restored to False. |
| 2026-08-28 | ~40m | 00-setup | 26 | 2 (+1 skip) | Second QA run against the corrected doc. Everything works up through §6 except: F-009 (`Md` helper collides with `md` alias for `mkdir`, so every downstream chapter's `Md <route>` call silently mkdirs instead of hitting the API — high), F-010 (installed plugin's `meta.json` reports `0.0.0.0` — medium), F-012 (`Environment.ffmpegPath` doesn't exist so §2.6 as written can't discover ffmpeg — medium), F-011 (dev box `DryRun=false` and 6 modes are `Automatic` — informational only, per §6.2). §2.6 fixture regen executed (Big Buck 1080p + Truncated Movie were missing; both restored via `make-fixtures.sh` into a scratch dir, then copied over). Library snapshot at `%TEMP%\lib-before.csv` (16 files). Chapter 01 not started — F-009 blocks it as written; workaround (`Remove-Item Alias:md` before defining the helper) documented inline at §3.4. |

---

## Blockers

Failing tests waiting on a fix. Move to "Resolved" when the fix lands and
the test re-passes.

### Open

- **F-020** (low, docs) — Issue `DetailsJson` fields drift from the doc
  in §H/§J/§O. Actual shape uses `reason`/`detail`/`extension`, not
  `metadata.parseError`/`metadata.ffprobeExitCode`/`metadata.reason`.
- **F-019** (high, env) — Jellyfin core on this dev-box fails to index
  freshly-seeded fixture folders (SQL exception in `MetadataService`).
  Blocks every §B/§D/§J/§K/§L/§N block that needs a new Movie item.
  Filesystem-based scanners (§H NfoScanner, §O MalwareRisk) still work.
- **F-038** (medium, correctness+docs) — `Issue.Id` isn't stable across
  rescans. A control run of two back-to-back scans (no library change)
  churned 9 of 10 IDs. Any UI or docs that assume "issue id → same issue
  next scan" is wrong.
- **F-037** (low, docs) — `StaleContentScanner` has no
  `ExcludeFavourites` field; favouriting an item doesn't remove it from
  Stale detection. Doc §L.8 references a feature that doesn't exist.
- **F-036** (medium, correctness+docs) — StaleContentScanner keys off
  Jellyfin's `DateCreated`, NOT filesystem mtime. Doc §L.2's mtime-hack
  fixture never trips detection. Rewrite to use `POST /Items/{id}` with
  a synthetic `DateCreated`, or clear the item to force re-import with
  a backdated FS stamp.
- **F-035** (high, env + docs) — EmbeddedCoverArtScanner: F-019 also
  bites the music library, AND the scanner detects "duplicated art"
  (per-file + folder cover both present), not "missing folder cover"
  as the doc §D.4/D.5 rules imply. Positive/negative fixtures are
  inverted.
- **F-034** (medium, env) — `make-fixtures.sh` produces a `Sub Heavy
  (2023).mkv` with only 1 eng subtitle track; the recipe's claim of
  eng+fra+deu is inaccurate. Every §N test that relied on `rus`/`fra`
  needs an in-place remux first.
- **F-033** (medium, correctness+docs) — SubtitleLanguageScanner has no
  "disable detection" toggle. `SubtitleFixMode=DetectOnly` still emits
  issues; the only way to stop detection is widening
  `AllowedSubtitleLanguages`. Doc §N.6 assumes a global on/off knob.
- **F-032** (low, docs) — SubtitleLanguage `DetailsJson` shape is
  `{removeIndexes[], externalFiles[], languages[]}` — no `unwantedTracks`
  field. Doc §N.5 wrong.
- **F-031** (medium, correctness) — QualityScanner emits
  `DetailsJson.videoBitrate` = 0 whenever Jellyfin `MediaStream.BitRate`
  is empty (which is often on Jellyfin-managed items). No ffprobe
  fallback. Breaks the Quality flag reason surface.
- **F-030** (medium, correctness) — Quality ceiling `= 0` (natural "off"
  sentinel) flags every file instead of disabling detection. Only very
  large ceilings (100 000) actually disable.
- **F-029** (high, correctness) — DuplicateScanner does not flag two
  Movie items that share a media path (Big Buck 1080p case). Should be
  its most obvious detection signal.
- **F-028** (medium, correctness+docs) — Config API accepts an
  out-of-range numeric `MediaSortSource` (e.g. 4) and persists it as a
  raw int, breaking the enum-string GET contract. Also: doc's source
  names `Folder`/`Filename`/`Ffprobe` are wrong — actual enum is
  `JellyfinMetadata` / `FilenameHeuristic`.
- **F-027** (high, env) — F-019 recurrence in the movies library with
  new SQLite `FOREIGN KEY constraint failed` bursts, blocking
  MediaSorterScanner's positive-path fixture.
- **F-026** (medium, docs+correctness) — MediaGrouperScanner's
  `DetailsJson` shape is `{action, source, target, title, franchise}` —
  no `suggestedFolder` field. F-019 also blocks the Loose (2019.mkv)
  positive path.
- **F-025** (high, correctness) — TrickplayOptimizeScanner's 5-item
  sample heuristic short-circuits the whole library walk when the
  sample contains no legacy sidecars. Four freshly-seeded fixture
  folders with `trickplay\<w>\<w>.jpg` sprites went undetected.
- **F-024** (medium, correctness) — TranscodeLogScanner silently drops
  logs written with UTF-8 BOM. Real Jellyfin logs are BOM-less so this
  only bites automation, but the scanner should either read past the
  BOM or warn.
- **F-023** (high, correctness) — `IssueType.FailedTranscode` never
  emitted. Failed transcode entries fold into `HeavyTranscode` rows
  with `DetailsJson.failures`, and the scanner's own summary line
  says `X heavy, Y failed` while the persisted rows use the opposite
  labels. `FailedTranscodeFixMode` / `FailedTranscodeDisposal` config
  surfaces can never fire.
- **F-022** (high, correctness) — SubtitleFontScanner returns 0 even
  when Jellyfin has an `.ass` file indexed as an external MediaStream
  on a real Movie item. Filesystem-based hypothesis contradicted.
- **F-021** (medium, correctness) — OrphanCleanupScanner double-emits
  on the Ghost fixture: an `OrphanSubtitle` on the `.srt` AND an
  `EmptyFolder` on the parent, at the same instant. If
  `OrphanedDebrisFixMode` moves off `DetectOnly`, the queue ordering
  matters.
- **F-018** (medium, correctness) — `MissingSubtitleScanner` emits the
  same file twice when Jellyfin holds two items with the same path
  (Big Buck 1080p). Every item-iterating scanner is probably in the
  same shape. Dedupe by path.
- **F-017** (low, docs) — Playability issue `DetailsJson` has
  `Reason`/`Detail`, not `ffprobeExitCode`. Update §J.5.
- **F-016** (medium, correctness) — Fixer rewrites bump the output
  file's `CreationTime`/`LastWriteTime` to now, so Jellyfin's
  "Date added"/"Date modified" sort promotes fixed items to the top
  of "Recently added" incorrectly. Preserve source timestamps on
  finalise. Maintainer-filed; chapter-02 will collect a live repro.
- **F-013** (medium, performance) — Fixer runs one ffmpeg call per queued
  fix category on the same file (transcode + audio-lang + sub-lang +
  downscale = 3–4 passes). Should combine into one ffmpeg invocation.
  Reported by maintainer; a chapter-02 test will collect a live repro.
- **F-014 / F-015** (high, docs) — Chapter 01's fixture design does not
  create Jellyfin items in several blocks (§A CorruptArtwork and §C
  Duplicate confirmed). Scanners iterate the item cache, not the
  filesystem, so planted fixtures with no `.mkv` beside them are
  invisible. Fix by co-locating with a real media file (§A) or using
  separate folders (§C). Blocks §A.7–A.13 until rewritten; §C is
  probably in the same state, needs re-verification.
- **F-009** (high, docs) — `00-setup.md` §3.4 helper `function Md` is
  shadowed by the built-in `md` alias in Windows PowerShell 5.1. Every
  downstream chapter that uses `Md <route> [<method>] [<body>]` silently
  invokes `mkdir` and never hits the API. Inline workaround pinned in
  §3.4 (`Remove-Item Alias:md` before the function definition).
- **F-010** (medium, env) — Installed plugin's `meta.json` version is
  `0.0.0.0`, so Jellyfin logs `Loaded plugin: MediaDash 0.0.0.0`. Every
  future finding filed against this build has no version anchor.
- **F-012** (medium, docs) — `00-setup.md` §2.6 reads `Environment.ffmpegPath`
  but that field is not in the `/MediaDash/Environment` response; fixture
  regen silently falls through to `ffmpeg` on PATH (which doesn't exist
  on this machine). Workaround: point at
  `%USERPROFILE%\Downloads\jellyfin_10.11.11-amd64\jellyfin\ffmpeg.exe`
  directly.
- **F-011** (low, env) — Dev-box safety posture: `DryRun=false`, six
  `*FixMode`s are `Automatic`. Recorded per §6.2, informational only.
- **F-005** (medium, env) — No `shows` library is registered on this box
  (registered: Movies, Audiobooks, Comics). Every 01-scanners step that
  seeds `$env:LIB\shows\...` is marked `[-]` until someone adds a Shows
  library. Not a plugin bug.

### Resolved

- **F-001** — Not a bug. Shipped defaults are `DryRun = true` and every
  `*FixMode = DetectOnly`. The dev box had them changed by its owner.
  See T-001.
- **F-002** — Not a bug. The default bin is `<DataPath>\mediadash\recycle`
  by design; it is not required to live inside the library. The suite's
  assertion was wrong. See T-002.
- **F-003** — Fixed. `$env:LIB` is `C:\dev\mediadash-fixtures`; the
  `mediadash-testlib` path was never real.
- **F-004** — Fixed. There is no `artifacts\fixtures\` store; fixtures are
  generated by `tools/make-fixtures.sh`. Documented in `00-setup.md` §2.5.
- **F-006** — Fixed. Log glob is `log_*.log`; `Get-JfLog` helper added.
- **F-007** — Fixed. Data dir is version-suffixed and is now discovered
  into `$env:JFDATA` rather than assumed.
- **F-008** — Fixed. §3 now uses `Invoke-RestMethod` + `ConvertTo-Json` and
  builds a full `Authorization` header into `$env:JFAUTH`.

---

## Safety invariants — must hold across the entire suite

Cross-referenced from `CLAUDE.md`. Every destructive test verifies these:

1. Never modify or delete a file outside configured library paths. (The
   recycle bin is the destination of a move, not a modification — it may
   legitimately sit outside the library. What matters is that the *source*
   of every move is inside a configured library path.)
2. Never remove a file's last audio track or video stream.
3. Never replace an original until ffprobe confirms duration (±2s) and
   expected streams.
4. All destructive ops respect per-fix-type disposal (bin vs permanent) and
   the global dry-run toggle. Dry-run defaults **ON**.
5. Free disk space ≥ 2× source size before any transcode.

Any test that appears to bypass one of these is itself the bug — file it as
a blocker and stop the session.

---

## Conventions used in every chapter

All of these are set up by `00-setup.md`. Run it first; the env vars persist
for the shell session only.

- **`$env:JF`** — base URL. Default `http://localhost:8099`.
- **`$env:TOKEN`** — Jellyfin access token from `00-setup.md` §3. Refresh
  once per session.
- **`$env:JFAUTH`** — the **full** `Authorization` header value. Jellyfin
  10.11+ rejects a bare `X-Emby-Token`; every authenticated call must send
  `-H "Authorization: $env:JFAUTH"`. Use the `Md` helper from `00-setup.md`
  §3 rather than hand-rolling curl.
- **`$env:LIB`** — test library root on disk: `C:\dev\mediadash-fixtures`
  (see `00-setup.md` §2).
- **`$env:JFDATA`** — Jellyfin data dir; version-suffixed, discover it, do
  not assume (see `00-setup.md` §1).
- **`$env:BIN`** — plugin recycle bin. Defaults to
  `$env:JFDATA\mediadash\recycle` — this is **by design**, it is not
  required to live inside `$env:LIB` (see `00-setup.md` §5).
- **Curl on Windows**: use `curl.exe` (not the PS alias) or the PS blocks
  provided.
- **Fixtures are generated**, not stored: `tools/make-fixtures.sh` writes
  them to a scratch dir, and you copy what a test needs into `$env:LIB`.
  See `00-setup.md` §2.5.
- A test that "confirms the log line" reads `$env:JFDATA\log\log_*.log`
  (dated files, not `jellyfin.log`). Use the `Get-JfLog` helper.
- **`$env:LIB` contains at least one large real media file** that is not a
  fixture. Never run a bulk delete inside `$env:LIB`. See `00-setup.md` §0.

---

## Legend

- `[ ]` = not run
- `[x]` = passed on this build
- `[!]` = failed — see linked blocker row
- `[-]` = intentionally skipped (feature disabled in this build) — annotate why
