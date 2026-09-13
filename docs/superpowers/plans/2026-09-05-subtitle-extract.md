# Subtitle Extract Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract embedded text subtitles to Jellyfin-conventional sidecar files and remove them from the container, with combined-pass integration so any subset of `{SubtitleExtract, AudioLanguage, SubtitleLanguage, Transcode}` on the same file resolves in a single ffmpeg pass.

**Architecture:** New `SubtitleExtractScanner` emits `IssueType.SubtitleExtract`; new `SubtitleExtractFixer` handles the standalone dispatch path; a shared `SubtitleExtractArgs` helper builds the ffmpeg fragments for extraction and is called from three sites (standalone fixer, `TrackFixer.FixCombinedAsync`, `TranscodeFixer.BuildArgs`). `FixTask`'s companion-detection block widens to include Extract as claimable by Transcode and eligible for combined-pair bundling with track fixers.

**Tech Stack:** C# / .NET 9, xUnit, Jellyfin plugin conventions (embedded `emby-*` UI in `configPage.html`), bundled ffmpeg via `FfmpegExecutor`.

**Spec:** `docs/superpowers/specs/2026-09-05-subtitle-extract-design.md`

**Target release:** 1.0.8.0 bundle (do NOT bump version in this plan — bundle-level decision).

---

## File structure

**New:**
- `Jellyfin.Plugin.MediaDash/Fixers/SubtitleExtractArgs.cs` — pure helper: sidecar naming, stream qualification, ffmpeg arg fragment generation, sidecar-exists filter. ~120 lines.
- `Jellyfin.Plugin.MediaDash/Scanners/SubtitleExtractScanner.cs` — emits `IssueType.SubtitleExtract` for videos with qualifying text-sub streams. ~90 lines.
- `Jellyfin.Plugin.MediaDash/Fixers/SubtitleExtractFixer.cs` — standalone dispatch path: extract + remux + verify + swap. ~220 lines.

**Modified:**
- `Jellyfin.Plugin.MediaDash/Data/IssueType.cs` — add `SubtitleExtract` enum value.
- `Jellyfin.Plugin.MediaDash/Configuration/PluginConfiguration.cs` — add `SubtitleExtractFixMode`, `SubtitleExtractDisposal`; wire into `GetFixMode` / `GetDisposal` switches.
- `Jellyfin.Plugin.MediaDash/Configuration/configPage.html` — settings card + load/save wiring.
- `Jellyfin.Plugin.MediaDash/PluginServiceRegistrator.cs` — register new scanner + fixer.
- `Jellyfin.Plugin.MediaDash/ScheduledTasks/FixTask.cs` — extend `BuildTranscodeCompanions` and combined-pair detection.
- `Jellyfin.Plugin.MediaDash/Fixers/TrackFixer.cs` — widen `FixCombinedAsync` to accept optional Extract companion.
- `Jellyfin.Plugin.MediaDash/Fixers/TranscodeFixer.cs` — thread `subsToExtract` through `FixAsync` → `BuildArgs`.
- `CHANGELOG.md` — bullet under 1.0.8.0 heading.

**New tests:**
- `Jellyfin.Plugin.MediaDash.Tests/SubtitleExtractArgsTests.cs` — helper unit tests.
- `Jellyfin.Plugin.MediaDash.Tests/SubtitleExtractScannerTests.cs` — scanner unit tests.
- `Jellyfin.Plugin.MediaDash.Tests/SubtitleExtractFixerTests.cs` — standalone-fixer unit tests.
- `Jellyfin.Plugin.MediaDash.Tests/SubtitleExtractCombinedPassTests.cs` — dispatch matrix tests.
- `Jellyfin.Plugin.MediaDash.Tests/FixTaskSubtitleExtractCompanionTests.cs` — companion detection tests.

**Real-world fixtures:**
- `tools/subtitle-extract-test/README.md`
- `tools/subtitle-extract-test/regenerate.ps1`

