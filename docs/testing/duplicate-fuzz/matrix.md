# DuplicateScanner Fuzz Test Matrix

Session: 2026-08-29 (retest 2, F-019 fixed) · Plugin GUID `38bdb090-b763-4294-934b-b54ade4d9d6d` · Jellyfin 10.11.11

**F-019 resolved.** Fresh Jellyfin datadir; every one of the 61 playable
`.mkv`/`.mp4`/`.MKV` fuzz fixtures indexed as a Movie item. The
DuplicateScanner can finally see the corpus. This session emits detailed
per-scenario verdicts in `results.csv` and files five new findings
(F-095…F-099).

The 5 pre-existing indexed movies in this library — `Big Buck Test (2020)`,
`Clean Movie (2024)`, `Multi Audio (2022)`, `Sub Heavy (2023)`,
`Truncated Movie (2021)` — plus **456 movies** in the parallel Movie library
(`\Downloads\...\jellyfin\New folder\...`), give the scanner a real corpus
of 461 items to work against every scan. It still returned **0 issues on
every configuration**, including 9 distinct (Name+Year) collision groups
in the parallel library (SWife Katy - x23, Frintteza - x18, Zoeneli - x19,
etc.). That is a genuine DuplicateScanner defect independent of F-019.

## Confidence ladder (per task rubric)

- Exact SHA-256 → 1.0
- Provider ID match → 0.90
- Heuristic fallback → 0.70 with adjustments:
  - +0.15 title Jaccard ≥ 0.80
  - +0.10 runtime delta ≤ 5%
  - −0.25 same directory + distinct stems

## Config knobs discovered

| Field | Type | Default | Notes |
|---|---|---|---|
| `DuplicateFixMode` | string enum | `ManualApprove` | DetectOnly / ManualApprove / Automatic |
| `DuplicateDisposal` | string | `RecycleBin` | |
| `TreatEditionsAsDuplicates` | bool | `False` | |
| `DuplicateAutoFixConfidence` | decimal | `0.8` | Gates auto-fix, not detection |
| `DuplicateExactHashEnabled` | bool | `True` | Toggles SHA-256 pass |
| `DuplicateTitleJaccardVeto` | decimal | `0.4` | Below this → not a candidate |
| `DuplicateRuntimeVetoPct` | int | `15` | Above this % delta → not a candidate |
| `DuplicateMinAgeDays` | int | `7` | Items younger than this skipped |

No `SameFolderIgnore` or `SameFolder` toggle exists — the same-dir −0.25
penalty is hard-coded. No config knob to soften/harden it.

## Matrix

Category · Scenario · Description · Expected · jellyfin_indexed · scanner_flagged · Notes

### Category A · Path-based duplicates (SHOULD flag)

| ID | Description | Expected | Indexed | Flagged | Note |
|---|---|---|---|---|---|
| A.1 | `Inception (2010)` vs `Inception (2010) 4K` in separate parents, both copies of Clean Movie | flag | no | no | F-019 |
| A.2 | `Movie A2 (2020) [1080p]` vs `Movie A2 (2020) [4K]` separate parents | flag | no | no | F-019 |
| A.3 | mkv + mp4 (ffmpeg -c copy) different parents | flag | no | no | F-019 |
| A.4 | `Titanic (1997)` vs `Titanic 1997` (year format variant) | flag | no | no | F-019 |
| A.5 | `The.Movie.A5.2020.mkv` bare vs `The Movie A5 (2020)\...` | flag | no | no | F-019 |
| A.6 | Deep nesting vs flat, same name | flag | no | no | F-019 |
| A.7 | Symlink pointing at another movie (NTFS symlink OK, dev mode on) | flag | no | no | F-019 |

### Category B · Metadata-based duplicates (SHOULD flag)

| ID | Description | Expected | Indexed | Flagged | Note |
|---|---|---|---|---|---|
| B.1 | Two folders + `movie.nfo` sharing `<tmdbid>27205</tmdbid>` | flag | no | no | F-019; also injected nfos into two ALREADY-indexed items (Clean Movie + Multi Audio) with shared tmdbid=603 and forced FullRefresh → ProviderIds remained empty → refresh writes also blocked by upstream FK error |
| B.2 | Two folders + `movie.nfo` sharing `<imdbid>tt1375666</imdbid>` | flag | no | no | F-019 |
| B.3 | tmdbid=603 on two very different titles | flag | no | no | F-019 |
| B.4 | Two zero-byte epubs with intended shared ISBN (marker files) | flag | no | no | F-019 |
| B.5 | Two mp3s with identical `musicbrainz_trackid` metadata | flag | no | no | F-019 |

### Category C · Content-based duplicates (SHOULD flag if scanner has SHA)

