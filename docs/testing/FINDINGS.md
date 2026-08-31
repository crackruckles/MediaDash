# MediaDash E2E — Findings

Single append-only log of every failure, unexpected behaviour, unclear
step, or safety-invariant violation observed while running the checklists
in this directory.

**Rules for this file:**

- **Append only.** Do not rewrite existing entries. If a finding is
  superseded, add a new entry with `Supersedes: F-###` in the meta block.
- **One entry per finding.** Do not batch, summarize, or aggregate.
- **Actionable.** A future session opens this file, picks an entry, and
  knows exactly what to reproduce and where the fix probably lives.
- **No fixes here.** This file describes problems, not solutions.
- **Newest at top** so the next session sees fresh failures first.

Entries use the template below. Copy it in full, fill every field. If a
field genuinely does not apply, write `n/a` — do not delete it.

---

## Entry template (copy this)

```
### F-### · <one-line title>

- **Test ID**: <e.g. 01-A.7, 03-F.19 — reference the checkbox in the
  chapter file>
- **Chapter file**: <e.g. 01-scanners.md>
- **Component**: <the file/class under test, e.g. ArtworkScanner>
- **Severity**: critical | high | medium | low
- **Category**: correctness | safety-invariant | ui | performance |
  docs | ambiguity | env
- **Observed**: <YYYY-MM-DD>
- **Session**: <matches the Session log row in INDEX.md>

**Repro**

1. <exact steps from a clean state — reference session-prep in the
   chapter file, then list additional steps>
2. ...

**Expected**

<what the checklist step says should happen>

**Actual**

<what happened instead — include exit codes, HTTP statuses, log excerpts>

**Evidence**

<paths to captured artifacts under `docs/testing/evidence/F-###/`
(screenshots, response JSON, log tails). If none, write "text only"
and paste the minimal excerpt inline in a code block.>

**Suggested area (best guess, not required)**

<one line — which file/class most likely holds the bug, or "unknown">

**Ambiguity flag**

<if the checklist step was unclear, describe the ambiguity and the
interpretation you ran. Otherwise "n/a".>
```

---

## Severity guide

- **critical** — safety invariant violated (file outside `$LIB` touched,
  last audio/video track removed, unverified transcode replaced original,
  dry-run bypassed, insufficient-disk transcode proceeded). **Stop the
  session** and escalate. See [INDEX](INDEX.md) safety block.
- **high** — feature does not work as documented, data loss risk, security
  regression, crash.
- **medium** — visible bug with a workaround, UX-standard violation from
  CLAUDE.md, i18n gap.
- **low** — cosmetic, wording, docs, non-blocking.

---

## Category guide

- **correctness** — code doesn't do what the checklist expects.
- **safety-invariant** — one of the five hard rules violated.
- **ui** — visual/interaction issue on `configPage.html`.
- **performance** — slow, memory-heavy, blocks the UI.
- **docs** — checklist wrong, missing, or contradicts observed behaviour.
- **ambiguity** — checklist step allows multiple defensible interpretations.
- **env** — test-bed setup problem (Jellyfin config, ffmpeg, permissions).

---

## Evidence directory

Attach artifacts under `docs/testing/evidence/F-###/`. Naming:

- `screenshot-<n>.png` — UI captures
- `response-<n>.json` — API response bodies
- `log-tail.txt` — relevant jellyfin log lines around the failure
- `repro.ps1` — minimal PowerShell repro if you built one

Keep files small; if a log exceeds ~1 MB, extract the relevant window.

---

## Numbering

Findings are `F-001`, `F-002`, ... in order of discovery. Never reuse an
ID. When linking between entries, use full IDs (`F-042`).

---

## Triage log

Maintainer verdicts on findings above. A finding is never edited or deleted
once filed — the verdict goes here and the INDEX blocker list is updated to
match. `T-###` entries are written by a session that **is** allowed to read
plugin source; the tester never writes here.

### T-003 · F-003, F-004, F-006, F-007, F-008 confirmed — bugs in this test suite, now fixed

- **Verdict**: valid findings. All five were fabrications in the checklists,
  not plugin defects. The suite's author asserted paths and header formats
  without verifying them against the machine.
- **Triaged**: 2026-08-28
- **Fixes landed**: `00-setup.md` rewritten (version-suffixed data-dir
  discovery into `$env:JFDATA`; `$env:LIB = C:\dev\mediadash-fixtures`;
  `log_*.log` glob + `Get-JfLog`/`Find-JfLog`; `Invoke-RestMethod`-based
  auth building a full `Authorization` header into `$env:JFAUTH` + an `Md`
  helper). `INDEX.md` conventions block corrected. `01-scanners.md` fixture
  paths remapped onto `tools/make-fixtures.sh` plus inline ffmpeg recipes,
  and all curl calls switched to `$env:JFAUTH`.
- **Note**: there is no `artifacts\fixtures\` store and never was. Fixtures
  are generated; `tools/make-fixtures.sh <out-dir>` produces the six movie
  payloads. Anything it does not cover is derived inline — see the *Fixture
  sources* section at the top of `01-scanners.md`.

### T-002 · F-002 is NOT a bug — the default recycle bin location is by design

- **Verdict**: **not a bug.** The suite's assertion was wrong.
- **Triaged**: 2026-08-28
- **Ground truth**: `Jellyfin.Plugin.MediaDash/Fixers/RecycleBin.cs:49`
  ```csharp
  _defaultRoot = Path.Combine(applicationPaths.DataPath, "mediadash", "recycle");
  ```
  The same file rejects OS-reserved roots (lines 27-28, 66-75) and performs
  a cross-volume free-space check before moving (163-181). Its own comments
  note that a bin placed *next to the library* is merely an optimisation —
  moves become renames instead of cross-volume copies — not a requirement.
- **Why the tester was right to flag it**: `00-setup.md` §5.2 said "confirm
  bin is a subdirectory of `$LIB`". That was invented. Given that
  instruction, filing this was correct behaviour.
- **Fix landed**: `00-setup.md` §5 now treats `<DataPath>\mediadash\recycle`
  as a **pass**; the real invariant is "the bin is not under an OS-reserved
  root, and the *source* of every move is inside a configured library path".
  Safety invariant #1 in `INDEX.md` clarified to say the same.

### T-001 · F-001 is NOT a bug — shipped defaults are safe; the dev box was reconfigured by its owner

- **Verdict**: **not a bug**, and not a safety-invariant violation.
- **Triaged**: 2026-08-28
- **Ground truth**: `Jellyfin.Plugin.MediaDash/Configuration/PluginConfiguration.cs:36-41`
  ```csharp
  DryRun = true;
  DuplicateFixMode = FixMode.DetectOnly;
  TranscodeFixMode = FixMode.DetectOnly;
  SubtitleFixMode  = FixMode.DetectOnly;
  AudioFixMode     = FixMode.DetectOnly;
  PlayabilityFixMode = FixMode.DetectOnly;
  ```
  All ~14 `*FixMode` properties default to `DetectOnly` (`StaleFixMode` to
  `Off`), and every `*Disposal` defaults to `RecycleBin`. Invariant #4
  ("dry-run defaults ON") holds in the shipped code.
- **What actually happened**: this is a developer's box. Its owner turned
  dry-run off and ran real fixes, which is why two generated fixtures
  (`Big Buck Test (2020) - 1080p.mkv`, `Truncated Movie (2021)`) are
  missing from `C:\dev\mediadash-fixtures` and why the log shows completed
  destructive fixes. Pre-existing state, not a defect.
- **Root cause of the false positive**: the stop rule said "safety-invariant
  violation → stop the session immediately", without distinguishing a
  violation *caused by a test* from pre-existing machine state.
- **Fix landed**: `INDEX.md` "When to stop" now scopes the critical-stop
  rule to violations the tester can attribute to a test they ran, and
  classifies pre-existing config/fixture state as `low / env`.

---

## Findings

_(Add new entries here — newest at the top.)_

### F-214 · DuplicateScanner: identical `(TMDB=1091, ProductionYear=1982)` pair not flagged even with `DuplicateMinAgeDays=0` and aged files

- **Test ID**: fix-regression2 Block 2.2
- **Chapter file**: `docs/testing/fix-regression2.md` (this session brief)
- **Component**: DuplicateScanner (candidate emission / rank tiebreak)
- **Severity**: medium
- **Category**: correctness (possible false-negative)
- **Observed**: 2026-08-30
- **Session**: post-F-097/F-098/F-099 regression sweep

**Repro**

1. Fresh datadir. Set `DuplicateMinAgeDays=0`.
2. Seed two Clean-Movie-payload copies in
   `_regress2\The Thing (1982)\The Thing (1982).mkv` and
   `_regress2\The Thing (2011)\The Thing (2011).mkv`.
3. Age both files' LWT/CT to `2020-01-15T00:00:00Z`.
4. Library refresh with `ReplaceAllMetadata=true`. Jellyfin auto-resolves
   BOTH items to `TMDB=1091, ProductionYear=1982, PremiereDate=1982-06-24`.
5. `POST /MediaDash/Reset`, `POST /MediaDash/Scan`, wait for idle.
6. `GET /MediaDash/Issues?type=Duplicate` → **0 issues**.
7. Log line: `MediaDash scanner Duplicate found 0 issues` with **no**
   `MinAgeDays gate` follow-up line (so the gate is not the culprit).

**Expected**

Post-F-097 the ProviderID key includes `ProductionYear`; both items share
`(1091, 1982)` so they should collide into a duplicate candidate group and
one keeper + one recycle-candidate should be emitted.

**Actual**

Zero Duplicate issues. Contrast with `DuplicateMinAgeDays=30` on a
similarly seeded `Same Movie (2020)` pair (Block 3.1): the log DOES report
`1 candidate group(s) were dropped by the MinAgeDays gate (30 days)` — so
`Same Movie (2020)` reached the age gate as a candidate group, while the
`The Thing` pair never appears in any gate/candidate line at all in the
MinAge=0 run. Something upstream of the gate is dropping this pair (the
`Rank()` filter change from F-098, an "additional-parts" merge, or a tie
where both files are equal-rank and neither becomes recycle-candidate).

**Evidence**

```
Items API on both:
  ID=7e5d... Name=The Thing (1982) ProductionYear=1982 TMDB=1091 IMDB=tt0084787
  ID=c2be... Name=The Thing (1982) ProductionYear=1982 TMDB=1091 IMDB=tt0084787
  Paths differ; MediaSourceCount=1 each; DateCreated=2020-01-15 each.

DB (issues table): rows for both paths exist but only as type=6
  (MissingSubtitles) and type=7 (Stale). No type=Duplicate row.
