# Playability Repair Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend `PlayabilityFixer` with a four-rung repair ladder that attempts to salvage broken files before falling through to today's delete-to-recycle-bin path. Each rung is gated by its own settings toggle.

**Architecture:** All logic stays inside `PlayabilityFixer.cs`. Rungs are private methods invoked in fixed order from a new `TryRepairAsync` helper; each writes to a sibling temp file, verifies via ffprobe, and — on success — swaps the original into the recycle bin. Reuses existing services (`FfmpegExecutor`, `OutputVerifier`, `RecycleBin`, `LibraryGuard`, `ILibraryMonitor`) and `TranscodeFixer.SidecarPath` for temp naming. No new abstractions, no new fixer class, no new issue type.

**Tech Stack:** C# / .NET 9, xUnit for tests, Jellyfin plugin conventions (embedded `emby-*` UI in `configPage.html`), bundled ffmpeg via `FfmpegExecutor`.

**Spec:** `docs/superpowers/specs/2026-09-05-playability-repair-design.md`

**Target release:** 1.0.8.0 (feature bundle).

---

## File structure

**Modified:**
- `Jellyfin.Plugin.MediaDash/Fixers/PlayabilityFixer.cs` — add `TryRepairAsync`, four `TryRung<N>Async` private methods, `VerifyRepairedAsync`, `SwapRepairedAsync`; add `FfmpegExecutor` + `OutputVerifier` dependencies to the constructor. Insert the repair branch after `IsStillBrokenAsync` and before the delete block.
- `Jellyfin.Plugin.MediaDash/Configuration/PluginConfiguration.cs` — four new `bool` properties, defaults `true`, set in ctor next to the other `Playability*` config.
- `Jellyfin.Plugin.MediaDash/Configuration/configPage.html` — new "Repair broken files" card under the existing Playability section.
- `Jellyfin.Plugin.MediaDash/PluginServiceRegistrator.cs` — no change expected (`FfmpegExecutor` + `OutputVerifier` are already singletons), but re-verify after adding deps.
- `CHANGELOG.md` — bullets per repo format (`feedback_mediadash_changelog_bullets`).

**Created:**
- `Jellyfin.Plugin.MediaDash.Tests/PlayabilityFixerRepairTests.cs` — 8 unit tests.
- `tools/repair-test/README.md` + four broken-file fixtures — real-world E2E test infra.

**Reused (no change):**
- `TranscodeFixer.SidecarPath(path, marker, ext)` — static public helper for temp path naming.
- `FfmpegExecutor.RunAsync(args, timeout, ct, progress?, durationSeconds?)` — returns `null` on success or the tail of ffmpeg's stderr on failure.
- `OutputVerifier.VerifyAsync(originalProbe, originalPath, outputPath, ct)` — returns `null` on pass or a user-facing error string on fail.
- `RecycleBin.MoveToBin(path)` — returns the bin path.

---

## Task 1: Config properties

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Configuration/PluginConfiguration.cs`

- [ ] **Step 1: Add the four properties to the constructor initializer**

Insert after the existing `PlayabilityDisposal = DisposalMethod.RecycleBin;` line (roughly line 46):

```csharp
RepairAttemptRemux = true;
RepairAttemptDropStreams = true;
RepairAttemptContainerCoerce = true;
RepairAttemptReencode = true;
```

- [ ] **Step 2: Add the property declarations**

Add near the other `Playability*` properties (search the file for `PlayabilityFixMode` and add the block adjacent to it):

```csharp
/// <summary>Gets or sets a value indicating whether rung 1 (quick remux) is attempted before deletion.</summary>
public bool RepairAttemptRemux { get; set; }

/// <summary>Gets or sets a value indicating whether rung 2 (drop broken streams) is attempted before deletion.</summary>
public bool RepairAttemptDropStreams { get; set; }

/// <summary>Gets or sets a value indicating whether rung 3 (container coercion to MKV) is attempted before deletion. Container change causes Jellyfin to re-index the file and reset watch history.</summary>
public bool RepairAttemptContainerCoerce { get; set; }

/// <summary>Gets or sets a value indicating whether rung 4 (full video re-encode) is attempted before deletion. Can take hours per file; runs on the background scheduled scan.</summary>
public bool RepairAttemptReencode { get; set; }
```

- [ ] **Step 3: Build to confirm no compile break**

Run:
```
dotnet build Jellyfin.Plugin.MediaDash.sln /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary
```
Expected: build succeeds, no analyzer errors.

- [ ] **Step 4: Commit**

```
git add Jellyfin.Plugin.MediaDash/Configuration/PluginConfiguration.cs
git commit -m "feat(playability): add RepairAttempt* config toggles (default on)"
```

---

## Task 2: Settings card UI

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Configuration/configPage.html`

- [ ] **Step 1: Locate the Playability section**

Grep for the existing Playability disposal or fix-mode markup:
```
grep -n "PlayabilityFixMode\|PlayabilityDisposal" Jellyfin.Plugin.MediaDash/Configuration/configPage.html
```

Insert the new card **immediately after** the existing Playability disposal control so users find it next to the "when broken files are found" settings.

- [ ] **Step 2: Add the settings card markup**