**Reference implementations** (don't rewrite — model after these):
- Scanner pattern: `Jellyfin.Plugin.MediaDash/Scanners/SubtitleLanguageScanner.cs`
- Fixer with temp-swap: `Jellyfin.Plugin.MediaDash/Fixers/TrackFixer.cs`
- Test setup (fake ffprobe): `Jellyfin.Plugin.MediaDash.Tests/TrackFixerSubtitleGuardTests.cs`
- Settings card pattern: existing Subtitles section in `configPage.html` (grep for `SubtitleFixMode`)

---

## Task 1: Foundation — enum, config, DI registration

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Data/IssueType.cs`
- Modify: `Jellyfin.Plugin.MediaDash/Configuration/PluginConfiguration.cs`
- Modify: `Jellyfin.Plugin.MediaDash/PluginServiceRegistrator.cs`

- [ ] **Step 1: Add enum value**

In `Data/IssueType.cs`, add `SubtitleExtract` to the enum. Place it adjacent to `SubtitleLanguage` for topical clustering:

```csharp
SubtitleExtract,
```

- [ ] **Step 2: Add config properties (initialization)**

In `Configuration/PluginConfiguration.cs` constructor, add near the other `Subtitle*` initializers (search for `SubtitleFixMode`):

```csharp
SubtitleExtractFixMode = FixMode.Off;
SubtitleExtractDisposal = DisposalMethod.RecycleBin;
```

- [ ] **Step 3: Add config properties (declarations)**

In the same file, add near the other `SubtitleFixMode` / `SubtitleDisposal` properties:

```csharp
/// <summary>
/// Gets or sets how the subtitle-extract fixer acts. Off by default — feature is opt-in and
/// changes on-disk files.
/// </summary>
public FixMode SubtitleExtractFixMode { get; set; }

/// <summary>
/// Gets or sets where replaced originals of subtitle-extract remuxes go.
/// </summary>
public DisposalMethod SubtitleExtractDisposal { get; set; }
```

- [ ] **Step 4: Wire into GetFixMode / GetDisposal switches**

In `PluginConfiguration.cs`, add to the `GetFixMode` switch (search for `IssueType.SubtitleLanguage => SubtitleFixMode`):

```csharp
Data.IssueType.SubtitleExtract => SubtitleExtractFixMode,
```

And to the `GetDisposal` switch (search for `IssueType.SubtitleLanguage => SubtitleDisposal`):

```csharp
Data.IssueType.SubtitleExtract => SubtitleExtractDisposal,
```

- [ ] **Step 5: Register the new scanner + fixer**

In `PluginServiceRegistrator.cs`, add near the other subtitle registrations (search for `SubtitleLanguageScanner`):

```csharp
serviceCollection.AddSingleton<Scanners.SubtitleExtractScanner>();
serviceCollection.AddSingleton<Fixers.SubtitleExtractFixer>();
```

Also add them to whatever `IScanner[]` / `IFixer[]` aggregation the registrator uses — grep the file for how `SubtitleLanguageScanner` and `SubtitleLanguageFixer` get into those collections and mirror it exactly.

- [ ] **Step 6: Build**

Run:
```
dotnet build Jellyfin.Plugin.MediaDash.sln /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary
```
Expected: build succeeds. The new scanner + fixer types don't exist yet, so the DI registration lines will fail to compile — that's fine, revert JUST the two `AddSingleton` lines temporarily (leave the enum + config in) OR skip Step 5 until Task 3 lands. Recommendation: **skip Step 5 to Task 3's commit** so the build stays green throughout.

- [ ] **Step 7: Commit**

```
git add Jellyfin.Plugin.MediaDash/Data/IssueType.cs Jellyfin.Plugin.MediaDash/Configuration/PluginConfiguration.cs
git commit -m "feat(subtitle-extract): add IssueType + config properties (foundation)"
```

---

## Task 2: SubtitleExtractArgs helper (pure logic)

**Files:**
- Create: `Jellyfin.Plugin.MediaDash/Fixers/SubtitleExtractArgs.cs`
- Create: `Jellyfin.Plugin.MediaDash.Tests/SubtitleExtractArgsTests.cs`

- [ ] **Step 1: Write the failing test — text-sub codec detection**

Create `Jellyfin.Plugin.MediaDash.Tests/SubtitleExtractArgsTests.cs`:

```csharp
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class SubtitleExtractArgsTests
{
    [Theory]
    [InlineData("subrip", true)]
    [InlineData("srt", true)]
    [InlineData("ass", true)]
    [InlineData("ssa", true)]
    [InlineData("webvtt", true)]
    [InlineData("mov_text", true)]
    [InlineData("hdmv_pgs_subtitle", false)]
    [InlineData("dvd_subtitle", false)]
    [InlineData("dvb_subtitle", false)]
    [InlineData("dvb_teletext", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsTextSubCodec_ClassifiesCorrectly(string? codec, bool expected)
    {
        Assert.Equal(expected, SubtitleExtractArgs.IsTextSubCodec(codec));
    }
}
```

- [ ] **Step 2: Run — expected FAIL (type doesn't exist)**

```
dotnet test --filter "FullyQualifiedName~SubtitleExtractArgsTests.IsTextSubCodec"
```
Expected: FAIL with "SubtitleExtractArgs could not be found".

- [ ] **Step 3: Create the helper file with the classifier**

Create `Jellyfin.Plugin.MediaDash/Fixers/SubtitleExtractArgs.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Probing;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Pure helper for the subtitle-extract feature. Determines which streams qualify for
/// extraction, computes their Jellyfin-conventional sidecar paths, and builds the ffmpeg
/// argument fragments that write them. Called from three sites: <see cref="SubtitleExtractFixer"/>
/// (standalone), <see cref="TrackFixer"/>.FixCombinedAsync (combined pass with track fixers),
/// and <see cref="TranscodeFixer"/>.BuildArgs (Transcode companion).
/// </summary>
internal static class SubtitleExtractArgs
{
    private static readonly HashSet<string> TextSubCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "webvtt", "mov_text"
    };

    /// <summary>Returns true when the ffprobe codec_name is a text-based subtitle format.</summary>
    /// <param name="codecName">The codec_name from ffprobe (may be null).</param>
    /// <returns>True when the codec is on the text-sub whitelist.</returns>
    public static bool IsTextSubCodec(string? codecName)
    {
        return !string.IsNullOrEmpty(codecName) && TextSubCodecs.Contains(codecName);
    }
}
```

- [ ] **Step 4: Run — expected PASS**

```
dotnet test --filter "FullyQualifiedName~SubtitleExtractArgsTests.IsTextSubCodec"
```

- [ ] **Step 5: Write the failing test — sidecar naming (basic, disambiguation, disposition flags)**

Append to `SubtitleExtractArgsTests.cs`:

```csharp
[Fact]
public void SidecarPath_BasicEnglishSub_EngExtension()
{
    var stream = MakeSub(index: 2, language: "eng", codec: "subrip");
    var path = SubtitleExtractArgs.SidecarPath("/movies/Movie (2024).mkv", stream, needsTitle: false);
    Assert.Equal(Path.Combine("/movies", "Movie (2024).eng.srt"), path);
}

[Fact]
public void SidecarPath_ForcedFlag_Appended()
{
    var stream = MakeSub(index: 2, language: "eng", codec: "subrip", forced: true);
    var path = SubtitleExtractArgs.SidecarPath("/movies/Movie (2024).mkv", stream, needsTitle: false);
    Assert.EndsWith(".eng.forced.srt", path);
}

[Fact]
public void SidecarPath_SdhFlag_Appended()
{
    var stream = MakeSub(index: 2, language: "eng", codec: "subrip", hi: true);
    var path = SubtitleExtractArgs.SidecarPath("/movies/Movie (2024).mkv", stream, needsTitle: false);
    Assert.EndsWith(".eng.sdh.srt", path);
}

[Fact]
public void SidecarPath_UnknownLanguage_UndFallback()
{
    var stream = MakeSub(index: 2, language: null, codec: "subrip");
    var path = SubtitleExtractArgs.SidecarPath("/movies/Movie (2024).mkv", stream, needsTitle: false);
    Assert.EndsWith(".und.srt", path);
}

[Fact]
public void SidecarPath_AssCodec_AssExtension()
{
    var stream = MakeSub(index: 2, language: "jpn", codec: "ass");
    var path = SubtitleExtractArgs.SidecarPath("/anime/Show S01E01.mkv", stream, needsTitle: false);
    Assert.EndsWith(".jpn.ass", path);
}

[Fact]
public void SidecarPath_WebVttCodec_VttExtension()
{
    var stream = MakeSub(index: 2, language: "eng", codec: "webvtt");
    var path = SubtitleExtractArgs.SidecarPath("/movies/M.mp4", stream, needsTitle: false);
    Assert.EndsWith(".eng.vtt", path);
}

[Fact]
public void SidecarPath_NeedsTitle_TitleAppended()
{
    var stream = MakeSub(index: 2, language: "eng", codec: "subrip", title: "Commentary");
    var path = SubtitleExtractArgs.SidecarPath("/movies/M.mkv", stream, needsTitle: true);
    Assert.EndsWith(".eng.Commentary.srt", path);
}

[Fact]
public void SidecarPath_MovText_SrtExtension()
{
    // MOV_text is Apple's subrip variant inside MP4; SRT is the correct sidecar extension.
    var stream = MakeSub(index: 2, language: "eng", codec: "mov_text");
    var path = SubtitleExtractArgs.SidecarPath("/movies/M.mp4", stream, needsTitle: false);
    Assert.EndsWith(".eng.srt", path);
}

private static FfprobeStreamInfo MakeSub(int index, string? language, string codec, bool forced = false, bool hi = false, string? title = null)
{
    var disposition = new Dictionary<string, int>();
    if (forced) disposition["forced"] = 1;
    if (hi) disposition["hearing_impaired"] = 1;
    var tags = new Dictionary<string, string>();
    if (language is not null) tags["language"] = language;
    if (title is not null) tags["title"] = title;
    return new FfprobeStreamInfo
    {
        Index = index,
        CodecType = "subtitle",
        CodecName = codec,
        Disposition = disposition.Count > 0 ? disposition : null,
        Tags = tags.Count > 0 ? tags : null
    };
}
```

- [ ] **Step 6: Run — expected FAIL (SidecarPath not implemented)**

```
dotnet test --filter "FullyQualifiedName~SubtitleExtractArgsTests.SidecarPath"
```

- [ ] **Step 7: Implement `SidecarPath`**

Add to `SubtitleExtractArgs.cs`:

```csharp
/// <summary>
/// Computes the Jellyfin-conventional sidecar path for a subtitle stream:
/// &lt;video-basename&gt;.&lt;lang&gt;[.forced][.sdh][.&lt;title&gt;].&lt;ext&gt;.
/// </summary>
/// <param name="videoPath">The full path to the source video file.</param>
/// <param name="stream">The subtitle stream to be extracted.</param>
/// <param name="needsTitle">True when another stream in the same file shares the same
/// lang+flags combo, requiring the title tag as a disambiguator. Caller decides — this
/// method never adds title unless told to.</param>
/// <returns>The full sidecar path.</returns>
public static string SidecarPath(string videoPath, FfprobeStreamInfo stream, bool needsTitle)
{
    var dir = Path.GetDirectoryName(videoPath) ?? string.Empty;
    var baseName = Path.GetFileNameWithoutExtension(videoPath);
    var parts = new List<string> { baseName };

    var lang = ResolveLanguage(stream);
    parts.Add(lang);

    if (stream.Disposition is not null)
    {
        if (stream.Disposition.TryGetValue("forced", out var f) && f != 0)
        {
            parts.Add("forced");
        }

        if (stream.Disposition.TryGetValue("hearing_impaired", out var h) && h != 0)
        {
            parts.Add("sdh");
        }
    }

    if (needsTitle && stream.Tags is not null && stream.Tags.TryGetValue("title", out var title) && !string.IsNullOrWhiteSpace(title))
    {
        // Sanitize the title to safe filename characters. Jellyfin's sidecar detector accepts spaces,
        // but path separators and reserved characters must not survive.
        parts.Add(SanitizeTitle(title));
    }

    var ext = ExtensionFor(stream.CodecName);
    var filename = string.Join('.', parts) + "." + ext;
    return Path.Combine(dir, filename);
}

private static string ResolveLanguage(FfprobeStreamInfo stream)
{
    if (stream.Tags is not null && stream.Tags.TryGetValue("language", out var lang) && !string.IsNullOrWhiteSpace(lang))
    {
        return lang.ToLowerInvariant();
    }

    return "und";
}

private static string ExtensionFor(string? codecName)
{
    return codecName?.ToLowerInvariant() switch
    {
        "ass" or "ssa" => "ass",
        "webvtt" => "vtt",
        _ => "srt" // subrip, srt, mov_text all extract to SRT
    };
}

private static string SanitizeTitle(string title)
{
    // Path.GetInvalidFileNameChars() covers OS-specific reserved chars. Replace with '_'.
    var invalid = Path.GetInvalidFileNameChars();
    var chars = title.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
    return new string(chars).Trim();
}
```

- [ ] **Step 8: Run — expected PASS**

```
dotnet test --filter "FullyQualifiedName~SubtitleExtractArgsTests.SidecarPath"
```

- [ ] **Step 9: Write the failing test — stream qualification (respects allow-list, hearing-impaired protection, skips image subs)**

Append to `SubtitleExtractArgsTests.cs`:

```csharp
[Fact]
public void QualifyingStreams_TextSubInAllowList_Included()
{
    var subs = new[] { MakeSub(2, "eng", "subrip") };
    var result = SubtitleExtractArgs.QualifyingStreams(subs, allowedLanguages: new[] { "eng", "spa" }, protectHearingImpaired: false);
    Assert.Single(result);
    Assert.Equal(2, result[0].Index);
}

[Fact]
public void QualifyingStreams_TextSubNotInAllowList_Excluded()
{
    var subs = new[] { MakeSub(2, "fra", "subrip") };
    var result = SubtitleExtractArgs.QualifyingStreams(subs, allowedLanguages: new[] { "eng" }, protectHearingImpaired: false);
    Assert.Empty(result);
}

[Fact]
public void QualifyingStreams_ImageSub_ExcludedRegardlessOfLanguage()
{
    var subs = new[] { MakeSub(2, "eng", "hdmv_pgs_subtitle") };
    var result = SubtitleExtractArgs.QualifyingStreams(subs, allowedLanguages: new[] { "eng" }, protectHearingImpaired: false);
    Assert.Empty(result);
}

[Fact]
public void QualifyingStreams_EmptyAllowList_AllTextSubsQualify()
{
    var subs = new[] { MakeSub(2, "eng", "subrip"), MakeSub(3, "fra", "ass"), MakeSub(4, "jpn", "hdmv_pgs_subtitle") };
    var result = SubtitleExtractArgs.QualifyingStreams(subs, allowedLanguages: Array.Empty<string>(), protectHearingImpaired: false);
    Assert.Equal(2, result.Count);
    Assert.All(result, s => Assert.NotEqual("hdmv_pgs_subtitle", s.CodecName));
}

[Fact]
public void QualifyingStreams_HearingImpairedProtection_IncludesSdhEvenIfLanguageDisallowed()
{
    var subs = new[] { MakeSub(2, "fra", "subrip", hi: true) };
    var result = SubtitleExtractArgs.QualifyingStreams(subs, allowedLanguages: new[] { "eng" }, protectHearingImpaired: true);
    Assert.Single(result);
}
```

Also add the `using System;` line at the top of the test file if it isn't already there.

- [ ] **Step 10: Run — expected FAIL**

```
dotnet test --filter "FullyQualifiedName~SubtitleExtractArgsTests.QualifyingStreams"
```

- [ ] **Step 11: Implement `QualifyingStreams`**

Add to `SubtitleExtractArgs.cs`:

```csharp
/// <summary>
/// Filters a stream collection to those eligible for extraction: text-based codec,
/// language matches allow-list (or list is empty), plus hearing-impaired protection.
/// </summary>
/// <param name="allStreams">The full ffprobe stream list (any codec_type — this method filters).</param>
/// <param name="allowedLanguages">ISO 639-2 codes from <c>AllowedSubtitleLanguages</c>. Empty means allow all.</param>
/// <param name="protectHearingImpaired">When true, HI-flagged subs pass the language check even if their language is not allowed.</param>
/// <returns>The streams that should be extracted.</returns>
public static IReadOnlyList<FfprobeStreamInfo> QualifyingStreams(
    IEnumerable<FfprobeStreamInfo> allStreams,
    IReadOnlyList<string> allowedLanguages,
    bool protectHearingImpaired)
{
    return allStreams
        .Where(s => string.Equals(s.CodecType, "subtitle", StringComparison.OrdinalIgnoreCase))
        .Where(s => IsTextSubCodec(s.CodecName))
        .Where(s =>
        {
            if (allowedLanguages.Count == 0)
            {
                return true;
            }

            var lang = ResolveLanguage(s);
            if (allowedLanguages.Any(a => string.Equals(a, lang, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (protectHearingImpaired && s.IsHearingImpaired)
            {
                return true;
            }

            return false;
        })
        .ToList();
}
```

- [ ] **Step 12: Run — expected PASS**

```
dotnet test --filter "FullyQualifiedName~SubtitleExtractArgsTests.QualifyingStreams"
```

- [ ] **Step 13: Write the failing test — plan step (sidecar-exists filter + title disambiguation + arg fragment)**

Append:

```csharp
[Fact]
public void Plan_TwoEnglishSubs_DifferentDispositions_NoTitleNeeded()
{
    // Different disposition flags disambiguate — no title needed.
    var subs = new[]
    {
        MakeSub(2, "eng", "subrip", forced: false),
        MakeSub(3, "eng", "subrip", forced: true)
    };
    var plan = SubtitleExtractArgs.Plan("/movies/M.mkv", subs, allowed: Array.Empty<string>(), protectHi: false, existingSidecarProbe: _ => false);
    Assert.Equal(2, plan.Count);
    Assert.EndsWith(".eng.srt", plan[0].SidecarPath);
    Assert.EndsWith(".eng.forced.srt", plan[1].SidecarPath);
}

[Fact]
public void Plan_TwoEnglishSubs_SameFlags_TitleUsedIfPresent()
{
    // Same lang+flags → need title. First has title, second doesn't → first uses title, second uses index disambig.
    var subs = new[]
    {
        MakeSub(2, "eng", "subrip", title: "Signs"),
        MakeSub(3, "eng", "subrip")
    };
    var plan = SubtitleExtractArgs.Plan("/movies/M.mkv", subs, allowed: Array.Empty<string>(), protectHi: false, existingSidecarProbe: _ => false);
    Assert.Equal(2, plan.Count);
    Assert.Contains(plan, p => p.SidecarPath.EndsWith(".eng.Signs.srt"));
    // The one without title falls back to including its stream index for uniqueness.
    Assert.Contains(plan, p => p.SidecarPath.EndsWith(".eng.track3.srt"));
}

[Fact]
public void Plan_ExistingSidecar_StreamSkipped()
{
    var subs = new[] { MakeSub(2, "eng", "subrip") };
    var plan = SubtitleExtractArgs.Plan(
        "/movies/M.mkv",
        subs,
        allowed: Array.Empty<string>(),
        protectHi: false,
        existingSidecarProbe: path => path.EndsWith(".eng.srt"));
    Assert.Empty(plan);
}

[Fact]
public void BuildFfmpegArgs_TwoStreams_EmitsMapAndCodecCopyPerStream()
{
    var plan = new List<SubtitleExtractPlanItem>
    {
        new() { StreamIndex = 2, CodecName = "subrip", SidecarPath = "/movies/M.eng.srt" },
        new() { StreamIndex = 3, CodecName = "ass", SidecarPath = "/movies/M.jpn.ass" }
    };
    var args = SubtitleExtractArgs.BuildFfmpegArgs(plan);
    Assert.Contains("-map", args);
    // Full contract: -map 0:2 -c:s copy /movies/M.eng.srt -map 0:3 -c:s copy /movies/M.jpn.ass
    var joined = string.Join(' ', args);
    Assert.Contains("-map 0:2", joined);
    Assert.Contains("-c:s copy /movies/M.eng.srt", joined);
    Assert.Contains("-map 0:3", joined);
    Assert.Contains("-c:s copy /movies/M.jpn.ass", joined);
}
```

- [ ] **Step 14: Run — expected FAIL**

```
dotnet test --filter "FullyQualifiedName~SubtitleExtractArgsTests.Plan|FullyQualifiedName~SubtitleExtractArgsTests.BuildFfmpegArgs"
```

- [ ] **Step 15: Implement `Plan` + `BuildFfmpegArgs` + `SubtitleExtractPlanItem`**

Add to `SubtitleExtractArgs.cs`:

```csharp
/// <summary>
/// One stream's extraction plan: the source stream index and its computed sidecar path.
/// Emitted by <see cref="Plan"/> and consumed by <see cref="BuildFfmpegArgs"/>.
/// </summary>
public sealed class SubtitleExtractPlanItem
{
    public int StreamIndex { get; init; }
    public string CodecName { get; init; } = string.Empty;
    public string SidecarPath { get; init; } = string.Empty;
}

/// <summary>
/// Computes the full extract plan for a file: qualifying streams, sidecar paths with
/// disambiguation, and the sidecar-exists skip filter applied. Streams whose sidecar
/// already exists are excluded from the returned list — the caller must NOT drop those
/// streams from the container either.
/// </summary>
/// <param name="videoPath">The source video path.</param>
/// <param name="allStreams">Every ffprobe stream (this method filters).</param>
/// <param name="allowed">ISO 639-2 language allow-list; empty means all.</param>
/// <param name="protectHi">Hearing-impaired protection.</param>
/// <param name="existingSidecarProbe">Callable that returns true when a given sidecar path exists on disk. Injected for testability; production calls <see cref="File.Exists"/>.</param>
/// <returns>Zero or more extraction plan items; empty when nothing to extract.</returns>
public static IReadOnlyList<SubtitleExtractPlanItem> Plan(
    string videoPath,
    IEnumerable<FfprobeStreamInfo> allStreams,
    IReadOnlyList<string> allowed,
    bool protectHi,
    Func<string, bool> existingSidecarProbe)
{
    var qualifying = QualifyingStreams(allStreams, allowed, protectHi);
    if (qualifying.Count == 0)
    {
        return Array.Empty<SubtitleExtractPlanItem>();
    }

    // Determine which streams need title disambiguation: any lang+flags combo used by >1 stream.
    var keyToCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var s in qualifying)
    {
        var key = LangFlagsKey(s);
        keyToCount[key] = (keyToCount.TryGetValue(key, out var c) ? c : 0) + 1;
    }

    var plan = new List<SubtitleExtractPlanItem>(qualifying.Count);
    foreach (var s in qualifying)
    {
        var needsTitle = keyToCount[LangFlagsKey(s)] > 1;
        string sidecarPath;
        if (needsTitle && (s.Tags is null || !s.Tags.TryGetValue("title", out var t) || string.IsNullOrWhiteSpace(t)))
        {
            // Fallback: stream index in the filename slot the title would occupy.
            // Ensures uniqueness even when the container gave us duplicate lang+flags with no title.
            sidecarPath = SidecarPath(videoPath, s, needsTitle: false);
            var ext = Path.GetExtension(sidecarPath);
            var withoutExt = sidecarPath[..^ext.Length];
            sidecarPath = withoutExt + ".track" + s.Index.ToString(CultureInfo.InvariantCulture) + ext;
        }
        else
        {
            sidecarPath = SidecarPath(videoPath, s, needsTitle);
        }

        if (existingSidecarProbe(sidecarPath))
        {
            continue;
        }

        plan.Add(new SubtitleExtractPlanItem
        {
            StreamIndex = s.Index,
            CodecName = s.CodecName ?? string.Empty,
            SidecarPath = sidecarPath
        });
    }

    return plan;
}

/// <summary>
/// Emits the ffmpeg argument fragments to extract each plan item to its sidecar:
/// <c>-map 0:&lt;idx&gt; -c:s copy &lt;sidecar-path&gt;</c> per item. Caller composes these
/// alongside its own -i / -map for the container output.
/// </summary>
/// <param name="plan">The extract plan produced by <see cref="Plan"/>.</param>
/// <returns>Flat argument list ready to append to an ffmpeg command.</returns>
public static IReadOnlyList<string> BuildFfmpegArgs(IReadOnlyList<SubtitleExtractPlanItem> plan)
{
    var args = new List<string>(plan.Count * 4);
    foreach (var item in plan)
    {
        args.Add("-map");
        args.Add("0:" + item.StreamIndex.ToString(CultureInfo.InvariantCulture));
        args.Add("-c:s");
        args.Add("copy");
        args.Add(item.SidecarPath);
    }

    return args;
}

private static string LangFlagsKey(FfprobeStreamInfo s)
{
    var lang = ResolveLanguage(s);
    var flags = string.Empty;
    if (s.Disposition is not null)
    {
        if (s.Disposition.TryGetValue("forced", out var f) && f != 0) flags += ":forced";
        if (s.Disposition.TryGetValue("hearing_impaired", out var h) && h != 0) flags += ":sdh";
    }

    return lang + flags;
}
```

- [ ] **Step 16: Run all helper tests — expected PASS**

```
dotnet test --filter "FullyQualifiedName~SubtitleExtractArgsTests"
```

- [ ] **Step 17: Commit**

```
git add Jellyfin.Plugin.MediaDash/Fixers/SubtitleExtractArgs.cs Jellyfin.Plugin.MediaDash.Tests/SubtitleExtractArgsTests.cs
git commit -m "feat(subtitle-extract): args helper (sidecar naming + qualification + planning)"
```

---

## Task 3: SubtitleExtractScanner + DI wiring

**Files:**
- Create: `Jellyfin.Plugin.MediaDash/Scanners/SubtitleExtractScanner.cs`
- Create: `Jellyfin.Plugin.MediaDash.Tests/SubtitleExtractScannerTests.cs`
- Modify: `Jellyfin.Plugin.MediaDash/PluginServiceRegistrator.cs` (finish Task 1 Step 5)

- [ ] **Step 1: Write the failing test — scanner flags file with allowed text sub**

Create `Jellyfin.Plugin.MediaDash.Tests/SubtitleExtractScannerTests.cs`. Model the setup after `Jellyfin.Plugin.MediaDash.Tests/TrickplayOptimizeScannerTests.cs` (fake probe + real scanner + Config injection). Minimum:

```csharp
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class SubtitleExtractScannerTests
{
    [Fact]
    public async Task Scan_FileWithAllowedLanguageTextSub_Flagged()
    {
        // Arrange: fake probe returns a video with one English subrip stream. Config.AllowedSubtitleLanguages = ["eng"].
        // Config.SubtitleExtractFixMode = FixMode.DetectOnly (any non-Off mode makes the scanner run).
        //
        // Act: scanner.ScanAsync on the file.
        //
        // Assert: one issue of type IssueType.SubtitleExtract is returned.
        Assert.True(false, "Fill in the ProbingScannerBase wiring — mirror TrickplayOptimizeScannerTests.cs setup exactly.");
    }

    [Fact]
    public async Task Scan_FileWithOnlyImageSub_NotFlagged()
    {
        // Arrange: probe returns one hdmv_pgs_subtitle stream.
        // Assert: no issues returned.
        Assert.True(false, "Fill in.");
    }

    [Fact]
    public async Task Scan_FileWithNoSubs_NotFlagged()
    {
        // Assert: no issues returned.
        Assert.True(false, "Fill in.");
    }

    [Fact]
    public async Task Scan_SubtitleExtractFixModeOff_ReturnsEmpty()
    {
        // Arrange: config.SubtitleExtractFixMode = FixMode.Off.
        // Assert: no issues returned (scanner short-circuits per its IsConfigured check).
        Assert.True(false, "Fill in.");
    }

    [Fact]
    public async Task Scan_DisallowedLanguageOnly_NotFlagged()
    {
        // Arrange: probe returns one French subrip stream; allowed = ["eng"].
        // Assert: no issues (SubtitleLanguageFixer's job, not ours).
        Assert.True(false, "Fill in.");
    }

    [Fact]
    public async Task Scan_HearingImpairedNonAllowedLanguage_WithHiProtection_Flagged()
    {
        // Arrange: French SDH sub; allowed = ["eng"]; SubtitleHearingImpairedMode = true.
        // Assert: one issue returned.
        Assert.True(false, "Fill in.");
    }
}
```

- [ ] **Step 2: Run — expected FAIL (asserts)**

```
dotnet test --filter "FullyQualifiedName~SubtitleExtractScannerTests"
```

- [ ] **Step 3: Create the scanner**

Create `Jellyfin.Plugin.MediaDash/Scanners/SubtitleExtractScanner.cs`:

```csharp
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Probing;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Flags videos with embedded text subtitles that qualify for extraction to sidecar files.
/// Uses <see cref="SubtitleExtractArgs.QualifyingStreams"/> for the eligibility check so the
/// scanner and fixer agree exactly on which streams matter.
/// </summary>
public sealed class SubtitleExtractScanner : ProbingScannerBase
{
    /// <summary>Initializes a new instance of the <see cref="SubtitleExtractScanner"/> class.</summary>
    /// <param name="ffprobe">The probe service.</param>
    /// <param name="logger">The logger.</param>
    public SubtitleExtractScanner(FfprobeService ffprobe, ILogger<SubtitleExtractScanner> logger)
        : base(ffprobe, logger)
    {
    }

    /// <inheritdoc />
    public override IssueType Type => IssueType.SubtitleExtract;

    /// <inheritdoc />
    protected override bool IsConfigured() => Config.SubtitleExtractFixMode != FixMode.Off;

    /// <inheritdoc />
    protected override Task<Issue?> EvaluateAsync(BaseItem item, string path, FfprobeData? probe, CancellationToken cancellationToken)
    {
        if (probe?.Streams is null)
        {
            return Task.FromResult<Issue?>(null);
        }

        var qualifying = SubtitleExtractArgs.QualifyingStreams(
            probe.Streams,
            Config.AllowedSubtitleLanguages,
            Config.SubtitleHearingImpairedMode);

        if (qualifying.Count == 0)
        {
            return Task.FromResult<Issue?>(null);
        }

        var details = JsonSerializer.Serialize(new
        {
            streamCount = qualifying.Count,
            reason = "extract-text-subs"
        });

        return Task.FromResult<Issue?>(new Issue
        {
            DetailsJson = details,
            SuggestedFix = $"Extract {qualifying.Count} embedded subtitle(s) to sidecar file(s) and remove from the container.",
            SizeSavings = 0
        });
    }
}
```

- [ ] **Step 4: Fill in the test bodies**

Reference `TrickplayOptimizeScannerTests.cs` for the fake `FfprobeService` and `Config` injection pattern. Wire so:
- Each test constructs a `Plugin.Instance.Configuration` (or the plugin's config-access equivalent used in tests) with the specific `SubtitleExtractFixMode`, `AllowedSubtitleLanguages`, `SubtitleHearingImpairedMode` values.
- Fake probe returns a `FfprobeData` with the described streams for the test's input path.
- Assert on the number and type of returned issues.

- [ ] **Step 5: Finish Task 1 Step 5 — register the scanner + fixer in DI**

In `PluginServiceRegistrator.cs`, add near the other subtitle registrations:

```csharp
serviceCollection.AddSingleton<Scanners.SubtitleExtractScanner>();
serviceCollection.AddSingleton<Fixers.SubtitleExtractFixer>();
```

Also add both to whatever `IEnumerable<IScanner>` / `IEnumerable<IFixer>` aggregation exists in the file (grep for `SubtitleLanguageScanner` / `SubtitleLanguageFixer` to see the pattern).

The `SubtitleExtractFixer` type doesn't exist yet — the build will fail. Either scaffold the fixer file with an empty stub class now (recommended) or hold this step until Task 4 lands.

Scaffold stub for now — create `Jellyfin.Plugin.MediaDash/Fixers/SubtitleExtractFixer.cs` with:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>Stub — real implementation lands in Task 4.</summary>
public sealed class SubtitleExtractFixer : IFixer
{
    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.SubtitleExtract;

    /// <inheritdoc />
    public Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
        => throw new NotImplementedException("Implemented in Task 4.");
}
```

- [ ] **Step 6: Build**

```
dotnet build Jellyfin.Plugin.MediaDash.sln /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary
```
Expected: build succeeds.

- [ ] **Step 7: Run scanner tests — expected PASS**

```
dotnet test --filter "FullyQualifiedName~SubtitleExtractScannerTests"
```

- [ ] **Step 8: Commit**

```
git add Jellyfin.Plugin.MediaDash/Scanners/SubtitleExtractScanner.cs Jellyfin.Plugin.MediaDash/Fixers/SubtitleExtractFixer.cs Jellyfin.Plugin.MediaDash/PluginServiceRegistrator.cs Jellyfin.Plugin.MediaDash.Tests/SubtitleExtractScannerTests.cs
git commit -m "feat(subtitle-extract): scanner + DI registration (fixer stub)"
```

---

## Task 4: SubtitleExtractFixer (standalone dispatch)

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Fixers/SubtitleExtractFixer.cs` (replace stub with full implementation)
- Create: `Jellyfin.Plugin.MediaDash.Tests/SubtitleExtractFixerTests.cs`

- [ ] **Step 1: Write the failing test — happy path (extract one text sub, remove from container)**

Create `Jellyfin.Plugin.MediaDash.Tests/SubtitleExtractFixerTests.cs`. Model after `TrackFixerSubtitleGuardTests.cs`. Minimum:

```csharp
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class SubtitleExtractFixerTests
{
    [Fact]
    public async Task Fix_OneQualifyingSub_ExtractsToSidecar_RemovesFromContainer()
    {
        // Arrange: fixture MKV in a temp library, fake ffprobe returning one English subrip stream.
        // Fake FfmpegExecutor that touches every sidecar path in its args + writes a valid container.
        // Config: SubtitleExtractFixMode = Automatic, disposal = RecycleBin.
        //
        // Act: FixAsync.
        //
        // Assert: sidecar exists at expected Jellyfin-conventional path;
        // original recycled; new container at original path; FixResult.Success with "extracted 1 subtitle" message.
        Assert.True(false, "Fill in with the fake ffprobe / ffmpeg wiring from TrackFixerSubtitleGuardTests.cs.");
    }

    [Fact]
    public async Task Fix_SidecarExists_StreamSkipped()
    {
        // Arrange: fixture with one English subrip stream; a `Movie.eng.srt` sidecar already on disk.
        // Assert: FfmpegExecutor NOT called (nothing to do); original untouched; FixResult message notes the skip.
        Assert.True(false, "Fill in.");
    }

    [Fact]
    public async Task Fix_NoQualifyingSubs_ReturnsNothingToExtract()
    {
        // Arrange: fixture with only image subs.
        // Assert: FfmpegExecutor NOT called; FixResult.Success with "nothing to extract" message.
        Assert.True(false, "Fill in.");
    }

    [Fact]
    public async Task Fix_FfmpegFails_TempsDeleted_OriginalUntouched()
    {
        // Arrange: fake FfmpegExecutor returns an error string.
        // Assert: any partially-written sidecar and the temp container are deleted; original file is untouched;
        // FixResult.Fail with the error tail in the message.
        Assert.True(false, "Fill in.");
    }

    [Fact]
    public async Task Fix_SidecarVerifyFails_AllCleanedUp()
    {
        // Arrange: FfmpegExecutor writes an empty sidecar file (0 bytes).
        // Assert: sidecar and temp container both deleted; original untouched; FixResult.Fail.
        Assert.True(false, "Fill in.");
    }

    [Fact]
    public async Task Fix_ContainerVerifyFails_SidecarsAlsoDeleted()
    {
        // Arrange: OutputVerifier returns an error string.
        // Assert: all sidecars written this pass ALSO deleted (all-or-nothing);
        // temp container deleted; original untouched.
        Assert.True(false, "Fill in.");
    }

    [Fact]
    public async Task Fix_DryRun_ReturnsDryRunResult_NoSideEffects()
    {
        // Arrange: Config.DryRun = true.
        // Assert: FfmpegExecutor NOT called; no filesystem side effects; FixResult.DryRun with the planned action.
        Assert.True(false, "Fill in.");
    }
}
```

- [ ] **Step 2: Run — expected FAIL**

```
dotnet test --filter "FullyQualifiedName~SubtitleExtractFixerTests"
```

- [ ] **Step 3: Replace the stub with the real fixer**

Replace `Jellyfin.Plugin.MediaDash/Fixers/SubtitleExtractFixer.cs` entirely with:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Standalone dispatch path for SubtitleExtract issues: re-probe, extract qualifying text subs
/// to sidecars, remux the container without those streams, verify sidecars + container, swap.
/// Combined-pass integration lives in <see cref="TrackFixer.FixCombinedAsync"/> and
/// <see cref="TranscodeFixer.FixAsync"/>; this fixer handles the "no other issues on the same file" case.
/// </summary>
public sealed class SubtitleExtractFixer : IFixer
{
    private static readonly TimeSpan RemuxTimeout = TimeSpan.FromHours(2);

    private readonly FfprobeService _ffprobe;
    private readonly FfmpegExecutor _ffmpeg;
    private readonly OutputVerifier _verifier;
    private readonly LibraryGuard _guard;
    private readonly RecycleBin _recycleBin;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILogger<SubtitleExtractFixer> _logger;

    /// <summary>Initializes a new instance of the <see cref="SubtitleExtractFixer"/> class.</summary>
    public SubtitleExtractFixer(
        FfprobeService ffprobe,
        FfmpegExecutor ffmpeg,
        OutputVerifier verifier,
        LibraryGuard guard,
        RecycleBin recycleBin,
        ILibraryMonitor libraryMonitor,
        ILogger<SubtitleExtractFixer> logger)
    {
        _ffprobe = ffprobe;
        _ffmpeg = ffmpeg;
        _verifier = verifier;
        _guard = guard;
        _recycleBin = recycleBin;
        _libraryMonitor = libraryMonitor;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.SubtitleExtract;

    /// <inheritdoc />
    public async Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;

        if (!File.Exists(issue.Path))
        {
            return FixResult.Fail("The file no longer exists; re-scan to refresh the list.");
        }

        if (!_guard.IsInsideLibrary(issue.Path))
        {
            return FixResult.Fail("The file is outside your library folders; MediaDash will not touch it.");
        }

        var probe = await _ffprobe.ProbeAsync(issue.Path, cancellationToken).ConfigureAwait(false);
        if (probe?.Streams is null)
        {
            return FixResult.Fail("The file could not be probed.");
        }

        var plan = SubtitleExtractArgs.Plan(
            issue.Path,
            probe.Streams,
            config.AllowedSubtitleLanguages,
            config.SubtitleHearingImpairedMode,
            File.Exists);

        if (plan.Count == 0)
        {
            return FixResult.Success("nothing to extract; all qualifying sidecars already present or no text subs on disk");
        }

        var extractedIndexes = new HashSet<int>(plan.Select(p => p.StreamIndex));
        var disposal = config.GetDisposal(IssueType.SubtitleExtract);
        var actionText = string.Format(
            CultureInfo.InvariantCulture,
            "extracted {0} subtitle(s) from {1} ({2})",
            plan.Count,
            Path.GetFileName(issue.Path),
            disposal == DisposalMethod.RecycleBin ? "original kept in recycle bin" : "original permanently deleted");

        if (config.DryRun)
        {
            return FixResult.DryRun(actionText, 0);
        }

        var ext = Path.GetExtension(issue.Path).TrimStart('.');
        if (string.IsNullOrEmpty(ext))
        {
            return FixResult.Fail("The source file has no extension; can't determine target container.");
        }

        var tempContainer = TranscodeFixer.SidecarPath(issue.Path, "subxtract.tmp", ext);

        var args = new List<string> { "-i", issue.Path };
        // Video + audio: copy every stream, unchanged.
        args.Add("-map"); args.Add("0:v?");
        args.Add("-map"); args.Add("0:a?");
        // Subtitles NOT being extracted: keep in container. Subtitles being extracted: omit from container.
        foreach (var s in probe.Streams.Where(s => string.Equals(s.CodecType, "subtitle", StringComparison.OrdinalIgnoreCase)))
        {
            if (extractedIndexes.Contains(s.Index))
            {
                continue;
            }

            args.Add("-map");
            args.Add("0:" + s.Index.ToString(CultureInfo.InvariantCulture));
        }

        args.Add("-c"); args.Add("copy");
        args.Add("-map_chapters"); args.Add("0");
        args.Add("-y"); args.Add(tempContainer);

        // Append sidecar extraction args at the end.
        args.AddRange(SubtitleExtractArgs.BuildFfmpegArgs(plan));

        // Track everything written this pass so we can clean up all-or-nothing on failure.
        var writtenSidecars = plan.Select(p => p.SidecarPath).ToList();

        try
        {
            var ffmpegErr = await _ffmpeg.RunAsync(args, RemuxTimeout, cancellationToken).ConfigureAwait(false);
            if (ffmpegErr is not null)
            {
                _logger.LogWarning("Subtitle extract ffmpeg failed on {Path}: {Error}", issue.Path, TranscodeFixer.Truncate(ffmpegErr));
                CleanupPartials(tempContainer, writtenSidecars);
                return FixResult.Fail("Subtitle extraction failed; the original is untouched. Details: " + TranscodeFixer.Truncate(ffmpegErr));
            }

            // Verify each sidecar exists, is >0 bytes, and parses.
            foreach (var sidecar in writtenSidecars)
            {
                if (!File.Exists(sidecar) || new FileInfo(sidecar).Length == 0)
                {
                    _logger.LogWarning("Sidecar verify failed for {Path}: {Sidecar} missing or empty.", issue.Path, sidecar);
                    CleanupPartials(tempContainer, writtenSidecars);
                    return FixResult.Fail("A subtitle sidecar was missing or empty after extraction; the original is untouched.");
                }

                var sidecarProbe = await _ffprobe.ProbeAsync(sidecar, cancellationToken).ConfigureAwait(false);
                if (sidecarProbe?.Streams is null || sidecarProbe.Streams.Count == 0)
                {
                    _logger.LogWarning("Sidecar verify failed for {Path}: {Sidecar} unparseable.", issue.Path, sidecar);
                    CleanupPartials(tempContainer, writtenSidecars);
                    return FixResult.Fail("A subtitle sidecar was unparseable after extraction; the original is untouched.");
                }
            }

            // Verify container.
            var verifyErr = await _verifier.VerifyAsync(probe, issue.Path, tempContainer, cancellationToken).ConfigureAwait(false);
            if (verifyErr is not null)
            {
                _logger.LogWarning("Container verify failed for {Path}: {Error}", issue.Path, verifyErr);
                CleanupPartials(tempContainer, writtenSidecars);
                return FixResult.Fail("The re-muxed container failed verification; the original is untouched. Details: " + verifyErr);
            }

            // Swap.
            string? recyclePath = null;
            if (disposal == DisposalMethod.RecycleBin)
            {
                recyclePath = _recycleBin.MoveToBin(issue.Path);
            }
            else
            {
                File.Delete(issue.Path);
            }

            File.Move(tempContainer, issue.Path);
            _libraryMonitor.ReportFileSystemChanged(issue.Path);
            foreach (var sidecar in writtenSidecars)
            {
                _libraryMonitor.ReportFileSystemChanged(sidecar);
            }

            _logger.LogInformation("Subtitle extract: {Action}", actionText);
            return new FixResult
            {
                Success = true,
                Message = actionText,
                BytesFreed = 0,
                RecyclePath = recyclePath
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Subtitle extract crashed on {Path}", issue.Path);
            CleanupPartials(tempContainer, writtenSidecars);
            throw;
        }
    }

    private static void CleanupPartials(string tempContainer, IReadOnlyList<string> sidecars)
    {
        TryDelete(tempContainer);
        foreach (var sc in sidecars)
        {
            TryDelete(sc);
        }
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
            // Best-effort cleanup.
        }
    }
}
```

- [ ] **Step 4: Fill in the test bodies**

Mirror `TrackFixerSubtitleGuardTests.cs` for the fake wiring: a `FakeFfprobeService`, a `FakeFfmpegExecutor` that writes stub sidecars and a stub container, a temp library root with real `LibraryGuard` scoped to it, and a real `RecycleBin` pointed at a per-test temp dir.

Set up per test:
- Populate `Plugin.Instance.Configuration` (or the test-injected equivalent) with the described values.
- Place a fixture file at a path inside the temp library.
- Instantiate the fixer with the fakes.
- Await `FixAsync(new Issue { Type = IssueType.SubtitleExtract, Path = fixturePath }, null, CancellationToken.None)`.
- Assert on file-system side effects and the returned `FixResult`.

- [ ] **Step 5: Run — expected PASS**

```
dotnet test --filter "FullyQualifiedName~SubtitleExtractFixerTests"
```

- [ ] **Step 6: Commit**

```
git add Jellyfin.Plugin.MediaDash/Fixers/SubtitleExtractFixer.cs Jellyfin.Plugin.MediaDash.Tests/SubtitleExtractFixerTests.cs
git commit -m "feat(subtitle-extract): standalone fixer (extract + remux + verify + swap)"
```

---

## Task 5: TrackFixer.FixCombinedAsync widening

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Fixers/TrackFixer.cs`
- Create: `Jellyfin.Plugin.MediaDash.Tests/SubtitleExtractCombinedPassTests.cs`

- [ ] **Step 1: Read the current FixCombinedAsync signature and call sites**

Run:
```
grep -n "FixCombinedAsync" Jellyfin.Plugin.MediaDash/Fixers/TrackFixer.cs Jellyfin.Plugin.MediaDash/ScheduledTasks/FixTask.cs
```

Note the current signature (Grep results from the exploration showed `FixCombinedAsync(issue, partner!, itemProgress, cancellationToken)` — audio + subtitle pair). You will widen it to accept a third optional companion.

- [ ] **Step 2: Write the failing test — combined pass with Extract companion**

Create `Jellyfin.Plugin.MediaDash.Tests/SubtitleExtractCombinedPassTests.cs`:

```csharp
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class SubtitleExtractCombinedPassTests
{
    [Fact]
    public async Task Combined_AudioLangPlusExtract_OneFfmpegPass_BothCompanionsFixed()
    {
        // Arrange: fixture with 1 video, 2 audio (Eng + Fra), 2 subs (Eng subrip, Fra subrip).
        // Config: AllowedAudioLanguages=["eng"], AllowedSubtitleLanguages=["eng"],
        //         SubtitleExtractFixMode=Automatic, all disposals=RecycleBin.
        // Issues: AudioLanguage + SubtitleExtract on same path.
        //
        // Act: TrackFixer.FixCombinedAsync(audioIssue, extractCompanion: subtitleExtractIssue, subLanguagePartner: null).
        //
        // Assert:
        // - FfmpegExecutor called exactly ONCE.
        // - args include -map -0:a:<French idx> (audio filter)
        // - args include the extract fragments for the English sub
        // - Sidecar exists at expected path
        // - Container has: video, English audio, no subs (Eng was extracted, Fra was filtered by language)
        Assert.True(false, "Fill in with the fake wiring pattern from SubtitleExtractFixerTests.");
    }

    [Fact]
    public async Task Combined_AudioLangPlusSubLangPlusExtract_OneFfmpegPass_ThreeCompanionsFixed()
    {
        // Arrange: fixture with 1 video, 2 audio, 3 subs (Eng, Fra, Jpn).
        // Config: allowed audio ["eng"], allowed subs ["eng","jpn"], extract on.
        // Issues: AudioLanguage + SubtitleLanguage + SubtitleExtract on same path.
        //
        // Act: FixCombinedAsync with all three companions.
        //
        // Assert:
        // - ONE ffmpeg call
        // - args include -map -0:a:<French idx>, -map -0:s:<French sub idx>
        // - args include extract fragments for the Eng and Jpn subs (survivors after language filter)
        // - Two sidecars exist; container has video + Eng audio + no subs
        Assert.True(false, "Fill in.");
    }

    [Fact]
    public async Task Combined_ExtractSkipsStreamsWithPreExistingSidecar_ContainerAlsoSkipsThoseStreams()
    {
        // Arrange: fixture with 1 English + 1 French subrip; a Fixture.eng.srt sidecar already on disk.
        // Config: extract on, allow all.
        // Issues: SubtitleExtract only (paired with itself — standalone through combined path is a valid case).
        //
        // Assert: FfmpegExecutor call args extract ONLY the French sub. English stream stays in the container
        // (per §4.6 rule 3: don't orphan an embedded sub whose sidecar we refused to write).
        Assert.True(false, "Fill in.");
    }
}
```

- [ ] **Step 3: Run — expected FAIL (signature doesn't exist yet)**

```
dotnet test --filter "FullyQualifiedName~SubtitleExtractCombinedPassTests"
```

- [ ] **Step 4: Widen `TrackFixer.FixCombinedAsync`**

In `Jellyfin.Plugin.MediaDash/Fixers/TrackFixer.cs`, find the current `FixCombinedAsync` method. Widen its signature:

```csharp
public async Task<FixResult> FixCombinedAsync(
    Issue issue,
    Issue? subLanguagePartner,
    Issue? extractCompanion,
    IProgress<double>? progress,
    CancellationToken cancellationToken)
{
    // Existing combined-remux logic stays intact for the AudioLang + SubLang case.
    // When extractCompanion is non-null, use SubtitleExtractArgs.Plan on the current probe
    // to compute the extract plan (after language-filter drops disallowed subs), then append
    // SubtitleExtractArgs.BuildFfmpegArgs(plan) to the ffmpeg command.
    // Verify each written sidecar (exists, >0 bytes, ffprobe-parseable). On any failure,
    // delete all sidecars written this pass alongside the temp container.
    // On success, additionally ReportFileSystemChanged for each sidecar path.
    ...
}
```

Full implementation guidance (fill in around the existing combined-pass body):

1. **After computing the current probe and running the existing language filter logic** to know which sub streams survive: if `extractCompanion is not null`, compute `var extractPlan = SubtitleExtractArgs.Plan(issue.Path, survivingSubs, config.AllowedSubtitleLanguages, config.SubtitleHearingImpairedMode, File.Exists);`
2. **When building the ffmpeg args**: the survivingSubs already excludes disallowed languages via existing `-map -0:...` logic. For every stream in the extract plan: (a) OMIT it from the container output's sub `-map` list, and (b) append the extract fragments via `SubtitleExtractArgs.BuildFfmpegArgs(extractPlan)`.
3. **After ffmpeg succeeds**: verify each `extractPlan[*].SidecarPath` exists, non-zero, parseable. If any fail, delete every sidecar written + the temp container + return `FixResult.Fail`.
4. **On success**: `ReportFileSystemChanged` for the container AND each sidecar.
5. **All existing callers must update to pass `extractCompanion: null`** for the existing two-companion cases (search all call sites for the old signature; only `FixTask.cs` should be one).

- [ ] **Step 5: Update the one existing caller in FixTask.cs**

Find the `FixCombinedAsync` call site in `FixTask.cs` (grep already located it near line 495). Update to pass `extractCompanion: null` at that call site — the new dispatch path for Extract-inclusive combined cases lands in Task 6.

- [ ] **Step 6: Fill in the combined-pass test bodies**

Reference `TrackFixerSubtitleGuardTests.cs`. Build fixtures per test, wire fake `FfmpegExecutor` that captures its args (so tests can assert on the arg list), fake probe, run `FixCombinedAsync` with the appropriate companion set, assert.

- [ ] **Step 7: Run — expected PASS**

```
dotnet test --filter "FullyQualifiedName~SubtitleExtractCombinedPassTests"
```

- [ ] **Step 8: Commit**

```
git add Jellyfin.Plugin.MediaDash/Fixers/TrackFixer.cs Jellyfin.Plugin.MediaDash/ScheduledTasks/FixTask.cs Jellyfin.Plugin.MediaDash.Tests/SubtitleExtractCombinedPassTests.cs
git commit -m "feat(subtitle-extract): TrackFixer combined-pass integration (3rd companion type)"
```

---

## Task 6: FixTask companion detection extended

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/ScheduledTasks/FixTask.cs`
- Create: `Jellyfin.Plugin.MediaDash.Tests/FixTaskSubtitleExtractCompanionTests.cs`

- [ ] **Step 1: Write the failing test — BuildTranscodeCompanions claims Extract**

Create `Jellyfin.Plugin.MediaDash.Tests/FixTaskSubtitleExtractCompanionTests.cs`. Model after existing `FixTask*Tests.cs` files. Tests:

```csharp
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class FixTaskSubtitleExtractCompanionTests
{
    [Fact]
    public void BuildTranscodeCompanions_TranscodePlusExtract_ExtractIsClaimed()
    {
        var queue = new List<Issue>
        {
            new() { Id = 1, Type = IssueType.Quality, Path = "/movies/M.mkv" },
            new() { Id = 2, Type = IssueType.SubtitleExtract, Path = "/movies/M.mkv" }
        };

        var companions = FixTask.BuildTranscodeCompanions(queue);

        Assert.True(companions.ContainsKey(1));
        Assert.Contains(companions[1], c => c.Id == 2);
    }

    [Fact]
    public void BuildTranscodeCompanions_TranscodePlusExtractPlusAudioLang_AllClaimed()
    {
        var queue = new List<Issue>
        {
            new() { Id = 1, Type = IssueType.Quality, Path = "/movies/M.mkv" },
            new() { Id = 2, Type = IssueType.AudioLanguage, Path = "/movies/M.mkv" },
            new() { Id = 3, Type = IssueType.SubtitleExtract, Path = "/movies/M.mkv" }
        };

        var companions = FixTask.BuildTranscodeCompanions(queue);

        Assert.True(companions.ContainsKey(1));
        Assert.Equal(2, companions[1].Count);
        Assert.Contains(companions[1], c => c.Type == IssueType.AudioLanguage);
        Assert.Contains(companions[1], c => c.Type == IssueType.SubtitleExtract);
    }

    [Fact]
    public void BuildTranscodeCompanions_ExtractAlone_NotClaimed()
    {
        // Extract with no Transcode present: BuildTranscodeCompanions returns nothing for it.
        var queue = new List<Issue>
        {
            new() { Id = 1, Type = IssueType.SubtitleExtract, Path = "/movies/M.mkv" }
        };

        var companions = FixTask.BuildTranscodeCompanions(queue);

        Assert.Empty(companions);
    }
}
```

- [ ] **Step 2: Run — expected FAIL (Extract not yet in the eligibility set)**

```
dotnet test --filter "FullyQualifiedName~FixTaskSubtitleExtractCompanionTests"
```

- [ ] **Step 3: Extend `BuildTranscodeCompanions` in FixTask.cs**

In `FixTask.cs`, find `BuildTranscodeCompanions` (around line 1114). Add `SubtitleExtract` to the eligibility filter:

```csharp
internal static Dictionary<long, List<Issue>> BuildTranscodeCompanions(IReadOnlyList<Issue> queue)
{
    var result = new Dictionary<long, List<Issue>>();
    var pathGroups = queue
        .Where(i => i.Type is IssueType.Quality
                              or IssueType.HeavyTranscode
                              or IssueType.FailedTranscode
                              or IssueType.AudioLanguage
                              or IssueType.SubtitleLanguage
                              or IssueType.SubtitleExtract)   // NEW
        .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase);
    // ...rest of the method: the loop that picks the transcode and claims the others as companions.
    // Read the existing body and confirm the companion-claim loop already picks up any non-transcode
    // issue in the group. If it hardcodes "AudioLanguage or SubtitleLanguage", add SubtitleExtract
    // to that check as well.
}
```

Read the full body of the method and adjust any explicit type check that filters companion candidates to include `SubtitleExtract`.

- [ ] **Step 4: Extend the combined-pair block in FixTask.cs to include Extract**

Find the block near line 321 (`combinedPairs` / `combinedPartners` computation). Widen from the current 2-way Audio+Sub check to any subset of `{AudioLanguage, SubtitleLanguage, SubtitleExtract}`:

```csharp
// Combined-pass detection: same file with any two-or-three of {AudioLanguage, SubtitleLanguage, SubtitleExtract} queued.
// TrackFixer.FixCombinedAsync handles all combinations in one ffmpeg remux.
// combinedPrimary maps the PRIMARY issue id (audio takes precedence, then sub-language, then extract)
// to its companion issues on the same path. combinedPartners is every claimed non-primary id.
var combinedPrimary = new Dictionary<long, CombinedCompanionSet>();
var combinedPartners = new HashSet<long>();
{
    var trackable = queue
        .Where(i => (i.Type == IssueType.AudioLanguage
                     || i.Type == IssueType.SubtitleLanguage
                     || i.Type == IssueType.SubtitleExtract)
                    && !transcodeCompanionIds.Contains(i.Id))
        .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase);
    foreach (var pathGroup in trackable)
    {
        var audio = pathGroup.FirstOrDefault(i => i.Type == IssueType.AudioLanguage);
        var subLang = pathGroup.FirstOrDefault(i => i.Type == IssueType.SubtitleLanguage);
        var extract = pathGroup.FirstOrDefault(i => i.Type == IssueType.SubtitleExtract);

        // Primary is the earliest-in-precedence non-null: audio > subLang > extract.
        var primary = audio ?? subLang ?? extract;
        if (primary is null)
        {
            continue;
        }

        // Only bundle if there are 2+ issues on this path OR the extract is present (extract needs the widened path).
        var companions = new List<Issue?> { audio, subLang, extract }.Where(i => i is not null && i.Id != primary.Id).Cast<Issue>().ToList();
        if (companions.Count == 0)
        {
            // Only the primary is queued — standalone fixer path handles it.
            continue;
        }

        combinedPrimary[primary.Id] = new CombinedCompanionSet
        {
            SubLanguage = primary.Type != IssueType.SubtitleLanguage ? subLang : null,
            Extract = primary.Type != IssueType.SubtitleExtract ? extract : null
        };
        foreach (var comp in companions)
        {
            combinedPartners.Add(comp.Id);
        }
    }
}

// Small carrier type for what FixCombinedAsync now needs.
private readonly record struct CombinedCompanionSet
{
    public Issue? SubLanguage { get; init; }
    public Issue? Extract { get; init; }
}
```

- [ ] **Step 5: Update the dispatch switch to route through the new companion set**

Find the dispatch site (near line 495 where the old `FixCombinedAsync(issue, partner!, ...)` lived). Replace:

```csharp
if (isCombinedPair)
{
    // OLD:
    // result = await ((TrackFixer)fixer!).FixCombinedAsync(issue, partner!, itemProgress, cancellationToken);

    // NEW:
    var companionSet = combinedPrimary[issue.Id];
    result = await ((TrackFixer)fixer!).FixCombinedAsync(
        issue,
        companionSet.SubLanguage,
        companionSet.Extract,
        itemProgress,
        cancellationToken).ConfigureAwait(false);
}
```

Also update the surrounding rename from `combinedPairs` / `partner` to `combinedPrimary` / `companionSet` — pure renaming.

- [ ] **Step 6: Additional history rows for the extract companion**

Follow the existing pattern that emits a second history row for the sub-language partner (search for `isCombinedPair && result.Success && partner is not null` near line 519). Extend to also emit a history row for the extract companion when present.

- [ ] **Step 7: Run — expected PASS**

```
dotnet test --filter "FullyQualifiedName~FixTaskSubtitleExtractCompanionTests"
```

- [ ] **Step 8: Run full test suite (regression check on FixTask changes)**

```
dotnet test --nologo
```
Expected: all tests pass. If any pre-existing `FixTask*Tests.cs` fails, it's because the combined-pair block rename broke it — fix the reference.

- [ ] **Step 9: Commit**

```
git add Jellyfin.Plugin.MediaDash/ScheduledTasks/FixTask.cs Jellyfin.Plugin.MediaDash.Tests/FixTaskSubtitleExtractCompanionTests.cs
git commit -m "feat(subtitle-extract): FixTask companion detection extended for Extract"
```

---

## Task 7: TranscodeFixer companion-aware BuildArgs

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Fixers/TranscodeFixer.cs`
- Modify: `Jellyfin.Plugin.MediaDash/ScheduledTasks/FixTask.cs` (dispatch update)
- Modify: `Jellyfin.Plugin.MediaDash.Tests/TranscodeFixerHwAccelTests.cs` (extend for new param)
- Create test rows in: `Jellyfin.Plugin.MediaDash.Tests/SubtitleExtractCombinedPassTests.cs`

- [ ] **Step 1: Write the failing test — Transcode + Extract companion emits extract fragments**

Append to `SubtitleExtractCombinedPassTests.cs`:

```csharp
[Fact]
public async Task Transcode_ClaimsExtractCompanion_BuildArgsEmitsExtractFragments()
{
    // Arrange: fixture with 1 video, 1 audio, 1 English subrip. Quality issue + SubtitleExtract on same path.
    // Config: extract on, allowed subs empty (all subs qualify).
    //
    // Act: TranscodeFixer.FixAsync (companion-aware) with extractCompanion set.
    //
    // Assert:
    // - Emitted ffmpeg args include extract fragments (-map 0:<sub_idx> -c:s copy <sidecar-path>)
    // - The container output does NOT map the sub stream
    // - Sidecar exists post-fix; container has video + audio, no subs
    Assert.True(false, "Fill in.");
}
```

- [ ] **Step 2: Run — expected FAIL**

```
dotnet test --filter "FullyQualifiedName~SubtitleExtractCombinedPassTests.Transcode_ClaimsExtractCompanion"
```

- [ ] **Step 3: Extend `TranscodeFixer.BuildArgs` to accept `subsToExtract`**

In `Jellyfin.Plugin.MediaDash/Fixers/TranscodeFixer.cs`, change the signature of `BuildArgs`:

```csharp
internal static List<string> BuildArgs(
    string inputPath,
    string tempPath,
    FfprobeData probe,
    FfprobeStreamInfo video,
    PluginConfiguration config,
    bool needsDownscale,
    string targetContainer,
    string? hardwareEncoder,
    string? vaapiDevice,
    IReadOnlyList<SubtitleExtractPlanItem>? subsToExtract = null)
{
    // ... existing body ...

    // Where subtitles get mapped into the output container (search for the MKV subtitle mapping
    // block that iterates `keptSubs`), EXCLUDE any stream whose index is in subsToExtract.
    var extractedIndexes = subsToExtract is null
        ? new HashSet<int>()
        : new HashSet<int>(subsToExtract.Select(s => s.StreamIndex));

    foreach (var stream in keptSubs)
    {
        if (extractedIndexes.Contains(stream.Index))
        {
            continue;
        }
        args.Add("-map");
        args.Add("0:" + stream.Index.ToString(CultureInfo.InvariantCulture));
    }

    // ... existing args ...

    // At the end of the method, before returning args, append the extract fragments.
    if (subsToExtract is not null && subsToExtract.Count > 0)
    {
        args.AddRange(SubtitleExtractArgs.BuildFfmpegArgs(subsToExtract));
    }

    return args;
}
```

Update the file's `using` list if it doesn't already reference `SubtitleExtractPlanItem`.

- [ ] **Step 4: Update the existing HW-accel tests to pass null for the new param**

The current `TranscodeFixerHwAccelTests.cs` calls `TranscodeFixer.BuildArgs(...)` with the old positional args. C# default parameter binding means adding a default at the end of the signature is source-compatible — the existing test calls should still compile and pass. Run:

```
dotnet test --filter "FullyQualifiedName~TranscodeFixerHwAccelTests"
```

Expected: 11/11 pass. If any fail with signature errors, add `subsToExtract: null` explicitly at each call.

- [ ] **Step 5: Thread the param through `TranscodeFixer.FixAsync`**

Add a new overload (or a new companion-aware entry point) that accepts the extract companion:

```csharp
public async Task<FixResult> FixAsync(Issue issue, Issue? extractCompanion, IProgress<double>? progress, CancellationToken cancellationToken)
{
    // Existing FixAsync body — with the additions below.

    // After probing and knowing which sub streams will survive AllowedSubtitleLanguages filtering
    // (search for `keptSubs` in the existing body), if extractCompanion is non-null, compute:
    var extractPlan = extractCompanion is null
        ? null
        : SubtitleExtractArgs.Plan(issue.Path, keptSubs, config.AllowedSubtitleLanguages, config.SubtitleHearingImpairedMode, File.Exists);

    // Pass extractPlan through every BuildArgs call site in this method (there are three: hwArgs, swArgs, args).
    var hwArgs = BuildArgs(issue.Path, tempPath, probe, video, config, needsDownscale, targetContainer, hwEncoder, vaapiDevice, extractPlan);
    // ... (analogous for the other two sites) ...

    // After ffmpeg succeeds and container verification passes, ALSO verify each sidecar exists,
    // is non-empty, and parses. On failure, delete sidecars, delete tempPath, return Fail.
    if (extractPlan is not null)
    {
        foreach (var item in extractPlan)
        {
            if (!File.Exists(item.SidecarPath) || new FileInfo(item.SidecarPath).Length == 0)
            {
                foreach (var it in extractPlan) TryDelete(it.SidecarPath);
                if (File.Exists(tempPath)) File.Delete(tempPath);
                return FixResult.Fail("A subtitle sidecar was missing or empty after transcode+extract; the original is untouched.");
            }
        }
    }

    // On final swap success, ReportFileSystemChanged for each sidecar too.
    if (extractPlan is not null)
    {
        foreach (var item in extractPlan)
        {
            _libraryMonitor.ReportFileSystemChanged(item.SidecarPath);
        }
    }

    // ... rest of existing method ...
}

// Keep the parameterless version for backward compat by delegating:
public Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    => FixAsync(issue, extractCompanion: null, progress, cancellationToken);
```

Note the `TryDelete` helper — add it as a private static in `TranscodeFixer.cs` if it isn't already there:

```csharp
private static void TryDelete(string path)
{
    try
    {
        if (File.Exists(path)) File.Delete(path);
    }
    catch (IOException) { }
}
```

- [ ] **Step 6: Route Transcode-companion dispatch in FixTask.cs**

At the dispatch site, when the issue is a transcode-family type AND has claimed an Extract companion via `transcodeCompanions`, call the new overload:

```csharp
// Existing: result = await RunFixWithSharingRetryAsync(fixer, issue, itemProgress, cancellationToken);

// If issue is a Transcode-family and transcodeCompanions[issue.Id] contains a SubtitleExtract issue:
Issue? extractCompanion = null;
if (transcodeCompanions.TryGetValue(issue.Id, out var claimed))
{
    extractCompanion = claimed.FirstOrDefault(c => c.Type == IssueType.SubtitleExtract);
}

result = extractCompanion is not null
    ? await ((Fixers.TranscodeFixer)fixer!).FixAsync(issue, extractCompanion, itemProgress, cancellationToken).ConfigureAwait(false)
    : await RunFixWithSharingRetryAsync(fixer, issue, itemProgress, cancellationToken).ConfigureAwait(false);
```

Also: emit a history row for the extract companion on success (mirror the existing sub-language history pattern).

- [ ] **Step 7: Run test — expected PASS**

```
dotnet test --filter "FullyQualifiedName~SubtitleExtractCombinedPassTests.Transcode_ClaimsExtractCompanion"
```

- [ ] **Step 8: Run full test suite (regression)**

```
dotnet test --nologo
```

- [ ] **Step 9: Commit**

```
git add Jellyfin.Plugin.MediaDash/Fixers/TranscodeFixer.cs Jellyfin.Plugin.MediaDash/ScheduledTasks/FixTask.cs Jellyfin.Plugin.MediaDash.Tests/SubtitleExtractCombinedPassTests.cs
git commit -m "feat(subtitle-extract): TranscodeFixer companion-aware BuildArgs"
```

---

## Task 8: Settings card UI

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Configuration/configPage.html`

- [ ] **Step 1: Locate the existing Subtitles section**

Run:
```
grep -n "SubtitleFixMode\|SubtitleDisposal\|Subtitle" Jellyfin.Plugin.MediaDash/Configuration/configPage.html | head -20
```

Insert the new card immediately after the existing Subtitles section, so users find extraction next to the other subtitle controls.

- [ ] **Step 2: Add the card markup**

```html
<div class="verticalSection" data-subtitle-extract-card>
  <h3 class="sectionTitle">Extract embedded subtitles</h3>
  <p class="fieldDescription">
    Save embedded text subtitles as separate <code>.srt</code> / <code>.ass</code> files
    next to the video, then remove them from the container. Only text subtitles in your
    allowed languages are extracted; picture-based subtitles (Blu-ray PGS, DVD VobSub)
    stay in the container.
  </p>

  <div class="selectContainer">
    <label class="selectLabel" for="SubtitleExtractFixMode">Fix mode</label>
    <select is="emby-select" id="SubtitleExtractFixMode" class="emby-select-withcolor emby-select">
      <option value="Off">Off</option>
      <option value="DetectOnly">Detect only</option>
      <option value="Automatic">Auto-fix</option>
    </select>
    <div class="fieldDescription">Off by default — extraction changes files on disk, so opt in explicitly.</div>
  </div>

  <div class="selectContainer">
    <label class="selectLabel" for="SubtitleExtractDisposal">When replacing the container</label>
    <select is="emby-select" id="SubtitleExtractDisposal" class="emby-select-withcolor emby-select">
      <option value="RecycleBin">Keep original in recycle bin</option>
      <option value="Permanent">Delete original permanently</option>
    </select>
  </div>
</div>
```

- [ ] **Step 3: Wire the controls in the load/save JS**

Find the JS block that loads config (search for `#SubtitleFixMode`). Add:

```js
page.querySelector('#SubtitleExtractFixMode').value = config.SubtitleExtractFixMode;
page.querySelector('#SubtitleExtractDisposal').value = config.SubtitleExtractDisposal;
```

Find the corresponding save block. Add:

```js
config.SubtitleExtractFixMode = page.querySelector('#SubtitleExtractFixMode').value;
config.SubtitleExtractDisposal = page.querySelector('#SubtitleExtractDisposal').value;
```

- [ ] **Step 4: Manual smoke test**

Deploy per CLAUDE.md:

```
Copy-Item Jellyfin.Plugin.MediaDash/bin/Debug/net9.0/publish/* "$env:LOCALAPPDATA/jellyfin/plugins/MediaDash_X.Y.Z.0/" -Recurse -Force
```

Restart Jellyfin at localhost:8099, open Settings → MediaDash, confirm:
- New "Extract embedded subtitles" card appears under Subtitles.
- Fix mode defaults to "Off" on a fresh install.
- Toggling to "Auto-fix", saving, reloading preserves the state.
- Toggling disposal, saving, reloading preserves the state.

- [ ] **Step 5: Commit**

```
git add Jellyfin.Plugin.MediaDash/Configuration/configPage.html
git commit -m "feat(subtitle-extract): settings card"
```

---

## Task 9: Real-world E2E fixtures

**Files:**
- Create: `tools/subtitle-extract-test/README.md`
- Create: `tools/subtitle-extract-test/regenerate.ps1`
- Create: `tools/subtitle-extract-test/fixtures/` (populated by regenerate.ps1)

- [ ] **Step 1: Author the regenerator script**

Create `tools/subtitle-extract-test/regenerate.ps1`:

```powershell
# Regenerates the four subtitle-extract E2E fixtures from a base video and a set of subtitle files.
# Usage: .\regenerate.ps1 -Source <path-to-30s-h264-mkv> -SrtEng <eng.srt> -SrtFra <fra.srt> -SrtJpn <jpn.srt>
param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$SrtEng,
    [Parameter(Mandatory=$true)][string]$SrtFra,
    [Parameter(Mandatory=$true)][string]$SrtJpn
)

$ff = 'C:\Users\crackruckles\Downloads\jellyfin_10.11.11-amd64\jellyfin\ffmpeg.exe'
$outDir = Join-Path $PSScriptRoot 'fixtures'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

# Fixture 1: single English subrip stream
& $ff -y -hide_banner -loglevel error `
    -i $Source -i $SrtEng `
    -map 0:v -map 0:a -map 1:s `
    -c:v copy -c:a copy -c:s srt `
    -metadata:s:s:0 language=eng `
    (Join-Path $outDir 'fixture-single-text-sub.mkv')

# Fixture 2: English + Spanish + French subrip streams
& $ff -y -hide_banner -loglevel error `
    -i $Source -i $SrtEng -i $SrtFra -i $SrtJpn `
    -map 0:v -map 0:a -map 1:s -map 2:s -map 3:s `
    -c:v copy -c:a copy -c:s srt `
    -metadata:s:s:0 language=eng -metadata:s:s:1 language=fra -metadata:s:s:2 language=jpn `
    (Join-Path $outDir 'fixture-multi-lang.mkv')

# Fixture 3: Text + image sub combo. Requires a PGS .sup file — reuse an existing one or extract from a Blu-ray sample.
# Placeholder: fixture-mixed-text-and-pgs.mkv must be regenerated by hand from a Blu-ray remux sample.
Write-Warning 'fixture-mixed-text-and-pgs.mkv requires a PGS source — regenerate manually.'

# Fixture 4: Pre-existing sidecar case — regenerated as a copy of fixture-single-text-sub.mkv with a co-located .eng.srt.
Copy-Item (Join-Path $outDir 'fixture-single-text-sub.mkv') (Join-Path $outDir 'fixture-preexisting-sidecar.mkv') -Force
Copy-Item $SrtEng (Join-Path $outDir 'fixture-preexisting-sidecar.eng.srt') -Force

Write-Output 'Fixtures regenerated in fixtures/.'
```

- [ ] **Step 2: Author the README**

Create `tools/subtitle-extract-test/README.md`:

```markdown
# subtitle-extract-test — E2E fixtures

Four fixtures for the SubtitleExtractFixer + combined-pass integration. Not
checked in as blobs; regenerate from source assets with `regenerate.ps1`.

| Fixture | What's inside | Expected behavior |
|---|---|---|
| fixture-single-text-sub.mkv | 1 video + 1 audio + 1 English subrip | Sidecar `fixture-single-text-sub.eng.srt` created; container stripped of subs. |
| fixture-multi-lang.mkv | 1 video + 1 audio + English/French/Japanese subrip | With AllowedSubtitleLanguages=[eng,jpn]: two sidecars (.eng.srt, .jpn.srt); French sub dropped by SubtitleLanguageFixer; container stripped of all subs. |
| fixture-mixed-text-and-pgs.mkv | 1 video + English subrip + English PGS | Only subrip extracted to .eng.srt; PGS stays in container. |
| fixture-preexisting-sidecar.mkv | Same as single, with a .eng.srt already on disk | No extraction (sidecar exists); container UNCHANGED (English sub stays inside — do not orphan it). |

## Regenerating

```powershell
.\regenerate.ps1 -Source ..\..\some-30s.mkv -SrtEng eng.srt -SrtFra fra.srt -SrtJpn jpn.srt
```

## E2E procedure (per feedback_mediadash_real_world_tests)

1. Deploy the current build to `%LOCALAPPDATA%\jellyfin\plugins\MediaDash_1.0.8.0.0\` and restart Jellyfin at localhost:8099.
2. Point a Jellyfin library at `tools/subtitle-extract-test/fixtures/`.
3. In MediaDash settings: enable Subtitle Extract (Auto-fix), set AllowedSubtitleLanguages appropriately per fixture. Also enable SubtitleLanguageFixer for the multi-lang test.
4. Run a MediaDash scan; confirm the expected fixtures are flagged (`SubtitleExtract` issue count matches).
5. Run a MediaDash fix.
6. For each fixture, verify:
   - Expected sidecar files exist with correct Jellyfin naming.
   - Container has expected residual streams (use `ffprobe` to inspect).
   - Jellyfin's Subtitles menu shows the new external subs after a metadata refresh.
   - Web player can select and play each sub track.
7. For fixture-preexisting-sidecar.mkv specifically: confirm the container is UNCHANGED (embedded sub still present) — this is the sidecar-exists safety check.
8. Test combined-pass cases: with all subtitle-related fixers enabled and a fixture that has both extract-eligible subs AND language-disallowed subs, confirm ONE ffmpeg command in the plugin logs (grep for `Running ffmpeg`), not two.
```

- [ ] **Step 3: Run the E2E procedure locally**

Follow the README steps end-to-end against localhost:8099. Iterate on the code if any fixture fails to produce the expected outcome; do not consider the feature done until every row of the table above passes.

- [ ] **Step 4: Commit**

```
git add tools/subtitle-extract-test
git commit -m "test(subtitle-extract): real-world E2E fixture harness"
```

---

## Task 10: CHANGELOG + version coordination

**Files:**
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Add the 1.0.8.0 section if it doesn't exist**

Open `CHANGELOG.md`. If there's no `## 1.0.8.0 (unreleased)` heading above the current unreleased section, add one:

```markdown
## 1.0.8.0 (unreleased)

- (bullets accumulate here as 1.0.8.0 features land)

---

## 1.0.7.5 (unreleased)
...
```

- [ ] **Step 2: Add the subtitle-extract bullet**

Under the 1.0.8.0 heading, add:

```markdown
- added Extract embedded subtitles setting (Subtitles → Extract embedded subtitles): saves text subtitles as `.srt` / `.ass` sidecar files next to the video and removes them from the container. Only text subtitles in your allowed languages are extracted; picture-based subtitles (Blu-ray PGS, DVD VobSub) stay in the container. Extraction runs in one ffmpeg pass alongside any other track/transcode fix queued for the same file. Off by default.
```

- [ ] **Step 3: Do NOT bump manifest.json or .csproj versions**

Version cut is a bundle-level decision. `manifest.json` stays at whatever the last released version was. The release script `tools/release.ps1 -Version 1.0.8.0 -Changelog "..."` handles the bump when the bundle owner cuts.

- [ ] **Step 4: Commit**

```
git add CHANGELOG.md
git commit -m "docs: changelog for subtitle-extract (1.0.8.0)"
```

---

## Self-review checklist (author, before handoff)

- [ ] Every spec §4 subsection has at least one task implementing it.
  - §4.1 New components → Tasks 1, 2, 3, 4
  - §4.2 Qualifying streams → Task 2 (helper) + Task 3 (scanner uses helper)
  - §4.3 Sidecar naming → Task 2 helper unit tests
  - §4.4 Duplicate handling → Task 2 (helper) + Task 4 (fixer test) + Task 5 (combined test) + Task 9 (E2E)
  - §4.5 Dispatch matrix → Tasks 4, 5, 6, 7
  - §4.6 Interaction rules → Task 5 (rule 1 + 3), Task 7 (rule 2)
  - §4.7 Standalone flow → Task 4
  - §4.8 Combined-pass flow → Task 5
  - §4.9 Transcode-companion flow → Task 7
  - §4.10 Settings card → Task 8
- [ ] Method + type names consistent across tasks (`SubtitleExtractArgs.Plan`, `SubtitleExtractArgs.BuildFfmpegArgs`, `SubtitleExtractArgs.QualifyingStreams`, `SubtitleExtractPlanItem`, `FixCombinedAsync(issue, subLanguagePartner, extractCompanion, progress, ct)`, `FixAsync(issue, extractCompanion, progress, ct)`).
- [ ] No placeholders (TBD/TODO/similar).
- [ ] Real-world procedure covers the sidecar-exists rule (Task 9 explicitly tests fixture 4).
- [ ] Changelog bullet lands with the code (Task 10 last, per `feedback_changelog_on_completion`).