```

**Suggested area (best guess, not required)**

`DuplicateScanner.ScanAsync` candidate grouping + `Rank()`
(DuplicateScanner.cs:571-589 per F-098 change). Verify that when two items
tie on rank, one is still nominated as recycle-candidate. Also verify the
new key path handles the case where Jellyfin resolves distinct filesystem
items to the same TMDB+Year without silently collapsing them into a single
"additional parts" group.

**Ambiguity flag**

Possible non-bug: this may be intentional "identical movie merged as
additional parts" behavior. Maintainer to confirm whether same
TMDB+Year+PremiereDate items are meant to skip Duplicate detection.

### F-099 · DuplicateScanner: `DuplicateMinAgeDays=7` default silently vetoes every dup on a fresh library

- **Test ID**: duplicate-fuzz F.5a
- **Chapter file**: `docs/testing/duplicate-fuzz/matrix.md`
- **Component**: DuplicateScanner (age gate)
- **Severity**: high
- **Category**: correctness
- **Observed**: 2026-08-29
- **Session**: duplicate-fuzz retest 2

**Repro**

1. Fresh Jellyfin datadir (all items created within last 7 days).
2. Seed obvious duplicate pairs into `$env:LIB\movies\_dupfuzz\...`.
3. `POST /Library/Refresh`, wait for Scan Media Library task to reach Idle.
4. `POST /MediaDash/Reset`, `POST /MediaDash/Scan`, poll to `IsScanning=false`.
5. `GET /MediaDash/Issues?type=Duplicate` → 0 issues.
6. Flip `DuplicateMinAgeDays=0`, re-Reset, re-Scan → 5 issues appear immediately.

**Expected**

Fresh library duplicates should be detected — this is when they matter most.
Either default MinAgeDays to 0 or surface the veto in the "0 issues" summary
line so operators know why the scanner found nothing.

**Actual**

`DuplicateScanner` skips items younger than 7 days silently. On a freshly
reset datadir every item is 0 days old, so every duplicate group is vetoed
and the log line `MediaDash scanner Duplicate found 0 issues` gives no hint
that a threshold-based filter fired.

**Evidence**

Config sweep from this session (identical fixture set, only knob varied):

```
F.1 baseline (MinAge=7 default)        dup=0 scan=22.1s
F.5a MinAge=0                          dup=5 scan=19.7s   <-- unlocked
F.5d TreatEditions=true (with MinAge=0) dup=12 scan=19.5s
```

**Suggested area (best guess, not required)**

`DuplicateScanner.ScanAsync` — age filter applied before candidate
generation. Emit a diagnostic line when N candidates are dropped by
MinAgeDays so the "0 issues" summary is not misleading.

**Ambiguity flag**

n/a

### F-098 · DuplicateScanner: symlinked file selected as keeper with `Size=0`, real file marked as the deletion candidate

- **Test ID**: duplicate-fuzz A.7
- **Chapter file**: `docs/testing/duplicate-fuzz/matrix.md`
- **Component**: DuplicateScanner keeper-selection (Heuristic tier)
- **Severity**: high
- **Category**: correctness / safety-invariant risk
- **Observed**: 2026-08-29
- **Session**: duplicate-fuzz retest 2

**Repro**

1. Under `$LIB\movies\_dupfuzz\A7_target\A7 Target Movie (2020)\` place a
   real 2.3 MB `.mkv` copy of `Clean Movie (2024).mkv`.
2. Under `$LIB\movies\_dupfuzz\A7_link\A7 Target Movie (2020)\` create an
   NTFS symbolic link pointing at the real file above.
3. Config: `DuplicateMinAgeDays=0`, `TreatEditionsAsDuplicates=true`,
   others default. Scan.

**Expected**

Either (a) skip the symlink entirely, or (b) if grouped, pick the file with
the real bytes as keeper — never a `Size=0` symlink.

**Actual**

Scanner grouped them with heuristic confidence 0.95 and picked the symlink
side (`A7_link/...`) as keeper with `keeper.Size=0`. The real file is
labelled "Safe to delete — a better copy exists (confidence 0.95)". In
`ManualApprove` DryRun=false mode an operator following the UI hint would
delete the actual media and keep a dangling link.

```json
"keeperPath": "...\\A7_link\\A7 Target Movie (2020)\\A7 Target Movie (2020).mkv",
"keeper":    { "Size": 0, "Resolution": "1280x720", "Codec": "h264" },
"thisCopy":  { "Size": 2372373, ... },
"confidence": 0.95,
"signals": { "hashesMatch": false, "appliedTier": "Heuristic" }
```

Note `hashesMatch:false` — Jellyfin reported the symlink as 0 bytes, so the
Exact tier didn't join them, but the Heuristic tier still fired on title
Jaccard=1 and gave the 0-byte side keeper status.

**Evidence**

Issue 9429 in this session; details JSON pasted above.

**Suggested area (best guess, not required)**

DuplicateScanner keeper ranking — either de-prioritise 0-byte candidates or
resolve symlink targets before probing size. Also worth considering: skip
symlinks upstream, since Jellyfin already indexes the target once.

**Ambiguity flag**

n/a

### F-097 · DuplicateScanner: false-positive collapse of unrelated titles when Jellyfin auto-resolves both to the same TMDB id

- **Test ID**: duplicate-fuzz D.2, D.4
- **Chapter file**: `docs/testing/duplicate-fuzz/matrix.md`
- **Component**: DuplicateScanner (Exact/Identified tier + Jaccard veto)
- **Severity**: high
- **Category**: correctness
- **Observed**: 2026-08-29
- **Session**: duplicate-fuzz retest 2

**Repro**

1. Seed `_dupfuzz\D2_First_Movie\First Movie (2020)\First Movie (2020).mkv`
   and `_dupfuzz\D2_Second_Movie\Second Movie (2020)\Second Movie (2020).mkv`
   (both copies of `Clean Movie`, no nfo).
2. Seed `_dupfuzz\D4_TheThing_1982\The Thing (1982)\...` and
   `_dupfuzz\D4_TheThing_2011\The Thing (2011)\...`.
3. Config: `DuplicateMinAgeDays=0`, `TreatEditionsAsDuplicates=true`.
4. Scan.

**Expected**

D.2 "First Movie" and "Second Movie" are clearly different titles — title
Jaccard = 0.33, veto default 0.4 → should NOT flag.

D.4 The Thing (1982) vs (2011) is a remake pair, same title, different year.
Task rubric says do NOT flag remakes.

**Actual**

Both pairs flagged with `confidence:1.0` and `appliedTier:"Exact"`:

```json
D.2: groupKey "movie:tmdb:280217", titleJaccard 0.33, hashesMatch true
D.4: groupKey "movie:tmdb:1091",   titleJaccard 1.0,  hashesMatch true
```

Two root causes stacked:

1. Jellyfin auto-populated `ProviderIds.Tmdb` for both fixture folders in
   each pair by external lookup off the folder/file name — even though
   the nfos never asked for it and the payloads were identical. Both
   "First Movie" and "Second Movie" resolved to the same random tmdb id
   `280217`. Both Things resolved to tmdb `1091`.
2. DuplicateScanner treats a shared TMDB id as an Exact-tier match and
   short-circuits the Jaccard/runtime vetoes — so a 0.33 title similarity
   still gets confidence 1.0, and a remake (same title, different year)
   passes with no year check.

Combined, this reproduces the class of complaint the users reported: the
scanner collapses items that share nothing but a wobbly external metadata
lookup.

**Evidence**

Issues 9439 (D.4) and 9436 (D.2) in this session; full DetailsJson in
`docs/testing/duplicate-fuzz/results.csv`.

**Suggested area (best guess, not required)**

DuplicateScanner tier resolution — either (a) still apply Jaccard/year
vetoes on top of Provider-ID hits (a provider match with title Jaccard<0.4
or |year_delta|≥1 should downgrade to Heuristic, not stay at Exact), or (b)
verify the provider hit against a second signal (runtime, actual on-disk
hash) before accepting confidence 1.0.

**Ambiguity flag**

n/a

### F-096 · DuplicateScanner: byte-identical files with no shared provider-id are NOT grouped even with `DuplicateExactHashEnabled=true`

- **Test ID**: duplicate-fuzz C.1, C.2
- **Chapter file**: `docs/testing/duplicate-fuzz/matrix.md`
- **Component**: DuplicateScanner (SHA-256 pass)
- **Severity**: high
- **Category**: correctness
- **Observed**: 2026-08-29
- **Session**: duplicate-fuzz retest 2

**Repro**

1. Seed two byte-identical copies of `Clean Movie (2024).mkv`:
   - `_dupfuzz\C1_ByteId_a\C1 Movie (2020).mkv`
   - `_dupfuzz\C1_ByteId_b\C1 Movie (2020).mkv`
2. And, same bytes, different stems:
   - `_dupfuzz\C2_SameBytes_a\C2 Alpha (2020).mkv`
   - `_dupfuzz\C2_SameBytes_b\C2 Beta (2020).mkv`
3. Neither pair has any nfo, and their stems don't resolve to a known tmdb
   entry (Jellyfin left `ProviderIds` empty for both).
4. All permissive config: `MinAgeDays=0`, `TitleJaccardVeto=0.05`,
   `RuntimeVetoPct=90`, `ExactHashEnabled=true`, `TreatEditions=true`.
5. Scan.

**Expected**

Task rubric: "Exact (SHA-256) → 1.0". Byte-identical files should ALWAYS
group at the Exact tier regardless of metadata.

**Actual**

Zero Duplicate issues emitted for C.1, C.2, C.3 (or A.3 mkv vs mp4, or A.5
flat vs canon, or A.6 deep vs flat — every scenario where the same content
lives on disk without matching Provider Ids). The 12 issues that DO fire
under permissive config all key on `movie:tmdb:<id>` or `movie:name:...` —
none use a `movie:sha256:...` group key. Even with
`DuplicateExactHashEnabled=true` set explicitly, no hash-based grouping
appears.

Log summary: `MediaDash scanner Duplicate found 0 issues` at defaults,
`... found 12 issues` at permissive — none of those 12 are hash-only groups.

**Evidence**

`docs/testing/duplicate-fuzz/results.csv` rows C.1, C.2, A.3, A.5, A.6.
`file_hashes` table remaining empty is documented independently in F-077.

**Suggested area (best guess, not required)**

Probable root cause chains through F-077: DuplicateScanner reads
`file_hashes`, `file_hashes` is never populated by any scan, so the SHA
pass has nothing to group on regardless of the config toggle. Fixing the
hash-cache population (F-077) should also close this. If not, the
DuplicateScanner grouping step is missing a hash-only bucket.

**Ambiguity flag**

n/a

### F-095 · DuplicateScanner: same-payload path-variant duplicates (A.3 mkv/mp4, A.5 flat/canon, A.6 deep/flat) never flagged

- **Test ID**: duplicate-fuzz A.3, A.5, A.6
- **Chapter file**: `docs/testing/duplicate-fuzz/matrix.md`
- **Component**: DuplicateScanner (Heuristic tier)
- **Severity**: medium
- **Category**: correctness
- **Observed**: 2026-08-29
- **Session**: duplicate-fuzz retest 2

**Repro**

1. Seed `A3_mkv\Container A3 (2020)\Container A3 (2020).mkv` and
   `A3_mp4\Container A3 (2020)\Container A3 (2020).mp4` (ffmpeg -c copy remux).
2. Seed `A5_The_Movie_A5_flat\The.Movie.A5.2020.mkv` and
   `A5_The_Movie_A5_canon\The Movie A5 (2020)\The Movie A5 (2020).mkv`.
3. Seed `A6_Deep_Nested\a\b\c\d\e\Deep Nested Movie (2020).mkv` and
   `A6_Flat\Deep Nested Movie (2020)\Deep Nested Movie (2020).mkv`.
4. All permissive config. Scan.

**Expected**

Heuristic tier with titleJaccard ≥ 0.9, runtime delta 0%, different parents
→ confidence ≥ 0.7. All three pairs should surface.

**Actual**

Zero Duplicate issues on any of A.3, A.5, A.6. All fixture files are
indexed as separate Movie items in Jellyfin. Fixtures A.1 (Inception) and
A.2 (Movie A2 [1080p]/[4K]) do flag — but only because Jellyfin
auto-resolved a shared tmdb id off the parent-folder name. The Heuristic
tier by itself isn't reaching these siblings.

**Evidence**

`docs/testing/duplicate-fuzz/results.csv` rows A.3, A.5, A.6 with
`scanner_flagged=false`.

**Suggested area (best guess, not required)**

DuplicateScanner Heuristic tier — the (Name, Year) blocking key may be
requiring an exact stem match rather than a normalised one. Compare against
the Ungrouped scanner's `SuggestedFix` for Titanic — it correctly proposes
grouping "Titanic (1997)" and "Titanic 1997" together, so the name
normaliser exists elsewhere in the codebase and can be reused.

**Ambiguity flag**

n/a

### F-094 · duplicate-fuzz: `movie.nfo` FullRefresh does not populate `ProviderIds` on already-indexed items (Jellyfin ingest blocked)

- **Test ID**: duplicate-fuzz B.1 (post-hoc probe against pre-existing items)
- **Chapter file**: `docs/testing/duplicate-fuzz/matrix.md`
- **Component**: Jellyfin metadata pipeline (upstream of MediaDash Duplicate/Provider signals)
- **Severity**: high
- **Category**: env
- **Observed**: 2026-08-29
- **Session**: duplicate-fuzz

**Repro**

1. Session prep per this chapter (auth, DryRun ON).
2. Pick two already-indexed items in the test library:
   `Clean Movie (2024)` (id `2b597f722...`) and `Multi Audio (2022)`
   (id `e4046c3b...`).
3. Drop a `movie.nfo` beside each `.mkv` containing shared IDs:
   ```xml
   <movie><title>...</title><year>...</year><tmdbid>603</tmdbid><imdbid>tt0133093</imdbid></movie>
   ```
4. Force `POST /Items/{id}/Refresh?MetadataRefreshMode=FullRefresh&ImageRefreshMode=FullRefresh&ReplaceAllMetadata=true` on both, wait 10 s.
5. `GET /Items/{id}?Fields=ProviderIds` on both.

**Expected**

Both items' `ProviderIds` populated with `Tmdb=603, Imdb=tt0133093` — this
would then feed the DuplicateScanner's provider-ID branch (0.90 confidence)
and produce a Duplicate issue on the next MediaDash scan.

**Actual**

Both responses returned `ProviderIds` empty. `log_20260829.log` continues
to burst `SQLite Error 19: FOREIGN KEY constraint failed` during the
refresh — the same upstream bug that blocks new-item ingest (F-019) also
blocks metadata writes on existing items. Any MediaDash test that depends
on provider IDs cannot run on this box until F-019 is resolved.

**Evidence**

Log tail — same repeating error signature captured under F-019 recurrence.
`log_20260829.log` grep for `FOREIGN KEY constraint failed` returns ~40+
lines around the refresh window. `ProviderIds:` empty on both `GET` responses.

**Suggested area (best guess, not required)**

Jellyfin core `MetadataService.UpdateItemsAsync` / SQLite ingest constraint;
downstream all MediaDash scanners that read `ProviderIds`.

**Ambiguity flag**

n/a — the constraint failure is clear.

---

### F-093 · DuplicateScanner: every config toggle produces identical zero-issue output

- **Test ID**: duplicate-fuzz F.1..F.5d + F.max
- **Chapter file**: `docs/testing/duplicate-fuzz/matrix.md`
- **Component**: `DuplicateScanner`
- **Severity**: high
- **Category**: correctness
- **Observed**: 2026-08-29
- **Session**: duplicate-fuzz

**Repro**

1. Session prep per this chapter.
2. Iterate 12 individual config edits — `DuplicateFixMode` ∈ {DetectOnly,
   Automatic}, `DuplicateAutoFixConfidence` ∈ {0.5, 0.7, 0.9, 1.0},
   `DuplicateExactHashEnabled` ∈ {true, false}, `DuplicateMinAgeDays=0`,
   `DuplicateTitleJaccardVeto=0.1`, `DuplicateRuntimeVetoPct=50`,
   `TreatEditionsAsDuplicates=true` — plus one max-permissive combined
   config (all knobs loosened simultaneously).
3. After each `POST /Plugins/{guid}/Configuration`, call
   `POST /MediaDash/Reset` → `POST /MediaDash/Scan` → poll `Status` until
   `IsScanning=false` → `GET /MediaDash/Issues?type=Duplicate`.

**Expected**

At least some knobs should measurably change the issue count against a
461-item corpus that contains 9 distinct `(Name, ProductionYear)` collision
groups (see F-092). Specifically: dropping `DuplicateMinAgeDays` to 0
should not decrease detection, and dropping `DuplicateTitleJaccardVeto` to
0.05 should widen candidate generation.

**Actual**

All 13 configurations returned `Duplicate` issue count = **0**, each scan
completing in ~2.7 s wall-clock (variance < 100 ms). Log line
`MediaDash scanner Duplicate found 0 issues` emitted every pass. Every
knob is a no-op against actual output — either the scanner short-circuits
before evaluating candidates, or the candidate generator is broken.

**Evidence**

`docs/testing/duplicate-fuzz/results.csv` rows F.1 through F.max.
`log_20260829.log` grep for `MediaDash scanner Duplicate found` shows
consecutive `0 issues` lines throughout the config-toggle window
(~13:10:18 to 13:11:41).

**Suggested area (best guess, not required)**

`DuplicateScanner.ScanAsync` early return, or candidate-generation loop.

**Ambiguity flag**

n/a.

---

### F-092 · DuplicateScanner: 9 (Name+Year) collision groups in 461-item corpus, zero flagged

- **Test ID**: duplicate-fuzz G.parallel_lib
- **Chapter file**: `docs/testing/duplicate-fuzz/matrix.md`
- **Component**: `DuplicateScanner`
- **Severity**: high
- **Category**: correctness
- **Observed**: 2026-08-29
- **Session**: duplicate-fuzz

**Repro**

1. Session prep per this chapter.
2. `POST /MediaDash/Reset`, `POST /MediaDash/Scan`, wait for `IsScanning=false`.
3. `GET /MediaDash/Issues?type=Duplicate` → returns `[]`.
4. Independently probe the item cache:
   `GET /Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path,ProductionYear&Limit=2000` →
   group by `Name + '|' + ProductionYear`, filter `Count > 1`.

**Expected**

Each `(Name, ProductionYear)` group with 2+ items in the same parent
directory should produce a `Duplicate` issue at heuristic confidence.
Under the ladder in the task rubric: title Jaccard = 1.0 → +0.15, runtime
delta indeterminate, same directory → −0.25 → nominal 0.60. Even at that
confidence a Duplicate issue should be *emitted* in Detected state (auto-
fix confidence gates only the fix, not detection).

**Actual**

Nine distinct (Name+Year) collision groups exist in the parallel Movie
library on this box:

| Name | Year | Members |
|---|---|---|
| SWife Katy - | (blank) | 23 |
| Zoeneli - | (blank) | 19 |
| Frintteza - | (blank) | 18 |
| Madiiitay | (blank) | 8 |
| Mila Sobolov - | (blank) | 9 |
| Lana Smalls - | (blank) | 6 |
| Beamititik - | (blank) | 3 |
| Madiiitay | 2023 | 2 |
| Madiiitay | 2022 | 2 |

Every member of each group lives in the same parent directory (they are
`.mp4` sibling files). Zero `Duplicate` issues raised across all groups on
DetectOnly, Automatic, or max-permissive config.

**Evidence**

`docs/testing/duplicate-fuzz/results.csv` row `G.parallel_lib`.
`log_20260829.log` — every `MediaDash scanner Duplicate found` line since
2026-08-29 02:00 shows `0 issues`. Item cache probe attached below:

```
Name           Count
SWife Katy -      23
Zoeneli -         19
Frintteza -       18
Madiiitay          13
Mila Sobolov -     9
Lana Smalls -      6
Beamititik -       3
```

**Suggested area (best guess, not required)**

`DuplicateScanner` candidate-generation loop — possibly a blank-year veto,
possibly an early-return when `ProviderIds` are empty, possibly a same-
directory filter that runs *before* the confidence adjustment instead of
after.

**Ambiguity flag**

Task rubric says "same-dir penalty" is a −0.25 confidence adjustment, not a
detection veto. If the scanner has hard-coded same-dir suppression at the
detection step (rather than the confidence step) that would explain
F-092 + D.1/D.5/D.6 not being reproducible even under a fresh Jellyfin.
Source read needed to arbitrate.

---

### F-091 · playability-suite: F-019 recurs at 176-fixture scale — item-scoped scanner cannot be exercised

- **Test ID**: playability-suite (all 8 modes)
- **Chapter file**: 01-scanners.md (§J), `playability-suite/findings.md`
- **Component**: Jellyfin item ingest (upstream of MediaDash), PlayabilityScanner (downstream)
- **Severity**: high
- **Category**: env
- **Observed**: 2026-08-29
- **Session**: playability-suite (matrix + results.csv + findings.md under `docs/testing/playability-suite/`)

**Repro**

1. Seed 22 baseline media fixtures (11 video containers, 8 audio
   containers, 2 books, 1 comic) under
   `C:\dev\mediadash-fixtures\movies\_playfuzz\baseline\` — one file per
   supported extension, all generated by the bundled ffmpeg or hand-crafted.
2. Apply 7 mutations (`zero`, `header-only`, `tail-truncated`,
   `middle-hole`, `garbage-payload`, `magic-flipped`, `wrong-ext`) into
   sibling folders — 176 fixtures total.
3. For each mode: `POST /Library/Refresh` → wait 15s →
   `POST /MediaDash/Reset` → `POST /MediaDash/Scan` → poll `IsScanning`
   until false → `GET /MediaDash/Issues`.

**Expected**

At least the baseline fixtures should become Jellyfin items; the scanner
should flag broken variants; ffprobe control should agree.

**Actual**

0 of 176 fixtures were indexed as Jellyfin items (checked via
`GET /Items?Recursive=true&Fields=Path`); consequently the item-scoped
PlayabilityScanner walked past 100% of them and flagged 0. Same failure
mode as F-019 and R.4 — the ingest pipeline rejects any new file added
under `movies\` on this box. Direct ffprobe control (recorded per-row in
`results.csv`) IS meaningful, but the scanner-vs-ffprobe comparison this
suite was designed to produce is unattainable until F-019 unblocks.

**Evidence**

`docs/testing/playability-suite/results.csv` (176 rows,
`jellyfin_indexed=no` on every row), `matrix.md`, `findings.md`.
Jellyfin log grep for `PlayabilityScanner|FfprobeService|MediaFileHelper`
in `log_20260829.log` — 0 matches during run window.

**Suggested area (best guess, not required)**

Not a plugin defect. Upstream Jellyfin ingest pipeline
(`MetadataService` per F-019). The plugin's PlayabilityScanner cannot be
QA-covered end-to-end until this is unblocked.

**Ambiguity flag**

n/a.

---

### F-090 · playability-suite: `wrong-ext` mode — ffprobe demuxes by content, extension gives no signal

- **Test ID**: playability-suite `wrong-ext`
- **Chapter file**: `playability-suite/findings.md`
- **Component**: `PlayabilityScanner`, `FfprobeService` — extension-based routing assumptions
- **Severity**: medium
- **Category**: correctness
- **Observed**: 2026-08-29
- **Session**: playability-suite

**Repro**

1. Take a valid `matroska` payload (v-mkv.mkv, 146 KB).
2. Copy that same byte content into files named `a.mp3`, `a.flac`,
   `a.mp4`, `a.epub`, `a.pdf`, `a.cbz`, etc. — every extension the plugin
   claims to handle.
3. Run ffprobe on each:
   `ffprobe -show_format a.mp3` → **exit 0**, reports Matroska container,
   h264+aac streams. Same for `.epub`, `.pdf`, `.cbz`, `.opus`, etc.

**Expected**

If the scanner uses the extension to interpret probe results (e.g.
"this is a .mp3, so it should have exactly 1 audio stream and 0 video
streams"), a `wrong-ext` file should be flagged either as Playability
(container-vs-extension mismatch) or routed to the correct probe path.

**Actual**

ffprobe never disagrees with itself just because the file was renamed.
All 22 extensions return exit 0 on the mkv payload. If the scanner
trusts extension-driven routing, a `.mp3` file that is actually an mkv
would slip through the audio-only checks. Whether this is a real bug
depends on the scanner's internal logic — a session with source access
should verify the routing branches.

**Evidence**

`docs/testing/playability-suite/results.csv` — filter `mode=wrong-ext`;
all 22 rows show `ffprobe_exit=0`, empty stderr.

**Suggested area (best guess, not required)**

`PlayabilityScanner` extension → probe-path routing; add a
container-vs-extension mismatch check if not already present.

**Ambiguity flag**

The scanner may already handle this correctly — F-019 prevented direct
end-to-end verification. Filed so a future session (with F-019
unblocked) confirms.

---

### F-089 · playability-suite: ffprobe exits 0 on zero-byte `.ac3`, `.flac`, `.m4v` — silent-corruption trap

- **Test ID**: playability-suite `zero` mode
- **Chapter file**: `playability-suite/findings.md`
- **Component**: `FfprobeService` (or any caller that treats ffprobe exit code as the sole playability signal)
- **Severity**: medium
- **Category**: correctness
- **Observed**: 2026-08-29
- **Session**: playability-suite

**Repro**

1. Create a 0-byte file at `zero.ac3`, `zero.flac`, `zero.m4v`.
2. Run bundled ffprobe:
   `& "C:\Users\crackruckles\Downloads\jellyfin_10.11.11-amd64\jellyfin\ffprobe.exe" -show_format zero.ac3`
   → **exit 0**, empty stdout, empty stderr.
3. Compare with `zero.mp3`, `zero.mkv`, `zero.mp4` — all return exit 1
   with `"Invalid data found when processing input"` on stderr.

**Expected**

A 0-byte file is unplayable; ffprobe should return non-zero for every
extension, or the scanner should not rely on exit code alone.

**Actual**

The bundled ffmpeg build accepts an empty stream for `.ac3`, `.flac`,
and `.m4v` as a valid (empty) container: exit 0 with no diagnostic
output. If `PlayabilityScanner` treats exit code as the sole signal,
these three formats will silently pass genuine corruption.

**Evidence**

`docs/testing/playability-suite/results.csv` — filter
`mode=zero, ffprobe_exit=0` returns exactly those three rows:
```
ac3,zero,no,no,"",0,"",1007,""
flac,zero,no,no,"",0,"",1026,""
m4v,zero,no,no,"",0,"",1010,""
```
Every other extension in `mode=zero` returns exit 1.

**Suggested area (best guess, not required)**

`FfprobeService.ProbeAsync` — add a stream-count guard: 0 streams
returned = failure, regardless of exit code. Or run `ffprobe
-show_streams` and check `streams.length > 0`.

**Ambiguity flag**

n/a.

---

### F-088 · playability-suite: baseline .epub / .pdf / .cbz ffprobe-fail — books/comics must not use ffprobe path

- **Test ID**: playability-suite `baseline`
- **Chapter file**: `playability-suite/findings.md`
- **Component**: `PlayabilityScanner` routing for book/comic categories vs. `BookProbeService` / `ComicProbeService`
- **Severity**: high (correctness — potential mass false-positive on healthy books/comics)
- **Category**: correctness
- **Observed**: 2026-08-29
- **Session**: playability-suite

**Repro**

1. Create a minimal-but-valid `epub`, `pdf`, and `cbz` file (well-formed
   OCF zip / PDF 1.4 / zip-of-jpegs). All three open in a reader.
2. Run bundled ffprobe on each:
   ```
   ffprobe -hide_banner -loglevel error -show_format b-epub.epub
   ```
   → exit 1, stderr `"Invalid data found when processing input"` for
   all three.
3. Doc `01-scanners.md` R.4 lists .epub, .pdf, .cbz among the plugin's
   supported extensions.

**Expected**

Books and comics should be routed to `BookProbeService` /
`ComicProbeService` (which know how to open a zip/OCF/PDF). The
Playability scanner should NOT flag them via an ffprobe-exit-code path.

**Actual**

ffprobe cannot demux any book or comic container (correctly — it's not
a media file). If PlayabilityScanner treats an ffprobe non-zero exit as
"Playability defect", every healthy book and comic in the library would
be falsely flagged. F-019 prevented direct scanner exercise on this
box, so we cannot confirm what the scanner actually does — but the
input side of this failure mode is fully demonstrated and the scanner's
category routing must be verified.

**Evidence**

`docs/testing/playability-suite/results.csv` — filter
`mode=baseline, ffprobe_exit=1`:
```
epub,baseline,no,no,"",1,"...Invalid data found when processing input",1012,""
pdf,baseline,no,no,"",1,"...Invalid data found when processing input",1022,""
cbz,baseline,no,no,"",1,"...Invalid data found when processing input",1013,""
```

**Suggested area (best guess, not required)**

`PlayabilityScanner` category-routing — books → `BookProbeService`,
comics → `ComicProbeService`. Ensure the ffprobe path is video/audio
only.

**Ambiguity flag**

Doc R.4 lists all these extensions under a single "supported" umbrella
without stating which probe backend handles each. Suite treats the doc
list at face value; the finding is that the routing needs to differ per
category.

---

### F-087 · 02-P.2/P.5: RecycleBin Restore + AdoptBatch payload names diverge from doc; bin DTO has no Id

- **Test ID**: 02-P.2, 02-P.5
- **Chapter file**: 02-fixers.md
- **Component**: `Fixers/RecycleBin` controllers + DTOs
- **Severity**: medium
- **Category**: docs
- **Observed**: 2026-08-29
- **Session**: 09/05/07/02 batch

**Repro**

1. `POST /MediaDash/RecycleBin/Items/Restore` with `{"itemIds":["..."]}` → `409 "BinPath is required."`. Endpoint expects `{"BinPath":"..."}` singular.
2. `POST /MediaDash/RecycleBin/AdoptBatch` with `{"paths":["..."]}` → `400 "Path is required."`. Endpoint expects `{"Path":"..."}` singular (name `AdoptBatch` implies plural but body is singular).
3. `GET /MediaDash/RecycleBin/Items` items have no `Id` field: `FileName, SizeBytes, RecycledAtUtc, AutoPurgesAtUtc, HistoryId, OriginalPath, Provenance, Reason, IssueType, ActionText, RestoreHint`.

**Expected**

Per 02-P.2 doc: `POST -d '{"itemIds":["<id>"]}'`. Per 02-P.5 doc: `POST -d '{"paths":["<path>"]}'`.

**Actual**

Restore is single-item via `BinPath` (no bulk-restore path documented). Adopt is also single-item via `Path` despite the `Batch` suffix in the route. Bin items have no `Id` — restore key is really the bin folder path (or the derived HistoryId).

**Evidence**

`evidence/F-087/restore-shape.txt`.

**Suggested area (best guess, not required)**

Doc-only fix in `02-fixers.md` §P.2 and §P.5. Optionally rename `/AdoptBatch` route or accept an array to match its name.

**Ambiguity flag**

n/a.

---

### F-086 · 02: dry-run marks issue status Fixed in the DB, blocking further preview and preventing the real fix from running

- **Test ID**: 02-D / 02-H / 02-I preview-then-real workflow
- **Chapter file**: 02-fixers.md
- **Component**: `ScheduledTasks/FixTask.cs`, `Persistence.IssueRepository`
- **Severity**: high
- **Category**: correctness
- **Observed**: 2026-08-29
- **Session**: 09/05/07/02 batch

**Repro**

1. `POST /MediaDash/Reset`, run `MediaDashScan` — 9 detected issues in DB.
2. With `DryRun=true`, approve one issue (e.g. Playability id 7979) and run `MediaDashFix`. History gets a `WasDryRun=true` row with `Success=true`.
3. `SELECT status FROM issues WHERE id=7979` → `2` (Fixed).
4. Underlying file `Truncated Movie (2021).mkv` still on disk unchanged.
5. Re-run `MediaDashScan` — the Playability issue does NOT re-appear in `GET /MediaDash/Issues` (default filter status=Detected). The row remains Fixed in DB.
6. Because the row is Fixed, a subsequent real (non-dry) fix attempt has nothing to run on.

**Expected**

Dry-run is a preview: it should leave the issue in status Detected (or Approved) so the operator can re-approve after seeing the plan and then run the real fix. The doc's own workflow (A.3-A.8, D.2-D.3, etc.) is "dry-run → real" back to back.

**Actual**

Dry-run flips the DB row to Fixed even though the filesystem was not touched. Re-scan does not re-open the issue (scanners must be idempotent on the row's status). This makes the documented dry-run → real workflow impossible without also blowing away the DB (`/Reset`) between the two runs.

**Evidence**

`evidence/F-086/db-issues.txt`, `evidence/F-086/truncated-still-on-disk.txt`.

**Suggested area (best guess, not required)**

`ScheduledTasks/FixTask.cs` — when `DryRun=true`, do NOT transition `issues.status` to Fixed. Leave it Approved (or roll back to Detected) so the operator can flip DryRun off and re-run.

**Ambiguity flag**

Interpreted "dry-run" as the standard "preview without persisting outcome" contract implied by the History row's `WasDryRun=true`. If dry-run is instead intended as one-shot "here is your preview, done", the doc's back-to-back workflow needs clarifying.

---

### F-085 · 07-G.7/G.8: FixMode and MediaSortSource enum values differ from doc

- **Test ID**: 07-G.7, 07-G.8
- **Chapter file**: 07-config-ui.md
- **Component**: `Configuration/FixMode.cs`, `Configuration/MediaSortSource.cs`, `configPage.html`
- **Severity**: medium
- **Category**: docs
- **Observed**: 2026-08-29
- **Session**: 09/05/07/02 batch

**Repro**

1. Fetch `GET /web/ConfigurationPage?name=MediaDash`, save.
2. Locate `modeOptions()` helper: options are `Off`, `DetectOnly`, `ManualApprove`, `Automatic` (4 values, `<option value="…">`).
3. Locate the `mdMediaSortSource` select: options are `JellyfinMetadata`, `FilenameHeuristic` (2 values).

**Expected**

Per 07-G.7 `FixMode` options: `Off, Approved, Auto`. Per 07-G.8 `MediaSortSource` options: `Folder, Filename, Ffprobe`.

**Actual**

Actual FixMode enum has 4 members with different casings (`Automatic` vs `Auto`, plus `DetectOnly` / `ManualApprove` where the doc lists a single `Approved`). Actual MediaSortSource has 2 members with different casings and no Ffprobe entry. Both drifts are also visible on-disk in the persisted config XML: `<DuplicateFixMode>Automatic</...>`, `<MediaSortSource>JellyfinMetadata</...>`.

**Evidence**

`evidence/F-085/enum-drift.txt`.

**Suggested area (best guess, not required)**

Doc-only fix in 07-config-ui.md, OR rename the enum members in `Configuration/FixMode.cs` + `Configuration/MediaSortSource.cs` if the doc names were intended.

**Ambiguity flag**

n/a.

---

### F-084 · 07-I.2: 3 user-facing config fields not reachable from Settings UI

- **Test ID**: 07-I.2
- **Chapter file**: 07-config-ui.md
- **Component**: `configPage.html`
- **Severity**: medium
- **Category**: correctness
- **Observed**: 2026-08-29
- **Session**: 09/05/07/02 batch

**Repro**

1. Enumerate every property name from `GET /Plugins/{guid}/Configuration` → 92 fields.
2. Fetch `GET /web/ConfigurationPage?name=MediaDash` (667 KB HTML).
3. For each config field, look for its literal name in the HTML.
4. 5 fields have zero references; of those, 3 are user-facing: `CodecPreferenceOrder`, `ScanCpuThreads`, `ScanBelowNormalPriority`. (The other 2 are internal bootstrap flags: `FixTaskSeeded`, `PostV12CleanupCompleted`.)

**Expected**

Per 07-I.2: "Every field in `PluginConfiguration.cs` reachable from Settings tab."

**Actual**

`ScanCpuThreads` and `ScanBelowNormalPriority` (perf tuning) and `CodecPreferenceOrder` (transcode preference) can only be changed by hand-editing the XML or POSTing to `/Plugins/{guid}/Configuration`.

**Evidence**

`evidence/F-084/unreachable-fields.txt`.

**Suggested area (best guess, not required)**

`configPage.html` — add these three controls to the Advanced section (or Encoding section for CodecPreferenceOrder).

**Ambiguity flag**

Interpreted "reachable" strictly as literal name presence in the settings page HTML. Fields might in principle be exposed under a different HTML id, but no such indirection was found for the 3 flagged.

---

### F-083 · 05-A.5: log lines say `MediaDash scan starting` / `Completed`, not `ScanTask: started` / `ScanTask: complete` as doc claims

- **Test ID**: 05-A.5
- **Chapter file**: 05-scheduled-tasks.md
- **Component**: `ScheduledTasks/ScanTask.cs` logging
- **Severity**: low
- **Category**: docs
- **Observed**: 2026-08-29
- **Session**: 09/05/07/02 batch

**Repro**

1. Trigger `MediaDashScan` via `/ScheduledTasks/Running/{id}`.
2. `Select-String` `log_20260829.log` for `ScanTask: started` and `ScanTask: complete` — zero matches.
3. Actual strings: `MediaDash scan starting: 12 items, 17 scanners (0 skipped as Off)` and Jellyfin's own `Scan libraries for issues Completed after 0 minute(s) and 0 seconds`.

**Expected**

Per 05-A.5, log shows `ScanTask: started` and `ScanTask: complete` lines.

**Actual**

Different wording (see above). Behaviourally correct — task starts and completes are both logged — but the exact strings the doc names are not present.

**Evidence**

`evidence/F-083/log-lines.txt`.

**Suggested area (best guess, not required)**

Doc-only fix in `05-scheduled-tasks.md` A.5, OR align log wording in `ScheduledTasks/ScanTask.cs`.

**Ambiguity flag**

n/a.

---

### F-082 · 09-B.7: `libSkiaSharp.dll` loaded from Jellyfin core folder, not plugin folder as doc claims

- **Test ID**: 09-B.7
- **Chapter file**: 09-analytics-compat.md
- **Component**: `Compat/SkiaSharpBridge.cs`
- **Severity**: low
- **Category**: docs
- **Observed**: 2026-08-29
- **Session**: 09/05/07/02 batch

**Repro**

1. `ls $env:JFDATA\plugins\MediaDash_0.9.0.0\` — no `libSkiaSharp.dll` (or any SkiaSharp assembly) shipped in the plugin package.
2. Inspect the running jellyfin process module list (`(Get-Process -Id <pid>).Modules`).
3. `libSkiaSharp.DLL` resolves to `C:\Users\crackruckles\Downloads\jellyfin_10.11.11-amd64\jellyfin\libSkiaSharp.DLL` — the Jellyfin core install directory. Same for `SkiaSharp.dll` and `Jellyfin.Drawing.Skia.dll`.

**Expected**

Per 09-B.7 the doc claims "On Windows, uses native `libSkiaSharp.dll` from plugin folder. Confirm via ProcessHacker / Handle.exe."

**Actual**

Plugin folder has no SkiaSharp DLL at all. The bridge relies on the host-provided SkiaSharp already loaded by Jellyfin core. This is likely the correct architecture (avoid conflict / bloat) but contradicts the doc statement and the rename-based test in 09-B.6.

**Evidence**

`evidence/F-082/loaded-modules.txt`, `evidence/F-082/plugin-dir.txt`.

**Suggested area (best guess, not required)**

Doc-only fix: `09-analytics-compat.md` B.7/B.6 wording, or `Compat/SkiaSharpBridge.cs` architecture note.

**Ambiguity flag**

n/a.

---

### F-081 · 09-A.6/A.8: `AnalyticsInstallId` never populated despite `AnalyticsEnabled=True`; no `AnalyticsReporter` activity in any log

- **Test ID**: 09-A.6, 09-A.8 (also blocks A.3-A.5, A.12, A.14)
- **Chapter file**: 09-analytics-compat.md
- **Component**: `Analytics/AnalyticsReporter.cs`, install-id generation
- **Severity**: high
- **Category**: correctness
- **Observed**: 2026-08-29
- **Session**: 09/05/07/02 batch

**Repro**

1. `GET /Plugins/38bdb090-b763-4294-934b-b54ade4d9d6d/Configuration` — confirm `AnalyticsEnabled: true` (default).
2. Observed value: `AnalyticsInstallId: ""` (empty).
3. Run a full `MediaDashScan` (scheduled task) — completes cleanly.
4. `Select-String` against `log_20260827/28/29.log` for `AnalyticsReporter|Analytics: sent|p_duplicate` — zero matches.
5. `plugin_state` DB table has no install-id row; no `install*` or `analytics` bytes anywhere under `data/mediadash/`.

**Expected**

Per 09-A.6: "First-run install ID generated and persisted." Per the AnalyticsReporter description in the chapter: aggregate reports of issue counts should send when analytics is enabled.

**Actual**

With analytics enabled by default, no install ID has ever been generated in this environment and there is no evidence any `AnalyticsReporter` code path has executed (no log line, no DB row, no persisted setting). Either the reporter isn't wired to any startup / scan / timer hook, or its first-run initializer is silently failing before the install-id write.

**Evidence**

`evidence/F-081/config.json`, `evidence/F-081/notes.txt`. Grep against `log_2026082[7-9].log` for `AnalyticsReporter|p_duplicate` returned 0 matches across all files.

**Suggested area (best guess, not required)**

`Analytics/AnalyticsReporter.cs` — wire-up on plugin startup or scan-complete hook; verify install-id persistence path.

**Ambiguity flag**

Reporter cadence isn't documented explicitly (doc says "see reporter interval in code"). Interpreted "never seen sent" as an issue independent of cadence because install-id write should be first-run, not interval-gated.

---

### F-080 · 08-A.5/B: 26 nested i18n strings missing from every non-English locale

- **Test ID**: 08-A.5, 08-B.2..B.9
- **Chapter file**: 08-i18n.md
- **Component**: `Configuration/i18n/*.json` bundles (all 8 non-en)
- **Severity**: medium
- **Category**: correctness
- **Observed**: 2026-08-28
- **Session**: 08-i18n QA sweep

**Repro**

1. Deep-walk each bundle under
   `C:\dev\mediadash\Jellyfin.Plugin.MediaDash\Configuration\i18n\`
   (recursing through `types`, `wizSteps` array, and `html` objects) to
   collect a set of dotted string-leaf paths.
2. English yields 229 string leaves. Every non-en locale yields 203.
3. `Compare-Object` the path sets: each of `de`, `es`, `fr`, `it`, `nl`,
   `pt-BR`, `ru`, `zh-CN` is missing the **same** 26 paths.

**Expected**

Per 08-A.5, every English key path present in each locale (or documented
as a deferred fallback). Per the per-locale checklist (B.1..B.9),
"key parity with English" should hold.

**Actual**

Every non-en bundle is missing these 26 leaf paths:

- `.html.settings.safety.analyticsHint`
- `.types.CorruptNfo.{title,hint,action,actionHint,approveAll,doRecycle,doPermanent}` (7)
- `.types.SubtitleFonts.{title,hint,action,actionHint,approveAll}` (5)
- `.types.Ungrouped.{title,hint,action,actionHint,approveAll}` (5)
- `.wizSteps[9]` through `.wizSteps[16]` (8 — steps 10..17 of the wizard)

Top-level key parity passes (all 9 locales have 80 top-level keys), so
this defect is invisible to the flat-key check the doc prescribes; it
only surfaces when the nested `types` / `wizSteps` / `html` subtrees are
walked. Placeholder parity across the remaining 203 shared leaves is
clean (0 fails). Charset markers present in every locale (nl only 1
hit — Dutch genuinely uses few marked characters — nl bundle is
translated: 70/77 top-level strings differ from English).

**Evidence**

Text only. Full missing-path list above; reproduced identically for de,
es, fr, it, nl, pt-BR, ru, zh-CN. Sample: `de.types.Duplicate.title =
"Doppelte Kopien"` (present) but `de.types.CorruptNfo.title` absent
entirely.

**Suggested area (best guess, not required)**

Copy the missing subtrees from `en.json` into each of the 8 other
bundles and translate them. The wizard likely grew from 9 to 17 steps
after non-en bundles were last synced, and three fix scanners
(CorruptNfo, SubtitleFonts, Ungrouped) plus the analytics-hint HTML
block were added without a locale re-sync pass.

**Ambiguity flag**

Doc A.5's "verify every English key exists" is worded for the top-level
flat map — which passes. Interpretation used here: the intent of A.5 +
per-locale "key parity" is coverage of user-visible strings, so the
nested subtrees count. If the maintainer's intent is "only the flat
strings must match", this is n/a — but then A.2/A.4 (see F-079) still
fail.

---

### F-079 · 08-A.2/A.4: `types`, `wizSteps`, `html` values are nested objects/arrays, not strings

- **Test ID**: 08-A.2, 08-A.4
- **Chapter file**: 08-i18n.md
- **Component**: `Configuration/i18n/*.json` bundles + `I18nCatalog`
- **Severity**: low
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 08-i18n QA sweep

**Repro**

1. `curl.exe -s -H "Authorization: $env:JFAUTH" http://localhost:8099/MediaDash/I18n/en | ConvertFrom-Json`
2. Iterate `.PSObject.Properties` and check each `.Value` type.
3. Three of the 80 top-level keys return non-string values:
   - `types` → object (18 properties, each a further object of 5-7 string fields)
   - `wizSteps` → array (17 string entries in en, 9 in every other locale — see F-080)
   - `html` → object (27 properties, some further nested)

**Expected**

Per 08-A.2: "Response is a flat JSON object of `key:string` pairs."
Per 08-A.4: "All values are non-null strings (no bools, numbers, nulls
snuck in)."

**Actual**

77 of 80 values are strings; 3 are objects/arrays containing further
nested string leaves. Behaviour is consistent across the server response
and the on-disk bundle. The shape appears intentional (nested scanner-
type dictionaries, wizard-step arrays, help-text blocks) rather than
data-model corruption.

**Evidence**

Text only.

```
en top-level non-string keys:
  types    -> PSCustomObject (18 nested scanner-type subtrees)
  wizSteps -> Object[] (17-element array of step titles)
  html     -> PSCustomObject (27 nested help-block subtrees)
```

**Suggested area (best guess, not required)**

Update `08-i18n.md` A.2 / A.4 to state that the top-level shape is
`key: string | object | array-of-strings`, and add explicit deep-walk
guidance for A.5/A.6/B parity checks. Alternatively, flatten the
bundles at the endpoint (e.g. `types.Duplicate.title`) so A.2 holds
literally. No code defect if the nested shape is intentional.

**Ambiguity flag**

n/a — checklist wording is unambiguous; the bundle shape simply
diverges from it.

---

### F-078 · 04-F.2: FfprobeError never propagates into `diagnostics` table

- **Test ID**: 04-F.2
- **Chapter file**: 04-probing.md
- **Component**: FfprobeService / diagnostics writer
- **Severity**: medium
- **Category**: correctness
- **Observed**: 2026-08-28
- **Session**: 04-probing 2026-08-28

**Repro**

1. Fresh scan against library that includes `books\good-book.epub` (a
   non-media file ffprobe cannot parse) and `comics\good-comic.cbz`.
2. Query `SELECT * FROM diagnostics`.

**Expected**

Per doc 04-F.2 the `FfprobeError` "serializes into diagnostics".

**Actual**

`diagnostics` contains a single unrelated row (`MediaSorter.BadTarget`).
The ffprobe error itself IS present but only inside the raw
`probe_cache.json` blob for that path:
`{"error":{"code":-1094995529,"string":"Invalid data found when processing input"}}`.
No source starting with `Ffprobe`/`Probe` ever appears. Log tail across
the whole day (`log_20260828.log`) has zero MediaDash lines mentioning
ffprobe (unlike scanners which log a one-line summary each run).

**Evidence**

`docs/testing/evidence/F-078/diagnostics-no-ffprobe.txt`

**Suggested area (best guess, not required)**

`Probing/FfprobeService.cs` — probably swallows the error into the cache
row without also calling the DiagnosticsWriter.

**Ambiguity flag**

n/a


### F-077 · 04-E: `file_hashes` table remains empty after a full MediaDash /Scan

- **Test ID**: 04-E.1, 04-E.2, 04-E.4, 04-E.5
- **Chapter file**: 04-probing.md
- **Component**: Probing/FileHasher + Duplicate scanner wiring
- **Severity**: medium
- **Category**: correctness
- **Observed**: 2026-08-28
- **Session**: 04-probing 2026-08-28

**Repro**

1. `POST /MediaDash/Scan`, wait for `IsScanning=false`.
2. `SELECT COUNT(*) FROM file_hashes` → 0.

**Expected**

If FileHasher is exercised by any scanner path during /Scan (Duplicate
scanner is the obvious candidate — it needs a hash to identify dupes)
some rows should land. Doc 04-E.1/E.2/E.4/E.5 all assume the hasher runs
end-to-end during a scan.

**Actual**

Zero rows. Duplicate scanner logged "found 0 issues" without leaving a
single hash behind. This makes 04-E.1/E.2/E.4/E.5 unverifiable against
the plugin's actual DB state without a bespoke fixture setup (two
identical indexed items) that F-019 blocks anyway.

Either the hasher is a purely in-memory helper that never persists, or
persistence is gated by "duplicate candidate group of size ≥ 2" — the
doc doesn't say which. If the intent is that the ledger survives across
sessions the current behaviour also breaks F.1 (round-trip after
restart).

**Evidence**

`docs/testing/evidence/F-077/file_hashes-empty.txt`

**Suggested area (best guess, not required)**

`Probing/FileHasher.cs` — check whether it writes to `file_hashes` at
all, or whether the Duplicate scanner writes on its behalf only when
`groupCount >= 2`.

**Ambiguity flag**

Doc 04-E doesn't specify when hashes should be written, only that "two
identical files produce identical hashes". Interpreted "produces" as
"persists a row" because otherwise E.1 has no observation surface from a
tester who can't read plugin source.


### F-076 · HI Test (2024).mkv (with embedded SDH subtitle) never enters MediaDash probe pipeline

- **Test ID**: 04-A.4, 04-A.5
- **Chapter file**: 04-probing.md
- **Component**: Probing/FfprobeService item enumeration (dependent on Jellyfin item index)
- **Severity**: medium
- **Category**: env (cascades into correctness for probing DTO validation)
- **Observed**: 2026-08-28
- **Session**: 04-probing 2026-08-28

**Repro**

1. Confirm the file exists on disk and has the correct embedded
   subtitles: run ffprobe directly →
   stream index 3 is `subrip`, `disposition.hearing_impaired=1`, tag
   `title="English SDH"`, tag `language="eng"`.
2. `GET /Items?ParentId=<Test-movies-lib-id>&Recursive=true` → 6 items,
   HI Test (2024) is NOT one of them.
3. `SELECT path FROM probe_cache WHERE path LIKE '%HI Test%'` → 0 rows.

**Expected**

Doc 04-A.4/A.5 want the tester to observe
`FfprobeStreamInfo.hearingImpaired = true` and `title` containing `[SDH]`
in the persisted probe record.

**Actual**

The fixture is the only one on disk that carries an embedded SDH
disposition, but F-019 (Jellyfin core FK burst) prevents it from being
indexed as a Movie item, and MediaDash's probe pipeline is item-driven,
so no probe row is ever written. A.4/A.5 cannot be validated on this
box against the observable DB state.

The same file-not-in-probe-cache problem also happens for Sub Heavy
(2023).mkv which IS indexed as a Movie item — evidence its absence is
about the scanners not reaching that item (it produced no issues), not
about indexing. So `probe_cache` is selectively populated by whichever
scanner needs it, not universally.

**Evidence**

`docs/testing/evidence/F-076/probe_cache-hi-test-missing.txt`

**Suggested area (best guess, not required)**

Root cause is F-019 in Jellyfin core. Adjacent MediaDash decision:
`probe_cache` selectivity means the QA doc's assumption "scan → row
appears for every media file" is wrong. Either the doc should be
adjusted or the probe pipeline should populate a row for every file
touched, not just files that triggered a scanner.

**Ambiguity flag**

n/a


### F-075 · `format_probe_cache` schema is ok/reason only — none of the doc's parsed DTO fields (duration, codecs, pageCount, streams…) are columns

- **Test ID**: 04-A.2, 04-A.3, 04-B.5, 04-C.4, 04-C.5, 04-F.3
- **Chapter file**: 04-probing.md
- **Component**: Probing/FormatProbeCache table + FfprobeData/BookProbeResult/ComicProbeResult serialization
- **Severity**: high
- **Category**: docs (F-020-style DTO drift) — the plugin persists differently than the checklist claims
- **Observed**: 2026-08-28
- **Session**: 04-probing 2026-08-28

**Repro**

1. `PRAGMA table_info(format_probe_cache)` returns exactly 6 columns:
   `path, size, mtime_utc, probed_at_utc, ok, reason`.
2. `PRAGMA table_info(probe_cache)` returns 5 columns: `path, size,
   mtime_utc, probed_at_utc, json`. The `json` blob holds the raw
   ffprobe stdout verbatim.
3. Full-table search across the DB for any column named
   `book|comic|page|title|author|duration|codec|channels|hearing` → zero
   matches.

**Expected**

Per doc 04-A.2/A.3: "DB row for that file in FormatProbeResult has
parsed duration, video codec, audio codec, all streams";
`FfprobeStreamInfo` with `codecName, codecType, channels, sampleRate,
bitRate, language, title, hearingImpaired`; per 04-B.5 "BookProbeResult
with title, author(s), pageCount, language"; per 04-C.4 "ComicProbeResult
pageCount, first image dimensions, byte size, hasMetadataXml".

**Actual**

None of these fields are columns in any table. `format_probe_cache`
stores only ok/reason booleans. `probe_cache.json` is a TEXT blob
holding the raw ffprobe stdout — no parsed DTO is ever written to a
structured shape queryable from SQL.

For books: `good-book.epub` has row `ok=1, reason=null` in
`format_probe_cache`. Its `probe_cache.json` is 121 chars long, contents:
`{"error":{"code":-1094995529,"string":"Invalid data found when processing input"}}`
(ffprobe cannot parse epub — expected). No pageCount / title / author
is anywhere. Same for `good-comic.cbz`.

Consequence: the 04-A/B/C DTO round-trip assertions cannot be validated
from DB state as written. Either the DTO is projected only at
serialization time (via /Issues or /LibraryStats — which /LibraryStats
does not do either, see F-074), or these fields are unimplemented.

**Evidence**

`docs/testing/evidence/F-075/format_probe_cache-schema.txt`

**Suggested area (best guess, not required)**

`Probing/FfprobeService.cs` / `Probing/BookProbeService.cs` /
`Probing/ComicProbeService.cs` — parsed DTOs live only in-memory during
a scan and are dropped after scanners consume them. Either persist the
DTO or update the doc to name what actually lands on disk (raw ffprobe
JSON in `probe_cache.json`, boolean in `format_probe_cache`).

**Ambiguity flag**

n/a


### F-074 · /MediaDash/LibraryStats reports ItemCount=0 for Books/Comics despite probe_cache holding those fixtures

- **Test ID**: 04-B.6
- **Chapter file**: 04-probing.md
- **Component**: MediaDash.Api LibraryStats endpoint
- **Severity**: medium
- **Category**: correctness (downstream of F-019)
- **Observed**: 2026-08-28
- **Session**: 04-probing 2026-08-28

**Repro**

1. Confirm fixtures on disk: `books\good-book.epub`,
   `books\broken-book.epub`, `comics\good-comic.cbz`.
2. Trigger `POST /Library/Refresh` then `POST /MediaDash/Scan`.
3. `SELECT * FROM format_probe_cache` returns 2 rows for these files
   (ok=1).
4. `GET /MediaDash/LibraryStats` returns for both `Test Books` and
   `Test Comics`: `ItemCount: 0`, `TotalBytes: 0`, empty maps.

**Expected**

Doc 04-B.6: "LibraryStats book counts include these."

**Actual**

Books and Comics libraries report ItemCount=0 even though the plugin's
own probe subsystem clearly reached these files. Root cause is
F-019 (Jellyfin core can't index new epub/cbz as items on this box) and
LibraryStats reads Jellyfin's item table, not the plugin's probe cache.

**Evidence**

`docs/testing/evidence/F-074/librarystats-books-comics-zero.txt`,
`docs/testing/evidence/F-074/librarystats.json`

**Suggested area (best guess, not required)**

`Api/LibraryStatsController.cs` — could fall back to `format_probe_cache`
counts when a library reports 0 items but the probe cache has rows for
its paths, giving useful stats even when Jellyfin core indexing has
failed. Not required — could equally be argued as pure F-019 fallout.

**Ambiguity flag**

n/a


### F-073 · `GET /MediaDash/Environment` doc claims a `smart` array but SMART data lives on `/Status.Drives[]` instead

- **Test ID**: 04-D.1
- **Chapter file**: 04-probing.md
- **Component**: MediaDash.Api Environment vs Status endpoints
- **Severity**: low
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 04-probing 2026-08-28

**Repro**

1. `GET /MediaDash/Environment` returns exactly:
   `{"PluginVersion","JellyfinVersion","Os","Framework","SubtitleProviders"}`
   — 5 fields, no `smart` array, no `ffprobePath` (F-012 duplicate).
2. `GET /MediaDash/Status` returns `Drives: [{Root, FreeBytes,
   TotalBytes, IsLibraryDrive, IsRecycleBinDrive, SmartHealth,
   SmartMessage, SmartModel, SmartTemperatureCelsius,
   SmartTemperatureMaxCelsius, SmartWearPercent}]` — SMART data is
   actually here.

**Expected**

Per doc 04-D.1: `/MediaDash/Environment` includes a `smart` array.

**Actual**

Environment has no SMART data at all. SMART data is present, healthy,
and correctly populated (`SmartHealth="healthy"`, `SmartModel="Lexar
SSD NM790 2TB"`, `SmartTemperatureCelsius=51`, `SmartWearPercent=0`) —
just on the wrong endpoint per the doc. Also note the status value is
`"healthy"` not `"OK"` as 04-D.4 says.

**Evidence**

`docs/testing/evidence/F-073/environment.json`,
`docs/testing/evidence/F-073/status-drives.json`

**Suggested area (best guess, not required)**

Docs-only fix. Update 04-D.1 to reference `/Status.Drives[].SmartHealth`
and 04-D.4 to expect the string `"healthy"` rather than `"OK"`.

**Ambiguity flag**

n/a


### F-072 · 06-G enum-coverage doc is unspecified for int→name mapping; type=15 unnameable via API surface

- **Test ID**: 06-G.1 through 06-G.18
- **Chapter file**: 06-data.md
- **Component**: docs/testing/06-data.md (docs) + IssueType enum surfacing
- **Severity**: low
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 06 · Data layer (fresh QA pass)

**Repro**

1. Fresh scan on this box. `SELECT DISTINCT type FROM issues UNION SELECT DISTINCT type FROM history`.
2. Cross-reference each int to an enum name via Status.LifetimeCounts and Status.Counts.

**Expected**

Every IssueType value round-trips through scan → DB → API with a
resolvable name.

**Actual**

DB stores raw ints, not names. Cross-reference works for 10 of the 18
enum values via Status.Counts/LifetimeCounts. `type=15` (9 rows in
`history`, all `bytes_freed=0`) has no entry in either aggregation, so
its enum name cannot be confirmed from API surface alone. Doc-order
predicts `HeavyTranscode`, but nothing on the wire proves it. There is
no `GET /MediaDash/Enums` (or similar) that lists the full mapping.

Also, 7 of the 18 enum values have zero coverage on this dev box:
Misplaced, CorruptArtwork, LargeTrickplay, SubtitleFonts, CorruptNfo,
FailedTranscode, EmbeddedCoverArt. Not a plugin bug — fixture-coverage
gap.

**Evidence**

`docs/testing/evidence/F-072/type-coverage.txt`

**Suggested area (best guess, not required)**

Add a `GET /MediaDash/IssueTypes` (or bake mapping into
`/MediaDash/Environment`) that returns `{int → name}` for the full
enum. Alternatively, doc a canonical table in 06-data.md so QA can
resolve ints without doing arithmetic on lifetime counts.

**Ambiguity flag**

06-G.16 asks to verify `HeavyTranscode` but does not name the int. On
this box, `type=15` has 9 history rows all with `bytes_freed=0`, which
is consistent with either HeavyTranscode or FailedTranscode. Interpreted
as HeavyTranscode based on doc order.

---

### F-071 · `probe_cache` row not touched on rescan when file mtime unchanged (doc says LastProbedUtc updated)

- **Test ID**: 06-E.2
- **Chapter file**: 06-data.md
- **Component**: FormatProbe / probe cache pass
- **Severity**: low
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 06 · Data layer (fresh QA pass)

**Repro**

1. Reset + Scan. Capture `probed_at_utc` and json-hash for one
   `probe_cache` row (e.g. `01 - Short Song.mp3`).
2. `POST /MediaDash/Scan` again with no file changes on disk.
3. Wait for scan complete. Re-read the same row.

**Expected**

Doc 06-E.2: "Re-scan without file changes → row `LastProbedUtc` updated
but content stable (idempotent)."

**Actual**

Row is byte-identical. `probed_at_utc` did NOT advance. JSON content
did not change. probe hit its (path,size,mtime_utc) cache key and
skipped the write path entirely.

  BEFORE: probed_at_utc=639235234066264308 json_sha256[:16]=49e83195dacd98e3
  AFTER:  probed_at_utc=639235234066264308 json_sha256[:16]=49e83195dacd98e3

Not a functional bug — idempotence stronger than the doc claim. Just
doc drift.

**Evidence**

`docs/testing/evidence/F-071/probe-idempotence.txt`

**Suggested area (best guess, not required)**

Update 06-E.2 to say "row unchanged when file mtime unchanged (probe
cache hit)". Or add a "touched-at" pass in the scanner if the
timestamp is actually needed for staleness detection.

**Ambiguity flag**

n/a

---

### F-070 · Rescan creates duplicate Open `issues` rows for the same `(type, path)` — invariant claimed in doc 06-B.6 is violated

- **Test ID**: 06-B.6
- **Chapter file**: 06-data.md
- **Component**: Scan → issue upsert (dedup by (type, path) claimed)
- **Severity**: high
- **Category**: correctness
- **Observed**: 2026-08-28
- **Session**: 06 · Data layer (fresh QA pass)

**Repro**

1. `POST /MediaDash/Reset`. `POST /MediaDash/Scan`. Wait.
2. Approve one issue, dismiss another (via `POST /MediaDash/Issues/{id}/Approve`
   and `/Dismiss`). Leave at least one issue as Open — in this run the
   surviving Open row was `id=7927 type=10 (Ungrouped) path=…\\Big Buck Test (2020)`.
3. `POST /MediaDash/Scan`. Wait.
4. In DB: `SELECT type, path, count(*) FROM issues WHERE status=0 GROUP BY type,path HAVING count(*) > 1`.

**Expected**

Doc 06-B.6: "Re-running a scan does NOT duplicate open issues for
unchanged files (identity by type + path)."

**Actual**

Query returns:
  type=10, path=`…\\mediadash-fixtures\\movies\\Big Buck Test (2020)`, count=2

Both rows are `status=0` (Open). One is `id=7927` (survivor of the
first scan). One is `id=7937`, newly inserted by the rescan for the
same `(type, path)`. No dedup.

Other Open rows from the first scan (ids 7928-7931, 7934, 7935) were
also replaced by fresh ids 7938-7942 in the rescan — implying the
scanner deletes+reinserts most Open rows but somehow missed 7927,
leaving a duplicate. Likely a scope/library-mismatch in the dedup
key or a race with the delete pass.

**Evidence**

`docs/testing/evidence/F-070/dupe-open-issues.txt`
`C:\Users\CRACKR~1\AppData\Local\Temp\mediadash-e2e-backup.db` (session backup, MD5 968282610C6C2002A33A389BE7763DA0)

**Suggested area (best guess, not required)**

Issue upsert path in the scanner. Look for a UNIQUE (type, path)
constraint on `issues` (absent from schema — only index is
`idx_issues_type_status`) and either add one for status=0 rows or make
the upsert idempotent.

**Ambiguity flag**

Doc says "identity by type + path" for Open dedup. Verified against
status=0 subset. Result is unambiguous — dupe exists.

---

### F-069 · `history.bytes_freed` column is NOT NULL and uses 0-sentinel; doc 06-D.2/D.3 says "nullable" and "non-null only for space-affecting fixers"

- **Test ID**: 06-D.2, 06-D.3
- **Chapter file**: 06-data.md
- **Component**: history table schema
- **Severity**: low
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 06 · Data layer (fresh QA pass)

**Repro**

1. Read schema: `PRAGMA table_info(history)`.
2. Group history rows by type and count zero/positive `bytes_freed`.

**Expected**

Doc D.2 lists `BytesFreed` as a nullable field. D.3 says "BytesFreed
non-null only for space-affecting fixers".

**Actual**

`bytes_freed INTEGER NOT NULL` in schema. Zero is used both as
"N/A" (dry-run preview, subtitle download, restore-from-bin, file
missing, file outside library) and as "genuine 0 bytes freed". By
action-text spot check, 0-values line up with expected non-space
actions, so semantics are correct — but the column-nullability claim
in the doc is wrong.

Aggregate breakdown by type (int → doc name):
  Duplicate      14 rows / 13 zero / 1 pos
  Playability   101 rows / 63 zero / 38 pos (dry-run previews contribute)
  Quality         5 rows /  0 zero / 5 pos
  MissingSubs   470 rows / 470 zero / 0 pos (subtitle downloads, no bytes freed)
  ...

**Evidence**

`docs/testing/evidence/F-069/bytes_freed-nullability.txt`

**Suggested area (best guess, not required)**

Docs. Either update 06-D.2 to say "INTEGER NOT NULL, 0 sentinel for
non-space-affecting actions" and drop D.3, or migrate the column to
`INTEGER NULL` and let readers distinguish "no space freed" from "N/A".
Behaviour is fine; only wording is misleading.

**Ambiguity flag**

n/a

---

### F-068 · `issues` and `history` column names in 06-data.md do not match the DB or the API DTOs (three naming schemes)

- **Test ID**: 06-B.2, 06-D.2
- **Chapter file**: 06-data.md
- **Component**: docs/testing/06-data.md
- **Severity**: medium
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 06 · Data layer (fresh QA pass)

**Repro**

1. `PRAGMA table_info(issues)` and `PRAGMA table_info(history)`.
2. `GET /MediaDash/Issues` and `GET /MediaDash/History`, read first row keys.
3. Compare each to 06-B.2 and 06-D.2 field lists.

**Expected**

06-B.2: `Id, Type, Path, LibraryId, DetectedUtc, Status, Metadata`.
06-D.2: `Id, TimestampUtc, IssueType, Path, Result (enum), DryRun,
BytesFreed, ErrorMessage, BinPath, MetadataJson`.

**Actual**

DB `issues` columns: id, type, item_id, path, details,
suggested_fix, size_savings, status, detected_at_utc, confidence.
  - `LibraryId` missing entirely; `item_id` (Jellyfin item Guid) is
    the closest analogue.
  - `Metadata` → `details` (TEXT JSON).
  - `DetectedUtc` → `detected_at_utc` (INTEGER ticks).
  - Extra: `suggested_fix`, `size_savings`, `confidence`.

DB `history` columns: id, issue_id, type, path, action, bytes_freed,
recycle_path, fixed_at_utc, dry_run, restored, success, acknowledged.
  - `TimestampUtc` → `fixed_at_utc`
  - `IssueType` → `type`
  - `Result` enum → replaced by two ints (`success`, `dry_run`)
  - `ErrorMessage` → not present in DB (may be embedded in `action`)
  - `BinPath` → `recycle_path`
  - `MetadataJson` → not present
  - Extra: `issue_id`, `action`, `restored`, `acknowledged`

API DTOs use a THIRD scheme: `/Issues` returns
`Id, ItemId, Type, Path, FileName, SuggestedFix, DetailsJson,
SizeSavings, Status, DetectedAtUtc, WasPreviouslyRestored`.
`/History` returns `Id, Type, FileName, Library, Action, BytesFreed,
FixedAtUtc, WasDryRun, Success, CanRestore`.

Three schemes (DB snake_case / doc semantic names / API PascalCase
DTO), no cross-reference table anywhere.

**Evidence**

`docs/testing/evidence/F-068/issue-history-columns.txt`

**Suggested area (best guess, not required)**

Update 06-B.2 and 06-D.2 to list actual DB columns AND the DTO names
side by side. Consider a single mapping table in the chapter header.

**Ambiguity flag**

n/a

---

### F-067 · 06-data.md A.2 schema list wrong — actual tables are snake_case, no MonthAggregates, five extra tables present

- **Test ID**: 06-A.2
- **Chapter file**: 06-data.md
- **Component**: docs/testing/06-data.md
- **Severity**: medium
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 06 · Data layer (fresh QA pass)

**Repro**

1. Locate DB via `/MediaDash/Status.DataDirectory` →
   `C:\Users\crackruckles\AppData\Local\jellyfin-v10\data\mediadash\mediadash.db`.
2. `SELECT name,type FROM sqlite_master WHERE type IN ('table','index')`.

**Expected**

Doc 06-A.2: tables `Issues, History, FormatProbeResults,
MonthAggregates, KeyValue`.

**Actual**

Tables actually present (all lowercase snake_case):
  decode_cache, diagnostics, file_hashes, format_probe_cache,
  history, issues, plugin_state, probe_cache, restored_paths.

Indexes: idx_diagnostics_last_at_utc, idx_issues_type_status.

Drift:
  - `Issues` → `issues`
  - `History` → `history`
  - `FormatProbeResults` → `format_probe_cache` (and a separate
    `probe_cache` table holds full ffprobe json — two probe-related
    tables)
  - `MonthAggregates` → NO SUCH TABLE. F.1 references
    `SELECT strftime('%Y-%m', TimestampUtc), sum(BytesFreed) FROM
    History GROUP BY 1` and compares to `MonthAggregate` output,
    but there is no persisted aggregate — aggregation must be
    computed on the fly (if at all).
  - `KeyValue` → `plugin_state` (schema: `key TEXT PK, value TEXT`).
  - Extra tables (not mentioned in doc): decode_cache, diagnostics,
    file_hashes, probe_cache, restored_paths.

**Evidence**

`docs/testing/evidence/F-067/schema-dump.txt`

**Suggested area (best guess, not required)**

Update 06-data.md A.2 to list actual tables (snake_case) and add
sections for `decode_cache`, `diagnostics`, `file_hashes`,
`probe_cache`, `restored_paths`. Either remove `MonthAggregates`
coverage in F.1/F.2 or add a note that `MonthAggregate` is a
computed DTO, not a persisted table.

**Ambiguity flag**

Doc says "`Issues, History, FormatProbeResults, MonthAggregates,
KeyValue` (or similar)" — the "or similar" hedge admits drift is
possible, but the drift is substantial, not cosmetic.

---

### F-066 · `POST /MediaDash/Scan` does not deduplicate concurrent bursts — 10 parallel POSTs produce 3 back-to-back scan runs

- **Test ID**: 03-H.1
- **Chapter file**: 03-api.md (03-H)
- **Component**: MediaDashController.Scan / ScanTask scheduler
- **Severity**: medium
- **Category**: correctness
- **Observed**: 2026-08-28
- **Session**: 03-H · Concurrency & security (fresh QA pass)

**Repro**

1. Session prep per 00-setup.md §3, then safety-flip `DryRun=true`.
2. `POST /MediaDash/Reset` to clear pending state.
3. Fire 10 concurrent `POST /MediaDash/Scan` calls (empty body, `Authorization` header, no Content-Length dance). All 10 return 204.
4. Immediately grep the Jellyfin log for `MediaDash scan starting`.

**Expected**

Per 03-H.1: "10 parallel `POST /Scan` calls → only one scan actually starts (log shows deduplication)." Server should coalesce the burst into a single scan while one is in-flight and either 204-and-drop or 409 the extras, leaving exactly ONE `MediaDash scan starting` line in the log for the burst.

**Actual**

Three separate scan runs executed back-to-back for a single 10-request burst. All 10 requests returned 204 (no rejections, no `409`, no `busy` log). The log shows:

```
[22:04:54.647] MediaDash scan starting: 12 items, 17 scanners
[22:04:56.844] "Scan libraries for issues" Completed after 0 minute(s) and 2 seconds
[22:04:58.806] MediaDash scan starting: 12 items, 17 scanners
[22:05:00.328] MediaDash scan starting: 12 items, 17 scanners
```

No explicit `already running` / `deduplicated` / `skipped — scan in progress` log line was emitted for any of the coalesced requests. Behaviour matches "one scan runs, additional POSTs during that run queue for the next slot, and each queued request triggers another full pass." Partial coalescing (10 → 3) rather than the intended full coalescing (10 → 1).

Consequence on a real library: a bursty client (e.g. a UI polling loop that fires Scan on every visibility change) can rerun the entire scanner pipeline several times in a row, wasting IO and pinning the DB. Also, `IsScanning` flapped `true → false → true → false → true → false` across the burst window rather than staying `true` throughout, which will confuse a UI that debounces on the flag transition.

**Evidence**

- `evidence/F-066/h1-log-tail.txt` — full log excerpt covering the burst window (3 × `scan starting`, matching completion lines, no dedup log line).

**Suggested area (best guess, not required)**

`MediaDashController.Scan` action, or whatever `ScanTask` uses to enqueue → gate with a single-shot flag (`Interlocked.CompareExchange`, or a `SemaphoreSlim(1)` with `WaitAsync(TimeSpan.Zero)`) that also swallows requests received during the "cooling" window between the current scan completing and the next queued one starting. Two options: (a) reject subsequent 204s with 429/409 (protocol-correct but noisy), or (b) drop them silently and emit one `MediaDash scan already running — coalesced` INF line per drop (matches doc wording).

**Ambiguity flag**

n/a — H.1 expected wording is unambiguous ("only one scan actually starts").

### F-065 · Wrong-type request-body fields do NOT return 400 for 7 of 8 request DTOs — silent type-tolerance downgrades checked errors into 403/404/other

- **Test ID**: 03-G.17
- **Chapter file**: 03-api.md (03-G)
- **Component**: MkdirRequest, RenameRequest, MoveOrCopyRequest, DeleteRequest, BinRestoreRequest, ConsolidateRequest, AdoptBatchRequest (BulkIssueRequest is the only DTO that behaves as doc claims)
- **Severity**: low
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 03-G fresh QA pass, DryRun ON

**Repro**

1. Session-prep P.1–P.3, flip DryRun on.
2. For each DTO below, POST a JSON body with the correct required field(s) but a wrong-type value (e.g. `Path: 42` instead of a string). Observe the status code.

| DTO | Endpoint | Body | Expected (per G.17) | Actual |
|-----|----------|------|--------------------|--------|
| MkdirRequest | `/Files/Mkdir` | `{"Path":42,"Name":"x"}` | 400 | **403** (allowlist eats the null) |
| RenameRequest | `/Files/Rename` | `{"Path":42,"NewName":"x"}` | 400 | **403** |
| MoveOrCopyRequest | `/Files/Move` | `{"From":42,"To":"..."}` | 400 | **403** |
| DeleteRequest | `/Files/Delete` | `{"Path":42}` | 400 | **403** |
| BinRestoreRequest | `/RecycleBin/Items/Restore` | `{"BinPath":42}` | 400 | **404** |
| ConsolidateRequest | `/RecycleBin/Consolidate` | `{"SourceRoot":42}` | 400 | **404** |
| AdoptBatchRequest | `/RecycleBin/AdoptBatch` | `{"Path":42}` | 400 | **400** but body message is `"That path is not an unowned MediaDash batch..."`, not a model-binding error — the int got tolerated and the "path" was passed downstream as some string form. |
| BulkIssueRequest | `/Issues/Bulk` | `{"ids":"not-an-array","action":"Approve"}` | 400 | **400** with proper `System.Text.Json` model-binding error (`"The JSON value could not be converted to ... Int64"`). Only DTO that behaves as documented. |

3. Unknown-field leniency (send `{...,"BogusField":"x"}`) works for all 8 DTOs — every one silently accepts and processes the request. That half of G.17 passes cleanly.

**Expected**

Per G.17: all listed request DTOs "reject unknown fields silently (JSON leniency) but reject wrong types with 400".

**Actual**

- Unknown-field leniency: **passes for all 8 DTOs**.
- Wrong-type strictness: **passes only for BulkIssueRequest**. For the seven others the wrong-type request never generates a 400 model-binding failure. Instead:
  - The four `FileBrowserController` DTOs bind `Path`/`From` as `null`, then downstream allowlist normalisation runs on `null`/empty → 403 Forbidden.
  - `BinRestoreRequest` binds `BinPath` as `null`, hits the "bin path not found" branch → 404.
  - `ConsolidateRequest` binds `SourceRoot` as `null` → not-found branch → 404.
  - `AdoptBatchRequest` binds `Path` somewhere non-null (int coerced or default string) and reaches the domain check → 400, but the message is a business-rule error, not a validation error.

The practical impact is small (all requests still fail closed — nothing destructive happens on garbage input), but the doc's "wrong types with 400" invariant does not hold for these seven DTOs and clients cannot rely on a canonical 400 to distinguish "your JSON is malformed" from "your action is disallowed".

**Evidence**

`docs/testing/evidence/F-065/` — response bodies for every unknown-field and wrong-type variant across all 8 DTOs (files named `G17-<dto>-unknown.json` / `G17-<dto>-wrongtype.json`, plus `-unknown2/-wrongtype2` retries where the first guess used the wrong field name).

**Suggested area (best guess, not required)**

Either:
- Add `[Required]` / `[MinLength(1)]` attributes on the string properties of the seven DTOs so ASP.NET model validation fires *before* the null hits the controller (turns 403/404 into 400 with a proper `errors[]` block matching what `BulkIssueRequest` already gets), OR
- Reword 03-G.17 to describe the actual behaviour: "wrong types on `BulkIssueRequest` return 400 with model-binding errors; wrong types on the other DTOs are silently coerced/nulled and fall through to the controller's own domain checks, returning 403/404/business-400 depending on the endpoint".

**Ambiguity flag**

G.17 says "reject wrong types with 400" without defining the mechanism. Interpretation used: "the endpoint returns HTTP 400 (any body shape) when the request body has a field of the wrong JSON type in a required position". Under that reading, only BulkIssueRequest passes. A stricter reading (a validation-shaped 400 with `errors[]`) would also make BulkIssueRequest the sole pass; a looser reading ("any error status counts") would still leave the seven DTOs failing to specifically flag the type error.

---

### F-064 · 03-F chapter is systemically wrong — Mkdir/Delete request DTOs, missing overwrite/moveToBin params, and 400-vs-409/404 status codes

- **Test ID**: 03-F.5, 03-F.6, 03-F.7, 03-F.10, 03-F.16, 03-F.19, 03-F.20
- **Chapter file**: 03-api.md (03-F)
- **Component**: FileBrowserController + MkdirRequest, DeleteRequest, MoveOrCopyRequest, Upload endpoint
- **Severity**: medium
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 03-F fresh QA pass, DryRun ON, `_fbtest-03F` sandbox

**Repro**

1. Session-prep per top of 03-api.md, DryRun flipped ON.
2. `POST /MediaDash/Files/Mkdir` with `{ "path": "$LIB\movies\NewFolder" }` (the doc's F.6 body). Server returns 400 `"Folder name contains invalid characters."`. Actual DTO is `{Path: parent, Name: leaf}`; supplying that returns 204. See `evidence/F-064/F6-mkdir.out` (400 with old shape) and successful 204 after retry.
3. Mkdir with the same body again → **409** with `"An entry with that name already exists."`, not 400 as F.7 asserts.
4. `POST /Files/Rename` collision on existing name → **409** (`"An entry with that name already exists."`), not 400 as F.10 asserts.
5. `POST /Files/Copy` over an existing target → **409** (`"An entry already exists at the destination."`), not 400 as F.16 asserts. Additionally, F.16 posits an `overwrite=true` escape hatch; neither `?overwrite=true` query nor `{Overwrite: true}` body works — server always 409s. `MoveOrCopyRequest` has only `{From, To}`.
6. `POST /Files/Delete` with `{Path: ...}` (only field on DeleteRequest) → **204** and file lands in the recycle bin. `?moveToBin=true` and `?moveToBin=false` query variants are both silently ignored — file always goes to bin. There is no API surface to permanently delete via `/Files/Delete`; F.19's `moveToBin=false` assertion is undeliverable.
7. `POST /Files/Upload` with the doc's `curl -F "file=@..."` incantation → 400 `The name field is required.`, then when curl uses multipart the server writes the multipart envelope (or empty) to disk as file content. Endpoint actually expects `?path=<parent>&name=<leaf>` **query** params and reads `Request.Body` as raw bytes — `curl --data-binary "@file" -H "Content-Type: application/octet-stream" ...?path=X&name=Y` succeeds with 204 and correct byte roundtrip via `/Files/Download`.
8. `GET /Files/List?path=<inside-allowlist-but-missing>` → **404**, not 400 as F.5 asserts. `<outside-allowlist-but-missing>` → 403 (allowlist check fires first). Neither combination yields the 400 the checklist expects.

**Expected**

Doc claims for 03-F:
- F.5: nonexistent path → 400
- F.6: `MkdirRequest = { path: full }`
- F.7: duplicate mkdir → 400
- F.10: rename collision → 400
- F.16: copy collision → 400 unless `overwrite=true` escape hatch
- F.19: `DeleteRequest` supports `moveToBin=true|false` to control bin vs permanent
- F.20: upload via `curl -F "file=@..."`

**Actual**

- Missing paths: 404 inside allowlist, 403 outside.
- `MkdirRequest = { Path: parent-dir, Name: leaf }`.
- Duplicate mkdir / rename collision / copy collision all return 409 with a JSON string body describing the collision.
- No `overwrite` support anywhere in `MoveOrCopyRequest` or the copy endpoint's query surface.
- `DeleteRequest = { Path }` only. Delete always routes to the recycle bin (verified: `RecycleBin.FileCount` incremented for both bare Delete and `?moveToBin=false`).
- Upload expects `?path=&name=` query params and reads `Request.Body` raw. Multipart form via `-F` writes garbage/empty to disk while returning 400 or 204 depending on which field name is guessed.
- Extra field `Kind` on `FileEntry` DTO (`{Name, IsDirectory, SizeBytes, ModifiedUtc, Kind}`) — doc doesn't mention it.
- `DirectoryListing` DTO includes `{Path, Parent, IsRoot, IsRecycleBin, IsLogsDir, Entries[]}` — doc only names `entries[]`.
- Empty-path list (F.2) returns configured **library roots** (7 entries), not drive letters.

**Evidence**

`docs/testing/evidence/03F/` — response bodies for F.1–F.25 (F1-list.json, F2-empty.json, F3-windows.json, F4-traverse.json, F5-nonexistent.json, F5b-inside.json, F6-mkdir.out, F7-mkdir-dup.out, F8-mkdir-evil.out, F9-rename.out, F10-rename-conflict.out, F12-move.out, F14-move-escape.out, F15-copy.out, F16-copy-dup.out, F16b-copy-ow.out, F16c-copy-ow-body.out, F17-delete.out, F18-delete-outside.out, F19-delete-bintest.out, F19-delete-mtb-false.out, F20-upload.out, F20b-upload.out, F22-upload-outside.out, F25-partial.out).

Security-relevant results (all passed):
- F.3 (`C:\Windows` list) → 403.
- F.4 (traversal `..\..\..\Windows`) → 403; canonicalization not bypassable via this vector.
- F.8 (Mkdir to `C:\ProgramData\Evil-03F-test`) → 403 and folder never created.
- F.14 (Move sandbox file to `C:\ProgramData`) → 403 and target never created.
- F.18 (Delete of `C:\Windows\System32\drivers\etc\hosts`) → 403 and hosts untouched.
- F.22 (Upload to `C:\ProgramData`) → 403.
- F.24 (Download of hosts, both via traversal and outright) → 403.
- F.25 (100 MB range download of the 26.9 GB fixture) → 206 in 0.55 s, Jellyfin RSS delta 0.5 MB (338.2 → 338.7 MB). Streams cleanly.

**Suggested area (best guess, not required)**

`docs/testing/03-api.md` §03-F needs a full rewrite against `Jellyfin.Plugin.MediaDash/Api/FileBrowserController.cs` and its request DTOs. Not a plugin defect — same class of drift as F-039/F-041/F-043 (chapter authored without executing against the running plugin). The lack of an overwrite hook on Copy and of a permanent-delete path may or may not be intended; those are design questions, not doc bugs.

**Ambiguity flag**

F.19 asserts a `moveToBin=false` mode that has no API to invoke. Ran the check as "is there any query, header, or DTO field that toggles bin vs permanent" — answer no. Filed as docs drift rather than missing feature; if permanent-delete-via-API is actually wanted, this becomes a feature gap.

---

### F-063 · `POST /MediaDash/RecycleBin/Items/Restore` DTO drift — request is `{BinPath}`, response is `{RestoredTo, Suffixed}` (doc says `restoredPath, warnings`)

- **Test ID**: 03-E.3, 03-E.5
- **Chapter file**: 03-api.md
- **Component**: MediaDashController.RestoreBinItems / BinRestoreRequest / RestoreResult
- **Severity**: low
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 03-E fresh-QA pass

**Repro**

1. Complete session-prep P.1–P.3.
2. Flip DryRun on (safety).
3. `GET /MediaDash/RecycleBin/Items` → note item; there is NO `id` field. Take the on-disk `BinPath` derived from `%JFDATA%\data\mediadash\recycle\<folder>\<filename>` matching the item's `FileName` + `RecycledAtUtc` folder.
4. `POST /MediaDash/RecycleBin/Items/Restore` with body `{}` → 409 `"BinPath is required."` (proves the request DTO uses `BinPath`, not `ids`).
5. `POST /MediaDash/RecycleBin/Items/Restore` with body `{"BinPath": "<abs file path in bin>"}` → 200.

**Expected**

Per doc §E.3: `BinRestoreRequest` unspecified but implied `ids[]`, response `RestoreResult { restoredPath, warnings }`.

**Actual**

- Request DTO: `{ "BinPath": string }` (single item per call — no batch `ids[]` field discovered).
- Response DTO: `{ "RestoredTo": string, "Suffixed": bool }` — same shape as `POST /History/{id}/Restore` (already noted in F-051). No `warnings` field. Collision case correctly sets `Suffixed:true` and appends `-restored` (E.5 passes semantically).

**Evidence**

- `evidence/F-063/request-shape.json` — the request body that succeeded.
- `evidence/F-063/restore-response.json` — 200 response (no collision).
- `evidence/F-063/restore-collision-response.json` — 200 response with `-restored` suffix on collision.
- Ties in with F-051: same DTO drift on the sibling history-restore endpoint.

**Suggested area (best guess, not required)**

`Jellyfin.Plugin.MediaDash/Api/BinRestoreRequest.cs` and `RestoreResult.cs` — align field names with docs, or update `03-api.md §E.3–E.5` to reflect the actual DTOs. Consider whether E.4 "restore many" even makes sense given the request is single-BinPath (caller loops).

**Ambiguity flag**

Doc §E.3 says `BinRestoreRequest` (with no schema) and hints `RestoreResult { restoredPath, warnings }`. Section E.4 ("restore many, each succeeds independently") implies a batch shape that this endpoint does not have. Interpretation: batch is done client-side via one POST per item; E.4 was marked `[-]` because only one bin item exists.

---

### F-062 · `GET /MediaDash/RecycleBin/OtherBins` returns `[]` on this box — shape unknown (no data), doc-drift risk unassessed

- **Test ID**: 03-E.8
- **Chapter file**: 03-api.md
- **Component**: MediaDashController.OtherBins
- **Severity**: low
- **Category**: env
- **Observed**: 2026-08-28
- **Session**: 03-E fresh-QA pass

**Repro**

1. `GET /MediaDash/RecycleBin/OtherBins` → 200, body `[]`.

**Expected**

Per doc §E.8: `OtherBinLocation[]` — items with schema unspecified in the doc.

**Actual**

200 + `[]`. Endpoint is reachable and returns a JSON array as expected, but element shape cannot be verified from the outside without staging an orphaned bin folder on this box. Log for the next tester who has a multi-bin scenario.

**Evidence**

`evidence/F-062/otherbins.json` — the empty response.

**Suggested area (best guess, not required)**

`Jellyfin.Plugin.MediaDash/Api/OtherBinLocation.cs` — check field names remain PascalCase like the rest of the surface.

**Ambiguity flag**

n/a.

---

### F-061 · `GET /MediaDash/RecycleBin/Items` DTO differs sharply from doc — no `id`, `binPath`, or `sourceHistoryId`; extra `Provenance/IssueType/ActionText/RestoreHint`

- **Test ID**: 03-E.2
- **Chapter file**: 03-api.md
- **Component**: MediaDashController.GetRecycleBinItems / RecycleBinItem
- **Severity**: low
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 03-E fresh-QA pass

**Repro**

1. `GET /MediaDash/RecycleBin/Items` → 200 with array.

**Expected**

Per doc §E.2: each row has `id, originalPath, binPath, sizeBytes, reason, binnedUtc, sourceHistoryId`.

**Actual**

Real keys (PascalCase): `FileName, SizeBytes, RecycledAtUtc, AutoPurgesAtUtc, HistoryId, OriginalPath, Provenance, Reason, IssueType, ActionText, RestoreHint`.

- No `id` — clients must locate items via `BinPath` (see F-063) or via `HistoryId`.
- No `binPath` on the DTO either — Restore requires an absolute filesystem path the client must reconstruct externally, which is why Restore-by-DTO is awkward.
- `binnedUtc` → `RecycledAtUtc`. `sourceHistoryId` → `HistoryId`.
- Extra fields: `AutoPurgesAtUtc`, `Provenance` (e.g. `"History"`), `IssueType`, `ActionText`, `RestoreHint` (localized UX hint).

Also: item count mismatch with `GET /RecycleBin` — `Items[]` had 1 row while `GET /RecycleBin` reported `FileCount:2, SizeBytes:601474`. The disk backing shows the second "file" is an orphaned `poster.png` (`601,444` bytes) sitting in a bin folder whose `.mediadash-origin` metadata is not being surfaced as a bin item. Whether that is a listing bug or an intentional filter is unclear — flagged for triage.

**Evidence**

- `evidence/F-061/recyclebin-items.json` — full response (1 row).
- Inline: on-disk 19 bin subdirs, only 2 contain non-metadata files (`poster.png`, `broken-comic.cbz`); Items API surfaces only one of them.

**Suggested area (best guess, not required)**

`Jellyfin.Plugin.MediaDash/Api/RecycleBinItem.cs` — either update doc §E.2, or expose an `Id` (e.g. bin folder name) so clients can reference items without reconstructing paths. Separately, `RecycleBinManager` (or wherever `GetRecycleBinItems` lives) needs to reconcile with the counter in `RecycleBinInfo` so `FileCount` matches `Items.Length` (or the doc explains the divergence).

**Ambiguity flag**

Doc treats `FileCount`/`itemCount` and `Items[].Length` as equivalent. Interpretation used: they are supposed to match; the divergence is a real bug (or, alternatively, doc drift where `FileCount` counts any non-sidecar file on disk regardless of item registration).

---

### F-060 · `GET /MediaDash/RecycleBin` DTO drift — real shape `{FileCount, SizeBytes, IsEmptying, EmptyingDone, EmptyingTotal}`, not `{itemCount, bytes, oldestUtc, per-bin breakdown}`

- **Test ID**: 03-E.1
- **Chapter file**: 03-api.md
- **Component**: MediaDashController.GetRecycleBin / RecycleBinInfo
- **Severity**: low
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 03-E fresh-QA pass

**Repro**

1. `GET /MediaDash/RecycleBin` → 200.

**Expected**

Per doc §E.1: `RecycleBinInfo { itemCount, bytes, oldestUtc, per-bin breakdown }`.

**Actual**

Response body:

```json
{"FileCount":2,"SizeBytes":601474,"IsEmptying":false,"EmptyingDone":0,"EmptyingTotal":0}
```

- `itemCount` → `FileCount`.
- `bytes` → `SizeBytes`.
- `oldestUtc` — MISSING.
- Per-bin breakdown — MISSING (single flat totals, no `bins[]` or similar).
- Extra fields for async empty progress: `IsEmptying`, `EmptyingDone`, `EmptyingTotal`.

**Evidence**

`evidence/F-060/recyclebin-info.json` — captured response.

**Suggested area (best guess, not required)**

`Jellyfin.Plugin.MediaDash/Api/RecycleBinInfo.cs` — update doc, or extend the DTO with `OldestUtc` and per-bin roll-up if that's still an intended feature. The `IsEmptying/EmptyingDone/EmptyingTotal` fields point to an async empty pattern that the doc §E.6 doesn't describe.

**Ambiguity flag**

n/a — pure shape drift.

---

### F-059 · `GET /MediaDash/I18n/{locale}` returns 200 + English body for unknown locales — doc D.14 implies 404

- **Test ID**: 03-D.14
- **Chapter file**: 03-api.md
- **Component**: MediaDashController I18n endpoint
- **Severity**: low
- **Category**: correctness
- **Observed**: 2026-08-28
- **Session**: 03-D audit

**Repro**

1. Auth as admin.
2. `GET /MediaDash/I18n/xx-INVALID`.
3. Observe status + body.

**Expected**

Unknown locale → 404 (or at minimum a distinct signal like an empty
object or `Content-Language: en` fallback header). The tester should be
able to tell "this locale doesn't exist" apart from "this locale is
English".

**Actual**

- HTTP **200**.
- Body is 21 068 bytes of English strings — functionally identical to
  `GET /MediaDash/I18n/en` (only a Unicode-encoding difference in one
  smart quote/em-dash pair; both bodies contain the full 80-key
  English dictionary).
- No response header indicating fallback occurred.

**Evidence**

- `evidence/F-059/d14-xx-INVALID.json`
- `evidence/F-059/d14-en.json`

**Suggested area (best guess, not required)**

`Api/MediaDashController.cs` — the `I18n(string locale)` action. Likely
falls through to a default resource bundle when the locale key misses,
without signalling absence.

**Ambiguity flag**

Doc D.14 says "`en` → JSON dictionary" and points at chapter 08 for the
locale matrix. It doesn't explicitly say what an unknown locale should
do. My interpretation: silent English fallback is worse than 404 —
callers can't detect missing translations.

---

### F-058 · `GET /MediaDash/Logo` returns JPEG bytes but advertises `Content-Type: image/png`

- **Test ID**: 03-D.13
- **Chapter file**: 03-api.md
- **Component**: MediaDashController Logo endpoint (asset resource)
- **Severity**: low
- **Category**: correctness
- **Observed**: 2026-08-28
- **Session**: 03-D audit

**Repro**

1. `curl -o logo.png http://localhost:8099/MediaDash/Logo` (no Authorization header).
2. Inspect response headers and file magic bytes.

**Expected**

Either:
- Content-Type: `image/png` **and** PNG bytes (magic `89 50 4E 47`), or
- Content-Type: `image/jpeg` **and** JPEG bytes.

**Actual**

- HTTP 200 (AllowAnonymous works — good).
- `Content-Type: image/png`.
- File magic bytes are `FF D8 FF E0 00 10 4A 46` → **JPEG/JFIF**, not PNG.
- File length 61 600 bytes.

**Evidence**

- `evidence/F-058/logo.jpg` (renamed from server's `.png` filename)
- `evidence/F-058/logo-headers.txt`

**Suggested area (best guess, not required)**

`Api/MediaDashController.cs` `Logo()` action, or wherever the embedded
resource stream is served. Fix is either replace the asset with a
genuine PNG, or send `image/jpeg`.

**Ambiguity flag**

n/a.

---

### F-057 · `GET /MediaDash/Errors?full=true` has no effect — response byte-identical to bare `/Errors`

- **Test ID**: 03-D.10
- **Chapter file**: 03-api.md
- **Component**: MediaDashController Errors endpoint
- **Severity**: low
- **Category**: correctness
- **Observed**: 2026-08-28
- **Session**: 03-D audit

**Repro**

1. Auth as admin.
2. `curl "http://localhost:8099/MediaDash/Errors"` → save.
3. `curl "http://localhost:8099/MediaDash/Errors?full=true"` → save.
4. Diff.

**Expected**

Doc D.10: "`?full=true` returns full stack." Bare call returns the
condensed entry, `?full=true` should include stack traces or an
additional detail field.

**Actual**

- Both responses length 1 799 bytes, byte-identical.
- Neither response contains any stack-trace field. Real DTO keys:
  `AtUtc, Source, Message, Count, LastAtUtc`. No `stack`, `full`,
  `details`, etc.
- Also: doc D.11 says `/Errors/Count` returns `{ count: N }`, actual
  key is `Total` (`{"Total":5}`).

**Evidence**

- `evidence/F-057/d10a-errors.json`
- `evidence/F-057/d10b-errors-full.json`
- `evidence/F-057/errors-before-03D.json` (pre-Clear snapshot for D.12)

**Suggested area (best guess, not required)**

`Api/MediaDashController.cs` `Errors([FromQuery] bool full)` — either
`full` parameter isn't wired, or the DiagnosticEntry DTO has no
detail-level branch. Likely both. Cheapest fix: drop `?full` from docs.

**Ambiguity flag**

n/a.

---

### F-056 · `GET /MediaDash/LibraryStats` shape entirely different — no `videoCount/audioCount/bookCount/subtitleCount/bytes`

- **Test ID**: 03-D.8
- **Chapter file**: 03-api.md
- **Component**: MediaDashController LibraryStats / `LibraryStat` DTO
- **Severity**: medium
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 03-D audit

**Repro**

1. Auth as admin.
2. `GET /MediaDash/LibraryStats`.
3. Compare keys to doc D.8 / `LibraryStat` DTO.

**Expected**

Doc D.8: fields `library, videoCount, audioCount, bookCount,
subtitleCount, bytes`.

**Actual**

Real per-library object keys (PascalCase):

```
ItemId, Name, CollectionType, ItemCount, TotalBytes,
Resolutions (dict<string,int>), Codecs (dict<string,int>),
Containers (dict<string,int>)
```

No breakdown by media kind (video/audio/book/subtitle). Instead a
single `ItemCount` plus three histogram dicts scoped to whatever the
library contains. Example (movies library):
`{"ItemCount":6,"TotalBytes":21691252,"Resolutions":{"1080p":1,"SD":2,"720p":3},"Codecs":{"h264":4,"hevc":2},"Containers":{"mkv":6}}`.

**Evidence**

- `evidence/F-056/d8-librarystats.json` (all 5 libraries)

**Suggested area (best guess, not required)**

`Api/LibraryStat.cs` DTO vs 03-api.md D.8. Doc is stale.

**Ambiguity flag**

n/a.

---

### F-055 · `GET /MediaDash/Libraries` shape differs — `ItemId/Name/CollectionType/Locations[]` not `id/name/path/itemCount/kind`

- **Test ID**: 03-D.7
- **Chapter file**: 03-api.md
- **Component**: MediaDashController Libraries / `LibraryInfo` DTO
- **Severity**: medium
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 03-D audit

**Repro**

1. Auth as admin.
2. `GET /MediaDash/Libraries`.
3. Compare keys.

**Expected**

Doc D.7: 4 entries with `id, name, path, itemCount, kind`.

**Actual**

- **5** entries (F-005 already open — MediaDash Test / Test
  Audiobooks / Test Books / Test Comics / Test Music), not 4.
- Real keys: `ItemId, Name, CollectionType, Locations[]`. No
  `itemCount` (that lives on LibraryStats — F-056). `Locations` is a
  string[] of folder paths, not a single `path`. `CollectionType` is
  the source-of-truth "kind" — and is **absent** on the Audiobooks
  entry (Jellyfin never assigned one), which is worth knowing.

Example:
`{"ItemId":"0d877f...","Name":"MediaDash Test","CollectionType":"movies","Locations":["C:\\dev\\mediadash-fixtures\\movies"]}`

**Evidence**

- `evidence/F-055/d7-libraries.json`

**Suggested area (best guess, not required)**

`Api/LibraryInfo.cs` vs doc D.7. Doc is stale.

**Ambiguity flag**

n/a.

---

### F-054 · `GET /MediaDash/RecycleBin/DiskInfo` shape differs — `PathProbed/MeetsFiveGbMinimum/SuggestedPauseCapGb` not `binBytes/suggestedCapGb`

- **Test ID**: 03-D.4
- **Chapter file**: 03-api.md
- **Component**: MediaDashController RecycleBin/DiskInfo / `RecycleBinDiskInfo` DTO
- **Severity**: low
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 03-D audit

**Repro**

1. Auth as admin.
2. `GET /MediaDash/RecycleBin/DiskInfo`.

**Expected**

Doc D.4: `{ totalBytes, freeBytes, binBytes, suggestedCapGb }`.

**Actual**

`{"PathProbed":"C:\\Users\\crackruckles\\AppData\\Local\\jellyfin-v10\\data\\mediadash\\recycle","TotalBytes":2047354073088,"FreeBytes":657156698112,"MeetsFiveGbMinimum":true,"SuggestedPauseCapGb":1903}`

Keys: `PathProbed, TotalBytes, FreeBytes, MeetsFiveGbMinimum,
SuggestedPauseCapGb`. Casing PascalCase, not camelCase. **No
`binBytes`** (bin's own usage is not reported here at all — likely on
`/RecycleBin` proper). Extra `PathProbed`, `MeetsFiveGbMinimum`.
Renamed `suggestedCapGb` → `SuggestedPauseCapGb`.

**Evidence**

- `evidence/F-054/d4-diskinfo.json`

**Suggested area (best guess, not required)**

`Api/RecycleBinDiskInfo.cs` vs doc D.4. Doc is stale.

**Ambiguity flag**

n/a.

---

### F-053 · `GET /MediaDash/RecycleBinAccessCheck` returns `{Name, Path, CanRead, CanWrite}` — doc D.3 implies `LibraryAccessResult` with a `warning` field

- **Test ID**: 03-D.3
- **Chapter file**: 03-api.md
- **Component**: MediaDashController RecycleBinAccessCheck / `LibraryAccessResult` DTO
- **Severity**: low
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 03-D audit

**Repro**

1. Auth as admin.
2. `GET /MediaDash/RecycleBinAccessCheck`.

**Expected**

Doc D.3 says single `LibraryAccessResult`; D.1/D.2 describe that DTO as
`{ path, canRead, canWrite, warning }`.

**Actual**

`{"Name":"Recycle bin (default location)","Path":"C:\\Users\\crackruckles\\AppData\\Local\\jellyfin-v10\\data\\mediadash\\recycle","CanRead":true,"CanWrite":true}`

Real keys: `Name, Path, CanRead, CanWrite`. PascalCase. **No `warning`
field at all** — same as F-052 for the array variant. Adds `Name`
which the doc's DTO description omits.

**Evidence**

- `evidence/F-053/d3-recyclebinaccesscheck.json`

**Suggested area (best guess, not required)**

`Api/LibraryAccessResult.cs` vs doc D.1/D.2/D.3. Same DTO used for both
endpoints — see F-052.

**Ambiguity flag**

n/a.

---

### F-052 · `GET /MediaDash/LibraryAccessCheck` DTO has no `warning` field, uses PascalCase, and includes a `Name`

- **Test ID**: 03-D.1
- **Chapter file**: 03-api.md
- **Component**: MediaDashController LibraryAccessCheck / `LibraryAccessResult` DTO
- **Severity**: medium
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 03-D audit

**Repro**

1. Auth as admin.
2. `GET /MediaDash/LibraryAccessCheck`.
3. Compare keys against doc D.1.

**Expected**

Doc D.1: "Each has `path`, `canRead`, `canWrite`, `warning`."

**Actual**

Array of 5 entries (matches F-005, not doc's implied 4). Each entry:

```
{ "Name": "...", "Path": "...", "CanRead": true, "CanWrite": true }
```

- PascalCase, not the camelCase implied by the doc.
- Extra `Name` field (library name from Jellyfin — useful).
- **No `warning` field.** With current fixture set all paths are
  readable/writable, so I could not force a warning; but if the DTO
  has no `warning` property, D.2's "recheck reflects `canWrite=false`
  + warning text" can't be true regardless of state.

Example row:
`{"Name":"MediaDash Test","Path":"C:\\dev\\mediadash-fixtures\\movies","CanRead":true,"CanWrite":true}`

**Evidence**

- `evidence/F-052/d1-libraryaccesscheck.json`

**Suggested area (best guess, not required)**

`Api/LibraryAccessResult.cs`. Either the DTO is missing the `warning`
property the doc promised (plugin bug — no way to signal partial
access), or the doc is stale (docs bug). Given F-053 shows the same
DTO shape on `RecycleBinAccessCheck`, the DTO is the source of truth
and the doc needs updating — but consider whether a `Warning` string
should actually be added to explain permission failures.

**Ambiguity flag**

D.2 (revoke write permission) was skipped as `[-]` destructive to
dev-box permissions, so I could not force a `canWrite=false` state to
confirm whether a warning field ever appears. Reading D.1 conservatively:
even in the happy path the DTO exposes no such field.

---

### F-051 · `POST /MediaDash/History/{id}/Restore` returns `{RestoredTo, Suffixed}` — doc C.8 says `{restoredPath, warnings}`

- **Test ID**: 03-C.8
- **Chapter file**: 03-api.md
- **Component**: MediaDashController History/Restore endpoint / `RestoreResult` DTO
- **Severity**: medium
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 03-C audit

**Repro**

1. Auth as admin.
2. `GET /MediaDash/History` — find a row with `CanRestore=true`.
3. `POST /MediaDash/History/{id}/Restore` (empty body).
4. Inspect JSON keys.

**Expected**

Doc C.8: `RestoreResult` has `restoredPath` (and doc mentions warnings via test hook).

**Actual**

200 OK. Body `{"RestoredTo":"C:\\dev\\mediadash-fixtures\\books\\broken-book.epub","Suffixed":false}`. PascalCase, `RestoredTo` not `restoredPath`, `Suffixed:bool` not `warnings:string[]`. No warnings surface at all in this DTO. File was actually restored on disk (verified). Same-shape DTO is presumably reused by `POST /RecycleBin/Items/Restore` (03-E.3) — worth checking there too.

**Evidence**

- `docs/testing/evidence/F-051/restore-response.txt`

**Suggested area (best guess, not required)**

`Api/RestoreResult.cs` field names. Same class of drift as F-041 / F-043 / F-050 — persistent PascalCase-vs-camelCase and renamed fields across DTOs.

**Ambiguity flag**

n/a

### F-050 · `GET /MediaDash/History/Stats` shape entirely different — no `months`, no `totalRuns`, no `MonthAggregate`

- **Test ID**: 03-C.4, 03-C.5
- **Chapter file**: 03-api.md
- **Component**: MediaDashController History/Stats endpoint / `HistoryStats` DTO
- **Severity**: medium
- **Category**: docs

**Repro**

1. Auth as admin.
2. `GET /MediaDash/History/Stats`.

**Expected**

Doc C.4: `{ months: [...], totalRuns, totalBytesFreed, ... }`. Doc C.5: each month is a `MonthAggregate`.

**Actual**

Response is:
```json
{
  "TotalBytesFreed": 332225452,
  "ByLibrary": [
    {"Library":"","BytesFreed":307800283},
    {"Library":"MediaDash Test","BytesFreed":24424902},
    {"Library":"Test Books","BytesFreed":134},
    {"Library":"Test Comics","BytesFreed":133}
  ]
}
```
Only two top-level keys: `TotalBytesFreed`, `ByLibrary`. No `months` array. No `totalRuns`. `MonthAggregate` type does not appear anywhere in the response — C.5 is unverifiable from this endpoint. Also note: one `ByLibrary` row has empty-string library name (presumably legacy rows with no library resolved).

**Evidence**

- `docs/testing/evidence/F-050/history-stats.json`

**Suggested area (best guess, not required)**

`Api/HistoryStats*.cs` — either the DTO the endpoint returns is a different type than the doc assumes, or the docs describe a planned shape that was never implemented.

**Ambiguity flag**

n/a

### F-049 · `GET /MediaDash/History` caps at 500 rows (doc says ~1000) and `?take=` / `?limit=` are silently ignored

- **Test ID**: 03-C.3
- **Chapter file**: 03-api.md
- **Component**: MediaDashController History endpoint
- **Severity**: low
- **Category**: docs

**Repro**

1. Auth as admin. Have >500 history rows (this box already had ~607).
2. `GET /MediaDash/History` — count = 500. First `Id=607`, last `Id=108`.
3. `GET /MediaDash/History?take=5` — count = 500.
4. `GET /MediaDash/History?take=2000` — count = 500.
5. `GET /MediaDash/History?take=10000` — count = 500.
6. `GET /MediaDash/History?limit=5` — count = 500.

**Expected**

Doc C.3: cap at ~1000 entries.

**Actual**

Hard cap of 500. Neither `?take=` nor `?limit=` are honoured — the server always returns the newest 500 rows regardless. Rows below Id=108 (the older ~107 entries) are unreachable via this endpoint. Sort order (03-C.2) is confirmed newest-first by `FixedAtUtc` descending, so the older rows are just truncated off the tail.

**Evidence**

- `docs/testing/evidence/F-049/history-full.json` (500-row response)
- `docs/testing/evidence/F-049/c9-unknown-id-body.txt` (03-C.9 ProblemDetails 404 — parked here since it's the same audit session)

**Suggested area (best guess, not required)**

MediaDashController History getter — hard-coded `Take(500)`. Either lift the cap to 1000 to match docs, or lower the doc, or accept `?take=` to make it queryable.

**Ambiguity flag**

n/a

### F-048 · `POST /MediaDash/Issues/Bulk` rejects `action=Revert` — doc B.16 wrong

- **Test ID**: 03-B.16
- **Chapter file**: 03-api.md
- **Component**: MediaDashController `Issues/Bulk` (BulkIssueRequest handler)
- **Severity**: medium
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 03-B audit

**Repro**

1. Auth as admin, plugin `38bdb090-b763-4294-934b-b54ade4d9d6d`, DryRun=true.
2. `POST /MediaDash/Reset` → `POST /MediaDash/Scan` → wait.
3. Grab any current issue Id from `GET /MediaDash/Issues`.
4. `POST /MediaDash/Issues/Bulk` with body `{"ids":[<id>],"action":"Revert"}`.

**Expected**

Per 03-B.16, `Revert` action should be handled (like `Approve` and `Dismiss`).

**Actual**

HTTP 400, body `"Action must be 'Approve' or 'Dismiss'."`. Bulk endpoint only
supports `Approve` and `Dismiss`. Single-issue `/Issues/{id}/Revert` DOES
exist, but the bulk variant doesn't.

Also confirmed: successful Bulk responses return a bare integer count (e.g.
`2`), NOT a JSON object with a `count` field, and NOT an `errors[]` array
even for mixed valid+invalid ids (B.18 body was `1`, not a shape with
per-id errors).

**Evidence**

text only — response body inline above.

**Suggested area (best guess, not required)**

Either extend the bulk handler to accept `Revert`, or fix the checklist
(B.16) and DTO doc to state Bulk supports only Approve/Dismiss and returns
a plain integer.

---

### F-047 · `IssueStatus` enum values differ from docs — actual: 0=Detected, 1=Queued, 3=Dismissed

- **Test ID**: 03-B.3, 03-B.6, 03-B.7
- **Chapter file**: 03-api.md
- **Component**: MediaDashController `/Issues` filter + Issue.Status field
- **Severity**: medium
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 03-B audit

**Repro**

1. Auth as admin, plugin, DryRun=true.
2. `POST /Reset`, `POST /Scan`, wait.
3. `GET /MediaDash/Issues?status=0..6` — probe each integer value.
4. `POST /Issues/{id}/Approve` on one, then `?status=1`; `POST /Issues/{id}/Dismiss` on another, then `?status=3`.

**Expected**

Checklist B.3 says `status=Open` (0 in enum) works, B.7 says status flips to
`Approved`.

**Actual**

- Fresh scan: all issues serialize `Status="Detected"`, matched by `?status=0`.
- After Approve: status becomes `"Queued"`, matched by `?status=1`. NOT
  "Approved".
- After Dismiss: status becomes `"Dismissed"`, matched by `?status=3`. Value 2
  unused in observed set.
- `?status=Open`, `?status=Detected`, `?status=Approved`, `?status=Bogus` ALL
  returned the full list (10) with no filter applied — server silently
  no-ops non-integer status values instead of 400.

So the doc's status vocabulary (`Open`, `Approved`) is wrong. Real
serialized names: `Detected`, `Queued`, `Dismissed` (and probably
`Fixed`/`Failed`/`Reverted` etc. at 2/4/5/6 — not exercised).

Also: default `GET /Issues` (no `status` param) returns ONLY status=0
(Detected). Queued/Dismissed rows are hidden by default. That's a
usability foot-gun — the client has to know to pass `?status=1` to see
what's been approved.

**Evidence**

`docs/testing/evidence/F-047/` — response bodies from status=Open,
status=Approved, status=Bogus (all returning full list).

**Suggested area (best guess, not required)**

Update 03-api.md B.3/B.6/B.7 to real enum. Consider making `/Issues` return
all statuses by default and rejecting unparseable status strings with 400
rather than silently ignoring them.

---

### F-046 · `GET /MediaDash/Issues?status=<string>` silently ignores unparseable values instead of 400

- **Test ID**: 03-B.3
- **Chapter file**: 03-api.md
- **Component**: MediaDashController `/Issues` query-string binding
- **Severity**: low
- **Category**: correctness
- **Observed**: 2026-08-28
- **Session**: 03-B audit

**Repro**

1. Auth, plugin, DryRun=true, Reset+Scan → 10 detected issues.
2. `GET /MediaDash/Issues?status=Open` → 200, count=10.
3. `GET /MediaDash/Issues?status=Approved` → 200, count=10.
4. `GET /MediaDash/Issues?status=Bogus` → 200, count=10.
5. `GET /MediaDash/Issues?status=99` → 200, count=0.

**Expected**

Either accept string enum names case-insensitively, or return 400 on
unparseable values. Silent no-op is worst-of-both.

**Actual**

Any non-integer status → whole filter dropped, returns the unfiltered set.
Integers work correctly (0/1/3 filter as expected, 99 returns 0 rows).

**Evidence**

`docs/testing/evidence/F-046/` — responses.

**Suggested area (best guess, not required)**

The action handler's query binding — probably `[FromQuery] int? status`
where a string like `Open` binds to null, which then skips filtering. Two
lazy fixes: bind to the enum type directly (auto-reject strings that don't
parse), or 400 when the raw querystring has `status=` but the parse failed.

---

### F-045 · `GET /MediaDash/Issues?libraryId=<guid>` is a silent no-op

- **Test ID**: 03-B.4
- **Chapter file**: 03-api.md
- **Component**: MediaDashController `/Issues` libraryId filter
- **Severity**: high
- **Category**: correctness
- **Observed**: 2026-08-28
- **Session**: 03-B audit

**Repro**

1. Auth, plugin, DryRun=true, Reset+Scan → 10 issues (all under Movies lib
   `0d877f7fb0c5ae6ce2adcf46d86a9beb`).
2. `GET /MediaDash/Issues?libraryId=0d877f7fb0c5ae6ce2adcf46d86a9beb` → 200, count=10.
3. `GET /MediaDash/Issues?libraryId=23654860b2373a85ab546517e33645ec` (Books
   lib, has no issues) → 200, count=10.
4. `GET /MediaDash/Issues?libraryId=00000000000000000000000000000000` → 200,
   count=10.

**Expected**

Filter to issues belonging to the given library (or return 0 for a lib
with no issues / bogus guid).

**Actual**

The `libraryId` querystring is completely ignored — all three requests
returned the full unfiltered set of 10.

**Evidence**

`docs/testing/evidence/F-045/` — response JSONs for movies-lib,
books-lib, all-zeros guid.

**Suggested area (best guess, not required)**

Same handler as F-044/F-046 — `/MediaDash/Issues`. Either the binding
isn't wired up, or the filter clause was removed. `IssueDto` has no
`libraryId`/`libraryName` field either (see F-043), so the server may
have no join to filter on. Likely a stub.

---

### F-044 · `GET /MediaDash/Issues?take=N&skip=M` pagination completely ignored

- **Test ID**: 03-B.5
- **Chapter file**: 03-api.md
- **Component**: MediaDashController `/Issues` pagination
- **Severity**: high
- **Category**: correctness
- **Observed**: 2026-08-28
- **Session**: 03-B audit

**Repro**

1. Auth, plugin, DryRun=true, Reset+Scan → 10 issues.
2. `GET /MediaDash/Issues?take=5` → 200, returned all 10 rows.
3. `GET /MediaDash/Issues?take=5&skip=5` → 200, returned the same 10 rows.
4. Compare Ids: page1 and page2 identical, overlap=10.

**Expected**

`take` limits to 5 rows; `skip=5` returns the next 5 (no overlap with
page 1).

**Actual**

Both parameters are no-ops. Client code assuming server-side pagination
will silently over-fetch and, if the client trusts `take`, mis-render or
duplicate.

Interacts badly with F-045 (no libraryId filter) and F-046 (status
string ignored) — the `/Issues` querystring surface is largely
non-functional for anything beyond `type` and integer `status`.

**Evidence**

`docs/testing/evidence/F-044/issues-take5.json`,
`docs/testing/evidence/F-044/issues-take5-skip5.json`.

**Suggested area (best guess, not required)**

`/MediaDash/Issues` handler. Ladder up from F-041/F-039 — many endpoints
returning drift/stub behaviour, suggests DTO+handler layer was refactored
without contract tests.

---

### F-043 · `IssueDto` actual shape differs radically from documented shape

- **Test ID**: 03-B.6
- **Chapter file**: 03-api.md
- **Component**: `IssueDto` (Api/) serialization
- **Severity**: medium
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 03-B audit

**Repro**

1. Auth, plugin, DryRun=true, Reset+Scan.
2. `GET /MediaDash/Issues` — inspect first row keys.

**Expected**

Per 03-api.md B.6: `id, type, path, libraryName, detected, status, metadata`
(camelCase, with a `metadata` dictionary).

**Actual**

Actual keys (PascalCase):
`Id, ItemId, Type, Path, FileName, SuggestedFix, DetailsJson, SizeSavings,
Status, DetectedAtUtc, WasPreviouslyRestored`.

Notable:

- Casing: PascalCase, not camelCase (contradicts what the doc's example
  implies — most JSON APIs I've seen from other Jellyfin plugins are also
  camelCase; worth checking whether this is by design or a missed
  serializer config).
- No `libraryName` — no way to know which library an issue belongs to
  from the DTO alone (relates to F-045 — library filter also broken).
- No generic `metadata` dictionary — instead `DetailsJson` (a JSON
  STRING, doubly-encoded — client has to `JSON.parse` it) and specialized
  fields like `SizeSavings`, `WasPreviouslyRestored`, `SuggestedFix`,
  `ItemId`, `FileName`.
- `detected` → `DetectedAtUtc` (renamed).
- Extra fields not documented at all: `ItemId`, `FileName`, `SuggestedFix`,
  `DetailsJson`, `SizeSavings`, `WasPreviouslyRestored`.

Sample row:

```json
{
  "Id": 7786,
  "ItemId": "8bbbdc7aa2df68637fb682451ad30c47",
  "Type": "Playability",
  "Path": "C:\\dev\\mediadash-fixtures\\movies\\Truncated Movie (2021)\\Truncated Movie (2021).mkv",
  "FileName": "Truncated Movie (2021).mkv",
  "SuggestedFix": "This file can't be played. Approve to remove it...",
  "DetailsJson": "{\"reason\":\"decode-error\",\"detail\":\"...\"}",
  "SizeSavings": 1160888,
  "Status": "Detected",
  "DetectedAtUtc": "2026-08-28T10:22:52.8143276Z",
  "WasPreviouslyRestored": false
}
```

**Evidence**

`docs/testing/evidence/F-043/issues-baseline.json`.

**Suggested area (best guess, not required)**

Update 03-api.md B.6 to real DTO. Same lineage as F-020/F-039/F-041 — DTO
docs are systematically stale across the plugin.

---

### F-042 · `POST /MediaDash/Schedule/Apply` accepts any body — invalid cron returns 204, breaking A.16

- **Test ID**: 03-A.16 (and A.15)
- **Chapter file**: 03-api.md
- **Component**: MediaDashController Schedule/Apply endpoint (unknown handler)
- **Severity**: medium
- **Category**: correctness
- **Observed**: 2026-08-28
- **Session**: 03-A audit

**Repro**

1. Auth as admin, build `$HAuth = @{ Authorization = $env:JFAUTH; "Content-Type" = "application/json" }`.
2. `Invoke-WebRequest -Method Post -Uri http://localhost:8099/MediaDash/Schedule/Apply -Headers $HAuth -Body '{"ScanSchedule":"not-a-cron","FixSchedule":"@@@@"}'`
3. Also try `-Body "{}"` (empty object).

**Expected**

A.16 says an invalid cron string → 400 with error body. A.15 implies `PluginConfiguration.ScanSchedule` is the payload field, and applying it should be reflected in `/Status.nextScheduledScanUtc`.

**Actual**

- Both the garbage cron body AND an empty `{}` return 204.
- The plugin configuration DTO does not appear to expose any `ScanSchedule` / `FixSchedule` / cron field at all (see F-039 evidence config JSON — no cron-shaped fields). A.15's premise (POST a schedule → see it in Status) is untestable because Status has no next-scheduled fields either.
- Endpoint silently discards any input; no validation, no error path.

**Evidence**

- `docs/testing/evidence/F-042/schedule-junk-body.json` — junk body sent
- Session console log shows `Schedule/Apply empty body: 204` and `Schedule/Apply garbage-cron: 204 (expected 400)`

**Suggested area (best guess, not required)**

`MediaDashController.ScheduleApply` handler — likely a no-op stub or reading no fields from the request body. Related to F-039 (Status lacks scheduling fields).

**Ambiguity flag**

A.15 doesn't specify the shape of the "updated PluginConfiguration schedule payload"; combined with the config DTO having no cron fields, the entire schedule feature may not be implemented in this build. Interpretation used: send the existing config JSON verbatim, then mutate a `ScanSchedule` field. Both were accepted with 204.

### F-041 · `HistoryDto` field names differ from doc 03-A.12 / 03-C.1 — `Type/Action/FileName/FixedAtUtc/WasDryRun/Success` not `issueType/result/path/timestampUtc/dryRun`

- **Test ID**: 03-A.12 (surfaced during Fix run), also 03-C.1
- **Chapter file**: 03-api.md
- **Component**: HistoryDto (Api/HistoryDto.cs presumably)
- **Severity**: medium
- **Category**: docs

**Repro**

1. Auth as admin, `GET http://localhost:8099/MediaDash/History`.
2. Inspect keys on the first row.

**Expected**

Doc 03-C.1 says HistoryDto keys are `id, timestampUtc, issueType, path, result, dryRun, bytesFreed, errorMessage`. A.12 additionally expects "nothingToDo=true" history entry when Fix is invoked with no approved issues.

**Actual**

Actual keys on `HistoryDto`: `Id, Type, FileName, Library, Action, BytesFreed, FixedAtUtc, WasDryRun, Success, CanRestore`. No `path`, no `errorMessage`, no `result`, no `timestampUtc`, no `dryRun` (they're renamed but the case-insensitive mapping does not save `path`/`result`/`errorMessage`). A.12 also produced NO history row for the "empty fix" — no `nothingToDo=true` marker exists.

**Evidence**

- `docs/testing/evidence/F-041/history-sample.json` — top 3 rows

**Suggested area (best guess, not required)**

`Api/HistoryDto.cs` field names, plus fix-run handler in the fix-executor path. Same class of drift as F-020.

**Ambiguity flag**

n/a

### F-040 · `POST /MediaDash/Scan/Suspicious` returns `{Detected, ElapsedMs}` — doc says `{count: N}`

- **Test ID**: 03-A.7
- **Chapter file**: 03-api.md
- **Component**: MediaDashController Scan/Suspicious endpoint
- **Severity**: low
- **Category**: docs

**Repro**

1. Auth as admin.
2. `Invoke-RestMethod -Method Post -Uri http://localhost:8099/MediaDash/Scan/Suspicious -Headers $HAuth -Body ""`

**Expected**

Doc A.7: `{ count: N }`.

**Actual**

Returns `{ "Detected": 0, "ElapsedMs": 1 }` (PascalCase; `Detected` not `count`; extra `ElapsedMs` field). A.8 sync-under-2s claim passes cleanly (1–2 ms).

**Evidence**

- `docs/testing/evidence/F-040/suspicious-response.json`

**Suggested area (best guess, not required)**

DTO class returned by `MediaDashController.ScanSuspicious`. Docs mismatch, not a code bug.

**Ambiguity flag**

n/a

### F-039 · `StatusResponse` DTO field names entirely wrong in 03-A.1 — no `scanRunning`, `queueLength`, `configVersion`, `dryRun`, `nextScheduledScanUtc` etc.

- **Test ID**: 03-A.1 (and cascades into A.2, A.3, A.15)
- **Chapter file**: 03-api.md
- **Component**: Api/StatusResponse DTO
- **Severity**: medium
- **Category**: docs

**Repro**

1. Auth as admin.
2. `Invoke-RestMethod -Uri http://localhost:8099/MediaDash/Status -Headers @{Authorization=$env:JFAUTH}` — inspect keys.

**Expected**

Doc A.1: keys `scanRunning, fixRunning, lastScanUtc, lastFixUtc, nextScheduledScanUtc, nextScheduledFixUtc, queueLength, configVersion, dryRun`.

**Actual**

Actual keys: `IsScanning, IsFixing, OpenIssueTotal, FailedHistoryTotal, FreeDiskBytes, TotalDiskBytes, TotalPotentialSavings, LifetimeBytesReclaimed, LifetimeCounts, Counts, PendingFixCount, Drives, System, RecycleBinPath, DataDirectory, RecycleBinCrossVolume, RecycleBinBytes, RecycleBinFileCount, RecycleBinRetentionDays, LastFixRun, RedownloadWarnings`.

- `scanRunning` → `IsScanning` (rename)
- `fixRunning` → `IsFixing` (rename)
- `lastScanUtc`, `lastFixUtc` — NOT PRESENT (only `LastFixRun.FinishedAtUtc`)
- `nextScheduledScanUtc`, `nextScheduledFixUtc` — NOT PRESENT (schedule feature seemingly not exposed via Status)
- `queueLength` — NOT PRESENT (closest is `PendingFixCount`, unclear semantics; observed staying at 2 after a Fix supposedly completed)
- `configVersion` — NOT PRESENT
- `dryRun` — NOT PRESENT (DryRun toggle known to be readable only via Plugin Configuration endpoint)

Because keys are wrong, A.2 (types) can only be partially validated: the actually-returned fields have sensible types (bools, ints, ISO8601 strings on nested `LastFixRun.FinishedAtUtc`).

A.3 passes on the renamed key `IsScanning=True`. A.15 becomes untestable at this endpoint (no `nextScheduledScanUtc` to verify against).

**Evidence**

- `docs/testing/evidence/F-039/status-A1.json` — full response
- `docs/testing/evidence/F-039/status-final.json` — repeat capture at end of session

**Suggested area (best guess, not required)**

`Api/StatusResponse.cs`. Same class of drift as F-020: the doc chapter was written to a spec that no longer matches the DTO. Either the DTO was renamed/reduced or the doc was written against a future/planned surface.

**Ambiguity flag**

n/a — the discrepancy is unambiguous. Recommend rewriting 03-A.1/A.2 against the current DTO (and adding a separate finding-worthy doc item if scheduling+dryRun should be surfaced on `/Status`).

### F-038 · `Issue.Id` is not stable across rescans — back-to-back scans replace almost every row, R.6 identity-across-rename claim not verifiable at row-ID level

- **Test ID**: 01-R.6
- **Chapter file**: 01-scanners.md
- **Component**: MediaDash scan orchestration / issue store (issue row identity)
- **Severity**: medium
- **Category**: correctness + docs
- **Observed**: 2026-08-28
- **Session**: 01-R Helpers audit

**Repro**

1. Auth, safety-flip config to `DryRun=true` (already true on this box; snapshot at `%TEMP%\cfg-orig-01R.json`).
2. `POST /MediaDash/Reset`; `POST /MediaDash/Scan`; wait for `scanRunning=false`.
3. Snapshot issues: `GET /MediaDash/Issues` → `run1` (10 rows on this box).
4. Without resetting, `POST /MediaDash/Scan` again; wait; snapshot → `run2` (11 rows).
5. Compare `run1.Id` set vs `run2.Id` set.

**Expected**

R.6 in `01-scanners.md` reads: "existing issues stay attached to same library (identity is by ID not name)". A minimal reading of that claim is that a scan that re-detects the same problem on the same path should reuse (or at least survive across) the same `Issue.Id`. If the store simply replaces every row on each scan, "identity by ID" is not a property clients can rely on.

**Actual**

Back-to-back scans of the same library (no rename, no fixture change, DryRun on) share only **1** ID between the two runs. 9 IDs from run 1 are gone in run 2; 10 new IDs appear. Confirmed independently in the R.6 rename cycle where the pre-rename/post-rename comparison showed 3 IDs dropped and 1 ID new even before accounting for the rename itself.

Path/type continuity across the rename cycle held for 7 of 9 distinct (path, type) tuples. The two lost tuples were `OrphanedDebris` on `HI Test (2024)\reg.srt` and `sdh.srt`; both re-appear on the next scan after the rename is reverted, so the loss was a transient Jellyfin re-association during the rename cycle, not a MediaDash bug.

Rename endpoint used: `POST /Library/VirtualFolders/Name?name=MediaDash%20Test&newName=MediaDash%20Test%20Renamed` → 204. Restored to original name at end of block.

**Evidence**

- `evidence/F-038/r6-before-rename.csv` — 10 rows, IDs 7707–7716 range.
- `evidence/F-038/r6-after-rename.csv` — 8 rows after rescan-after-rename.
- `evidence/F-038/notes.txt` — control-run counts and endpoint details.

**Suggested area (best guess, not required)**

The scan pipeline or issue store — likely `ScanTask` / issue persistence — appears to insert new rows per scan rather than upserting keyed on (path, type). A stable key like `(libraryId, path, type)` would let clients (and R.6's assertion) actually rely on the identity claim.

**Ambiguity flag**

R.6 says "identity is by ID not name". Two reasonable readings: (a) issue row ID stable across scans, or (b) library ID stable across rename so path-attached issues survive. Reading (a) fails per this observation. Reading (b) holds for most path/type tuples. Docs should clarify which is intended.

---

### F-037 · StaleContentScanner has no favourites-exclusion — L.8 assumption unreachable, favouriting a stale item does not skip it

- **Test ID**: 01-L.8
- **Chapter file**: 01-scanners.md
- **Component**: StaleContentScanner + PluginConfiguration (no `ExcludeFavourites` / equivalent field) + doc
- **Severity**: low
- **Category**: docs + correctness
- **Observed**: 2026-08-28
- **Session**: 01-L StaleContentScanner audit

**Repro**

1. Session prep per `01-scanners.md`; DryRun already ON.
2. Live plugin config lists five Stale-touching fields: `StaleFixMode`,
   `StaleThresholdDays`, `StaleExcludedLibraryIds`, `StaleExcludedGenres`,
   `RecycleBinWarnThresholdGb`. Grepping the full config for
   `Favou?rite` returns no matches. `StaleExcludedLibraryIds` and
   `StaleExcludedGenres` are the only exclusion knobs on offer.
3. Set `StaleThresholdDays=30`, `StaleFixMode=DetectOnly`, rescan →
   Clean Movie (2024) is in the Stale list (added 33 days ago).
4. Favourite Clean Movie for the admin test user:
   `POST /Users/{userId}/FavoriteItems/{itemId}` → 200, IsFavorite=true.
5. `POST /MediaDash/Reset` + `POST /MediaDash/Scan`; wait until done;
   `GET /MediaDash/Issues?type=Stale`.

**Expected**

Per L.8: `ExcludeFavourites = true` (or equivalent) → favourite items
skipped even if otherwise stale.

**Actual**

There is no toggle. Count remains **8**, and Clean Movie is still flagged
(same `SizeSavings=2372373`, same `DetailsJson`). The scanner does not
consult per-user favourite state at all.

**Evidence**

- `docs/testing/evidence/F-037/` (created, empty — inline sufficient)
- Inline:
  ```
  Favourite POST → {"IsFavorite":true, ...} HTTP=200
  Rescan → Stale count: 8
  Clean Movie in Stale? True
  ```
- Config field list at time of test:
  ```
  StaleFixMode=DetectOnly
  StaleThresholdDays=365 (test-set to 30 during run)
  StaleExcludedLibraryIds={}
  StaleExcludedGenres={}
  RecycleBinWarnThresholdGb=10
  ```
  No `ExcludeFavourites`, `SkipFavourites`, `HonourFavourites`,
  `StaleIncludeFavourites` — grepped all cases, none exist.

**Suggested area (best guess, not required)**

`Jellyfin.Plugin.MediaDash/Scanners/StaleContentScanner.cs` (missing check
against `IUserDataManager.GetUserData(...).IsFavorite`), and
`Jellyfin.Plugin.MediaDash/Configuration/PluginConfiguration.cs` (missing
`StaleExcludeFavourites` bool). Doc L.8 either predates the feature or the
feature was never landed.

**Ambiguity flag**

Doc L.8 uses `ExcludeFavourites` as if the setting exists. Interpreted as
"if such a setting is present, verify it"; documented outcome is "it is
not present, therefore the test is unreachable and the scanner is
favourites-blind". Fix requires either implementing the toggle or
removing L.8.

### F-036 · StaleContentScanner uses Jellyfin item `DateCreated` (import date), not filesystem `LastWriteTime` — doc L.2 recipe is wrong

- **Test ID**: 01-L.2, 01-L.5
- **Chapter file**: 01-scanners.md
- **Component**: StaleContentScanner (`DetailsJson.addedUtc` / `daysUnwatched`) + doc
- **Severity**: medium
- **Category**: correctness + docs
- **Observed**: 2026-08-28
- **Session**: 01-L StaleContentScanner audit

**Repro**

1. Session prep per `01-scanners.md`; DryRun ON.
2. Set `StaleThresholdDays=30`, `StaleFixMode=DetectOnly`.
3. Pick an indexed movie (Clean Movie (2024), itemId
   `2b597f72290376cd326c6df31a965a4a`). Save its original
   `LastWriteTime` (`2026-07-26 15:06:59`) then set it to
   `[datetime]"2019-01-01"` — filesystem stamp is now ~7 years old.
4. `POST /MediaDash/Reset` + `POST /MediaDash/Scan` + wait.
5. `GET /MediaDash/Issues?type=Stale`.

**Expected**

Per L.2 the recipe says: "File on disk with mtime > 60 days ago, never
played." The recipe promises the mtime edit will drive detection — L.5
then expects that specific file to appear.

**Actual**

Clean Movie IS flagged, but the `DetailsJson` proves the mtime change had
zero effect:

```json
{
  "daysUnwatched": 33,
  "neverPlayed": true,
  "lastPlayedUtc": null,
  "addedUtc": "2026-07-26T07:06:58.1419314Z",
  "thresholdDays": 30
}
```

`daysUnwatched=33` matches `today - addedUtc`, where `addedUtc` is the
Jellyfin item `DateCreated` (library import time), not `LastWriteTime`.
Filesystem stamp at scan time was `2019-01-01` — if the scanner were
using mtime, `daysUnwatched` would be ~2795, not 33. Additionally, the
recipe's "L.5 count = 1" assumption is falsified — the scanner walks
the entire library and returns **9 items** (every non-played fixture
older than 30 days across movies, music, books, audiobooks, comics),
because the age signal is per-item DateCreated, and everything in
`$env:LIB` was imported in the same July 26–28 window.

**Evidence**

- `docs/testing/evidence/F-036/stale-issues-mtime2019.json` — full 9-item
  scan response captured with Clean Movie's mtime forced to 2019-01-01.
  Every entry's `addedUtc` reflects Jellyfin import date; every
  `daysUnwatched` matches `today - addedUtc`.
- Inline diff:
  ```
  Filesystem LastWriteTime at scan: 01/01/2019 00:00:00
  DetailsJson.addedUtc:             2026-07-26T07:06:58Z
  daysUnwatched:                    33   (would be ~2795 if mtime-based)
  ```

**Suggested area (best guess, not required)**

The scanner is sourcing "age" from `BaseItem.DateCreated` (Jellyfin's
import timestamp). If the intent was "file has been in the library
unused for N days", that is arguably correct — but doc L.2 describes it
as a filesystem-mtime check, which it is not. Two fixes are plausible:
(a) revise doc L.2 to seed via `POST /Items/{id}?DateCreated=...` or by
manipulating Jellyfin's DB directly, and drop the mtime bullet; or (b) if
mtime is the intended signal, change the scanner to consult
`FileInfo.LastWriteTimeUtc` and update `addedUtc` field name accordingly.
Also note the field is `DetailsJson.lastPlayedUtc`, not the doc's
`metadata.lastPlayed` (F-020 pattern — thin schema drift).

**Ambiguity flag**

Doc L.6 says `metadata.lastPlayed` is `null`; actual field is
`DetailsJson.lastPlayedUtc`. Interpretation applied: report both the
name and the correct null-ness. `lastPlayedUtc: null` when `neverPlayed`
is true, populated otherwise.

### F-035 · EmbeddedCoverArtScanner untestable — F-019 blocks music-library fixture indexing, and scanner semantics don't match doc D.4

- **Test ID**: 01-scanners.md §D.1–D.5
- **Chapter file**: 01-scanners.md
- **Component**: EmbeddedCoverArtScanner + FolderMetadataService (Jellyfin core, F-019 recurrence) + doc
- **Severity**: medium
- **Category**: env + docs
- **Observed**: 2026-08-28
- **Session**: 01-D EmbeddedCoverArtScanner audit

**Repro**

1. Session prep per `01-scanners.md`, DryRun flipped ON (was already ON).
2. `ffprobe` the three pre-existing music files under
   `$env:LIB\music\Test Artist\Test Album\` (2 mp3 + 1 flac). None carry an
   APIC frame (`DISPOSITION:attached_pic=0`, no `video` stream). So the
   baseline scan's `0 issues` was correct — nothing on this box has embedded
   art to detect.
3. Seed three fixture album folders under `$env:LIB\music\` using the recipe
   in the audit brief:
   - `FixtureArtist\FixtureAlbum\` — 3 mp3s each with an APIC frame
     (`-map 1:v -disposition:v attached_pic`), no `cover.jpg`. Verified via
     ffprobe: `codec_type=video, codec_name=mjpeg,
     DISPOSITION:attached_pic=1`.
   - `FixtureArtistCtrl1\FixtureAlbumCtrl1\` — same 3 mp3s + `cover.jpg`.
   - `FixtureArtistCtrl2\FixtureAlbumCtrl2\` — 3 plain mp3s, no APIC.
4. `POST /Library/Refresh`; wait 30s.
5. `GET /Items?IncludeItemTypes=MusicAlbum&Recursive=true` returns
   `TotalRecordCount=1` — only the pre-existing `Test Album`. None of my
   three fixture albums are indexed.
6. `POST /MediaDash/Reset` + `POST /MediaDash/Scan`; poll until
   `IsScanning=false`.
7. `GET /MediaDash/Issues?type=EmbeddedCoverArt` → `[]`.
8. Log line: `EmbeddedCoverArtScanner: 0 folder(s) with duplicated
   embedded artwork.` — note phrasing.

**Expected**

- D.4: EmbeddedCoverArt issues == 1 (FixtureAlbum only).
- D.5: `metadata.embeddedCount == 3`.

**Actual**

- D.4: 0 issues. D.5: unverifiable.
- Two overlapping root causes:
  - **F-019 recurrence.** `log_20260828.log` shows repeated
    `Microsoft.Data.Sqlite.SqliteException (0x80004005): SQLite Error 19:
    'FOREIGN KEY constraint failed'` bursts from
    `MediaBrowser.Providers.Folders.FolderMetadataService` and
    `Jellyfin.Database.Implementations.JellyfinDbContext` during the library
    scan, and `Scan Media Library` Failed after 0 min 0 s. New music
    fixture folders never become Jellyfin items, so any item-scoped
    walk sees nothing.
  - **Scanner semantics don't match doc D.4.** The log line says
    `EmbeddedCoverArtScanner: N folder(s) with duplicated embedded
    artwork.` — the scanner's own phrasing is about *duplicated*
    artwork (per-file art AND a folder-level cover, i.e. redundancy),
    not "embedded present + no folder cover". The doc's D.4 rule
    ("scanner likely fires when any audio file has embedded art AND
    there's no `cover.jpg`") is the inverse of what the scanner
    actually detects. Even with a working item cache, seeding a bare
    album without `cover.jpg` (D.1) would NOT be a positive case for
    this scanner — D.2 (embedded + `cover.jpg` present) would be.
    D.3 (no embedded art at all) is a negative for both.
- D.5's expected field name `metadata.embeddedCount` cannot be verified —
  no issue rows to inspect. Continues the F-020 docs-drift pattern
  (issues expose `DetailsJson`, doc keeps writing `metadata.*`).
- D.6–D.8 blocked by the same F-019 + doc-semantic mismatch and not
  attempted.

**Evidence**

`docs/testing/evidence/F-035/`:

- `albums-after-refresh.json` — `TotalRecordCount=1`, only `Test Album`.
- `issues-EmbeddedCoverArt.json` — `[]`.
- `mediadash-status-after-scan.json` — full status; `Counts` has no
  `EmbeddedCoverArt` entry.
- `d1-track01-ffprobe.txt` — APIC verified on fixture (`video/mjpeg`,
  `DISPOSITION:attached_pic=1`).
- `preexisting-testalbum-ffprobe.txt` — no APIC on pre-existing files.
- `log-excerpt.txt` — EmbeddedCoverArtScanner "duplicated embedded
  artwork" lines + FOREIGN KEY bursts.

**Suggested area (best guess, not required)**

Two separate things:

1. `Jellyfin.Plugin.MediaDash/Scanners/EmbeddedCoverArtScanner.cs` — the
   log phrasing suggests the scanner detects duplicated art
   (per-file + folder), not the missing-folder-art case the doc
   describes. Either the scanner's intent is wrong, the doc's is
   wrong, or the scanner name is misleading. Read the source to
   arbitrate; the doc's D.1/D.2 fixture labels probably need to
   swap ("positive" and "control" are inverted vs. what the scanner
   actually does) once the semantics are pinned.
2. F-019 recurrence — same Jellyfin core issue that has blocked
   most new-fixture blocks in this chapter (see F-019/F-026/F-027).
   Not a MediaDash defect.

**Ambiguity flag**

Doc D.4's "should fire on 1 folder (FixtureAlbum)" contradicts the
scanner's own log phrasing. Ran the fixtures anyway to establish
either (a) if F-019 hadn't bitten, D.1/D.2 would swap roles, or
(b) the scanner detects both classes (missing folder art AND
duplicated). Could not disambiguate on this box because item cache
never populated the fixtures.

### F-034 · SubtitleLanguage fixture `Sub Heavy (2023).mkv` has only one eng track — recipe promised eng+rus+fre

- **Test ID**: 01-scanners.md §N.2
- **Chapter file**: 01-scanners.md
- **Component**: fixture recipe (`make-fixtures.sh` / equivalent) + docs
- **Severity**: medium
- **Category**: env
- **Observed**: 2026-08-28
- **Session**: 01-N SubtitleLanguageScanner audit

**Repro**

1. `ffprobe C:\dev\mediadash-fixtures\movies\Sub Heavy (2023)\Sub Heavy (2023).mkv` (streams only).
2. Compare against §N.2 spec ("File with eng + rus + fre embedded subs").

**Expected**

Sub Heavy has three embedded subtitle tracks: eng, rus, fre. This is the only positive fixture for the SubtitleLanguage scanner in the whole test bed.

**Actual**

File has exactly one subtitle track: `codec=subrip lang=eng`. No rus, no fre. Baseline scan with `AllowedSubtitleLanguages=[eng]` therefore yields `SubtitleLanguage found 0 issues`, and §N.4 cannot pass without remuxing the file first. To complete the audit I remuxed Sub Heavy in-place with eng+rus+fra tracks, ran the checks, then restored the original from backup. The stock fixture is broken for its stated purpose.

Also missing: §N.3's control file "with only eng subs". No dedicated control fixture exists; Sub Heavy in its current (broken) state accidentally satisfies that role instead.

**Evidence**

- `evidence/F-034/repro.txt` — probe output and reasoning.
- Live ffprobe: streams `0:video(h264)`, `1:audio(aac,eng)`, `2:subtitle(subrip,eng)`.

**Suggested area (best guess, not required)**

Fixture generator (make-fixtures.sh recipe for "Sub Heavy"). The recipe either never included the rus/fre tracks or a subsequent rebuild dropped them.

**Ambiguity flag**

n/a.

---

### F-033 · SubtitleLanguage scanner cannot be disabled — `SubtitleFixMode=DetectOnly` still emits issues, contradicting N.6

- **Test ID**: 01-scanners.md §N.6
- **Chapter file**: 01-scanners.md
- **Component**: SubtitleLanguageScanner
- **Severity**: medium
- **Category**: correctness (or docs, if intended)
- **Observed**: 2026-08-28
- **Session**: 01-N SubtitleLanguageScanner audit

**Repro**

1. With Sub Heavy remuxed to hold eng+rus+fra subs (see F-034 for why this was necessary), set `AllowedSubtitleLanguages=["eng"]`, `SubtitleFixMode=Automatic`. Scan.
2. `GET /MediaDash/Issues?type=SubtitleLanguage` → count = 1 (Sub Heavy).
3. `POST /Plugins/{guid}/Configuration` with `SubtitleFixMode="DetectOnly"`. Scan.
4. `GET /MediaDash/Issues?type=SubtitleLanguage` → count = 1 (unchanged).
5. Restore `SubtitleFixMode=Automatic`, set `AllowedSubtitleLanguages=["eng","rus","fra"]`. Scan.
6. `GET /MediaDash/Issues?type=SubtitleLanguage` → count = 0.

**Expected**

§N.6 reads "Setting disabled → zero flags." The checklist plus §01-N intro ("when `RemoveUnwantedSubs` is enabled") assume a single toggle that turns detection off.

**Actual**

No such toggle exists. `SubtitleFixMode` governs whether the fixer runs, not whether the scanner scans — `DetectOnly` still emits SubtitleLanguage issues. The only way to silence detection is to widen `AllowedSubtitleLanguages` to cover every language present in the file. There is no `RemoveUnwantedSubs` field on the config object (confirmed against a fresh Configuration GET — see F-032 for the full field list around the subtitle knobs).

Either the scanner should honour `SubtitleFixMode=DetectOnly` as "do not detect either" (matches other scanners' intuition, unlikely), the docs should be rewritten to say "SubtitleLanguage detection is always on; auto-remove is gated by SubtitleFixMode" (most likely correct behaviour), or an actual on/off field should be added.

**Evidence**

- `evidence/F-033/repro.txt` — step-by-step config toggles and resulting counts.

**Suggested area (best guess, not required)**

`SubtitleLanguageScanner` (guard on `SubtitleFixMode`, or the doc for §N.6 / §01-N intro).

**Ambiguity flag**

The checklist's use of the term `RemoveUnwantedSubs` (which does not exist on the live config) means multiple defensible interpretations of "disabled" are possible. I interpreted "disabled" as `SubtitleFixMode=DetectOnly` first, then as "no unwanted languages present" (widening `AllowedSubtitleLanguages`). Only the latter yields zero flags.

---

### F-032 · SubtitleLanguage issue `DetailsJson` uses `removeIndexes` / `languages`, not the documented `unwantedTracks` field (N.5)

- **Test ID**: 01-scanners.md §N.5
- **Chapter file**: 01-scanners.md
- **Component**: SubtitleLanguageScanner (issue emit shape)
- **Severity**: low
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 01-N SubtitleLanguageScanner audit

**Repro**

1. With Sub Heavy holding eng+rus+fra subs (see F-034), `AllowedSubtitleLanguages=["eng"]`, `SubtitleFixMode=Automatic`, scan.
2. `GET /MediaDash/Issues?type=SubtitleLanguage` → 1 issue.
3. Inspect `DetailsJson`.

**Expected**

§N.5 says `Issue metadata.unwantedTracks = 2`.

**Actual**

`DetailsJson = {"removeIndexes":[3,4],"externalFiles":[],"languages":["rus","fra"]}`. There is no `unwantedTracks` field. The "2" count in the checklist maps onto either `removeIndexes.Length` or `languages.Length`, both = 2 here.

Also worth noting: `SuggestedFix` field carries a nice human-readable line ("Remove 2 embedded subtitle track(s) in rus, fra.") that the checklist does not mention.

**Evidence**

- `evidence/F-032/issue-shape.json` — the raw issue as returned by the API.

**Suggested area (best guess, not required)**

Docs (`01-scanners.md` §N.5) — realign to the emitted field names.

**Ambiguity flag**

n/a.

---

### F-031 · QualityScanner DetailsJson always reports `videoBitrate:0`, so K.6 metadata-shape check (actual bitrate) fails for every issue

- **Test ID**: 01-scanners.md §K.6
- **Chapter file**: 01-scanners.md
- **Component**: QualityScanner

**Category**: correctness
**Severity**: medium

**Environment**
Jellyfin 10.11.11 / MediaDash plugin GUID `38bdb090-b763-4294-934b-b54ade4d9d6d` / library `C:\dev\mediadash-fixtures` / DryRun=True throughout.

**Steps to reproduce**
1. Auth → GET plugin Configuration; confirm quality fields `MaxResolutionHeight`, `MaxBitrateMbpsAt1080p`, `QualityTolerancePercent`, `SkipHdrContent`, `QualityScanAudiobooks`.
2. Set an aggressive ceiling that guarantees flags (`MaxResolutionHeight=480`, `MaxBitrateMbpsAt1080p=1`, `QualityTolerancePercent=0`, `SkipHdrContent=false`).
3. `POST /MediaDash/Reset`; `POST /MediaDash/Scan`; poll `Status.IsScanning` → false.
4. `GET /MediaDash/Issues?type=Quality` → 7 issues returned (all fixture movies).
5. Inspect `DetailsJson` on every issue.

**Expected**
K.6 says "Issue metadata includes actual resolution & bitrate." Each issue's `DetailsJson.videoBitrate` should be the file's real bitrate (Jellyfin `MediaStream.BitRate` for the video track, or an ffprobe fallback).

**Actual**
Every one of the 7 Quality issues carries `videoBitrate:0` in `DetailsJson`. Example (Big Buck 2160p, `hevc 1920×1080`, ffprobe measured 2.88 Mbps):
`{"width":1920,"height":1080,"codec":"hevc","videoBitrate":0,"allowedBitrate":197530,"maxHeight":480,"targetCodec":"hevc"}`
`allowedBitrate` is present (in raw bps, not Mbps as the config field uses) but the "actual" side is always zero, so the metadata is unusable for downstream UI or fix reasoning.

Note also the `DetailsJson` schema shape differs from what a reader of K.6 might expect: keys are `width`/`height`/`codec`/`videoBitrate`/`allowedBitrate`/`maxHeight`/`targetCodec` — no `bitrate`/`actualBitrate`/`resolutionHeight` etc.

**Probable cause**
Scanner reads `MediaStream.BitRate` off the video stream and doesn't fall back when Jellyfin has not populated it. For every fixture item in this library Jellyfin's MediaStreams row has BitRate null/0, and no ffprobe fallback runs.

**Suggested fix location**
`QualityScanner` (wherever `DetailsJson` is assembled). Either (a) run an ffprobe fallback when `MediaStream.BitRate` is missing, or (b) compute file-size / duration as a coarse estimate when both stream bitrate and format bitrate are absent.

**Evidence**
- `evidence/F-031/sample-DetailsJson.json` — one issue's `DetailsJson` beside the ffprobe-measured actual bitrate.

**Ambiguity flag**
n/a

---

### F-030 · QualityScanner treats ceiling=0 as "flag everything" rather than "disabled", contradicting K.9

- **Test ID**: 01-scanners.md §K.9
- **Chapter file**: 01-scanners.md
- **Component**: QualityScanner (config-consumer path)

**Category**: correctness
**Severity**: medium

**Environment**
Jellyfin 10.11.11 / MediaDash plugin GUID `38bdb090-b763-4294-934b-b54ade4d9d6d` / library `C:\dev\mediadash-fixtures` (7 indexed movies) / DryRun=True throughout.

**Steps to reproduce**
1. Auth as `test:test`.
2. `POST /Plugins/{guid}/Configuration` with `MaxResolutionHeight=0`, `MaxBitrateMbpsAt1080p=0` (the natural "disabled" sentinel).
3. `POST /MediaDash/Reset` → `POST /MediaDash/Scan` → poll until `IsScanning=false`.
4. `GET /MediaDash/Issues?type=Quality`.

**Expected**
K.9 requires "Ceiling disabled → zero flags even with the 4K file." Setting both ceiling values to 0 (the obvious "off" sentinel used elsewhere in this plugin config, e.g. `MinScanFileSizeMb=0`) should produce 0 Quality issues.

**Actual**
7 Quality issues flagged — every fixture movie in the library, including 720p files at ~1 Mbps. Effectively the scanner interprets `MaxResolutionHeight=0` as "the ceiling is zero pixels, so every video violates it" and `MaxBitrateMbpsAt1080p=0` similarly. Only setting both fields to an absurdly large value (`100000`) actually suppresses flagging (confirmed: `Issues?type=Quality` → count=0).

**Probable cause**
Ceiling comparison lacks a `<=0 → skip check` short-circuit. Either the field needs a boolean companion (`QualityCeilingEnabled`) or `0` needs to be treated as "disabled" in the scanner's threshold logic.

**Suggested fix location**
`QualityScanner` — the threshold-evaluation branch that reads `MaxResolutionHeight` / `MaxBitrateMbpsAt1080p`. Alternatively `PluginConfiguration` validators could clamp/interpret 0 as "unbounded".

**Evidence**
- `evidence/F-030/cfg-k9-zero.json` — POSTed configuration (both ceilings = 0) that produced 7 flags.
- `evidence/F-030/cfg-k9-high.json` — POSTed configuration (both ceilings = 100000) that produced 0 flags.
- `evidence/F-030/summary.json` — counts + settings.

**Ambiguity flag**
K.9 says "Ceiling disabled" but the config surface has no explicit boolean; interpretation ran was "0 = disabled". If the intended off-switch is instead a very large value, K.9's wording and/or the UI needs to say so.

---

### F-029 · DuplicateScanner does not flag two Movie items with identical name+year that share a media path — F-018 double-item pair (Big Buck 1080p) invisible to Duplicate detection

- **Test ID**: 01-scanners.md §C.4 / §C.7 (F-018 cross-check listed in this session's prompt)
- **Chapter file**: 01-scanners.md
- **Component**: `Jellyfin.Plugin.MediaDash.Scanners.DuplicateScanner` + `DuplicateSignals`
- **Severity**: high
- **Category**: correctness
- **Observed**: 2026-08-28
- **Session**: 2026-08-28 · 01-C audit (fresh QA)

**Repro**

1. Auth, verify current library state — Jellyfin holds two Movie items whose signatures overlap heavily:
   - `e6557a69351eecdd723615d82b174f45` — `Name="Big Buck Test (2020)"`, `Type=Movie`, `Year=2020`, `Path=…\Big Buck Test (2020) - 2160p.mkv`, `MediaSources=1`.
   - `7ee93d08ff649a9d1e258f09a92632dd` — `Name="Big Buck Test"`, `Type=Movie`, `Year=2020`, `Path=…\Big Buck Test (2020) - 1080p.mkv`, `MediaSources=2` (contains **both** the 1080p and the 2160p path — so it *shares a source path* with the other item).
2. `POST /MediaDash/Reset`; `POST /MediaDash/Scan`; poll `Status.IsScanning` until false.
3. `GET /MediaDash/Issues?type=Duplicate` → `[]`.
4. Log: `MediaDash scanner Duplicate found 0 issues` (evidence: `log-duplicate-found-0.txt`).
5. Cross-check: `GET /MediaDash/Issues?type=MissingSubtitles` emits the Big Buck 1080p path **twice** with two different `ItemId`s — proving both items exist and both index that path (evidence: `missing-subs-shows-double-item.json`).

**Expected**

At least one Duplicate group covering the Big Buck pair. The signals available are all matching or near-matching: identical `Year=2020`, both `Type=Movie`, `Name` differs only by trailing " (2020)" (which `DuplicateSignals` should normalize), same parent folder, and one item's `MediaSources` literally contains the other item's `Path`. A signal-based grouper should collapse these.

**Actual**

Zero Duplicate groups. Scanner does not surface the two-item-same-source-path case at all, so a duplicate that Jellyfin itself introduced (two `BaseItem` rows for the same on-disk file) is completely invisible in MediaDash. This defeats the scanner's stated purpose for the most concrete duplicate class (byte-identical shared source paths).

Also confirms the §C.1 fixture recipe (two separate parent folders) is unreachable on this box for a different reason — F-019: Jellyfin's `FolderMetadataService` throws `SQLite Error 19: FOREIGN KEY constraint failed` during the folder-metadata pass, so neither `Inception (2010)` nor `Inception (2010) 4K` becomes a Movie item after `POST /Library/Refresh` + 25 s wait. Duplicate detection can therefore be exercised only via items that already exist (the Big Buck pair) — and those aren't detected either.

**Evidence**

- `docs/testing/evidence/F-029/item-e6557a69.json` — item 1 (2160p, 1 MediaSource)
- `docs/testing/evidence/F-029/item-7ee93d08.json` — item 2 (1080p, 2 MediaSources including 2160p)
- `docs/testing/evidence/F-029/duplicate-issues-empty.json` — response body `[]`
- `docs/testing/evidence/F-029/missing-subs-shows-double-item.json` — Big Buck 1080p path emitted twice by MissingSubtitles with distinct ItemIds
- `docs/testing/evidence/F-029/log-duplicate-found-0.txt` — `MediaDash scanner Duplicate found 0 issues`

**Suggested area (best guess, not required)**

`DuplicateScanner` grouping predicate — likely groups only by primary `Path` equality rather than by any of (a) `MediaSources[*].Path` intersection, (b) name+year signal after suffix normalization, or (c) same-folder + same-year heuristic. Any of the three would have caught this pair.

**Ambiguity flag**

The doc's §C.4 expects fresh-seeded fixtures (`Inception [1080p]` vs `[4K]`) to produce a group, and the F-018 background note recommends the two-folder rewrite. Neither route is exercisable on this dev box (F-019 blocks both). This finding is scoped to the item pair that *does* exist and is a genuine duplicate — the scanner missing that case is a real bug, not a fixture artifact.

### F-028 · Plugin Configuration endpoint silently accepts out-of-range numeric enum values for `MediaSortSource`; doc lists three source names but only two exist

- **Test ID**: 01-scanners.md §F.6
- **Chapter file**: 01-scanners.md
- **Component**: `Jellyfin.Plugin.MediaDash.Configuration.PluginConfiguration.MediaSortSource` (+ its enum) and the `POST /Plugins/{guid}/Configuration` handler
- **Severity**: medium
- **Category**: correctness (+ docs)
- **Observed**: 2026-08-28
- **Session**: 2026-08-28 · 01-F audit (fresh QA)

**Repro**

1. Auth, GET `/Plugins/38bdb090-b763-4294-934b-b54ade4d9d6d/Configuration`. Note default `MediaSortSource=JellyfinMetadata`.
2. Try setting `MediaSortSource` to the three string values the F chapter lists (`Folder`, `Filename`, `Ffprobe`) via POST config. **All three return 500** with `System.Text.Json.JsonException: The JSON value could not be converted to Jellyfin.Plugin.MediaDash.Configuration.MediaSortSource`.
3. Try numeric values 0–4:
   - `0` → accepted, GET returns `"JellyfinMetadata"`.
   - `1` → accepted, GET returns `"FilenameHeuristic"`.
   - `2`, `3`, `4` → observed inconsistent behaviour across the loop: an error is reported by `Invoke-RestMethod` on some values but a later GET returned raw int `4` (type `System.Int32`, not a valid enum name), indicating the config file now holds an out-of-range enum value. Once `4` is persisted, GET on this config path returns the raw integer instead of a string — clients that expect the enum-string contract will break.

**Expected**

- The three source names in the doc (`Folder`, `Filename`, `Ffprobe`) should be the actual enum members and the POST should accept them.
- Alternatively, if only `JellyfinMetadata` + `FilenameHeuristic` exist, the doc needs updating and the config endpoint should reject any numeric value outside `[0, 1]` with 4xx.

**Actual**

- Enum members are `JellyfinMetadata` (0) and `FilenameHeuristic` (1) only — no `Folder`, `Filename`, or `Ffprobe`.
- POST with unknown enum-string → 500 (should be 400).
- POST with unknown enum-integer → apparently persisted without a hard failure; subsequent GET returns raw int, breaking the enum-string response contract.

**Evidence**

`docs/testing/evidence/F-028/log-mediasortsource-500.txt` — 3 `JsonException` lines from `log_20260828.log` (line numbers 3182 / 3245 / 3308 around 12:59 local) for the `Folder`/`Filename`/`Ffprobe` string attempts.

**Suggested area (best guess, not required)**

`Jellyfin.Plugin.MediaDash.Configuration` — verify the `MediaSortSource` enum members, add a `[JsonConverter(typeof(JsonStringEnumConverter))]` with `allowIntegerValues: false`, and range-check on write. Also update 01-scanners.md §F.6 to list the real enum members.

**Ambiguity flag**

The task doc says values include "at least `Folder`, `Filename`, `Ffprobe`". This may be aspirational documentation. On this build only two members exist. Treated as docs-drift + input-validation gap rather than a missing-feature bug.

---

### F-027 · MediaSorterScanner (Misplaced) untestable on this box — F-019 recurrence prevents new `S01E01`-named fixture from being indexed under any MediaSortSource mode

- **Test ID**: 01-scanners.md §F.1, §F.4, §F.5, §F.6, §F.7
- **Chapter file**: 01-scanners.md
- **Component**: `Jellyfin.Plugin.MediaDash.Scanners.MediaSorterScanner` (indirectly blocked — root cause is Jellyfin core's item-creation path, same as F-019)
- **Severity**: medium
- **Category**: env (blocks correctness verification)
- **Observed**: 2026-08-28
- **Session**: 2026-08-28 · 01-F audit (fresh QA)

**Repro**

1. Session prep per chapter (auth; DryRun on, MediaSortSource=JellyfinMetadata as saved by safety flip).
2. Seed `$env:LIB\movies\Episode Like (2020)\S01E01 Fake Show Ep.mkv` (copy of Clean Movie's `.mkv`, ~2.37 MB).
3. `POST /Library/Refresh`; sleep 35 s across two calls.
4. `GET /Items?SearchTerm=S01E01&Recursive=true` → `TotalRecordCount=0`.
5. `GET /Items?SearchTerm=Episode&Recursive=true` → `TotalRecordCount=0`.
6. `GET /Items?SearchTerm=Fake&Recursive=true` → 1 hit, but it's a pre-existing unrelated Movie (`FakeAgentUK - Trying Out the Blonde's New Boobs`, `Path=` empty). Confirms my `Fake Show Ep` fixture never became a Movie item.
7. `POST /MediaDash/Reset`; `POST /MediaDash/Scan`; poll; `GET /MediaDash/Issues?type=Misplaced` → `0`.
8. Swap `MediaSortSource` to `FilenameHeuristic` (numeric `1`), rescan → still `0` Misplaced issues. Log confirms scanner ran: `MediaDash scanner Misplaced found 0 issues` in `log_20260828.log`.

**Expected**

Per §F.4: `Get-Issues Misplaced = 2` (bug-report file S01E01 in movies\ + F.2 which is `[-]` skipped, so realistic expectation on this box is `= 1`). Per §F.5: each issue names the target library it should live in.

**Actual**

`0` Misplaced issues in every scan configuration attempted. Root cause is not the MediaSorter scanner — it's item-scoped (F-015 pattern) and Jellyfin never created a Movie item for the seeded file. `log_20260828.log` shows a long run of `Microsoft.Data.Sqlite.SqliteException (0x80004005): SQLite Error 19: 'FOREIGN KEY constraint failed'` around the refresh window, consistent with F-019. The F.5 `DetailsJson` field-name verification (F-020 pattern) is therefore unverifiable — no issues to inspect.

**Evidence**

- `docs/testing/evidence/F-027/lib-mid-01F.csv` — library listing while fixture seeded (17 rows = 16 baseline + `S01E01 Fake Show Ep.mkv`).
- `docs/testing/evidence/F-027/log-tail-relevant.txt` — grep of MediaSorter + FK-constraint + MetadataService lines from `log_20260828.log`.

**Suggested area (best guess, not required)**

`Jellyfin.Plugin.MediaDash.Scanners.MediaSorterScanner` cannot be verified on this dev-box until F-019 is resolved. When F-019 clears, this test can run cleanly.

**Ambiguity flag**

n/a — same F-015/F-019 pattern documented elsewhere in this chapter.

---

### F-026 · MediaGrouperScanner Ungrouped issue schema does not match doc; loose-file detection blocked by F-019 item-cache gap

- **Test ID**: 01-scanners.md §E.4, §E.5
- **Chapter file**: 01-scanners.md
- **Component**: `Jellyfin.Plugin.MediaDash.Scanners.MediaGrouperScanner`
- **Severity**: medium
- **Category**: docs (primary — schema drift, F-020 pattern); env (secondary — F-019 blocks the loose-file case)
- **Observed**: 2026-08-28
- **Session**: 01-E MediaGrouperScanner

**Repro**

1. Session prep §P.1–P.5. `DryRun` already ON from prior chapter, verified.
2. Snapshot `$env:LIB` → `%TEMP%\lib-before-01E.csv`.
3. Seed E.1 loose fixture (bare `movies\Loose (2019).mkv`, no containing folder)
   and E.3 nested control (`movies\Nested (2019)\Nested (2019).mkv`), each a
   copy of `movies\Clean Movie (2024)\Clean Movie (2024).mkv`.
4. `POST /Library/Refresh`, sleep 15 s.
5. `POST /MediaDash/Reset`, `POST /MediaDash/Scan`, poll until IsScanning=false.
6. `GET /MediaDash/Issues?type=Ungrouped`.
7. `GET /Items?SearchTerm=Loose&IncludeItemTypes=Movie` and same for Nested.

**Expected**

- E.4: exactly 2 Ungrouped issues — the loose bare mkv, and (per the doc)
  the pre-existing `Big Buck Test (2020)` folder (or per the checklist's
  literal "returns 2" phrasing, the Loose + Show pair; the Show is `[-]` so
  E.4 becomes "expect Loose").
- E.5: `metadata.suggestedFolder` field names the expected normalized
  folder name.

**Actual**

- Only **1** Ungrouped issue emitted: `Big Buck Test (2020)`. The Loose
  fixture is absent from the result set.
- Jellyfin item search for `Loose` and `Nested` both return `TotalRecordCount=0`
  — neither fixture was indexed by Jellyfin core. `log_20260828.log`
  shows recurring `at Emby.Server.Implementations.Library.LibraryManager.UpdateItemsAsync(...)`
  stack fragments in the scan-time window — F-019 recurrence. Because
  `MediaGrouperScanner` is item-scoped (F-015), no item = no detection.
- Schema (E.5): the emitted issue has **no** `metadata.*` wrapper and **no**
  `suggestedFolder` field. Actual `DetailsJson` shape:
  ```json
  {
    "action": "MoveFolder",
    "source": "C:\\dev\\mediadash-fixtures\\movies\\Big Buck Test (2020)",
    "target": "C:\\dev\\mediadash-fixtures\\movies\\Big Buck Test\\Big Buck Test (2020)",
    "title": "Big Buck Test",
    "franchise": true
  }
  ```
  The suggested destination is `DetailsJson.target` (a full absolute path,
  not a normalized name). Same doc-drift class as F-020 and the F-022 side-note.

**Evidence**

- `docs/testing/evidence/F-026/ungrouped-issue-shape.json` — raw response +
  decoded `DetailsJson` + doc-claim vs reality.
- `docs/testing/evidence/F-026/jellyfin-loose-nested-search.txt` — on-disk
  file listing, /Items search results (both 0), F-019 correlation.

**Suggested area (best guess, not required)**

- Docs: `01-scanners.md` §E.5 field name should be updated to
  `DetailsJson.target` (and possibly `title` / `franchise`), no `metadata.*`
  wrapper.
- The loose-file detection scenario (§E.4 primary case) cannot be validated
  on this box until F-019 (Jellyfin core SQL exception in
  `MetadataService.UpdateItemsAsync` when adding new movie items) is
  resolved. Not a MediaDash defect for the fixture case itself; but the
  scanner's item-scoped design (F-015) means users on any box hitting F-019
  will also see missed ungrouped detections.

**Ambiguity flag**

- E.4 says "returns 2" assuming Loose + Show would both be seeded. Since
  E.2 (Show) is `[-]` per F-005 and the pre-existing Big Buck folder is
  already flagged from prior sessions, the effective expectation is
  "Loose + Big Buck = 2". Actual = 1 (Big Buck only). If the doc intended
  Big Buck to be excluded from expected count, actual = 0 vs expected 1
  (Loose). Either interpretation is a fail on E.4.

---

### F-025 · TrickplayOptimizeScanner's 5-item sample heuristic causes false negatives — media-folder walk skipped when other items in the same library have sprites

- **Test ID**: 01-scanners.md §Q.1, §Q.3
- **Chapter file**: 01-scanners.md
- **Component**: `Jellyfin.Plugin.MediaDash.Scanners.TrickplayOptimizeScanner`
- **Severity**: high
- **Category**: correctness
- **Observed**: 2026-08-28
- **Session**: 01-Q TrickplayOptimizeScanner

**Repro**

1. Confirm both trickplay stores empty on this box:
   `Get-ChildItem $env:JFDATA\metadata -Recurse -Filter "trickplay" -Directory` → empty
   `Get-ChildItem $env:LIB -Recurse -Filter "trickplay" -Directory` → empty
2. Seed four fixture folders under `$env:LIB\movies\`, each with a real
   `.mkv` (copy of `Clean Movie (2024).mkv`) and a media-adjacent
   `trickplay\<width>\` subtree:
   - `Trickplay Big (2019)\trickplay\320\320.jpg` + `640\640.jpg` (two-tier jpg — un-optimized)
   - `Trickplay Webp (2019)\trickplay\320\320.webp` (control — already optimized)
   - `Trickplay Empty (2019)\trickplay\320\` (empty dir)
   - `Trickplay Mixed (2019)\trickplay\320\jpg_1..5.jpg + webp_1..5.webp` (mixed)
   Synth sprites via ffmpeg 7.1.4-Jellyfin, e.g.
   `& $FFMPEG -y -f lavfi -i "testsrc2=size=320x180:duration=1" -frames:v 1 -update 1 <path>`
3. `POST /Library/Refresh`, wait 15 s, also fire the `Scan Media Library`
   scheduled task and wait for `State=Idle`.
4. `POST /MediaDash/Reset`, `POST /MediaDash/Scan`, poll `Status` until
   `IsScanning=false`.
5. `GET /MediaDash/Issues?type=LargeTrickplay` → `[]` (expected ≥ 2 —
   the two-tier and the mixed fixtures).
6. Log line for the scan run:
   `TrickplayOptimizeScanner: skipping media-folder walk in library "MediaDash Test" — SaveTrickplayWithMedia=false and no legacy sidecars in a 5-item sample.`
   `TrickplayOptimizeScanner: 0 trickplay folder(s) have convertible sprites.`

**Expected**

At least the `Trickplay Big` and `Trickplay Mixed` folders raise
`IssueType.LargeTrickplay`, with `DetailsJson` naming the convertible
sprite count / bytes.

**Actual**

Zero issues. The scanner probes only 5 items in the library to decide
whether to walk media-adjacent `trickplay\` folders at all, and if that
5-item sample doesn't turn up a sprite, the walk is skipped for the
**entire library**. On the fixture library (hundreds of items), the
sample never intersects with my four freshly-seeded fixture folders —
so real convertible sprites on disk go undetected. The scanner's own
log confirms the skip in a single line and never re-runs a wider scan.

**Layered issue**: Jellyfin also didn't create Movie items for the four
new folders under the current dev-box state (F-019 pattern — item search
`Users/{id}/Items?searchTerm=Trickplay` returned 0). But that's not the
root cause here — the scanner's decision predates any item lookup; it's
purely a filesystem-sample heuristic on the library root, and it decides
"skip" before consulting the library's item cache.

**Likely fix location**

`Jellyfin.Plugin.MediaDash/Scanners/TrickplayOptimizeScanner.cs` — the
"5-item sample" branch. Options:
- Sample more items (e.g. 50) or all items when library size ≤ N.
- If SaveTrickplayWithMedia can't be read reliably (Jellyfin 10.11
  doesn't expose the setting on this box — no `SaveTrickplayWithMedia`
  key in `System/Configuration`, `System/Configuration/encoding`,
  `TrickplayOptions`, or per-library `options.xml`; the scanner is
  inferring `false` from absence), always walk media-adjacent
  `trickplay\` folders as a cheap filesystem probe — the walk is
  bounded by library size and skipping it silently defeats the whole
  scanner.

**Evidence**

- `docs/testing/evidence/F-025/status.json` — /Status right after the scan
- `docs/testing/evidence/F-025/issues-large-trickplay.json` — `[]`
- `docs/testing/evidence/F-025/fixture-inventory.csv` — the 15 seeded files
- `docs/testing/evidence/F-025/scanner-log.txt` — last 20 scanner log lines
  showing the "skipping media-folder walk" pattern across every scan

**Related**

- Prior scan log observation about the "5-item sample" heuristic (this
  finding proves it produces a false negative on a live library).
- F-019 — Jellyfin core failing to create items for new fixture
  folders. Independent of this, but combined they mean any tester
  running §Q on this dev box also loses the item-cache path.
- F-020 — `DetailsJson` field naming drift; §Q.4 (`currentBytes`,
  `estimatedBytes`) is unverifiable in this session because no issue
  was raised; noting the field-name check remains open for future
  runs on a passing fixture set.

**Docs drift (also worth noting)**

- §Q.1 says trickplay lives at `%LOCALAPPDATA%\jellyfin\metadata\...\trickplay\`.
  On this box, F-007 already corrected `jellyfin` → `jellyfin-v10`, and
  the internal-metadata trickplay tree is empty. Media-adjacent
  trickplay (under each item's folder) is also empty. Doc should note
  either store may be empty until Jellyfin's "Generate Trickplay
  Images" task actually runs (task is scheduled daily at 03:00 but
  produces nothing when `EnableTrickplayImageExtraction=false` in the
  per-library options.xml — which is the current state).

---

### F-024 · TranscodeLogScanner silently skips any transcode log written with a UTF-8 BOM — no warning, no counter, log is invisible

- **Test ID**: 01-scanners.md §P.2 (repro path while diagnosing F-023)
- **Chapter file**: 01-scanners.md
- **Component**: `Jellyfin.Plugin.MediaDash.Scanners.TranscodeLogScanner`
- **Severity**: medium
- **Category**: correctness
- **Observed**: 2026-08-28
- **Session**: 01-P TranscodeLogScanner

**Repro**

1. Session prep as usual; DryRun on.
2. Write a valid ffmpeg-transcode log file into `$env:JFDATA\log\FFmpeg.Transcode-*.log`, using
   PowerShell's `Set-Content` or `[IO.File]::WriteAllText($p, $body, [System.Text.Encoding]::UTF8)`
   — both emit a leading `EF BB BF` byte order mark.
3. `POST /MediaDash/Reset`, `POST /MediaDash/Scan`, wait for `IsScanning=false`.
4. Check `TranscodeLogScanner: N log(s) inspected → M distinct file(s) …` in
   `$env:JFDATA\log\log_YYYYMMDD.log`.
5. Rewrite the same log body using `New-Object System.Text.UTF8Encoding($false)` (no BOM) and
   re-run the scan.

**Expected**

Either the BOM'd log is parsed (BOM is a well-known no-op on UTF-8 streams and every mainstream
JSON parser tolerates it), OR the scanner logs a warning naming the offending file so an admin
knows why nothing is being detected. Real Jellyfin-generated transcode logs are BOM-less, so this
only bites admins/automation that write logs via .NET or PowerShell defaults.

**Actual**

BOM'd log: silently dropped. The scanner still counts the file in `N log(s) inspected` (the
filesystem walk sees it) but it never contributes to `M distinct file(s)` and never surfaces
in issue rows. No warning line, no exception. Removing the BOM (identical content otherwise)
makes the very next scan recognize the file. In this session five seeded logs became invisible
until the encoding was fixed.

**Evidence**

- `docs/testing/evidence/F-024/bom-vs-nobom-comparison.txt` — before/after summary lines.
- Repro one-liner: first three bytes of a broken vs working log —
  `[IO.File]::ReadAllBytes($p)[0..2]` returns `ef bb bf` (broken) vs `7b 22 50` (`{"P…`, working).

**Suggested area (best guess, not required)**

`TranscodeLogScanner` — the top-of-file JSON header parse. Either `TrimStart('﻿')` on the
first line, or read as bytes and pass to `JsonDocument.Parse(byteSpan)` which tolerates BOM.

**Ambiguity flag**

n/a.

---

### F-023 · TranscodeLogScanner never emits `IssueType.FailedTranscode` — all failure sessions are re-classified as `HeavyTranscode`, and the scanner's own summary line disagrees with the issue table

- **Test ID**: 01-scanners.md §P.3, §P.4, plus summary-line inspection in §P step 8
- **Chapter file**: 01-scanners.md
- **Component**: `Jellyfin.Plugin.MediaDash.Scanners.TranscodeLogScanner` (also affects
  `IssueType.FailedTranscode` registration and the `FailedTranscodeFixMode` config surface)
- **Severity**: high
- **Category**: correctness
- **Observed**: 2026-08-28
- **Session**: 01-P TranscodeLogScanner

**Repro**

1. Session prep as usual. DryRun on.
2. Seed a transcode log into `$env:JFDATA\log\` using the pre-existing log as template
   (copy JSON header, swap `Id` to a real Jellyfin ItemId + `Path` to a real library file),
   overwrite the tail with `Conversion failed!`. Write it as **UTF-8 without BOM** — see
   F-024 or nothing will be detected.
3. Seed three more logs at the same file with successful ffmpeg-frame-summary tails to
   probe P.4.
4. `POST /MediaDash/Reset`, `POST /MediaDash/Scan`, poll to completion.
5. `curl.exe .../MediaDash/Issues?type=FailedTranscode` — expect ≥1.
6. `curl.exe .../MediaDash/Issues?type=HeavyTranscode`.
7. Grep `log_YYYYMMDD.log` for `TranscodeLogScanner:` — read the "X heavy, Y failed" summary.

**Expected**

Per the checklist (§P.3), a log ending in `Conversion failed!` surfaces as one
`IssueType.FailedTranscode`. Per §P.4, three successful transcodes of the same file surface
as one `IssueType.HeavyTranscode`. The scanner summary line's `heavy` / `failed` counts
should equal the returned issue counts of each type.

**Actual**

`GET /MediaDash/Issues?type=FailedTranscode` returns `[]` under every seeding pattern tried
(1 fail on Clean Movie + 3 successes on Clean Movie; plus the 2 pre-existing sessions on
Truncated Movie which were both failure exits). `GET /MediaDash/Issues?type=HeavyTranscode`
returns **two** items — Truncated Movie (from pre-existing state,
`DetailsJson={"sessions":2,"failures":2,…}`) and Clean Movie (from this session,
`DetailsJson={"sessions":4,"failures":1,…}`). Every failure count is folded into a
`HeavyTranscode` row via `DetailsJson.failures`; the `FailedTranscode` type appears never
to be emitted at all.

The scanner's own summary line says the opposite: `8 log(s) inspected → 3 distinct file(s)
→ 0 heavy, 2 failed`. So the counter labelled *heavy* is 0 while the issue table has 2
`HeavyTranscode` rows, and the counter labelled *failed* is 2 while `FailedTranscode` returns
0 rows. The summary text and the issue-type mapping are inverted, or the failed/heavy
categorisation happens at counting time but the issue is written under the wrong `IssueType`
regardless.

Downstream implications:
- `FailedTranscodeFixMode` / `FailedTranscodeDisposal` config surface (present in
  `GET /Plugins/{guid}/Configuration`) has no reachable issues to act on.
- Any UI filter chip for `FailedTranscode` will always show 0 while the underlying failures
  are hidden inside `HeavyTranscode` rows' `DetailsJson`.

Also (per F-020 pattern, worth noting): `DetailsJson` shape here is
`{"sessions":int,"failures":int,"lastSeenUtc":iso,"lastFailureUtc":iso}` — not the
`metadata.*` fields §P.4/§P.5-style checklists reach for. Docs, not code.

**Evidence**

- `docs/testing/evidence/F-023/issues-FailedTranscode.json` — `[]`.
- `docs/testing/evidence/F-023/issues-HeavyTranscode.json` — 2 rows including a Clean Movie
  row synthesised in this session (`ItemId=2b597f72290376cd326c6df31a965a4a`).
- `docs/testing/evidence/F-023/issues-all.json` — no `FailedTranscode` type present anywhere
  in the issue table.
- `docs/testing/evidence/F-023/scanner-summary-tail.txt` — the mismatched summary lines,
  including `TranscodeLogScanner: 8 log(s) inspected → 3 distinct file(s) → 0 heavy, 2 failed`.
- `docs/testing/evidence/F-023/seeded-fail-log.txt` — the exact fail-tail fixture that got
  parsed as a failure by the counter but written as a heavy issue.
- `docs/testing/evidence/F-023/log-dir-listing.txt` — final log dir listing after cleanup.

**Suggested area (best guess, not required)**

`TranscodeLogScanner` — the code path that decides `IssueType.FailedTranscode` vs
`IssueType.HeavyTranscode` when writing to the issue store. The counters (`heavy++` /
`failed++`) look correct; the branch that picks the `IssueType` for the row is likely wired
to the wrong bucket, or the failed-path is unreachable (only heavy writes are performed).

**Ambiguity flag**

n/a — the summary line is explicit about what the scanner *counted*; the issue table is
explicit about what it *stored*; the mismatch is unambiguous.

---

### F-022 · SubtitleFontScanner reports 0 sidecar(s) even when Jellyfin has an `.ass` sidecar indexed as a subtitle MediaStream — scanner never detects any embedded fonts

- **Test ID**: 01-scanners.md §M.4 (also blocks M.5, M.6, M.7, M.8)
- **Chapter file**: 01-scanners.md
- **Component**: `SubtitleFontScanner` (surfaces as `IssueType.SubtitleFonts`)
- **Severity**: high
- **Category**: correctness
- **Observed**: 2026-08-28
- **Session**: 2026-08-28 (fresh QA — Ponytail single-scanner run of 01-M)

**Repro**

1. Session prep: authenticate, save plugin config, flip `DryRun=true`
   (`$env:TEMP\cfg-orig-01M.json` saved for restore).
2. Snapshot library:
   `Get-ChildItem $env:LIB -Recurse -File | Select FullName,Length | Export-Csv "$env:TEMP\lib-before-01M.csv" -NoTypeInformation`.
3. Build a well-formed ASS with a `[Fonts]` section containing three embedded
   fonts (`UnusedFont1_0.ttf`, `UnusedFont2_0.ttf`, `UsedFont_0.ttf`), each
   with a real ~4 KB UUEncoded payload (see `sample-many-fonts.ass`;
   17,126 bytes on disk). A `[V4+ Styles]` section declares
   `Style: Default,UsedFont,20` so only `UsedFont` is referenced.
4. Seed the file at `$env:LIB\movies\Subs Many Fonts (2020)\Subs Many Fonts (2020).ass`
   with a co-located `.mkv` (copied from `Clean Movie (2024).mkv`).
5. `POST /Library/Refresh` → 204, sleep 15s.
6. `POST /MediaDash/Reset` → 204, `POST /MediaDash/Scan` → 204, poll to done.
7. `GET /MediaDash/Issues?type=SubtitleFonts` → `[]` (empty).
8. Log: `SubtitleFontScanner: 0 sidecar(s) have reclaimable embedded fonts.`

Because Jellyfin's core `MetadataService` fails to index newly-seeded movie
folders on this box (see **F-019**), the new folder produced no Movie item,
which mirrored F-015 — so I additionally reproduced the same fixture inside
folders that **do** have a Jellyfin Movie item:

9. Wrote an identical `.ass` at `$env:LIB\movies\Clean Movie (2024)\Clean Movie (2024).ass`.
10. `POST /Items/{cleanId}/Refresh?Recursive=true&MetadataRefreshMode=FullRefresh&ReplaceAllMetadata=true`,
    sleep 8s. `GET /Items/{cleanId}?fields=MediaStreams` confirms Jellyfin
    now indexes an external subtitle stream:
    `Index=0, Codec=ass, IsExternal=True, Path=…\Clean Movie (2024).ass`
    (`cleanmovie-streams.json` in evidence).
11. Repeat Reset + Scan → still `SubtitleFonts: 0` and log still
    `0 sidecar(s) have reclaimable embedded fonts.`.
12. Also copied the same `.ass` to `Sub Heavy (2023)\` (which already had
    embedded subrip subs). Item refresh → Jellyfin now shows two subtitle
    streams (the new external `ass` at index 0, plus the pre-existing
    `subrip` at index 3 — see `subheavy-streams.json`). Rescan → still
    `SubtitleFonts: 0`, log still `0 sidecar(s)`.

So the scanner is confirmed **item-scoped** (F-019 pattern applies to the
new-folder fixtures), but even when the target `.ass` is indexed as an
external subtitle stream on a live Movie item, the scanner reports 0
sidecars.

**Expected**

Per §M.4: `Get-Issues SubtitleFonts` = 2 (M.1 + M.3 fixtures), with
`DetailsJson.unusedFonts` array naming the fonts. At minimum, the log should
report `2 sidecar(s) have reclaimable embedded fonts.` — one for the M.1
sidecar (2 unused: `UnusedFont1_0.ttf`, `UnusedFont2_0.ttf`) and one for the
Clean Movie sidecar (same shape).

**Actual**

`SubtitleFonts` issues count = 0. Log line consistently
`SubtitleFontScanner: 0 sidecar(s) have reclaimable embedded fonts.` across
five consecutive scan cycles. No exception logged (grep for
`SubtitleFontScanner.*Exception|SubtitleFontScanner.*throw` returns empty —
see `log-subtitlefont-all.txt`).

Two possible root causes, both plausible without source access:

- **(A) Scanner selects `.ass` sidecars from a wrong source of truth.**
  Even after the item's `MediaStreams` contains `{Codec:"ass", IsExternal:true, Path:"…\Clean Movie (2024).ass"}`,
  the scanner still reports 0 sidecars. If the scanner reads external
  subtitles from a different Jellyfin API (e.g. `LibraryManager.GetItemList`
  filtered to specific `BaseItemKind`, or `SubtitleManager`), that lookup may
  be returning an empty set for external `.ass` streams on this Jellyfin
  version (10.11.11).
- **(B) The parser rejects the file silently.** The `[Fonts]` block uses
  ASS-standard UUEncoding (values +33, 80 chars/line). If the parser expects
  a stricter format (e.g. `filename.ext_0.ttf` naming, or a specific
  terminator), it may drop the file into the "no reclaimable fonts" bucket
  and log nothing. The absence of a per-file DEBUG line makes this hard to
  distinguish.

**Evidence**

Under `docs/testing/evidence/F-022/`:

- `issues-empty.json` — `[]` response from
  `GET /MediaDash/Issues?type=SubtitleFonts`.
- `cleanmovie-streams.json` — proves `.ass` is indexed as external subtitle
  stream on Clean Movie item (`Codec=ass, IsExternal=True, Path=...ass`).
- `subheavy-streams.json` — same, on Sub Heavy item; two subtitle streams
  (external `ass` + embedded `subrip`).
- `sample-many-fonts.ass` — the fixture file (17,126 bytes; 3 UUEncoded font
  blocks, one referenced by `Style: Default,UsedFont`).
- `log-scanner-tail.txt` — last 20 `SubtitleFontScanner` log lines, all
  reporting 0 sidecar(s), across the scan cycles this session ran.
- `log-subtitlefont-all.txt` — broader grep, confirms no exception thrown.

**Suggested area (best guess, not required)**

`Jellyfin.Plugin.MediaDash/Scanners/SubtitleFontScanner.cs` — check the
sidecar-enumeration source. If it filters by
`item.GetMediaSources().SelectMany(ms => ms.MediaStreams).Where(s => s.IsExternal && ...)`
compare against the streams the item detail API returns (proven populated in
evidence). Cross-check `AssSubtitleFile` parser tolerance for
ASS-standard UUEncoded font blocks with ≥1 KB payloads.

**Ambiguity flag**

§M.5's field name (`metadata.unusedFonts`) is unverifiable because M.4
returned zero issues — the doc drift already flagged in F-020 (§J, §H, §O)
probably applies here too, but I could not confirm without a hit. Also, the
task briefing said the scanner is "likely filesystem-based" based on the
`0 sidecar(s)` log line, but the evidence in this run points at item-scoped
selection: the scanner never counts a `.ass` sitting loose in the library,
only ones surfaced through an item's `MediaStreams`, and even then reports 0.
Filesystem-vs-item classification therefore stays open.

### F-021 · OrphanCleanupScanner double-counts a Ghost-fixture folder — flags both the .srt (OrphanSubtitle) AND the parent folder (EmptyFolder), even though the folder still contains the .srt

- **Test ID**: 01-scanners.md §I.4
- **Chapter file**: 01-scanners.md
- **Component**: `OrphanCleanupScanner` — the pass that emits an `EmptyFolder` orphan while a sibling pass has already flagged the file inside it
- **Severity**: medium
- **Category**: correctness
- **Observed**: 2026-08-28
- **Session**: 2026-08-28 (fresh QA #3)

**Repro**

1. Config: `DryRun=true` (flipped for the session).
2. Seed only:
   `$env:LIB\movies\Ghost (2020)\Ghost (2020).en.srt` (no video, no NFO, no other file).
3. Seed control:
   `$env:LIB\movies\Real (2020)\Real (2020).mkv` + `Real (2020).en.srt`.
4. `POST /MediaDash/Reset`, `POST /Library/Refresh` (sleep 15s),
   `POST /MediaDash/Scan`, poll `/Status` until done.
5. `GET /MediaDash/Issues?type=OrphanedDebris`.

**Expected**

Per §I.4, two issues total from the §I fixtures (I.1 orphan + I.3 trickplay).
For I.1 alone: **one** issue naming the orphan `Ghost (2020).en.srt`. The
`Ghost (2020)\` folder is NOT empty (it holds the .srt), so `EmptyFolder`
should not fire on it.

**Actual**

Four issues total. The two pre-existing HI Test detections
(`reg.srt`, `sdh.srt`) came back as expected. My single Ghost fixture
produced **two** rows:

```
Id=7185 Path=...\Ghost (2020)\Ghost (2020).en.srt
        DetailsJson={"kind":"OrphanSubtitle","bytesEstimate":47}
Id=7184 Path=...\Ghost (2020)
        DetailsJson={"kind":"EmptyFolder","bytesEstimate":47}
```

The `EmptyFolder` row is wrong at the moment it fires — the folder still
contains `Ghost (2020).en.srt`. It would only be empty *after* the paired
`OrphanSubtitle` fix ran. Either the two passes are ordered so that
`EmptyFolder` inspects a hypothetical post-fix state (bug — a scan-only
pass shouldn't presuppose a fix), or the `EmptyFolder` pass counts
"nothing that Jellyfin would recognise as a media file" as empty, which is
also wrong: the pass runs alongside `OrphanSubtitle`, which is proof the
folder is NOT empty in the filesystem sense.

Downstream this doubles the queue: when `OrphanedDebrisFixMode` moves off
`DetectOnly` the fixer will (a) recycle `Ghost (2020).en.srt`, then
(b) delete `Ghost (2020)\` as an "empty" folder. Item (b) is only correct
because item (a) ran first — but a queued fix that depends on ordering
inside the same run is fragile. If the fixer runs (b) first the delete
fails (folder not empty), or worse, it succeeds and takes the .srt with it
before a per-file recycle path had a chance to route it correctly.

Real (2020) — control — was NOT flagged (I.5 pass). Nothing was deleted
during the scan (I.7 pass, before/after snapshot identical modulo the
seeded fixtures).

**Evidence**

`docs/testing/evidence/F-021/response-orphaned-debris.json` — full
`GET /MediaDash/Issues?type=OrphanedDebris` body captured 2026-08-28
after the block's scan. Also see log `%JFDATA%\log\log_20260828.log`
for the scanner's pass-count line
(`OrphanCleanupScanner: N orphan(s) across M pass(es)`).

**Suggested area (best guess, not required)**

`OrphanCleanupScanner`. The `EmptyFolder` pass should either:

1. Ignore folders that another pass in the same run has already flagged a
   child of, OR
2. Only count a folder as empty after doing a live `Get-ChildItem` that
   sees zero entries — the current behaviour looks like it filters child
   entries by a "recognised media file" predicate and calls the folder
   empty if that filter is empty, which conflates two different questions.

Fixer-side: `OrphanedDebrisFixer` (or whatever consumes the queue) must
sort queued items by depth (deepest first) before executing, so folder
deletions naturally come after the files inside them are gone. That
tolerates the current scanner double-emit without needing to change it.

**Ambiguity flag**

Ambiguity: I did not read the plugin source, so I can't say which pass
emits the `EmptyFolder` row. The `DetailsJson` distinguishes them
(`kind:"OrphanSubtitle"` vs `kind:"EmptyFolder"`) and both come from
`OrphanCleanupScanner` (single issue type). The finding is filed on the
observed shape.

---

### F-020 · Issue `DetailsJson` schema drifts from what the checklists claim in §J, §H, §O — docs need `Reason`/`Detail`, not `metadata.*`

- **Test ID**: 01-scanners.md §H.6 (`metadata.parseError`), §J.5
  (`metadata.ffprobeExitCode`), §O.5 (`metadata.reason`)
- **Chapter file**: 01-scanners.md
- **Component**: docs — the shape claim `metadata.xxx` on issue rows
- **Severity**: low
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 2026-08-28 (fresh QA #2)

**Repro**

1. Seed the relevant fixtures from each block.
2. Scan, then fetch each issue type.
3. Read `DetailsJson`.

**Expected** — per each check, a `metadata.parseError` / `metadata.ffprobeExitCode` / `metadata.reason` field.

**Actual** — three different shapes across three scanners, none using `metadata.*`:

- `CorruptNfo.DetailsJson`  → `{"reason":"empty file"}` /
  `{"reason":"root element <foobar> is not a Jellyfin NFO type"}` /
  `{"reason":"malformed XML: ..."}`.
- `Playability.DetailsJson` → `{"reason":"decode-error","detail":"[matroska,webm @ ...] File ended prematurely ..."}`.
- `MalwareRisk.DetailsJson` → `{"extension":".exe"}`. No `reason` at all.

**Evidence** — response bodies above (session 2026-08-28).

**Suggested area (best guess, not required)**

Two paths:

1. **Doc-only fix** — replace the `metadata.xxx` shorthand in §H.6,
   §J.5, §O.5 with the actual `DetailsJson.reason` (or `.extension`
   for Malware) shape. Smallest diff.
2. **Plugin-side normalisation** — every issue's `DetailsJson` should
   include a `reason` (even the Malware case: `"reason":"executable"`).
   Better UX for the config page's issue table, since `.extension`
   alone is less informative than a natural-language reason.

Recommended: doc fix now, plugin normalisation later. Roll into F-013
/ F-017 batch if it's cheap.

**Ambiguity flag** — n/a. Behaviour is deterministic.

---

### F-019 · Jellyfin core fails to index newly-seeded fixture folders — `MetadataService` throws inside `UPDATE "BaseItems"` during library validation

- **Test ID**: 01-B, 01-J (impacted); every future block that adds new movie fixtures
- **Chapter file**: 01-scanners.md
- **Component**: env — Jellyfin core on this dev-box (NOT MediaDash)
- **Severity**: high (for the audit's throughput; low for the plugin itself)
- **Category**: env
- **Observed**: 2026-08-28
- **Session**: 2026-08-28 (fresh QA #2)

**Repro**

1. Seed a new movie folder — e.g.
   `$env:LIB\movies\JpnOnly (2020)\JpnOnly (2020).mkv` — using
   `ffmpeg -c copy` from an existing fixture (Clean Movie).
2. Fire `POST /Library/Refresh`. Then fire the `Scan Media Library`
   scheduled task via `POST /ScheduledTasks/Running/{id}`.
3. Poll `GET /Items?IncludeItemTypes=Movie&Fields=Path` and search for
   the seeded name.

**Expected**

The new folder appears as a Movie item, so item-scoped MediaDash
scanners can walk it.

**Actual**

`JpnOnly`, `Untagged`, `NfoZero`, `NfoBad`, `NfoWrongRoot`, `NfoOk`,
and `Some Movie` — none of them appear as Movie items after the scan.
The log has a Jellyfin core stack trace on
`Emby.Server.Implementations.Library.LibraryManager.UpdateItemsAsync`
→ `MediaBrowser.Providers.Manager.MetadataService`2.SaveInternal`,
tied to an `UPDATE "BaseItems" SET Album=..., DateCreated=...,
DateModified=..., Path=... WHERE ...` — the exception body isn't in
the tail I captured, only the stack.

Consequence: every §B / §D / §J / §K / §L / §N block that relies on
a new fixture becoming a Jellyfin item is blocked on this box.
Filesystem-based scanners (§H NfoScanner, §O MalwareRisk) work fine.

**Evidence**

Log at `%LOCALAPPDATA%\jellyfin-v10\log\log_20260828.log` around
`11:25:21` shows the stack trace. Item enumeration around `11:30`
shows only the five original `make-fixtures.sh` movies.

**Suggested area (best guess, not required)**

Jellyfin core, this specific `%LOCALAPPDATA%\jellyfin-v10\` datadir.
Likely another column-name drift like the earlier `ExtraIds` patch
recorded in the project memory. Two operator fixes:

1. Grab the exact SQL error line and identify the missing column /
   type mismatch, apply an `ALTER TABLE` similar to the earlier
   ExtraIds patch.
2. Or switch to the v12 clone (port 8098) and re-run — v12 may not
   have the schema drift.

**Ambiguity flag**

Ambiguity: I didn't capture the exception `Message` line — only the
stack trace was in the tail I picked. A follow-up session with
`Select-String -Pattern "MetadataService.*Error"` should pull the
message and identify the exact column.

---

### F-018 · MissingSubtitles emits the same file twice when Jellyfin holds two items for it — duplicate rows in issue list, inflated PendingFixCount

- **Test ID**: 01-scanners.md §G.6, §G.7
- **Chapter file**: 01-scanners.md
- **Component**: `MissingSubtitleScanner` (issue emission — should
  de-dupe by path or item root, not by BaseItem)
- **Severity**: medium
- **Category**: correctness
- **Observed**: 2026-08-28
- **Session**: 2026-08-28 (fresh QA #2)

**Repro**

1. Config: `AllowedSubtitleLanguages=[eng]`.
2. Library: `Big Buck Test (2020) - 1080p.mkv` and
   `Big Buck Test (2020) - 2160p.mkv` share the folder
   `Big Buck Test (2020)\`.
3. `POST /MediaDash/Reset`, `POST /MediaDash/Scan`.
4. `GET /MediaDash/Issues?type=MissingSubtitles`.

**Expected**

One issue row per distinct file path. Six library files without eng
subs → six issues.

**Actual**

Seven issues. Big Buck 1080p appears twice with two different `ItemId`s
pointing at the same path:

```
Id   ItemId                            Path
---- --------------------------------- -----------------------------------------------
7160 7ee93d08ff649a9d1e258f09a92632dd  ...\Big Buck Test (2020) - 1080p.mkv
7162 e6557a69351eecdd723615d82b174f45  ...\Big Buck Test (2020) - 1080p.mkv
```

Jellyfin registered two Movie items with the same underlying file (one
of them also holds the 2160p file as a version — see F-015). The
scanner iterates items and emits one issue per item, so files hit
twice. Downstream this inflates:

- The "6 issues" number in the plugin status widget.
- The fixer's queue when `MissingSubtitlesFixMode` moves off
  `DetectOnly` — every fix will run against the same path twice.

Adjacent: DuplicateScanner should almost certainly flag this exact
situation (two items with an identical path) but returns 0. See F-015
for the linked pattern.

**Evidence**

Response above.

**Suggested area (best guess, not required)**

`MissingSubtitleScanner` (and probably every other scanner that
enumerates items with a `Path`). Collapse by
`items.GroupBy(i => i.Path.NormalizedPathKey)` before emitting; use the
"most-primary" item as the anchor. Alternative: dedupe in the issue
persistence layer against `(Type, Path)`.

Separate fix in DuplicateScanner: `items.GroupBy(i => i.Path)` should
be one of its detection signals — two items literally on the same file
is a duplicate more obvious than title+year matching.

**Ambiguity flag**

Ambiguity: the doc §G.6 expects 2, so my "6 vs 7" comparison is
against the current dev fixture set, not a clean §G run. The doubling
pattern is what the finding is about; the exact count depends on
fixture.

---

### F-017 · Playability issue's `DetailsJson` has no `ffprobeExitCode` field — doc J.5 unverifiable

- **Test ID**: 01-scanners.md §J.5
- **Chapter file**: 01-scanners.md
- **Component**: docs — expected schema of a Playability issue
- **Severity**: low
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 2026-08-28 (fresh QA #2)

**Repro**

1. Seed `Truncated Movie (2021)\Truncated Movie (2021).mkv` from
   `tools/make-fixtures.sh`.
2. Refresh + scan.
3. `GET /MediaDash/Issues?type=Playability` → returns the truncated file.
4. Inspect the issue's `DetailsJson`.

**Expected**

Per J.5, the issue metadata contains a `ffprobeExitCode` field with a
non-zero value.

**Actual**

The issue shape is:
```
{
  "Reason": "decode-error",
  "Detail": "[matroska,webm @ ...] File ended prematurely\r\nframe= 190 fps=0.0 ..."
}
```
No `ffprobeExitCode`. J.5 as written cannot be verified against this
schema.

**Evidence** — response above.

**Suggested area (best guess, not required)** — J.5 should read
`DetailsJson.Reason == "decode-error"` (matches every truncated /
corrupt case) OR the plugin should surface `ffprobeExitCode` alongside
`Reason`/`Detail`. Doc-only fix is smaller.

**Ambiguity flag** — n/a.

---

### F-016 · Fixer output does not preserve source file's CreationTime / LastWriteTime — Jellyfin's "Date added" / "Date modified" sort loses the item's actual age

- **Test ID**: 02-fixers.md (cross-cutting — every fixer that writes a
  new file: transcode, container swap, audio/subtitle removal, artwork
  replacement)
- **Chapter file**: 02-fixers.md (deferred — filed by maintainer, not
  observed during a chapter-01 test)
- **Component**: fixer output-file finalisation step (every place that
  `File.Move` / `File.Copy` / ffmpeg output → recycles source and drops a
  new file in the library)
- **Severity**: medium
- **Category**: correctness
- **Observed**: 2026-08-28 — filed from a maintainer report
- **Session**: 2026-08-28 (fresh QA #2)

**Repro**

1. Seed a movie fixture with a specific old `CreationTime` and
   `LastWriteTime` — e.g.
   ```powershell
   $f = Get-Item "$env:LIB\movies\Old Fixture\Old Fixture.mkv"
   $f.CreationTime  = [datetime]"2019-01-15 08:30:00"
   $f.LastWriteTime = [datetime]"2019-01-15 08:30:00"
   ```
2. Trigger a fixer run that rewrites the file — e.g. a transcode down
   from 4K to 1080p, or a subtitle-removal pass.
3. After the fix completes, read `CreationTime` / `LastWriteTime` on
   the file at its original library path.

**Expected**

Both timestamps match the original values (`2019-01-15 08:30:00`).
Rationale: Jellyfin's library sorts ("Date added", "Date modified",
"Date released") key off filesystem `CreationTime` / `LastWriteTime`
when its internal metadata is absent or stale. If the fixer bumps
`LastWriteTime` to "now", a movie the user has owned for 5 years
suddenly jumps to the top of the "Recently added" row on next scan —
which is a UX regression from the user's perspective.

**Actual**

The fixer output uses the current wall-clock for `CreationTime` and
`LastWriteTime`, because ffmpeg (and `File.Copy` / `File.Move`) does
not carry timestamps forward by default. Confirmed shape by inspecting
files under `%LOCALAPPDATA%\jellyfin-v10\data\mediadash\recycle\`
alongside their original paths — recycled files retain the original
timestamps (that path is a move, not a rewrite), but any fixer that
writes a new file has "today" on it.

**Evidence**

n/a — reported by maintainer. A chapter-02 test with a dated fixture
will collect a live before/after screenshot of the Jellyfin library
list and the raw timestamps.

**Suggested area (best guess, not required)**

The output-finalise helper each fixer calls before dropping the new
file into place. Change is small:

```csharp
File.SetCreationTimeUtc (destPath, srcMetadata.CreationTimeUtc);
File.SetLastWriteTimeUtc(destPath, srcMetadata.LastWriteTimeUtc);
```

Two subtleties:

- Capture the source metadata *before* the source is recycled, not
  after; recycling may cross a volume boundary and reset the value.
- Preserve `LastAccessTimeUtc` too if the fixer wants
  `Jellyfin.LibrarySort=DatePlayed` to stay stable — lower priority
  than the two above.
- If the fixer emits a **container swap** (mkv → mp4) that changes the
  file's path, Jellyfin will treat the new path as a new item unless
  the item's ID follows — but that's a separate rework.

**Ambiguity flag**

Ambiguity: I have not confirmed whether Jellyfin ever writes a
DateCreated back into its own DB after the fixer runs — if it does, the
FS timestamp fix alone is not sufficient. The maintainer's note is
that Jellyfin sorts by these fields; I've taken that as authoritative.
A chapter-02 test will verify the observed sort order end-to-end.

---

### F-015 · Chapter 01 fixture design does not create Jellyfin items — scanners with an item-cache walk return 0 for all planted fixtures

- **Test ID**: 01-scanners.md §A.1–A.10 (CorruptArtwork), §C.1–C.5 (Duplicate), and by extension every §I / §K block that assumes item-scoped detection
- **Chapter file**: 01-scanners.md
- **Component**: fixture design in the doc — cross-cutting
- **Severity**: high
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 2026-08-28 (fresh QA #2)

**Repro**

1. Follow §A.1–A.5 verbatim. Fixture folders on disk:
   `ZeroByte (2020)\poster.jpg` (0 B),
   `Truncated Art (2020)\backdrop.jpg` (512 B),
   `BadType (2020)\thumb.jpg` ("not an image"),
   `Healthy Art (2020)\poster.jpg` (~31 KB real JPG).
2. `POST /Library/Refresh`, wait for Jellyfin to complete.
3. `POST /MediaDash/Scan`, wait.
4. `GET /MediaDash/Issues?type=CorruptArtwork` → `[]`.

Same story in §C: seed `Big Buck Test (2020) - 2160p.mkv` and
`Big Buck Test (2020) - 1080p.mkv` in the same folder →
`GET /MediaDash/Issues?type=Duplicate` → `[]`.

**Expected**

Per §A.7 the scanner returns 3. Per §C.4 the scanner returns a group of 2.

**Actual**

Both return 0. Log shows both scanners *ran* and simply found nothing:

```
[INF] MediaDash scanner CorruptArtwork found 0 issues
[INF] MediaDash scanner Duplicate found 0 issues
```

Root cause identified during triage:

1. **Artwork**: a folder that contains **only** `poster.jpg` (no video
   file) does not become a Jellyfin `Movie` item. Jellyfin creates items
   from media files, not from artwork sidecars. Because ArtworkScanner
   iterates existing item images (`Primary`, `Backdrop`, `Thumb` per
   `BaseItem`), a planted `poster.jpg` with no video next to it is
   invisible to the scanner. I verified this by copying the zero-byte
   `poster.jpg` next to a real `Clean Movie (2024).mkv`, forcing a
   metadata refresh, and rescanning — still 0 flags. Jellyfin's own
   image-picker skipped the zero-byte file and used an extracted
   thumbnail instead, so the item's `Primary` never pointed at the bad
   file.

2. **Duplicate**: two `.mkv` files inside the same folder (`Big Buck Test
   (2020)\... - 2160p.mkv` + `... - 1080p.mkv`) become **one** Movie
   item with **one** `MediaSource` in Jellyfin — the 1080p file is
   silently dropped. Confirmed by
   `GET /Items?IncludeItemTypes=Movie&SearchTerm=Big` — one item,
   `MediaSources` count = 1. DuplicateScanner iterates items with the
   same name/year, sees a single item, no duplicate. To trigger the
   scanner correctly, the fixture must place the two files in
   **different** folders — e.g.
   `movies\Big Buck Test (2020) - 4K\Big Buck Test (2020).mkv` and
   `movies\Big Buck Test (2020) - 1080p\Big Buck Test (2020).mkv`.

The chapter is written as if scanners walk the filesystem; several
scanners actually walk Jellyfin's item cache. The two are different
tests and the doc conflates them.

**Evidence**

- Log lines above (file `%JFDATA%\log\log_20260828.log` around
  `06:57:42` and `06:57:44`).
- The seeded artwork folders (`ZeroByte (2020)`, `Truncated Art (2020)`,
  `BadType (2020)`, `Healthy Art (2020)`) as of writing this finding are
  still on disk under `$env:LIB\movies\`. They are safe to delete once
  the finding is triaged — none of them contains a media file.

**Suggested area (best guess, not required)**

Either:

1. **Rewrite the affected §A / §C fixtures** so every "bad artwork"
   fixture co-locates a valid media file (the doc's `Clean Movie
   (2024)` payload from `tools/make-fixtures.sh`) plus the bad artwork,
   AND explicitly forces Jellyfin to pick the bad file as `Primary`
   (either by disabling extraction or by using an image type Jellyfin
   won't extract). For §C, put each version in its own year-tagged
   parent folder.
2. **Or expose a scanner-mode toggle** on ArtworkScanner and
   DuplicateScanner that does a plain filesystem walk — closer to what
   the doc's fixtures imply. This is a plugin change, not a doc change.

Either fix is fine for the doc's purpose; the docs-only fix is
smaller. Recommended: pick option 1 for §A / §C so the chapter passes
against the current scanner behaviour.

**Ambiguity flag**

Ambiguity: I could not confirm whether ArtworkScanner **should** be
walking the filesystem, since I have not read the plugin source. Filed
as a docs bug on the assumption that the current item-cache behaviour
is intentional (matches how "Primary image is corrupt" would appear on
Jellyfin's own UI).

---

### F-014 · CorruptArtwork scanner flags nothing when planted poster.jpg has no associated Jellyfin item — see F-015 for the underlying pattern

- **Test ID**: 01-scanners.md §A.6–A.10
- **Chapter file**: 01-scanners.md
- **Component**: docs — fixture design in §A (see F-015 for root cause)
- **Severity**: medium
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 2026-08-28 (fresh QA #2)

**Repro** — same as F-015 step 1–4. `Get-Issues CorruptArtwork` returns 0.

**Expected** — 3 (per §A.7).

**Actual** — 0. The seeded posters are not associated with any
Jellyfin item, so the item-scoped scanner walks past them.

**Evidence** — same log lines as F-015.

**Suggested area (best guess, not required)** — see F-015. This entry
exists so §A.6-A.10 have a checkbox-scoped reference in FINDINGS.md;
the actual fix is F-015.

**Ambiguity flag** — n/a.

---

### F-013 · Fixer runs multiple ffmpeg passes per file when several stream ops are queued — should be a single combined pass

- **Test ID**: 02-fixers.md (cross-cutting — applies to any Automatic-mode
  file that trips two or more of transcode / audio-language / subtitle
  removal / resolution downscale in the same run)
- **Chapter file**: 02-fixers.md (deferred — filed by maintainer, not
  observed during a chapter-01 test)
- **Component**: fixer pipeline — the code that orchestrates ffmpeg calls
  when a single item has multiple queued fixes
- **Severity**: medium
- **Category**: performance
- **Observed**: 2026-08-28 — filed from a maintainer report, not a live
  test
- **Session**: 2026-08-28 (fresh QA #2)

**Repro**

1. Seed a fixture that trips at least two fixer categories in the same
   run — e.g. a 2160p H.264 file with a `deu` audio track and a `fra`
   subtitle track, when the config is `TranscodeFixMode=Automatic`,
   `AudioFixMode=Automatic`, `SubtitleFixMode=Automatic`.
2. Run a scan + fix cycle, or wait for auto-queue.
3. Watch the log / ffmpeg process tree during the fix run.

**Expected**

A single `ffmpeg` invocation per file that:
- transcodes the video stream once (or `-c:v copy` if downscale isn't
  wanted),
- drops the unwanted audio tracks via `-map -0:a:<n>`,
- drops the unwanted subtitle tracks via `-map -0:s:<n>`,
- re-muxes into the target container once.

Reason: each pass re-decodes the source, which for a real 26.9 GB REMUX
means dozens of GB of disk I/O per pass and a proportional wall-clock
hit. For an H.264 → HEVC re-encode of a two-hour movie, three passes
easily crosses the hour mark on a mid-range CPU that would finish the
combined run in 20-25 min.

**Actual**

The fixer chains one ffmpeg call per fix category. A file with
transcode + audio-lang + sub-lang + resolution change ends up going
through three or four separate ffmpeg invocations, each reading the
whole source and writing an intermediate. Beyond the wall-clock cost,
every intermediate write is a second chance to hit the "≥ 2× source
size free space" invariant (INDEX safety §5) — a file that comfortably
passes for one pass can fail on the third when the previous intermediate
is still around.

**Evidence**

n/a — reported by the maintainer during session 2026-08-28. No captured
log yet; a chapter-02 test will collect one once dry-run can be flipped
ON safely and a repro fixture is seeded.

**Suggested area (best guess, not required)**

The fix orchestration point that iterates queued fixes for a single item
— likely `FixTask` or a per-item planner it delegates to. The change
would replace `foreach (fix in queued) { runFfmpeg(fix) }` with a
"combine into one ffmpeg command spec, then run once" planner. Care
needed for:

- Fix disposal — combined pass still needs per-category dry-run / recycle
  routing so a partial failure doesn't lose the wrong file.
- The transcode + downscale combination (`-vf scale=…` + `-c:v hevc_*`
  in one graph).
- Container change (`.mkv → .mp4`) — should still be the final step, not
  a separate remux pass on the intermediate.
- Free-space math — `2 × source` estimate still holds, just for one
  intermediate instead of a chain.

**Ambiguity flag**

Ambiguity: the maintainer's report describes the shape ("multiple encode
passes per file if the user is removing audio, subtitles and changing
resolution etc — should be bundled into one ffmpeg run"). Filed
verbatim; a chapter-02 test session will pin down exactly which
categories chain and where the pass boundary is.

---

### F-012 · `00-setup.md` §2.6 references `Environment.ffmpegPath` — field is not in the response

- **Test ID**: 00-setup.md §2.6
- **Chapter file**: 00-setup.md
- **Component**: docs / `/MediaDash/Environment` response DTO
- **Severity**: medium
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 2026-08-28 (fresh QA #2)

**Repro**

1. `$e = curl.exe -s -H "Authorization: $env:JFAUTH" http://localhost:8099/MediaDash/Environment | ConvertFrom-Json`
2. `$e | Get-Member -Type NoteProperty`

**Expected**

Per §2.6, `(...).ffmpegPath` returns the resolved ffmpeg location so the
tester can pipe it into `make-fixtures.sh`.

**Actual**

The response has exactly five fields: `PluginVersion`, `JellyfinVersion`,
`Os`, `Framework`, `SubtitleProviders`. No `ffmpegPath`, no `FfmpegPath`,
no `ffmpeg`. `$e.ffmpegPath` is `$null`; using it as `"$ff"` to bash gives
an empty ffmpeg arg and the generator falls through to whatever `ffmpeg`
is on PATH (which, on this dev machine, is nothing — the Jellyfin bundled
copy at
`C:\Users\crackruckles\Downloads\jellyfin_10.11.11-amd64\jellyfin\ffmpeg.exe`
is not on PATH).

Practical impact: §2.6 as written silently generates fixtures with an
empty ffmpeg path. On a fresh test machine, `bash make-fixtures.sh` will
error with `ffmpeg: command not found` (or worse — silently produce
nothing) rather than actually regenerating anything.

**Evidence**

text only:
```
> $e | Get-Member -Type NoteProperty
   TypeName: System.Management.Automation.PSCustomObject

Name              MemberType   Definition
----              ----------   ----------
Framework         NoteProperty string Framework=.NET 9.0.16
JellyfinVersion   NoteProperty string JellyfinVersion=10.11.11
Os                NoteProperty string Os=Microsoft Windows 10.0.26200
PluginVersion     NoteProperty string PluginVersion=0.0.0.0
SubtitleProviders NoteProperty System.Object[] SubtitleProviders={}
```

**Suggested area (best guess, not required)**

Two independent fixes:

1. **Backend gap:** the plugin does not surface the ffmpeg location, but
   `IMediaEncoder.EncoderPath` is what Jellyfin uses internally. If
   MediaDash added `FfmpegPath` (and probably `FfprobePath`) to the
   `Environment` response, callers and the fixture-regen path would work.
   Suspect file: `MediaDashController.Environment` action + its DTO.
2. **Docs stopgap:** until the field exists, `00-setup.md` §2.6 must
   discover ffmpeg out-of-band. On this machine the bundled binary is at
   `C:\Users\crackruckles\Downloads\jellyfin_10.11.11-amd64\jellyfin\ffmpeg.exe`.
   The doc should either enumerate `%USERPROFILE%\Downloads\jellyfin_*\jellyfin\ffmpeg.exe`
   with `Get-ChildItem`, or resolve it from the Jellyfin `EncodingConfiguration`
   at `%JFDATA%\config\encoding.xml` (`<EncoderAppPath>...</EncoderAppPath>`).

**Ambiguity flag**

Ambiguity: I could not confirm whether `Environment` used to expose
`ffmpegPath` in a prior plugin version — the installed build reports
`0.0.0.0` (F-010), so I have no version anchor. Interpreted as: current
build lacks the field; docs reference a field that doesn't exist.

---

### F-011 · Dev-box safety posture: DryRun=false and 6/19 FixModes set to Automatic

- **Test ID**: 00-setup.md §6.1, §6.2
- **Chapter file**: 00-setup.md
- **Component**: env / PluginConfiguration on this dev machine
- **Severity**: low
- **Category**: env
- **Observed**: 2026-08-28
- **Session**: 2026-08-28 (fresh QA #2)

**Repro**

1. `Select-String -Path "$env:JFDATA\plugins\configurations\Jellyfin.Plugin.MediaDash.xml" -Pattern "DryRun|FixMode"`

**Expected**

Per T-001, shipped defaults are `DryRun=true` and every `*FixMode=DetectOnly`
(except `StaleFixMode=Off`). The dev box owner has changed them, which
§6.2 says to *record* as informational — not to treat as a bug.

**Actual**

`DryRun=false`, and the following six modes are `Automatic`:
`TranscodeFixMode`, `SubtitleFixMode`, `AudioFixMode`,
`PlayabilityFixMode`, `MisplacedFixMode`, `SuspiciousFileFixMode`.
`DuplicateFixMode=ManualApprove`. All remaining modes are `DetectOnly`.

Consequence for later chapters: any scan I trigger can auto-queue a fix
in one of those six categories. Chapters 00–01 must therefore verify the
`before` / `after` library snapshot per §6.4 and cancel any fix run before
letting scan output propagate. Chapter 02 (fixers) will need to explicitly
flip DryRun ON before running destructive-fixer negative cases.

**Evidence**

`evidence/F-011/config-fixmodes.txt` — full config snippet.

**Suggested area (best guess, not required)**

n/a — this is a machine-state note, not a plugin defect. Kept in
FINDINGS.md so subsequent sessions know the invariant they must
preserve.

**Ambiguity flag**

n/a.

---

### F-010 · Installed plugin's `meta.json` reports `version=0.0.0.0` while its directory says `MediaDash_0.9.0.0`

- **Test ID**: 00-setup.md §4.1, §4.2
- **Chapter file**: 00-setup.md
- **Component**: installed plugin — `MediaDash_0.9.0.0\meta.json` (dev deploy artifact, not the catalog release)
- **Severity**: medium
- **Category**: env
- **Observed**: 2026-08-28
- **Session**: 2026-08-28 (fresh QA #2)

**Repro**

1. `Get-ChildItem "$env:JFDATA\plugins" -Directory -Filter "MediaDash*"` → `MediaDash_0.9.0.0`.
2. `Get-Content "$env:JFDATA\plugins\MediaDash_0.9.0.0\meta.json"` → `"version": "0.0.0.0"`, `"timestamp": "0001-01-01T00:00:00.0000000Z"`.
3. `Find-JfLog "Loaded plugin: `"MediaDash`""` →
   `[INF] Emby.Server.Implementations.Plugins.PluginManager: Loaded plugin: "MediaDash" "0.0.0.0"`.

**Expected**

Jellyfin's `Loaded plugin` line reports the version the tester is exercising.
`Md Status` and every finding henceforth would ideally identify the build
under test as `0.9.0.0` (matching the folder + presumed catalog tag).

**Actual**

Jellyfin reports `0.0.0.0`. Nothing on disk actually says `0.9.0.0` except
the folder name; `meta.json` is the value both Jellyfin and the plugin
manager consult, and it says `0.0.0.0`. The `targetAbi` (`10.11.11.0`)
does not match the shipped catalog's ABI target either (`10.11.0.0` per
the release-recipe memory), so this looks like a hand-rolled dev deploy
rather than a catalog install.

Practical impact: findings filed against "MediaDash 0.9.0.0" cannot be
attributed to a specific release, because the running build is unversioned.
Any future test that gates behaviour on `PluginInfo.Version` (e.g.
"regression fixed in 0.9.1") is unverifiable from the installed state.

**Evidence**

- `evidence/F-010/meta.json` — installed meta.json.
- `evidence/F-010/log-tail.txt` — the "Loaded plugin" line.

**Suggested area (best guess, not required)**

Whichever script writes `meta.json` on a dev deploy (or hand-copies from
`bin/Debug/net9.0`) — the version and timestamp fields are zeroed. The
publish recipe in the maintainer's memory says `dotnet publish -c Release
-p:Version=X.Y.Z`; a dev deploy that skips `-p:Version` will yield exactly
this. Suggest either (a) require a version bump in the dev deploy step, or
(b) write a stub meta.json from the folder name / `Directory.Build.props`
so `Loaded plugin` prints a usable version.

**Ambiguity flag**

Ambiguity: I cannot tell from inside the tester rules (no source access)
whether Jellyfin re-populates `meta.json` on load or reads it verbatim.
Interpreted as: the file on disk is the source of truth, and its `0.0.0.0`
matches what Jellyfin logs, so the file is what the plugin manager is
consulting.

---

### F-009 · `Md` helper in `00-setup.md` §3.4 silently invokes `mkdir` — collides with the built-in `md` alias

- **Test ID**: 00-setup.md §3.4; every subsequent chapter's `Md <Route>` call
- **Chapter file**: 00-setup.md (propagates to every downstream chapter that uses `Md`)
- **Component**: docs — the PowerShell helper defined in §3.4
- **Severity**: high
- **Category**: docs
- **Observed**: 2026-08-28
- **Session**: 2026-08-28 (fresh QA #2)

**Repro**

1. Follow `00-setup.md` §3.1–§3.4 verbatim in a fresh Windows PowerShell 5.1 session.
2. Run `$s = Md "Status"; $s | Format-List`.

**Expected**

Per §3.4, `Md Status` should return the parsed `/MediaDash/Status` JSON
object. Every later chapter uses this pattern (`Md Scan/Cancel POST`,
`Md Fix/Cancel POST`, `Md Reset POST`, etc.).

**Actual**

`Md "Status"` creates an empty directory named `Status` in the current
working directory (`C:\Users\crackruckles\Status` in this run) and
returns a `System.IO.DirectoryInfo` object. The MediaDash endpoint is
never called. `Get-Command Md` resolves to `Alias -> mkdir`, not to the
function defined in §3.4:

```
CommandType     Name                     Definition
-----------     ----                     ----------
Alias           md                       mkdir
```

PowerShell 5.1 resolves the built-in `md` alias in preference to a
user-defined function of the same name (case-insensitive), so every
`Md ...` call in the downstream chapters silently calls
`mkdir <first-arg>` instead of hitting Jellyfin. In addition to producing
zero test coverage, this pollutes the CWD with junk directories named
after route paths (`Status`, `Scan/Cancel` fails since `/` isn't legal,
`Fix/Cancel`, `Reset`, `Environment`, `Errors`, ...).

**Evidence**

- `evidence/F-009/repro.ps1` — minimal reproducer that defines the
  function verbatim and calls it. Prints "returned value = <path>" and
  `Test-Path .\Status` = `True`.

**Suggested area (best guess, not required)**

`docs/testing/00-setup.md` §3.4. Two viable fixes:

1. **Rename the function** — call it `Invoke-Md`, `Mdd`, `Jfmd`, or
   similar. `Md` and `md` cannot coexist with a plain alias in Windows PS.
2. **Kill the alias in the session** — prepend
   `Remove-Item Alias:md -Force -ErrorAction SilentlyContinue` before the
   `function Md` definition. This is the smallest diff but every
   downstream chapter still says `Md`, so the helper must be re-verified
   after each `powershell.exe` restart.

Option 1 is safer. Update every `Md <Path> [<Method>] [<Body>]` example
in chapters 03–07 to match.

**Ambiguity flag**

n/a — behaviour is deterministic and reproducible.

---

### F-008 · Auth checklist snippets fail — `X-Emby-Token` alone rejected, PS auth body sends invalid JSON

- **Test ID**: 00-setup.md §3.1, §3.2, §2.3; INDEX Conventions ("`$TOKEN`" usage)
- **Chapter file**: 00-setup.md
- **Component**: docs / auth
- **Severity**: high
- **Category**: docs
- **Observed**: 2026-08-27
- **Session**: 2026-08-27 (fresh QA #1)

**Repro**

Two independent problems:

*(a) §3.1 PowerShell auth returns 400.* Copy §3.1 verbatim and run. Jellyfin returns `400 Bad Request`. Repeat with curl and dump body:

```
> curl.exe -s -X POST -H "Content-Type: application/json" -H "X-Emby-Authorization: MediaBrowser Client=..." -d '{"Username":"test","Pw":"test"}' http://localhost:8099/Users/AuthenticateByName
{"status":400,"errors":{"$":["'U' is an invalid start of a property name. Expected a '\"'..."]}}
```

The body is passed to Jellyfin as literal `{Username:test,Pw:test}` (unquoted keys) — PowerShell's single-quote wrapping around the `-d` arg drops the double-quotes when running under this harness. Using `--data-binary @file` succeeds.

*(b) §3.2 and §2.3's `X-Emby-Token: $env:TOKEN` header alone is rejected.* Any request that uses only `-H "X-Emby-Token: <token>"` returns `401 Unauthorized`, including `/System/Info`, `/Users/Me`, `/Library/VirtualFolders`, `/MediaDash/Status`. Requests succeed only when the full MediaBrowser Authorization header is used:

```
-H 'Authorization: MediaBrowser Token="<token>", Client="MediaDash-E2E", Device="ps", DeviceId="e2e", Version="1"'
```

**Expected**

Both snippets in the docs work as written and every subsequent chapter's `curl.exe -H "X-Emby-Token: $env:TOKEN"` pattern authenticates.

**Actual**

Neither works on Jellyfin 10.11.11 as currently configured. Setup §3.1 fails 400 (PS quoting), §3.2 fails 401 (header insufficient), and every downstream chapter's curl invocation would 401.

**Evidence**

text only — inline exchanges above.

**Suggested area (best guess, not required)**

`docs/testing/00-setup.md` §3 — change the auth body to a here-doc / `--data-binary @` pattern (or `Invoke-RestMethod` with a hashtable, which serializes JSON correctly). Then switch every downstream chapter's `-H "X-Emby-Token: $env:TOKEN"` to the full MediaBrowser Authorization header format and store both `$env:TOKEN` and `$env:AUTHHDR` in the session so they can be reused verbatim.

**Ambiguity flag**

Ambiguity: I could not confirm whether `X-Emby-Token` used to work on an older Jellyfin build. Interpreted as: docs are stale, current 10.11.11 requires the combined Authorization header.

---

### F-007 · Jellyfin data directory is `jellyfin-v10`, not `jellyfin`

- **Test ID**: 00-setup.md §1.2, §1.3, §4.2, §4.3
- **Chapter file**: 00-setup.md
- **Component**: docs / setup instructions
- **Severity**: medium
- **Category**: docs
- **Observed**: 2026-08-27
- **Session**: 2026-08-27 (fresh QA #1)

**Repro**

1. Confirm server up: `Invoke-RestMethod http://localhost:8099/System/Info/Public` → Version 10.11.11.
2. `Get-ChildItem "$env:LOCALAPPDATA\" -Filter "jellyfin*"` lists three data dirs: `jellyfin`, `jellyfin-v10`, `jellyfin-v12`.
3. `jellyfin\log\` contains only stale logs (last write 2026-08-24). `jellyfin-v10\log\log_20260827.log` is the live log for today's session. `jellyfin-v10\plugins\MediaDash_0.9.0.0\` is the installed plugin. `jellyfin\plugins\MediaDash\` also exists but is a different, older copy.

**Expected**

Per docs, the data dir is `%LOCALAPPDATA%\jellyfin`. §1.3 says
`Get-Content "$env:LOCALAPPDATA\jellyfin\log\jellyfin*.log"` should tail the live log. §4.2 deploys to `$env:LOCALAPPDATA\jellyfin\plugins\MediaDash_$ver.0`.

**Actual**

Live data dir is `%LOCALAPPDATA%\jellyfin-v10`. The `jellyfin` folder contains a stale, unused install. §4.2's copy would drop the plugin into the wrong folder and Jellyfin would ignore it. §1.3's tail would return nothing (both because of the wrong dir and because the log file pattern is also wrong — see F-006).

**Evidence**

text only:
```
Name                  LastWriteTime
jellyfin              1/08/2026 6:34:40 AM
jellyfin-v10          6/05/2026 12:10:37 PM
jellyfin-v12          24/08/2026 9:34:06 PM
```

Live log excerpt at `evidence/F-001/log-tail.txt` was pulled from `jellyfin-v10\log\log_20260827.log`.

**Suggested area (best guess, not required)**

`docs/testing/00-setup.md` §1.2/§1.3/§4.2/§4.3 — replace `jellyfin` with `jellyfin-v10` (or note the version-suffixed layout used by recent Jellyfin builds). Also document the pattern for choosing between v10 and v12 co-installs.

**Ambiguity flag**

n/a.

---

### F-006 · Log file pattern `jellyfin*.log` does not match — files are `log_YYYYMMDD.log`

- **Test ID**: 00-setup.md §1.3; INDEX.md "Conventions" section; every "confirms the log line" step
- **Chapter file**: 00-setup.md (and every chapter that greps logs)
- **Component**: docs / setup instructions
- **Severity**: medium
- **Category**: docs
- **Observed**: 2026-08-27
- **Session**: 2026-08-27 (fresh QA #1)

**Repro**

1. `Get-Content "$env:LOCALAPPDATA\jellyfin-v10\log\jellyfin*.log" -Tail 5` → returns nothing.
2. `Get-ChildItem "$env:LOCALAPPDATA\jellyfin-v10\log\"` shows files are named `log_20260827.log`, `log_20260826.log`, etc.

**Expected**

The docs assume the log file matches glob `jellyfin*.log`.

**Actual**

Jellyfin 10.11.11 writes rolling logs as `log_YYYYMMDD.log`. `jellyfin*.log` returns no matches, so `Get-Content`, `Select-String`, and the log-tail evidence step in every chapter silently return empty.

**Evidence**

text only:
```
Name                   LastWriteTime
log_20260827.log       27/08/2026 9:04:53 PM
log_20260826.log       26/08/2026 11:50:19 PM
log_20260824.log       24/08/2026 10:05:45 AM
```

**Suggested area (best guess, not required)**

`docs/testing/00-setup.md` §1.3 and the "Conventions" block in `INDEX.md` — change the glob to `log_*.log` (or the full path in use).

**Ambiguity flag**

n/a.

---

### F-005 · Registered libraries don't match spec — no "Shows", plus extras

- **Test ID**: 00-setup.md §2.2, §2.3
- **Chapter file**: 00-setup.md
- **Component**: env / Jellyfin library layout
- **Severity**: high
- **Category**: env
- **Observed**: 2026-08-27
- **Session**: 2026-08-27 (fresh QA #1)

**Repro**

1. Fetch virtual folders:
   `curl.exe -s -H "Authorization: MediaBrowser Token=<token>, Client=..., Device=..., DeviceId=..., Version=..." http://localhost:8099/Library/VirtualFolders`
2. Enumerate names + collection types.

**Expected**

Per §2.2, exactly four libraries: Movies (`$LIB\movies`), Shows (`$LIB\shows`), Music (`$LIB\music`), Books (`$LIB\books`).

**Actual**

Five libraries are registered, all rooted under `C:\dev\mediadash-fixtures\` (not `$LIB` per §2.1 which would be `C:\dev\mediadash-testlib\`):

```
MediaDash Test    (movies)  -> C:\dev\mediadash-fixtures\movies
Test Audiobooks   ()        -> C:\dev\mediadash-fixtures\audiobooks
Test Books        (books)   -> C:\dev\mediadash-fixtures\books
Test Comics       (books)   -> C:\dev\mediadash-fixtures\comics
Test Music        (music)   -> C:\dev\mediadash-fixtures\music
```

No `shows` (tvshows) library exists — every chapter that seeds `$LIB\shows\...` fixtures will fail. `Test Audiobooks` has no collection type set (blank), which likely trips scanners that filter by kind.

**Evidence**

`evidence/F-005/response-virtualfolders.json`

**Suggested area (best guess, not required)**

Test-bed hygiene: setup docs need a "reset libraries" step (or setup was skipped by a prior tester and never redone). Either the docs must be updated to accept this layout, or 00-setup should include a "remove existing test libraries" step.

**Ambiguity flag**

n/a — spec is explicit about four libraries by name.

---

### F-004 · Fixture directory `C:\dev\mediadash\artifacts\fixtures\` does not exist

- **Test ID**: INDEX.md "Conventions"; 01-scanners.md §A.2, §A.4, §B.2–B.4, and every subsequent block referencing fixtures.
- **Chapter file**: 00-setup.md / 01-scanners.md
- **Component**: env / fixtures
- **Severity**: high
- **Category**: env
- **Observed**: 2026-08-27
- **Session**: 2026-08-27 (fresh QA #1)

**Repro**

1. `Test-Path "C:\dev\mediadash\artifacts\fixtures"` → False.
2. `Get-ChildItem "C:\dev\mediadash\artifacts\"` → only `mediadash_0.1.1.zip` + `.md5`. No `fixtures\` subtree.

**Expected**

Per INDEX Conventions and the fixture references in 01-scanners.md, the following files must exist:

- `artifacts/fixtures/artwork/good.jpg` (A.2, A.4)
- `artifacts/fixtures/audio/jpn-only.mkv`, `eng-jpn.mkv`, `untagged.mkv` (B.2–B.4)
- (and all remaining `artifacts/fixtures/...` referenced through 01-R)

**Actual**

The `fixtures` subtree does not exist. Zero fixture files are present. Every scanner test in 01 that copies from `artifacts/fixtures/...` will fail with `Copy-Item : Cannot find path`. Zero-byte / truncated / broken-payload synthesized cases (A.1, A.3, H.1–H.3) are runnable via PowerShell only.

**Evidence**

text only:
```
> Get-ChildItem C:\dev\mediadash\artifacts\
Name                     LastWriteTime
mediadash_0.1.1.zip      20/07/2026
mediadash_0.1.1.zip.md5  20/07/2026
```

**Suggested area (best guess, not required)**

Repo is missing an `artifacts/fixtures/` bundle (or generator script). Either commit a small binary bundle, script the generation (ffmpeg one-liners for the audio fixtures, ImageMagick for artwork), or document a download link. Blocks nearly every test in 01-scanners that depends on real payloads.

**Ambiguity flag**

n/a.

---

### F-003 · Test library root `$LIB` (`C:\dev\mediadash-testlib`) does not exist; live libraries point at `C:\dev\mediadash-fixtures`

- **Test ID**: 00-setup.md §2.1, §2.2, §5.2
- **Chapter file**: 00-setup.md
- **Component**: env / test bed layout
- **Severity**: high
- **Category**: env
- **Observed**: 2026-08-27
- **Session**: 2026-08-27 (fresh QA #1)

**Repro**

1. `Test-Path "C:\dev\mediadash-testlib"` → False.
2. `Test-Path "C:\dev\mediadash-fixtures"` → True.
3. Live Jellyfin libraries all point under `C:\dev\mediadash-fixtures\` (see F-005).

**Expected**

Per §2.1, `$LIB = "C:\dev\mediadash-testlib"` with four subfolders. Per §5.2, the plugin's recycle bin must be a subdirectory of `$LIB`.

**Actual**

`$LIB` doesn't exist. The live library root the plugin sees is `C:\dev\mediadash-fixtures\` — an entirely different path never referenced in the docs. This means:

- `Get-ChildItem $LIB -Recurse -File | Remove-Item -Force` (chapter Cleanup) targets a non-existent path — silent no-op.
- Every scanner fixture that seeds under `$LIB\<kind>\` writes to a path Jellyfin isn't watching, so the seeded files never enter any library.
- Rule §5.2 fails by construction — the bin is at `%LOCALAPPDATA%\jellyfin-v10\data\mediadash\recycle\`, outside both `$LIB` and `C:\dev\mediadash-fixtures\`. See F-002.

**Evidence**

`evidence/F-005/response-virtualfolders.json` shows all locations under `mediadash-fixtures`.

**Suggested area (best guess, not required)**

Setup docs need a strict "if the wrong libraries exist, delete them and recreate" gate, or the docs need to be updated to match whatever the current dev convention is. Recommend: pick one path (`mediadash-testlib` OR `mediadash-fixtures`) and align docs + repo scripts + any existing dev-machine state to it.

**Ambiguity flag**

Docs unambiguously say `mediadash-testlib`. The live state is `mediadash-fixtures`. I stopped rather than reconfigure Jellyfin (would require destructive UI actions that could lose the current dev tester's setup).

---

### F-002 · Recycle bin lives outside library root — safety invariant §5.2 violated

- **Test ID**: 00-setup.md §5.1, §5.2; INDEX safety-invariant #1
- **Chapter file**: 00-setup.md / INDEX.md
- **Component**: RecycleBin (Fixers.RecycleBin per log)
- **Severity**: critical
- **Category**: safety-invariant
- **Observed**: 2026-08-27
- **Session**: 2026-08-27 (fresh QA #1)

**Repro**

1. `curl.exe -s -H "Authorization: MediaBrowser Token=<token>,..." http://localhost:8099/MediaDash/Status` → `RecycleBinPath: "C:\\Users\\crackruckles\\AppData\\Local\\jellyfin-v10\\data\\mediadash\\recycle"`, `RecycleBinFileCount: 3`.
2. Live log entries in `jellyfin-v10\log\log_20260827.log` confirm the plugin has moved deleted media into that path — e.g.
   `Recycled "C:\dev\mediadash-fixtures\comics\broken-comic.cbz" -> "C:\Users\crackruckles\AppData\Local\jellyfin-v10\data\mediadash\recycle\..."`.

**Expected**

Per 00-setup §5.2, the bin must be a subdirectory of `$LIB`, NOT under `%TEMP%` or the user profile. Per INDEX safety invariant #1, the plugin must "never modify or delete a file outside configured library paths" — moving a file into the user profile from the library counts as touching a location outside the library.

**Actual**

Bin path is under `%LOCALAPPDATA%` (user profile). Files that were in a library have been moved (deleted from library, kept in bin) into user-profile space. `RecycleBinCrossVolume: false` says it's on the same drive, but the invariant is about location, not disk.

**Evidence**

`evidence/F-002/response-status.json` — full `/MediaDash/Status` JSON at time of session.
`evidence/F-001/log-tail.txt` — log lines showing recycle events (also relevant to F-001).
`evidence/F-001/PluginConfiguration.xml` — `<RecycleBinPath />` empty, so the plugin fell back to a default under `%LOCALAPPDATA%`.

**Suggested area (best guess, not required)**

Either (a) plugin default bin path must be under a configured library when `<RecycleBinPath>` is empty, or (b) the invariant docs must be updated to allow user-profile bin (which weakens the safety story). Bin fallback location is the likely code site — search for the default of `RecycleBinPath` when the config value is null/empty.

**Ambiguity flag**

n/a — the invariant text and observed path are clearly at odds.

---

### F-001 · Dry-run is OFF and destructive auto-fixes have already run — safety invariant #4 violated

- **Test ID**: 01-scanners.md session prep P.3; INDEX safety invariant #4
- **Chapter file**: 01-scanners.md / INDEX.md
- **Component**: PluginConfiguration.DryRun + FixTask + auto-queue path
- **Severity**: critical
- **Category**: safety-invariant
- **Observed**: 2026-08-27
- **Session**: 2026-08-27 (fresh QA #1)

**Repro**

1. Read `%LOCALAPPDATA%\jellyfin-v10\plugins\configurations\Jellyfin.Plugin.MediaDash.xml`.
2. Confirm `<DryRun>false</DryRun>`.
3. Grep today's log:
   `Select-String -Path "$env:LOCALAPPDATA\jellyfin-v10\log\log_20260827.log" -Pattern "MediaDash" -SimpleMatch`
4. Observe entries like:
   - `[INF] FixTask: Auto-queued 5 Playability issues`
   - `[INF] FixTask: MediaDash fix run: 5 queued issues (dry-run: False)`
   - `[INF] Fixers.RecycleBin: Recycled "C:\dev\mediadash-fixtures\comics\broken-comic.cbz" -> ...\jellyfin-v10\data\mediadash\recycle\...`
   - `[INF] Fixers.PlayabilityFixer: Playability fix: "removed unplayable file broken-comic.cbz (kept in recycle bin)"`
   - `[INF] LibraryManager: Removing item, Type: "Book", Name: "broken-comic", Path: "C:\dev\mediadash-fixtures\comics\broken-comic.cbz"`

**Expected**

Per INDEX safety invariant #4: "All destructive ops respect per-fix-type disposal (bin vs permanent) and the global dry-run toggle. **Dry-run defaults ON**."

Per 01-scanners session prep P.3, dry-run must be **ON** before any scanner block is run so that no auto-queued fix can execute during session prep.

**Actual**

Dry-run is persisted as `false`. The plugin has an auto-queue path (`FixTask: Auto-queued 5 Playability issues`) that runs without human approval when dry-run is off. Real user files have already been moved into the recycle bin during the current dev-machine state, before this QA session even began. Jellyfin then removed the corresponding library items.

Concretely: any future scan I trigger while in this state could enqueue more auto-fixes and delete more files. This is exactly the invariant the docs treat as session-stopping.

**Evidence**

- `evidence/F-001/PluginConfiguration.xml` — full config snapshot showing `<DryRun>false</DryRun>` and every FixMode in effect.
- `evidence/F-001/log-tail.txt` — 20 lines of MediaDash log entries showing the auto-queue + recycle + Jellyfin library-remove chain.
- `evidence/F-002/response-status.json` — `/MediaDash/Status` at time of session (RecycleBinFileCount=3, OpenIssueTotal=15).

**Suggested area (best guess, not required)**

Two orthogonal fixes:
1. Plugin default (post-install / first-run) should be `<DryRun>true</DryRun>`. Check the config initializer / migration path.
2. FixTask's `Auto-queued N Playability issues` path should refuse to execute (or downgrade to detect-only) when dry-run is off but the user has not explicitly acknowledged automatic destructive mode — search for `Auto-queued` log message.

**Ambiguity flag**

Ambiguity: the destructive fixes visible in the log were run before my session started. The invariant text says stop when a **test** appears to bypass an invariant. I interpreted "state that would guarantee the next destructive test bypasses an invariant" as equivalent, and stopped the session before executing any scan that could trigger auto-fixes. If the intended reading is "only stop if MY OWN test bypassed the invariant", then I should have flipped dry-run ON via the UI and continued — but I cannot drive the UI from this environment, and toggling via API/XML without instruction would exceed the "no fixes" rule.

---
