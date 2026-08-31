# Single-pass Encode System — Design

Goal: **every file that has queued fixes gets touched by ffmpeg once,
not N times**.

Ties together:
- Issues **#28** (combine sub + audio) and **#32** (multiple processes
  on same file)
- Issue **#31** (file dates — solved along the way)
- Issue **#34** (throttle — becomes cheaper, since throttling once is
  simpler than throttling N times)
- Audit finding **F-013** (fixer perf) and **F-016** (timestamp
  preservation)
- `ISSUES-WORKTHROUGH.md` **PR-2**
- `FEATURE-REQUESTS.md` **F-REQ-1**, **F-REQ-2**, **F-REQ-3**

---

## Current pipeline (what we're replacing)

`ScheduledTasks/FixTask.cs:267-346` iterates queued issues one at a
time. For each `Issue`, it looks up the registered `IFixer` and calls
`fixer.FixAsync(issue, ct)`. Each fixer:

1. Reads the source file
2. Runs its own ffmpeg command (via `Fixers/FfmpegExecutor.cs`)
3. Verifies the output (via `Fixers/OutputVerifier.cs` — 2 s slack)
4. Moves the OLD source to the recycle bin
5. Renames the tmp output into place

For a file with three queued fixes (transcode + audio-lang +
sub-lang), that's:

- **3 × source read** — 3 × the file's byte size hit the disk
- **3 × ffmpeg encode** — proportional CPU/GPU
- **3 × verify** — 3 × decode of the produced file
- **2 × recycle intermediate** — bin fills up with transient states
- **~3 × wall clock**

For a 20 GB REMUX, three passes ≈ 60 GB of transient I/O plus 2 – 3
hours of encode time on mid-range hardware. F-013 filed against this;
users hit it in #28 and #32.

---

## Proposed pipeline

**One `FixPlan` per file. One ffmpeg command per `FixPlan`. One
verify per plan. One bin entry per plan.**

Data flow:

```
FixTask
  ├── group Queued issues by Path            (new: PlanCoordinator)
  ├── for each group:
  │     ├── build FixPlan (list of ops)      (new: FixPlanBuilder)
  │     ├── if plan.HasFfmpegOps:
  │     │     ├── FfmpegExecutor.RunPlan     (extended)
  │     │     ├── OutputVerifier.VerifyPlan  (extended)
  │     │     └── on success:
  │     │           ├── preserve timestamps  (new: OutputFinalizer)
  │     │           ├── recycle source       (existing RecycleBin)
  │     │           └── swap into place      (existing)
  │     ├── else (no ffmpeg — e.g. recycle-only, artwork-only):
  │     │     └── fall back to per-fixer path (unchanged)
  │     └── mark each Issue in the group Fixed | Failed
```

---

## Component design

### 1 · `Fixers/FixPlan.cs` (new)

```csharp
public sealed class FixPlan
{
    public string SourcePath { get; init; }
    public FfprobeData SourceProbe { get; init; }
    public IReadOnlyList<Issue> Issues { get; init; }
    public IReadOnlyList<IFixOp> Ops { get; init; }

    public bool HasFfmpegOps => Ops.Any(o => o.Kind != FixOpKind.NonFfmpeg);
    public string TargetContainer { get; init; } // .mkv unless swap op
    public bool DryRun { get; init; }
}

public interface IFixOp
{
    FixOpKind Kind { get; }
    Guid IssueId { get; }
    // Contribute to the argv:
    void ContributeMap(FfmpegArgsBuilder args);        // e.g. -map -0:a:2
    void ContributeCodec(FfmpegArgsBuilder args);      // e.g. -c:v libx265
    void ContributeFilter(FfmpegArgsBuilder args);     // e.g. -vf scale=1920:1080
    // Contribute an assertion the verifier checks post-encode:
    void ContributeVerification(VerificationSpec vs);  // e.g. no audio track 2
}

public enum FixOpKind
{
    NonFfmpeg,          // recycle, artwork, orphan — skip planner
    TranscodeVideo,     // video codec / bitrate / resolution change
    DropAudioTrack,     // remove one audio stream
    DropSubtitleTrack,  // remove one sub stream
    StripEmbeddedArt,   // -map -0:v:1 for attached_pic
    ContainerSwap,      // .mkv → .mp4
    RenameSpec,         // (no ffmpeg) — filename fix; skip planner
    SubtitleExtract     // subs → sidecar files (see F-REQ-6)
}
```