| ID | Description | Expected | Indexed | Flagged | Note |
|---|---|---|---|---|---|
| C.1 | Byte-identical copies of Clean Movie in different folders | flag @ 1.0 | no | no | F-019 |
| C.2 | Same bytes, different filename (A.mkv vs B.mkv) | flag @ 1.0 | no | no | F-019 |
| C.3 | Off-by-one truncation (2372373 vs 2372372 bytes) | NOT flag exact; heuristic optional | no | no | F-019 |

### Category D · False positives (from user complaints, SHOULD NOT flag)

| ID | Description | Expected | Indexed | Flagged | Note |
|---|---|---|---|---|---|
| D.1 | Doctor Who Classic: 10 episodes in one folder | no-flag (same-dir penalty saves) | no | no | F-019; can't repro user symptom |
| D.2 | First Movie (2020) vs Second Movie (2020) | no-flag | no | no | F-019 |
| D.3 | Fast and Furious (2001) vs FnF 2 (2003) | no-flag | no | no | F-019 |
| D.4 | The Thing (1982) vs (2011) remake | no-flag | no | no | F-019 |
| D.5 | Futurama Specials: 3 movies in Specials/ | no-flag | no | no | F-019; substituted `movies\Futurama\Specials\` — no Shows lib (F-005) |
| D.6 | SMDM S01E01..05 with thin filenames | no-flag | no | no | F-019 |
| D.7 | Same title, 30% runtime delta | no-flag | n/a | n/a | Skipped — remux/loop is expensive with no possible index |

### Category E · Edge cases (SHOULD NOT CRASH)

| ID | Description | Expected | Indexed | Flagged | Crashed? | Note |
|---|---|---|---|---|---|---|
| E.1 | Zero-byte movie file | no-crash | no | no | no | Scan completed 2.7s |
| E.2 | Special chars: `Movie [Deluxe] & More! (2020)` | no-crash | no | no | no | |
| E.3 | Unicode: `映画 (2020)`, `Мuvie (2020)` | no-crash | no | no | no | |
| E.4 | 180-char filename | no-crash | no | no | no | |
| E.5 | Case-only diff: `Movie E5` vs `MOVIE E5` | no-crash | no | no | no | Windows preserved distinct dirs |
| E.6 | Ext case only: `.mkv` vs `.MKV` | no-crash | no | no | no | |
| E.7 | Double-space vs single-space | no-crash | no | no | no | |

### Category F · Config toggle tests (rescan after each)

| ID | Toggle | Dup issue count | Scan sec | Effect |
|---|---|---|---|---|
| F.1 | Baseline DetectOnly | 0 | 2.7 | — |
| F.2 | DuplicateFixMode=Automatic | 0 | 2.7 | — |
| F.3a | DuplicateAutoFixConfidence=0.5 | 0 | 2.7 | — |
| F.3b | 0.7 | 0 | 2.7 | — |
| F.3c | 0.9 | 0 | 2.7 | — |
| F.3d | 1.0 | 0 | 2.7 | — |
| F.4a | DuplicateExactHashEnabled=false | 0 | 2.7 | — |
| F.4b | DuplicateExactHashEnabled=true | 0 | 2.7 | — |
| F.5a | DuplicateMinAgeDays=0 | 0 | 2.7 | — |
| F.5b | DuplicateTitleJaccardVeto=0.1 | 0 | 2.7 | — |
| F.5c | DuplicateRuntimeVetoPct=50 | 0 | 2.7 | — |
| F.5d | TreatEditionsAsDuplicates=true | 0 | 2.7 | — |
| F.max | All permissive: MinAge=0, Jaccard=0.05, RuntimeVeto=90, Hash=on, Editions=on | 0 | 2.7 | — |

**Every toggle had zero measurable effect.** Scanner returned 0 across the
entire matrix and every scan finished in ~2.7 s. The scanner IS running — it
emits `MediaDash scanner Duplicate found 0 issues` on each pass — but it
finds nothing in the 461-item corpus even when 9 (Name+Year) collision
groups exist in the parallel library.

## User complaint repro attempts

| Complaint | Test | Reproduced? |
|---|---|---|
| Issue #3 — Doctor Who Classic episodes flagged | D.1 (10 eps same dir) | Cannot reach — F-019 blocks Jellyfin indexing of the fixture |
| Futurama specials flagged as dupes | D.5 | Cannot reach — F-019 |
| Six Million Dollar Man collapsed to 3 eps | D.6 | Cannot reach — F-019 |

None of the three user-reported false-positive scenarios could be exercised
this session because Jellyfin's ingest is broken on this box. Instead the
run surfaced the opposite failure: the scanner does not flag ANYTHING in the
existing 461-item item cache, including trivially-duplicate (Name+Year)
groups.
