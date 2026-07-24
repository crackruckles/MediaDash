using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Flags videos that have no subtitle track — embedded or external — in any of the wanted languages.
/// The fixer downloads one via Jellyfin's configured subtitle providers.
/// </summary>
public sealed class MissingSubtitleScanner : ProbingScannerBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MissingSubtitleScanner"/> class.
    /// </summary>
    /// <param name="ffprobe">The probe service.</param>
    /// <param name="logger">The logger.</param>
    public MissingSubtitleScanner(FfprobeService ffprobe, ILogger<MissingSubtitleScanner> logger)
        : base(ffprobe, logger)
    {
    }

    /// <inheritdoc />
    public override IssueType Type => IssueType.MissingSubtitles;

    /// <inheritdoc />
    protected override bool IsConfigured()
        => Config.AllowedSubtitleLanguages.Length > 0 && Config.MissingSubtitlesFixMode != FixMode.Off;

    /// <inheritdoc />
    protected override Task<Issue?> EvaluateAsync(BaseItem item, string path, FfprobeData? probe, CancellationToken cancellationToken)
    {
        // Only Video items can carry subtitle streams / receive downloaded subs.
        if (item is not Video)
        {
            return Task.FromResult<Issue?>(null);
        }

        var wanted = Config.AllowedSubtitleLanguages;

        // Embedded subtitle streams — trust the ffprobe result, same source as SubtitleLanguageScanner.
        var haveEmbedded = probe?.Streams?.Any(s =>
            string.Equals(s.CodecType, "subtitle", StringComparison.OrdinalIgnoreCase)
            && HasAnyMatch(s.Language, wanted)) ?? false;

        // External sidecar files (.en.srt, .fra.ass, etc.) — Jellyfin indexes these with a parsed language.
        var haveExternal = string.Equals(item.Path, path, StringComparison.Ordinal)
            && item.GetMediaStreams().Any(s =>
                s.Type == MediaStreamType.Subtitle
                && s.IsExternal
                && HasAnyMatch(s.Language, wanted));

        if (haveEmbedded || haveExternal)
        {
            return Task.FromResult<Issue?>(null);
        }

        var missingLanguages = wanted.Select(LanguageHelper.Normalize).Distinct().ToArray();
        var issue = new Issue
        {
            DetailsJson = JsonSerializer.Serialize(new { missingLanguages }),
            SuggestedFix = $"Download subtitles in {string.Join(", ", missingLanguages)} from Jellyfin's configured providers.",
            SizeSavings = 0
        };
        return Task.FromResult<Issue?>(issue);
    }

    private static bool HasAnyMatch(string? language, IReadOnlyList<string> wanted)
    {
        // IsAllowed treats "und" as always allowed (it's the safe default for the removal scanner). Here we're
        // deciding whether a wanted language is *present*, so "und" doesn't satisfy the check — a track with
        // no language tag can't be trusted to be the language the user wants.
        var normalized = LanguageHelper.Normalize(language);
        if (normalized == "und")
        {
            return false;
        }

        foreach (var entry in wanted)
        {
            if (string.Equals(LanguageHelper.Normalize(entry), normalized, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