### 2 · `Fixers/FixPlanBuilder.cs` (new)

- Input: `IReadOnlyList<Issue>` for a single file path
- For each Issue, look up its `IFfmpegFixPlanner` (registered in DI
  by IssueType), call `planner.Plan(issue, sourceProbe)` → `IFixOp`
- Validate ops are compatible. E.g. two `TranscodeVideo` for the same
  file is a bug — coalesce or error.
- Order ops for ffmpeg (filters before codecs before format).
- Return `FixPlan` or `PlanError` (unfixable — one op is
  unimplemented in combined mode → fall back to per-fixer path).

### 3 · `Fixers/IFfmpegFixPlanner.cs` (new interface)

One implementation per IssueType:

- `TranscodeFixPlanner` — reads `PluginConfiguration.MaxResolutionHeight`,
  `MaxBitrateMbpsAt1080p`, `CodecPreferenceOrder`, produces
  `TranscodeVideo` op with `-vf scale=…`, `-c:v libx265`, `-b:v …`.
- `TrackFixPlanner` — for AudioLanguage / SubtitleLanguage issues
  from `IssueType.AudioLanguage` + `SubtitleLanguage`. Produces one
  or more `DropAudioTrack` / `DropSubtitleTrack` ops (per removed
  index). Uses `Issue.DetailsJson.removeIndexes`.
- `EmbeddedCoverArtFixPlanner` — strips attached_pic streams.
- `SubtitleFontFixPlanner` — for `.ass` sidecar font pruning (not
  ffmpeg; skips planner but consumes the plan spot).
- `SubtitleExtractFixPlanner` — F-REQ-6 sidecar export.

Each planner receives the same `FfprobeData` and adds ops idempotently.

### 4 · `Fixers/FfmpegExecutor.cs` extension

`RunPlan(FixPlan plan, string tmpOutputPath, CancellationToken ct)`:

- Assemble argv:
  ```
  ffmpeg -y -v error \
    -avoid_negative_ts make_zero -fflags +genpts \  (fixes F-039)
    -i <source> \
    <all -map from all ops, combined> \
    <all -c:v / -c:a / -c:s from ops> \
    <all -vf filter chain from ops> \
    -movflags +faststart \                            (if mp4 target)
    <tmpOutputPath>
  ```
- Set `ProcessPriorityClass.BelowNormal` (already there).
- On Linux, prefix `ionice -c 3 -n 7` if configured (F-REQ-3).
- On Windows, apply `FixerMaxIoBandwidthMBps` via `SetInformationJobObject`
  with `JobObjectIoRateControl` if configured (F-REQ-3).
- Stream stderr into `Diagnostics` so the "why did it fail" tail is
  captured for issue #36 / F-REQ-10.

### 5 · `Fixers/OutputVerifier.cs` extension

`VerifyPlan(FixPlan plan, string outputPath)`:

- Run ffprobe once against `outputPath`
- For each op, call `op.VerifyOutput(probe)`:
  - `TranscodeVideo` → codec matches target, width/height ≤ ceiling
  - `DropAudioTrack` → audio stream count = source_count − dropped
  - `DropSubtitleTrack` → sub stream count = source_count − dropped
  - `StripEmbeddedArt` → no `codec_type=video` with `disposition.attached_pic=1`
