using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Flags embedded subtitle tracks and external subtitle files in unwanted languages.
/// </summary>
public sealed class SubtitleLanguageScanner : ProbingScannerBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleLanguageScanner"/> class.
    /// </summary>
    /// <param name="ffprobe">The probe service.</param>
    /// <param name="logger">The logger.</param>
    public SubtitleLanguageScanner(FfprobeService ffprobe, ILogger<SubtitleLanguageScanner> logger)
        : base(ffprobe, logger)
    {
    }

    /// <inheritdoc />
    public override IssueType Type => IssueType.SubtitleLanguage;

    /// <inheritdoc />
    protected override bool IsConfigured() => Config.AllowedSubtitleLanguages.Length > 0;

    /// <inheritdoc />
    protected override Task<Issue?> EvaluateAsync(BaseItem item, string path, FfprobeData? probe, CancellationToken cancellationToken)
    {
        var allowed = Config.AllowedSubtitleLanguages;

        var embedded = probe?.Streams?
            .Where(s => string.Equals(s.CodecType, "subtitle", StringComparison.OrdinalIgnoreCase)
                && !LanguageHelper.IsAllowed(s.Language, allowed))
            .ToList() ?? [];

        // External subtitle files are indexed by Jellyfin with language parsed from the filename.
        var external = string.Equals(item.Path, path, StringComparison.Ordinal)
            ? item.GetMediaStreams()
                .Where(s => s.Type == MediaStreamType.Subtitle && s.IsExternal && !string.IsNullOrEmpty(s.Path)
                    && !LanguageHelper.IsAllowed(s.Language, allowed))
                .Select(s => new { s.Path, Language = LanguageHelper.Normalize(s.Language) })
                .ToList()
            : [];

        if (embedded.Count == 0 && external.Count == 0)
        {
            return Task.FromResult<Issue?>(null);
        }

        var languages = embedded.Select(t => LanguageHelper.Normalize(t.Language))
            .Concat(external.Select(e => e.Language))
            .Distinct()
            .ToList();

        var issue = new Issue
        {
            DetailsJson = JsonSerializer.Serialize(new
            {
                removeIndexes = embedded.Select(t => t.Index).ToArray(),
                externalFiles = external.Select(e => e.Path).ToArray(),
                languages
            }),
            SuggestedFix = external.Count > 0
                ? $"Remove subtitles in {string.Join(", ", languages)} ({embedded.Count} embedded, {external.Count} separate file(s))."
                : $"Remove {embedded.Count} embedded subtitle track(s) in {string.Join(", ", languages)}.",
            SizeSavings = 0
        };
        return Task.FromResult<Issue?>(issue);
    }
}