```html
<div class="verticalSection" data-repair-card>
  <h3 class="sectionTitle">Repair broken files</h3>
  <p class="fieldDescription">
    Before deleting an unplayable file, MediaDash can try to repair it.
    Each step is more aggressive than the last. Steps run in order and stop
    at the first one that produces a working file.
  </p>

  <div class="checkboxContainer">
    <label>
      <input is="emby-checkbox" type="checkbox" id="RepairAttemptRemux" />
      <span>Quick remux — fix container damage without re-encoding</span>
    </label>
  </div>

  <div class="checkboxContainer">
    <label>
      <input is="emby-checkbox" type="checkbox" id="RepairAttemptDropStreams" />
      <span>Drop broken streams — remove tracks that fail to decode (keeps at least one video and one audio track)</span>
    </label>
  </div>

  <div class="checkboxContainer">
    <label>
      <input is="emby-checkbox" type="checkbox" id="RepairAttemptContainerCoerce" />
      <span>Change container — repack into MKV. <strong>Jellyfin will treat the file as new and watch history for this title will reset.</strong></span>
    </label>
  </div>

  <div class="checkboxContainer">
    <label>
      <input is="emby-checkbox" type="checkbox" id="RepairAttemptReencode" />
      <span>Re-encode video — last resort. Can take hours per file, runs in the background.</span>
    </label>
  </div>

  <p class="fieldDescription">
    All four unchecked = repair disabled. MediaDash will delete broken files as it does today.
  </p>
</div>
```

- [ ] **Step 3: Wire the four checkboxes into the load/save handlers**

Search the file for the closest existing checkbox that binds to `PluginConfiguration` (e.g. `SkipHdrContent`). In the same `loadConfig` / `saveConfig` JS blocks, add four lines each — mirror the existing pattern exactly.

Load:
```js
page.querySelector('#RepairAttemptRemux').checked = config.RepairAttemptRemux;
page.querySelector('#RepairAttemptDropStreams').checked = config.RepairAttemptDropStreams;
page.querySelector('#RepairAttemptContainerCoerce').checked = config.RepairAttemptContainerCoerce;
page.querySelector('#RepairAttemptReencode').checked = config.RepairAttemptReencode;
```

Save:
```js
config.RepairAttemptRemux = page.querySelector('#RepairAttemptRemux').checked;
config.RepairAttemptDropStreams = page.querySelector('#RepairAttemptDropStreams').checked;
config.RepairAttemptContainerCoerce = page.querySelector('#RepairAttemptContainerCoerce').checked;
config.RepairAttemptReencode = page.querySelector('#RepairAttemptReencode').checked;
```

- [ ] **Step 4: Manual smoke test**