- Duration check with slack `max(2.0s, srcDurationSec × 0.005)`
  (fixes #39; wider than current 2 s hard slack).
- If ANY op fails verification → rollback the whole plan. All
  Issues in the plan stay `Queued`.
- If all pass → issue-by-issue status flip to `Fixed`.

### 6 · `Fixers/OutputFinalizer.cs` (new — F-016)

`SwapAndPreserveStamps(srcPath, tmpOutputPath, container)`:

```csharp
var src = new FileInfo(srcPath);
var origCreated = src.CreationTimeUtc;
var origModified = src.LastWriteTimeUtc;

// Recycle old source with manifest that lists all the fixes applied
_recycleBin.MoveToBin(srcPath, planManifest);

// Move tmp to final path (may be a different extension if container swap)
var finalPath = container == null
    ? srcPath
    : Path.ChangeExtension(srcPath, container);
File.Move(tmpOutputPath, finalPath);

// Preserve source stamps (fixes #31)
File.SetCreationTimeUtc(finalPath, origCreated);
File.SetLastWriteTimeUtc(finalPath, origModified);

return finalPath;
```

Called from `FixTask` after `VerifyPlan` succeeds.

### 7 · `ScheduledTasks/FixTask.cs` change

Before the current per-issue loop at `:267-346`, add:

```csharp
var groups = queuedIssues
    .GroupBy(i => i.Path)
    .Select(g => new { Path = g.Key, Issues = g.ToList() });

foreach (var group in groups)
{
    if (group.Issues.Count == 1)
    {
        // Single-issue path — use existing per-fixer flow
        var fixer = _fixers[group.Issues[0].Type];
        await ProcessSingleAsync(fixer, group.Issues[0], ct);
        continue;
    }

    // Multi-issue combined path
    var plan = _planBuilder.Build(group.Issues);
    if (plan is null)
    {
        // Any op unplannable → fall back to serial single-issue path
        foreach (var i in group.Issues)
            await ProcessSingleAsync(_fixers[i.Type], i, ct);
        continue;
    }

    var result = await _combinedFixExecutor.RunAsync(plan, ct);
    foreach (var i in group.Issues)
        _db.UpdateIssueStatus(i.Id, result.PerIssueStatus[i.Id]);
}
```

**Key correctness rule (fixes F-086):** `plan.DryRun` must be threaded
into every `FixResult.WasDryRun`. `FixTask.cs:383-386` gate on
`!result.WasDryRun` — the combined path must respect it too. Add
`Debug.Assert(config.DryRun == result.WasDryRun)` at the join point.

---

## What can and can't combine

**Can combine (share one ffmpeg pass):**
- `TranscodeFixer` (video codec / bitrate / resolution)
- `TrackFixer` for audio-language drops
- `TrackFixer` for subtitle-language drops
- `EmbeddedCoverArtFixer` (strip attached_pic)
- `SubtitleExtractFixer` (F-REQ-6 — extract subs to sidecar; this is
  a companion pass but same source read)

**Cannot combine (different tools):**
- `SubtitleFontFixer` — parses `.ass` sidecar, edits in place
- `NfoFixer` — XML edit
- `ArtworkFixer` — image processing (SkiaSharp)
- `TrickplayOptimizeFixer` — JPG → WebP conversion

**Should not combine (destructive; runs alone):**
- `PlayabilityFixer` — recycles the whole file
- `OrphanCleanupFixer` — deletes files/folders
- `MediaGrouperFixer` — moves files into per-title folders
- `MediaSorterFixer` — cross-library moves
- `DuplicateFixer` — recycles the duplicate
- `SuspiciousFileFixer` — recycles the .exe

The planner detects these and either splits them out or falls back
to serial per-fixer.

---

## Migration plan

**Phase 0 — refactor without behaviour change:**
- Extract `FfmpegArgsBuilder` from the existing fixers.
- Wrap ffmpeg-touching fixers behind a shared entry point.
- No user-visible change.

**Phase 1 — combined path behind a feature flag:**
- New config field `PluginConfiguration.CombinedFixEnabled` (bool,
  default **false**).
- `FixTask` uses combined path only when enabled.
- Both paths active in parallel; users opt in.
- Ship telemetry (opt-in): total fixes run, per-file pass count
  before/after, avg wall time delta.

**Phase 2 — flip default to true:**
- After a release cycle of production traffic, default becomes true.
- Serial path stays available via the flag for regression testing
  and edge cases (large-file, low-memory scenarios).

**Phase 3 — remove per-fixer ffmpeg calls:**
- Once combined path is proven, delete the duplicated ffmpeg-invoking
  code inside `TranscodeFixer` / `TrackFixer` / etc; they become
  planners only.
- Kills the possibility of drift between "single fix" and "combined
  fix" behaviour.

---

## Testing plan

Every phase needs a regression harness. The audit already has the
skeleton — extend it:

**Unit level (`Jellyfin.Plugin.MediaDash.Tests`):**
- `FixPlanBuilder_TranscodeAndAudioDrop_MergesIntoOnePass()`
- `FixPlanBuilder_TranscodeAndArtworkFix_SplitsIntoTwoPasses()`
- `FixPlanBuilder_DryRunNeverInvokesFfmpeg()`
- `OutputVerifier_MultipleOpVerifications_AllPassOnHappyPath()`
- `OutputVerifier_OneOpFailsVerification_WholePlanRollsBack()`
- `OutputFinalizer_PreservesSourceTimestamps()`

**Integration level (against a Jellyfin dev instance):**
- Seed `Multi Audio + Sub Heavy` fixture with both audio-lang AND
  sub-lang issues queued. With `CombinedFixEnabled=true`, verify:
  - one ffmpeg process spawned
  - resulting file has only English audio + no fra/deu subs
  - one bin entry with two-issue manifest
  - `DateCreated` / `DateModified` preserved from source
- Same fixture with `CombinedFixEnabled=false`: verify old behaviour
  (two ffmpeg processes, two bin entries).
- **F-091 prereq**: this requires F-019 fixed to seed items on the
  dev box.

**Load level:**
- Repeat with 100 fixtures with random mixes of queued fix types.
- Compare wall clock + total ffmpeg process count old vs new.
- Confirm no memory growth over the loop (RSS returns to baseline
  each GC).

---

## Rollout risks

**R-1 · Complex ffmpeg command lines are harder to debug.**
- Mitigation: `Diagnostics.LastFfmpegCommand` field captures the
  exact argv on failure. `[Copy diagnostics]` button (F-REQ-11)
  surfaces it to bug reports.

**R-2 · One pass failing means the whole plan rolls back — user
  loses ALL fixes for that file even though some may have been
  fine.**
- Mitigation: on rollback, the planner writes each issue's
  `Detail = "combined plan rollback: op X failed verification with …"`
  so the user knows which op poisoned the plan. Optional: on the
  second attempt, the planner excludes the failing op and tries
  the rest.

**R-3 · Combined -vf filter chains can produce different visual
  output than sequential passes.**
- Mitigation: golden-file regression tests over a small library
  of representative fixtures. First release restricts the combined
  path to configurations where `-c:v copy` is possible on video +
  only audio/sub drops (no re-encode) — safest combination.

**R-4 · Config surface grows: users need to understand what
  combines and what doesn't.**
- Mitigation: docs pattern — one-line rule table. UI: on the
  issue card, when multiple fixes are queued for the same file,
  render a "will run as one combined fix" hint.

**R-5 · Combined pass can exceed the 2 × source-size free-space
  invariant if the intermediate + source both sit on the same
  volume mid-run.**
- Mitigation: pre-flight free-space check runs against the maximum
  expected intermediate size — for `-c:v copy` that's ≈ source
  size + 5 %; for full transcode it's still ≈ source size (bounded
  by target bitrate).
- Reuse existing `FixTask` free-space guard at `:274` but scope it
  to the plan, not per-issue.

**R-6 · `dry-run` in the combined path must NOT invoke ffmpeg.**
- Mitigation: the executor's first line is `if (plan.DryRun)
  { LogPlannedCommand(); return DryRunResult(plan); }`. Same
  contract F-086 enforces: `WasDryRun` = `config.DryRun`, no
  exceptions.

---

## Estimated impact

- **Wall clock**: 60-70 % reduction for files with 2 fix categories,
  ~80 % for files with 3+.
- **Disk I/O**: reduction proportional to N-1 avoided intermediate
  writes.
- **CPU/GPU**: cleaner utilisation (one long ffmpeg vs three
  medium ones with process-spawn overhead + priority resets).
- **User pain**: bin no longer accumulates one entry per
  intermediate (fewer files in `[Recycle bin] Restore` picker).

---

## Prerequisites in the audit / open work

Before this can land or be tested end-to-end:

- **F-019 / F-091** — Jellyfin item indexing must work. Otherwise
  the E2E tests can't seed multi-fix items.
- **F-020 / F-032** — the `DetailsJson` shape drift needs to be
  stable so the planner can rely on `removeIndexes` etc.
- **F-086** — dry-run DB status semantics fixed. The combined
  path's dry-run must not flip status to Fixed.

---

## References

- Source review: `Fixers/TrackFixer.cs:170-178`,
  `Fixers/TranscodeFixer.cs:216-229`,
  `Fixers/FfmpegExecutor.cs:97, 233-248`,
  `Fixers/OutputVerifier.cs:53-100`,
  `ScheduledTasks/FixTask.cs:267-346, 383-399, 612-625`.
- Feature request tracker: `FEATURE-REQUESTS.md` F-REQ-1, F-REQ-2,
  F-REQ-3, F-REQ-6.
- Bug workthrough: `ISSUES-WORKTHROUGH.md` PR-2, PR-5.