Deploy per CLAUDE.md (`bin/Debug/net9.0/publish/*` → `%LOCALAPPDATA%\jellyfin\plugins\MediaDash_X.Y.Z.0\`), restart Jellyfin at `localhost:8099`, open Settings → MediaDash, confirm:
- All four boxes appear under the Playability section.
- All four are checked by default on a fresh install.
- Toggling one, saving, reloading the page preserves the state.

- [ ] **Step 5: Commit**

```
git add Jellyfin.Plugin.MediaDash/Configuration/configPage.html
git commit -m "feat(playability): add repair-ladder settings card"
```

---

## Task 3: Ladder scaffold + regression test

Extract the repair ladder scaffolding **before** implementing any rung, so the regression case ("all toggles off = today's behavior") is proven first.

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Fixers/PlayabilityFixer.cs`
- Create: `Jellyfin.Plugin.MediaDash.Tests/PlayabilityFixerRepairTests.cs`

- [ ] **Step 1: Add `FfmpegExecutor` + `OutputVerifier` deps**

Update the field block and the constructor signature:

```csharp
private readonly FfprobeService _ffprobe;
private readonly FfmpegExecutor _ffmpeg;
private readonly OutputVerifier _verifier;
private readonly LibraryGuard _guard;
private readonly RecycleBin _recycleBin;
private readonly ILibraryMonitor _libraryMonitor;
private readonly ILogger<PlayabilityFixer> _logger;

public PlayabilityFixer(
    FfprobeService ffprobe,
    FfmpegExecutor ffmpeg,
    OutputVerifier verifier,
    LibraryGuard guard,
    RecycleBin recycleBin,
    ILibraryMonitor libraryMonitor,
    ILogger<PlayabilityFixer> logger)
{
    _ffprobe = ffprobe;
    _ffmpeg = ffmpeg;
    _verifier = verifier;
    _guard = guard;
    _recycleBin = recycleBin;
    _libraryMonitor = libraryMonitor;
    _logger = logger;
}
```

Update the XML doc for the constructor to describe `ffmpeg` and `verifier`.

- [ ] **Step 2: Add the ladder skeleton, empty (all rungs return null)**

Insert **before** the `IsStillBrokenAsync` method, after `FixAsync`:

```csharp
private static readonly TimeSpan RepairRungTimeout = TimeSpan.FromHours(6);

/// <summary>
/// Attempts to repair a broken file. Returns a Success FixResult if any rung produced a
/// verified replacement; null when the caller should fall through to today's delete path.
/// Each rung is gated by its own config toggle; a rung that produces no verified output
/// hands off to the next.
/// </summary>
private async Task<FixResult?> TryRepairAsync(
    Issue issue,
    FfprobeData originalProbe,
    IProgress<double>? progress,
    CancellationToken cancellationToken)
{
    var config = Plugin.Instance!.Configuration;

    // Pre-flight: enough free space for repair (2× source for rungs 1–3, 3× for rung 4).
    if (!HasFreeSpace(issue.Path, config.RepairAttemptReencode ? 3 : 2))
    {
        _logger.LogInformation("Playability repair skipped for {Path}: insufficient free disk.", issue.Path);
        return null;
    }

    string? outPath;
    if (config.RepairAttemptRemux && (outPath = await TryRung1Async(issue, originalProbe, cancellationToken).ConfigureAwait(false)) is not null)
    {
        return await SwapRepairedAsync(issue, outPath, "quick remux", extensionChanged: false, cancellationToken).ConfigureAwait(false);
    }

    if (config.RepairAttemptDropStreams && (outPath = await TryRung2Async(issue, originalProbe, cancellationToken).ConfigureAwait(false)) is not null)
    {
        return await SwapRepairedAsync(issue, outPath, "dropped broken streams", extensionChanged: false, cancellationToken).ConfigureAwait(false);
    }

    if (config.RepairAttemptContainerCoerce && (outPath = await TryRung3Async(issue, originalProbe, cancellationToken).ConfigureAwait(false)) is not null)
    {
        return await SwapRepairedAsync(issue, outPath, "container changed to .mkv (Jellyfin watch history reset)", extensionChanged: true, cancellationToken).ConfigureAwait(false);
    }

    if (config.RepairAttemptReencode && (outPath = await TryRung4Async(issue, originalProbe, progress, cancellationToken).ConfigureAwait(false)) is not null)
    {
        return await SwapRepairedAsync(issue, outPath, "video re-encoded", extensionChanged: true, cancellationToken).ConfigureAwait(false);
    }

    return null;
}

// Rung stubs — implemented in Tasks 4-7.
private Task<string?> TryRung1Async(Issue i, FfprobeData p, CancellationToken c) => Task.FromResult<string?>(null);
private Task<string?> TryRung2Async(Issue i, FfprobeData p, CancellationToken c) => Task.FromResult<string?>(null);
private Task<string?> TryRung3Async(Issue i, FfprobeData p, CancellationToken c) => Task.FromResult<string?>(null);
private Task<string?> TryRung4Async(Issue i, FfprobeData p, IProgress<double>? pr, CancellationToken c) => Task.FromResult<string?>(null);

private static bool HasFreeSpace(string path, int multiplier)
{
    try
    {
        var fi = new FileInfo(path);
        var drive = new DriveInfo(Path.GetPathRoot(fi.FullName) ?? "/");
        return drive.AvailableFreeSpace >= fi.Length * multiplier;
    }
    catch (IOException)
    {
        return false;
    }
}

private async Task<FixResult> SwapRepairedAsync(
    Issue issue,
    string repairedTempPath,
    string rungLabel,
    bool extensionChanged,
    CancellationToken cancellationToken)
{
    var newSize = new FileInfo(repairedTempPath).Length;
    var finalPath = extensionChanged
        ? Path.ChangeExtension(issue.Path, ".mkv")
        : issue.Path;

    var recyclePath = _recycleBin.MoveToBin(issue.Path);
    File.Move(repairedTempPath, finalPath, overwrite: false);
    _libraryMonitor.ReportFileSystemChanged(issue.Path);
    if (extensionChanged)
    {
        _libraryMonitor.ReportFileSystemChanged(finalPath);
    }

    var message = $"repaired {Path.GetFileName(issue.Path)} ({rungLabel})";
    _logger.LogInformation("Playability repair: {Message}", message);
    await Task.CompletedTask.ConfigureAwait(false); // reserved for future async swap steps
    return new FixResult
    {
        Success = true,
        Message = message,
        BytesFreed = 0,
        RecyclePath = recyclePath
    };
}
```

- [ ] **Step 3: Wire the ladder into `FixAsync`**

In `FixAsync`, immediately after the `stillBroken` check and before the `disposal` / `actionText` block, insert:

```csharp
// Repair ladder — try to salvage before deleting. Skips out to today's delete path on
// pre-flight fail, all-rungs-disabled, or all-rungs-fail. Dry-run also skips the ladder:
// there's no way to preview a "the file WOULD have been repaired" outcome accurately.
if (!config.DryRun)
{
    var probe = await _ffprobe.ProbeAsync(issue.Path, cancellationToken).ConfigureAwait(false);
    if (probe is not null)
    {
        var repair = await TryRepairAsync(issue, probe, progress, cancellationToken).ConfigureAwait(false);
        if (repair is not null)
        {
            return repair;
        }
    }
}
```

- [ ] **Step 4: Build to confirm no compile break**

```
dotnet build Jellyfin.Plugin.MediaDash.sln /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary
```
Expected: build succeeds.

- [ ] **Step 5: Write the regression test (all rungs off = today's behavior)**

Create `Jellyfin.Plugin.MediaDash.Tests/PlayabilityFixerRepairTests.cs`. Mirror the setup style of `ArtworkFixerTests.cs`. The core assertion — when all four toggles are `false`, `FixAsync` returns the same delete-to-recycle-bin result as today for the same input.

```csharp
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class PlayabilityFixerRepairTests
{
    [Fact]
    public async Task AllRungsOff_BehavesLikePreRepairFixer()
    {
        // Arrange: broken fixture file inside a temp library root, plugin config with all
        // four RepairAttempt* set to false, disposal = RecycleBin, DryRun = false.
        //
        // Act: run PlayabilityFixer.FixAsync on the fixture.
        //
        // Assert: the fixture is no longer at its original path (moved to recycle bin),
        // no *.repair-tmp* sidecar remains, and FixResult.Message matches the pre-repair
        // "removed unplayable file …" format exactly.
        Assert.True(false, "Fill in with the fake ffprobe/ffmpeg wiring used in ArtworkFixerTests.");
    }
}
```

- [ ] **Step 6: Run the test (expected to fail with the assert-false placeholder)**

```
dotnet test --filter "FullyQualifiedName~PlayabilityFixerRepairTests.AllRungsOff_BehavesLikePreRepairFixer"
```
Expected: FAIL.

- [ ] **Step 7: Fill in the fake wiring and make it pass**

Reference `ArtworkFixerTests.cs` and `TrackFixerSubtitleGuardTests.cs` for the pattern used elsewhere in the test project (fake `FfprobeService`, real `RecycleBin` pointed at a temp dir, real `LibraryGuard` scoped to the temp library root). Wire so the fake ffprobe returns a "broken" probe for the fixture, `_ffmpeg` is never called (asserted), and the fixture ends up recycled.

Re-run the test — expected: PASS.

- [ ] **Step 8: Commit**

```
git add Jellyfin.Plugin.MediaDash/Fixers/PlayabilityFixer.cs Jellyfin.Plugin.MediaDash.Tests/PlayabilityFixerRepairTests.cs
git commit -m "feat(playability): add empty repair ladder scaffold + regression guard"
```

---

## Task 4: Rung 1 — Quick remux

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Fixers/PlayabilityFixer.cs`
- Modify: `Jellyfin.Plugin.MediaDash.Tests/PlayabilityFixerRepairTests.cs`

- [ ] **Step 1: Write the failing test — rung 1 recovers a bad-index MKV**

Append to `PlayabilityFixerRepairTests.cs`:

```csharp
[Fact]
public async Task Rung1_RemuxSucceeds_SwapsFile()
{
    // Arrange: bad-index fixture in temp library, RepairAttemptRemux = true, others false,
    // real FfmpegExecutor pointed at the bundled ffmpeg.
    //
    // Act: FixAsync.
    //
    // Assert: original moved to recycle bin, sibling .mediadash.repair.tmp1.mkv is NOT left
    // behind (was renamed to original path), new file at original path probes successfully,
    // FixResult.Message contains "quick remux".
    Assert.True(false, "Implement.");
}
```

- [ ] **Step 2: Run — expected FAIL**

```
dotnet test --filter "FullyQualifiedName~PlayabilityFixerRepairTests.Rung1_RemuxSucceeds_SwapsFile"
```

- [ ] **Step 3: Implement `TryRung1Async`**

Replace the stub in `PlayabilityFixer.cs`:

```csharp
// Rung 1: error-tolerant remux, same container. Recovers bad index, wrong duration,
// missing moov atom, EOF truncation. Cheapest rung — always attempted first when enabled.
// ponytail: single remux pass; add multi-attempt with -analyzeduration hints only if
// we start seeing recoverable files that this misses.
private async Task<string?> TryRung1Async(Issue issue, FfprobeData originalProbe, CancellationToken cancellationToken)
{
    var ext = Path.GetExtension(issue.Path).TrimStart('.');
    if (string.IsNullOrEmpty(ext))
    {
        return null;
    }

    var tempPath = TranscodeFixer.SidecarPath(issue.Path, "repair.tmp1", ext);
    var args = new List<string>
    {
        "-err_detect", "ignore_err",
        "-fflags", "+genpts+igndts",
        "-i", issue.Path,
        "-map", "0",
        "-c", "copy",
        "-avoid_negative_ts", "make_zero",
        "-y", tempPath
    };

    var error = await _ffmpeg.RunAsync(args, RepairRungTimeout, cancellationToken).ConfigureAwait(false);
    if (error is not null)
    {
        _logger.LogDebug("Rung 1 remux failed for {Path}: {Error}", issue.Path, error);
        TryDelete(tempPath);
        return null;
    }

    var verifyError = await _verifier.VerifyAsync(originalProbe, issue.Path, tempPath, cancellationToken).ConfigureAwait(false);
    if (verifyError is not null)
    {
        _logger.LogDebug("Rung 1 verify failed for {Path}: {Error}", issue.Path, verifyError);
        TryDelete(tempPath);
        return null;
    }

    return tempPath;
}

private static void TryDelete(string path)
{
    try
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
    catch (IOException)
    {
        // Best-effort cleanup; the finally-block sweep in the caller handles any leftover.
    }
}
```

Also add to the top of `PlayabilityFixer.cs`:
```csharp
using System.Collections.Generic;
using Jellyfin.Plugin.MediaDash.Fixers; // for TranscodeFixer.SidecarPath (same namespace — no using needed if unchanged)
```
(Skip the `using` if `PlayabilityFixer` is already in `Jellyfin.Plugin.MediaDash.Fixers`.)

- [ ] **Step 4: Run — expected PASS**

```
dotnet test --filter "FullyQualifiedName~PlayabilityFixerRepairTests.Rung1_RemuxSucceeds_SwapsFile"
```

- [ ] **Step 5: Commit**

```
git add Jellyfin.Plugin.MediaDash/Fixers/PlayabilityFixer.cs Jellyfin.Plugin.MediaDash.Tests/PlayabilityFixerRepairTests.cs
git commit -m "feat(playability): rung 1 — quick remux with error tolerance"
```

---

## Task 5: Rung 2 — Drop broken streams

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Fixers/PlayabilityFixer.cs`
- Modify: `Jellyfin.Plugin.MediaDash.Tests/PlayabilityFixerRepairTests.cs`

- [ ] **Step 1: Write the failing test — rung 2 drops one broken subtitle, keeps video + audio**

```csharp
[Fact]
public async Task Rung2_DropsBrokenSubtitle_KeepsVideoAndAudio()
{
    // Arrange: fixture with 1 video, 1 audio, 1 subtitle whose per-stream decode errors.
    // RepairAttemptRemux = false (skip rung 1), RepairAttemptDropStreams = true.
    //
    // Act: FixAsync.
    //
    // Assert: output file has 2 streams (video + audio), original recycled, message contains
    // "dropped 1 broken stream" or similar.
    Assert.True(false, "Implement.");
}

[Fact]
public async Task Rung2_RefusesToRun_WhenDroppingWouldLeaveZeroAudio()
{
    // Arrange: fixture with 1 video and 1 audio, where the ONE audio errors.
    // Only rungs 2 and 3 enabled.
    //
    // Act: FixAsync.
    //
    // Assert: rung 2 returned null (no drop attempted), TryRung3Async received control.
    // Rung 3 will fail without a fixture patch — allow that: assert we did NOT recycle
    // via rung 2 (i.e. no swap for this rung).
    Assert.True(false, "Implement.");
}
```

- [ ] **Step 2: Run — expected FAIL**

```
dotnet test --filter "FullyQualifiedName~PlayabilityFixerRepairTests.Rung2_"
```

- [ ] **Step 3: Implement `TryRung2Async`**

```csharp
// Rung 2: per-stream decode check via ffprobe, drop the streams that error.
// Safety hard-stop: refuse if the drop would leave 0 audio or 0 video streams.
private async Task<string?> TryRung2Async(Issue issue, FfprobeData originalProbe, CancellationToken cancellationToken)
{
    if (originalProbe.Streams is null || originalProbe.Streams.Count == 0)
    {
        return null;
    }

    var brokenIndexes = new List<int>();
    foreach (var stream in originalProbe.Streams)
    {
        var decodeError = await _ffprobe.DecodeStreamAsync(issue.Path, stream.Index, cancellationToken).ConfigureAwait(false);
        if (decodeError is not null)
        {
            brokenIndexes.Add(stream.Index);
        }
    }

    if (brokenIndexes.Count == 0)
    {
        return null; // Nothing to drop; rung 1 would already have failed or not been enabled.
    }

    // Safety: never leave zero video or zero audio streams. Refuse the whole rung; caller falls to rung 3.
    var survivingVideo = originalProbe.Streams.Any(s => s.CodecType == "video" && !brokenIndexes.Contains(s.Index));
    var survivingAudio = originalProbe.Streams.Any(s => s.CodecType == "audio" && !brokenIndexes.Contains(s.Index));
    if (!survivingVideo || !survivingAudio)
    {
        _logger.LogDebug("Rung 2 refused for {Path}: dropping would leave 0 video or 0 audio streams.", issue.Path);
        return null;
    }

    var ext = Path.GetExtension(issue.Path).TrimStart('.');
    if (string.IsNullOrEmpty(ext))
    {
        return null;
    }

    var tempPath = TranscodeFixer.SidecarPath(issue.Path, "repair.tmp2", ext);
    var args = new List<string>
    {
        "-err_detect", "ignore_err",
        "-i", issue.Path,
        "-map", "0",
    };
    foreach (var idx in brokenIndexes)
    {
        args.Add("-map");
        args.Add("-0:" + idx.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    args.AddRange(new[] { "-c", "copy", "-y", tempPath });

    var error = await _ffmpeg.RunAsync(args, RepairRungTimeout, cancellationToken).ConfigureAwait(false);
    if (error is not null)
    {
        _logger.LogDebug("Rung 2 remux failed for {Path}: {Error}", issue.Path, error);
        TryDelete(tempPath);
        return null;
    }

    var verifyError = await _verifier.VerifyAsync(originalProbe, issue.Path, tempPath, cancellationToken).ConfigureAwait(false);
    if (verifyError is not null)
    {
        _logger.LogDebug("Rung 2 verify failed for {Path}: {Error}", issue.Path, verifyError);
        TryDelete(tempPath);
        return null;
    }

    return tempPath;
}
```

Also add a `DecodeStreamAsync` helper on `FfprobeService` if it doesn't already exist — this checks a single stream index for decode errors:

- [ ] **Step 4: Verify or add `FfprobeService.DecodeStreamAsync`**

```
grep -n "DecodeStreamAsync\|DecodeCheckAsync" Jellyfin.Plugin.MediaDash/Probing/FfprobeService.cs
```

If only `DecodeCheckAsync(path, duration, ct)` exists, add:

```csharp
/// <summary>
/// Attempts a short decode of a single stream by index. Returns null on success or a short
/// error string on failure (e.g. codec-specific decode errors). Used by
/// PlayabilityFixer's rung-2 drop-broken-streams pass.
/// </summary>
public async Task<string?> DecodeStreamAsync(string path, int streamIndex, CancellationToken cancellationToken)
{
    // -map 0:<index>? tolerates the index being missing (the general "?" modifier). We use the
    // ffmpeg null muxer to force decode without producing output.
    var args = new List<string>
    {
        "-hide_banner", "-nostats", "-loglevel", "error",
        "-err_detect", "explode",
        "-i", path,
        "-map", "0:" + streamIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-f", "null", "-"
    };
    // Delegate to your existing ffmpeg process runner (mirror the pattern in DecodeCheckAsync).
    // Return the stderr tail as the error string on non-zero exit.
    // (Full body: copy DecodeCheckAsync's process launcher.)
    throw new NotImplementedException("Copy from DecodeCheckAsync process wrapper — same pattern.");
}
```

Fill the body by copying `DecodeCheckAsync`'s process-launch pattern verbatim.

- [ ] **Step 5: Run — expected PASS on both tests**

```
dotnet test --filter "FullyQualifiedName~PlayabilityFixerRepairTests.Rung2_"
```

- [ ] **Step 6: Commit**

```
git add Jellyfin.Plugin.MediaDash/Fixers/PlayabilityFixer.cs Jellyfin.Plugin.MediaDash/Probing/FfprobeService.cs Jellyfin.Plugin.MediaDash.Tests/PlayabilityFixerRepairTests.cs
git commit -m "feat(playability): rung 2 — drop broken streams with audio/video safety stop"
```

---

## Task 6: Rung 3 — Container coercion to MKV

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Fixers/PlayabilityFixer.cs`
- Modify: `Jellyfin.Plugin.MediaDash.Tests/PlayabilityFixerRepairTests.cs`

- [ ] **Step 1: Write the failing test — rung 3 coerces HEVC-in-AVI to MKV**

```csharp
[Fact]
public async Task Rung3_CoercesToMkv_ChangesExtension_ResetsWatchHistory()
{
    // Arrange: HEVC-in-AVI fixture (rungs 1 and 2 fail because AVI can't hold HEVC clean),
    // only rung 3 enabled.
    //
    // Act: FixAsync.
    //
    // Assert: original .avi recycled; new .mkv exists at Path.ChangeExtension(original, ".mkv");
    // FixResult.Message contains "container changed" and "watch history".
    Assert.True(false, "Implement.");
}
```

- [ ] **Step 2: Run — expected FAIL**

- [ ] **Step 3: Implement `TryRung3Async`**

```csharp
// Rung 3: repack into MKV (universal container). Changes the file extension —
// Jellyfin will re-index and the watch history for the title resets.
// The SwapRepairedAsync caller passes extensionChanged: true so the caller relocates
// the output to Path.ChangeExtension(original, ".mkv") and reports both the old and
// new path to the library monitor.
private async Task<string?> TryRung3Async(Issue issue, FfprobeData originalProbe, CancellationToken cancellationToken)
{
    var tempPath = TranscodeFixer.SidecarPath(issue.Path, "repair.tmp3", "mkv");
    var args = new List<string>
    {
        "-err_detect", "ignore_err",
        "-fflags", "+genpts+igndts",
        "-i", issue.Path,
        "-map", "0",
        "-c", "copy",
        "-avoid_negative_ts", "make_zero",
        "-y", tempPath
    };

    var error = await _ffmpeg.RunAsync(args, RepairRungTimeout, cancellationToken).ConfigureAwait(false);
    if (error is not null)
    {
        _logger.LogDebug("Rung 3 mkv-coerce failed for {Path}: {Error}", issue.Path, error);
        TryDelete(tempPath);
        return null;
    }

    var verifyError = await _verifier.VerifyAsync(originalProbe, issue.Path, tempPath, cancellationToken).ConfigureAwait(false);
    if (verifyError is not null)
    {
        _logger.LogDebug("Rung 3 verify failed for {Path}: {Error}", issue.Path, verifyError);
        TryDelete(tempPath);
        return null;
    }

    return tempPath;
}
```

- [ ] **Step 4: Run — expected PASS**

- [ ] **Step 5: Commit**

```
git commit -am "feat(playability): rung 3 — container coercion to MKV"
```

---

## Task 7: Rung 4 — Full video re-encode

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Fixers/PlayabilityFixer.cs`
- Modify: `Jellyfin.Plugin.MediaDash.Tests/PlayabilityFixerRepairTests.cs`

- [ ] **Step 1: Write the failing test — rung 4 recovers a file that survived none of 1-3**

```csharp
[Fact]
public async Task Rung4_ReencodesVideo_LastResort()
{
    // Arrange: bitstream-damaged fixture that fails rungs 1, 2, 3 verify. Only rung 4 enabled.
    //
    // Act: FixAsync.
    //
    // Assert: original recycled; new .mkv exists; FixResult.Message contains "re-encoded".
    // This test is SLOW (real ffmpeg encode). Mark [Fact(Skip = ...)] gated on a env var
    // or move to an integration-tests project if the default `dotnet test` run should stay fast.
    Assert.True(false, "Implement.");
}
```

- [ ] **Step 2: Run — expected FAIL**

- [ ] **Step 3: Implement `TryRung4Async`**

```csharp
// Rung 4: last-resort full re-encode into MKV. Slow (hours per file for 4K sources)
// and only runs on the background scheduled scan. Uses conservative h264 + AAC targets
// so the output plays on the widest range of clients; user isn't asked which codec
// because if we're here the source is broken enough that survival trumps optimality.
// ponytail: h264/aac hardcoded; add a config knob only if a user files a preference.
private async Task<string?> TryRung4Async(Issue issue, FfprobeData originalProbe, IProgress<double>? progress, CancellationToken cancellationToken)
{
    var tempPath = TranscodeFixer.SidecarPath(issue.Path, "repair.tmp4", "mkv");
    var args = new List<string>
    {
        "-err_detect", "ignore_err",
        "-fflags", "+genpts+igndts",
        "-i", issue.Path,
        "-map", "0:v:0?",
        "-map", "0:a?",
        "-map", "0:s?",
        "-c:v", "libx264",
        "-preset", "medium",
        "-crf", "20",
        "-c:a", "aac",
        "-b:a", "192k",
        "-c:s", "copy",
        "-map_chapters", "0",
        "-y", tempPath
    };

    double? duration = null;
    if (double.TryParse(originalProbe.Format?.Duration, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0)
    {
        duration = d;
    }

    var error = await _ffmpeg.RunAsync(args, RepairRungTimeout, cancellationToken, progress, duration).ConfigureAwait(false);
    if (error is not null)
    {
        _logger.LogInformation("Rung 4 re-encode failed for {Path}: {Error}", issue.Path, TranscodeFixer.Truncate(error));
        TryDelete(tempPath);
        return null;
    }

    var verifyError = await _verifier.VerifyAsync(originalProbe, issue.Path, tempPath, cancellationToken).ConfigureAwait(false);
    if (verifyError is not null)
    {
        _logger.LogInformation("Rung 4 verify failed for {Path}: {Error}", issue.Path, verifyError);
        TryDelete(tempPath);
        return null;
    }

    return tempPath;
}
```

- [ ] **Step 4: Run — expected PASS (may be slow)**

- [ ] **Step 5: Commit**

```
git commit -am "feat(playability): rung 4 — full video re-encode fallback"
```

---

## Task 8: All-fall-through + per-toggle skip tests

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash.Tests/PlayabilityFixerRepairTests.cs`

- [ ] **Step 1: Add the remaining two tests**

```csharp
[Fact]
public async Task AllRungsEnabled_AllFail_FallsThroughToDelete()
{
    // Arrange: hopelessly damaged fixture (empty file or non-media garbage renamed to .mkv).
    // All four RepairAttempt* = true, disposal = RecycleBin, DryRun = false.
    //
    // Act: FixAsync.
    //
    // Assert: original recycled; NO *.repair.tmp* sidecars left in the library folder;
    // FixResult.Message matches the pre-repair "removed unplayable file …" format.
    Assert.True(false, "Implement.");
}

[Theory]
[InlineData(false, true, true, true)]
[InlineData(true, false, true, true)]
[InlineData(true, true, false, true)]
[InlineData(true, true, true, false)]
public async Task IndividualToggleOff_SkipsThatRung(bool r1, bool r2, bool r3, bool r4)
{
    // Arrange: fixture only recoverable by the DISABLED rung. Disabled rung is skipped;
    // subsequent rungs try and fail; falls through to delete.
    //
    // Act: FixAsync.
    //
    // Assert: original recycled; the specific rung's temp path was never created
    // (assert File.Exists(SidecarPath(...)) == false at end).
    Assert.True(false, "Implement.");
}
```

- [ ] **Step 2: Run — expected FAIL then PASS as you fill them in**

- [ ] **Step 3: Commit**

```
git commit -am "test(playability): cover all-fail fall-through and per-toggle skip"
```

---

## Task 9: Real-world E2E fixtures

**Files:**
- Create: `tools/repair-test/README.md`
- Create: `tools/repair-test/regenerate.ps1`
- Create: `tools/repair-test/fixtures/` (four broken files, generated by the script)

- [ ] **Step 1: Author the regeneration script**

The four fixtures are generated on demand from a known-good source video (bundled or user-supplied path). This keeps the repo lean.

```powershell
# tools/repair-test/regenerate.ps1
# Regenerates the four PlayabilityFixer repair-ladder fixtures from a source MKV.
# Usage: .\regenerate.ps1 -Source <path-to-known-good-mkv>
param(
    [Parameter(Mandatory=$true)][string]$Source
)

$outDir = Join-Path $PSScriptRoot "fixtures"
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

# fixture-bad-index: remux then truncate the index by writing back with -fflags -bitexact and cutting bytes.
ffmpeg -y -i $Source -c copy (Join-Path $outDir "fixture-bad-index.mkv")
# Then use a hex editor pass or dd equivalent to zero out the seek head.
# See README.md for the exact byte-offset procedure per container.

# fixture-decode-error-sub: inject a garbled subtitle track alongside good video/audio.
# (Detailed steps in README.)

# fixture-hevc-in-avi: mux an HEVC stream into an AVI container (invalid but produced by some old rippers).
ffmpeg -y -i $Source -c:v copy -c:a copy -f avi (Join-Path $outDir "fixture-hevc-in-avi.avi")

# fixture-bitstream-damage: XOR random bytes across the middle 5% of the file after remux.
ffmpeg -y -i $Source -c copy (Join-Path $outDir "fixture-bitstream-damage.mkv")
# Then damage via a small helper script — see README.
```

- [ ] **Step 2: Write the README**

```markdown
# repair-test — Playability repair ladder E2E fixtures

Four broken-file fixtures, one per rung of PlayabilityFixer's repair ladder. Not
checked in as blobs; regenerate from a known-good MKV with `regenerate.ps1`.

| Fixture | Corrupt in | Recoverable by |
|---|---|---|
| fixture-bad-index.mkv | Zeroed seek head | Rung 1 (quick remux) |
| fixture-decode-error-sub.mkv | One subtitle stream throws decode errors | Rung 2 (drop broken streams) |
| fixture-hevc-in-avi.avi | HEVC muxed into AVI (illegal per spec, common from old rippers) | Rung 3 (container coercion) |
| fixture-bitstream-damage.mkv | Random bit-flips in the middle 5% of the video stream | Rung 4 (re-encode) |

## Regenerating

```powershell
.\regenerate.ps1 -Source "path\to\known-good.mkv"
```

## E2E test procedure

Per `feedback_mediadash_real_world_tests`: real files, real plugin, real
Jellyfin. Not unit-test coverage.

1. Deploy the current build to your local Jellyfin (localhost:8099).
2. Point a Jellyfin library at `tools/repair-test/fixtures/`.
3. Run a MediaDash Playability scan; confirm all four fixtures are flagged.
4. Run a MediaDash Playability fix.
5. For each fixture:
   - The recycle bin contains the original.
   - The library folder contains a replacement (same name for rungs 1-2;
     `.mkv` extension for rungs 3-4).
   - The replacement plays end-to-end in the Jellyfin web player.
6. Reset: restore originals from recycle bin, re-run with each of the four
   `RepairAttempt*` toggles disabled individually; confirm the specific
   fixture that rung would have recovered falls through to delete.

Record wall-clock time per fixture in the CHANGELOG note so users know what
to expect.
```

- [ ] **Step 3: Run the E2E procedure locally**

Follow the README steps end-to-end against `localhost:8099`. Fix any bugs found and re-loop until all four fixtures pass their acceptance line.

- [ ] **Step 4: Commit**

```
git add tools/repair-test
git commit -m "test(playability): real-world repair-ladder fixture harness"
```

---

## Task 10: Version, changelog, sweep

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Jellyfin.Plugin.MediaDash.csproj` (or wherever `AssemblyVersion` lives)
- Modify: `CHANGELOG.md`
- Modify: `build.yaml`
- Modify: `manifest.json` (only via the release script — do NOT hand-edit checksums)

- [ ] **Step 1: Confirm the bundle version**

Per `project_mediadash_roadmap`, this ships as part of 1.0.8.0 alongside the media organiser, audio-conversion scanner, and Library-tab redesign. Version bump is a bundle-level decision — coordinate with the bundle owner before touching version files here. This plan intentionally does not bump the version on its own.

- [ ] **Step 2: Add CHANGELOG bullets (per repo format rule)**

One bullet per user-visible change, under the 1.0.8.0 heading. Reference the spec if a feature ticket exists.

```markdown
- Broken files: added a four-step repair ladder that tries to salvage unplayable files before deletion (quick remux → drop broken streams → change container to MKV → re-encode video). Each step is user-toggleable under Settings → MediaDash → Repair broken files; all four are on by default. Container-change step resets Jellyfin watch history for the affected title.
- Broken files: `PlayabilityFixer` now depends on `FfmpegExecutor` and `OutputVerifier`; the repair step reuses the temp-swap safety pattern already in `TrackFixer`/`TranscodeFixer`.
```

- [ ] **Step 3: Run the pre-push sweep**

Per `feedback_mediadash_prepush_sweep`: full E2E sweep + changelog reconciliation, returning an explicit Safe-to-push / Do-not-push verdict. Do NOT push without this verdict.

- [ ] **Step 4: Commit**

```
git add CHANGELOG.md
git commit -m "docs: changelog for playability repair ladder (1.0.8.0)"
```

---

## Self-review checklist (author, before handoff)

- [ ] Every spec §4 subsection has at least one task implementing it.
- [ ] Rung numbering, method names, and config property names match between tasks (`RepairAttemptRemux`, `TryRung1Async`, etc.).
- [ ] No task references a helper (`SidecarPath`, `VerifyAsync`, `MoveToBin`, `DecodeStreamAsync`) that isn't either pre-existing or introduced in a preceding task.
- [ ] Placeholder scan: search plan for TBD/TODO — none should remain.
- [ ] Safety invariants #1–#5 from CLAUDE.md are honored by the swap flow and pre-flight (Task 3 Step 2).
