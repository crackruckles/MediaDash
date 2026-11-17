using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using Jellyfin.Plugin.MediaDash.Probing;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Fixer for <see cref="IssueType.FormatCompatibility"/> issues. Dispatches to the
/// registered <see cref="IFormatConverter"/> for the row's converter key, then hands the
/// staged output to <see cref="VerifiedReplacementService"/> for the audited
/// swap-and-recycle protocol.
/// </summary>
public sealed class FormatCompatibilityFixer : IFixer
{
    private const string InflightTail = ".mediadash.tmp";

    private readonly IFormatConverterRegistry _converters;
    private readonly ILibraryCollectionResolver _resolver;

    // Precheck tests may omit replacement; the live path requires DI wiring below.
    private readonly VerifiedReplacementService? _replacement;

    // ponytail: nullable guard/protection — null = skip check in tests; production DI always supplies them.
    private readonly LibraryGuard? _guard;
    private readonly ProtectionService? _protection;

    private readonly IPluginConfigurationProvider _configProvider;

    // ponytail: delegate instead of full ILibraryManager — tests supply a lambda returning
    // a stub item; DI wires `id => libraryManager.GetItemById(id)`. The success path
    // (DeleteItem + monitor) is never reached by the 4 unit tests, so those two remain optional.
    private readonly Func<Guid, BaseItem?> _getItemById;
    private readonly ILibraryManager? _libraryManager;
    private readonly ILibraryMonitor? _libraryMonitor;

    // External review #7: used to probe AV outputs (JPG/MP3/FLAC/MKV/M4B) via ffprobe for
    // stream and duration sanity before VRS commits. Nullable — tests that stay in the refusal
    // path never reach the AV verification branch.
    private readonly FfprobeService? _ffprobe;

    private readonly ILogger<FormatCompatibilityFixer> _logger;
    private readonly Func<string, int, string?> _spaceCheck;

    /// <summary>
    /// Initializes a new instance of the <see cref="FormatCompatibilityFixer"/> class for
    /// production DI. All parameters are mandatory.
    /// </summary>
    /// <param name="converters">Converter registry.</param>
    /// <param name="resolver">Library collection-type resolver.</param>
    /// <param name="replacement">Verified replacement service.</param>
    /// <param name="guard">Library guard.</param>
    /// <param name="protection">Protection service.</param>
    /// <param name="configProvider">Config snapshot provider.</param>
    /// <param name="libraryManager">Jellyfin library manager for post-fix item purge.</param>
    /// <param name="libraryMonitor">Jellyfin library monitor for FS-change notifications.</param>
    /// <param name="ffprobe">ffprobe service for AV output integrity checks (external review #7).</param>
    /// <param name="logger">The logger.</param>
    public FormatCompatibilityFixer(
        IFormatConverterRegistry converters,
        ILibraryCollectionResolver resolver,
        VerifiedReplacementService replacement,
        LibraryGuard guard,
        ProtectionService protection,
        IPluginConfigurationProvider configProvider,
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        FfprobeService ffprobe,
        ILogger<FormatCompatibilityFixer> logger)
        : this(
            converters,
            resolver,
            replacement,
            guard,
            protection,
            configProvider,
            id => libraryManager.GetItemById(id),
            libraryManager,
            libraryMonitor,
            ffprobe,
            logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FormatCompatibilityFixer"/> class with
    /// injectable seams for unit tests. Guards and VRS may be null; the unit tests exit via
    /// refusal prechecks before any of those are invoked.
    /// </summary>
    /// <param name="converters">Converter registry.</param>
    /// <param name="resolver">Library collection-type resolver.</param>
    /// <param name="replacement">Verified replacement service. Null is accepted — throws if reached.</param>
    /// <param name="guard">Library guard. Null = skip IsInsideLibrary check.</param>
    /// <param name="protection">Protection service. Null = skip IsFixerBlocked check.</param>
    /// <param name="configProvider">Config snapshot provider.</param>
    /// <param name="getItemById">Item-lookup delegate. Return null to treat item as not found.</param>
    /// <param name="libraryManager">Optional — used only on the success path for DeleteItem.</param>
    /// <param name="libraryMonitor">Optional — used only on the success path for FS change notification.</param>
    /// <param name="ffprobe">Optional — AV output verification seam; tests pass null.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="spaceCheck">Optional free-space preflight for deterministic refusal tests.</param>
    internal FormatCompatibilityFixer(
        IFormatConverterRegistry converters,
        ILibraryCollectionResolver resolver,
        VerifiedReplacementService? replacement,
        LibraryGuard? guard,
        ProtectionService? protection,
        IPluginConfigurationProvider configProvider,
        Func<Guid, BaseItem?> getItemById,
        ILibraryManager? libraryManager,
        ILibraryMonitor? libraryMonitor,
        FfprobeService? ffprobe,
        ILogger<FormatCompatibilityFixer> logger,
        Func<string, int, string?>? spaceCheck = null)
    {
        _converters = converters;
        _resolver = resolver;
        _replacement = replacement;
        _guard = guard;
        _protection = protection;
        _configProvider = configProvider;
        _getItemById = getItemById;
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _ffprobe = ffprobe;
        _logger = logger;
        _spaceCheck = spaceCheck ?? FreeSpaceGuard.DescribeShortfall;
    }

    // Overload keeping the pre-review ctor shape for tests that haven't been updated yet.
    internal FormatCompatibilityFixer(
        IFormatConverterRegistry converters,
        ILibraryCollectionResolver resolver,
        VerifiedReplacementService? replacement,
        LibraryGuard? guard,
        ProtectionService? protection,
        IPluginConfigurationProvider configProvider,
        Func<Guid, BaseItem?> getItemById,
        ILibraryManager? libraryManager,
        ILibraryMonitor? libraryMonitor,
        ILogger<FormatCompatibilityFixer> logger,
        Func<string, int, string?>? spaceCheck = null)
        : this(converters, resolver, replacement, guard, protection, configProvider, getItemById, libraryManager, libraryMonitor, ffprobe: null, logger, spaceCheck)
    {
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.FormatCompatibility;

    /// <inheritdoc />
    public async Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = _configProvider.GetSnapshot();

        // 2. Details must parse (done first so we know the kind before the existence check).
        var details = ParseDetails(issue.DetailsJson);
        if (details is null || string.IsNullOrEmpty(details.ConverterKey) || string.IsNullOrEmpty(details.TargetExt))
        {
            return FixResult.Refused(FixReason.SourceExtensionNotConvertible, "Issue details are missing the conversion target.");
        }

        // 1. Source must exist — for folder-merge, the path is a directory.
        var isFolderMerge = string.Equals(details.Kind, "audiobook-folder-merge", StringComparison.OrdinalIgnoreCase);
        if (isFolderMerge ? !Directory.Exists(issue.Path) : !File.Exists(issue.Path))
        {
            return FixResult.Fail(FixReason.SourceMissing, "The source file is missing; re-scan to refresh the issue list.");
        }

        // 3. Converter must be registered.
        var converter = _converters.Get(details.ConverterKey);
        if (converter is null)
        {
            return FixResult.Refused(FixReason.ConverterUnavailable, "Converter '" + details.ConverterKey + "' is not registered.");
        }

        // 4. Per-kind toggle.
        if (!IsKindEnabled(config, converter.Kind))
        {
            return FixResult.Refused(FixReason.TypeDisabled, "The per-kind converter toggle is disabled in Settings.");
        }

        // 5. Resolve library type via item lookup + resolver.
        // External review #4: unindexed files used to inherit details.LibraryKind, which was
        // frozen at scan time. A user who reclassified a Books library to mixed after the scan
        // but before approving the fix would bypass the mixed-library approval gate below.
        // Always re-resolve against the CURRENT library layout via ResolveByPath — the frozen
        // libraryKind is still consulted only as a tiebreaker for files whose path no longer
        // sits under any virtual folder (e.g. library root was renamed; the file physically
        // moved during the fix window). The scanner itself still tags mixedLibrary=true for
        // files that were genuinely mixed at detection; that stays as a hard override because
        // approvedInMixedLibrary flipping to true came from that scan context.
        // ponytail: Jellyfin's LibraryManager.GetItemById throws ArgumentException on Guid.Empty
        // rather than returning null — guard here so folder-merge issues (ItemId always empty) don't crash.
        var item = issue.ItemId == Guid.Empty ? null : _getItemById(issue.ItemId);
        CollectionType? libraryType;
        if (item is not null)
        {
            libraryType = _resolver.Resolve(item);
        }
        else if (details.MixedLibrary)
        {
            libraryType = null; // genuinely mixed at scan time; honour the approvedInMixedLibrary gate.
        }
        else
        {
            // Re-resolve against the current virtual-folder layout. We deliberately do NOT fall
            // back to details.LibraryKind here — ResolveByPath returning null means either "file
            // is outside every library" OR "owning library is now mixed." Both cases must flow
            // through the mixed-library approval gate below; silently preferring the stale value
            // is exactly the bypass the external review flagged.
            libraryType = _resolver.ResolveByPath(issue.Path);
        }

        var isMixed = libraryType is null;

        // 6. Mixed-library gate: require per-row approval.
        if (isMixed && !details.ApprovedInMixedLibrary)
        {
            return FixResult.Refused(
                FixReason.MixedLibraryUserReviewOnly,
                "This file is in a mixed-content library. Approve the specific row on the Issues tab to convert it.");
        }

        // 7. Library-type mismatch: converter not valid for this library kind.
        if (!isMixed && !converter.AllowedLibraryTypes.Contains(libraryType))
        {
            return FixResult.Refused(
                FixReason.LibraryTypeMismatch,
                "This converter does not run inside " + libraryType + " libraries.");
        }

        // 8. Library guard.
        if (_guard is not null && !_guard.IsInsideLibrary(issue.Path))
        {
            return FixResult.Refused(FixReason.OutsideLibrary, "File is outside every configured library folder.");
        }

        // 9. Protection check.
        if (_protection is not null && _protection.IsFixerBlocked(issue.Path, IssueType.FormatCompatibility))
        {
            return FixResult.Refused(FixReason.Protected, "This file is on the never-modify list.");
        }

        // 10. Ensure toolchain.
        var readiness = await converter.EnsureToolchainAsync(cancellationToken).ConfigureAwait(false);
        if (readiness != ConverterAvailability.Ready)
        {
            return FixResult.Refused(
                FixReason.ConverterUnavailable,
                "Converter toolchain is not ready. Install Calibre or wait for the download to complete.");
        }

        // P2-7 / P4-2: user override for converters that can actually honour the target choice.
        // ponytail: ImagePreferredTarget requires plumbing into ImageConverter args; ImageConverter
        // always emits JPEG bytes regardless of stagedTargetPath extension — skip for Phase 4.
        // ponytail: MusicPreferredTarget requires plumbing into MusicConverter.ArgsForSource;
        // ArgsForSource dispatches on source extension only (WMA→MP3, rest→FLAC) — skip for Phase 4.
        // Only EbookConverter (Calibre ebook-convert respects output extension) is wired here.
        // Do NOT mutate details.
        var targetExt = details.TargetExt;
        if (converter.Kind == MediaKind.Ebook && !string.IsNullOrWhiteSpace(config.EbookPreferredTarget))
        {
            var pref = config.EbookPreferredTarget.Trim().ToLowerInvariant();
            if (pref == "epub" || pref == "pdf")
            {
                targetExt = "." + pref;
            }
        }

        // 11. Dry-run — return preview without touching files.
        if (config.DryRun)
        {
            return FixResult.DryRun("convert " + Path.GetFileName(issue.Path) + " to " + targetExt, 0);
        }

        var shortfall = _spaceCheck(issue.Path, 2);
        if (shortfall is not null)
        {
            return FixResult.Refused(FixReason.InsufficientSpace, shortfall);
        }

        if (!isFolderMerge && config.FormatCompatibilityDisposal == DisposalMethod.RecycleBin
            && _replacement is not null && !_replacement.CanRecycle(issue.Path))
        {
            return FixResult.Refused(FixReason.AccessDenied, "No safe recycle location is available for the original.");
        }

        // 12. Conversion.
        var destinationPath = isFolderMerge
            ? Path.TrimEndingDirectorySeparator(issue.Path) + targetExt
            : Path.ChangeExtension(issue.Path, targetExt);

        // Folder-merge: converter writes a new M4B next to the source folder; the source folder
        // is left untouched (VRS cannot handle a directory source). No recycle, no swap protocol.
        // ponytail: VRS bypass for folder-merge — VRS.GetLength on a directory throws or returns 0;
        // no swap needed because we're creating a new sibling file, not replacing any existing one.
        if (isFolderMerge)
        {
            // External review #1: refuse destination collisions BEFORE spending minutes to convert.
            // The old `File.Move(..., overwrite: true)` silently replaced an existing sibling .m4b
            // without recycling it. The scanner's `*.m4b` short-circuit at ClassifyFolderAsAudiobookMerge
            // caught the common case but raced any file placed between scan and fix.
            if (File.Exists(destinationPath))
            {
                return FixResult.Refused(
                    FixReason.OutputConflict,
                    "A file already exists at " + destinationPath + ". Move or delete it and re-scan — MediaDash will not overwrite an existing file during a folder merge.");
            }

            // External review #3: shared commit-time policy check — folder-merges used to proceed
            // straight to the rename, so a dry-run flip, converter toggle disable, or protection
            // rule added during the long audiobook concat silently committed anyway. Routes
            // through the same static helper the file-conversion path uses so the two never drift.
            var converterKindForFolder = converter.Kind;
            PolicyDecision CheckCurrentPolicyFolder() => EvaluateCommitPolicy(
                _configProvider.GetSnapshot(),
                converterKindForFolder,
                _protection,
                issue.Path,
                issue.Type);

            var stagedPath = issue.Path + InflightTail + targetExt;
            ConversionResult folderConversion;
            try
            {
                folderConversion = await converter.ConvertAsync(issue.Path, stagedPath, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SafeDelete(stagedPath);
                throw;
            }
            catch (Exception ex)
            {
                SafeDelete(stagedPath);
                return FixResult.Fail(FixReason.ConverterFailed, "Converter threw unexpectedly.", ex.Message);
            }

            if (!folderConversion.Success || folderConversion.StagedPath is null || !File.Exists(folderConversion.StagedPath))
            {
                SafeDelete(stagedPath);
                return FixResult.Fail(FixReason.ConverterFailed, folderConversion.Reason, folderConversion.TechnicalDetail);
            }

            // Verify then rename staged → destination directly.
            var verifyError = await VerifyOutputAsync(folderConversion.StagedPath, cancellationToken).ConfigureAwait(false);
            if (verifyError is not null)
            {
                SafeDelete(stagedPath);
                return FixResult.Fail(FixReason.VerificationFailed, verifyError);
            }

            // Live-policy recheck AFTER the long conversion but BEFORE the irreversible rename.
            // Same pattern as the file-conversion commit path; a user who flips to dry-run or
            // adds a protection rule mid-encode now aborts cleanly instead of silently committing.
            var folderPolicy = CheckCurrentPolicyFolder();
            if (!folderPolicy.Allowed)
            {
                SafeDelete(stagedPath);
                return FixResult.Refused(folderPolicy.Reason, "Policy changed mid-conversion; staged merge discarded.");
            }

            // Second collision check: the destination may have appeared during the long concat.
            if (File.Exists(destinationPath))
            {
                SafeDelete(stagedPath);
                return FixResult.Refused(
                    FixReason.OutputConflict,
                    "A file was placed at " + destinationPath + " during the merge. Staged output discarded.");
            }

            try
            {
                File.Move(folderConversion.StagedPath, destinationPath, overwrite: false);
            }
            catch (IOException ex)
            {
                SafeDelete(stagedPath);
                return FixResult.Fail(FixReason.ProcessFailed, "Could not rename staged M4B to destination.", ex.Message);
            }

            _libraryMonitor?.ReportFileSystemChanged(destinationPath);
            _logger.LogInformation("FormatCompatibilityFixer: folder-merged {Source} → {Dest}", issue.Path, destinationPath);
            return new FixResult
            {
                Success = true,
                Message = "Merged " + Path.GetFileName(issue.Path) + " → " + Path.GetFileName(destinationPath)
            };
        }

        // P1-2: capture source identity BEFORE conversion so a mid-conversion source mutation
        // is detected by VRS's identity recheck and the stale encode is refused.
        var sourceInfoBeforeConvert = new FileInfo(issue.Path);
        var expectedSourceLength = sourceInfoBeforeConvert.Length;
        var expectedSourceMtimeTicks = sourceInfoBeforeConvert.LastWriteTimeUtc.Ticks;

        var stagedFilePath = issue.Path + InflightTail + targetExt;

        // Keep converter.Kind in a local so the P1-1 policy callback below can capture it
        // without capturing the converter object itself (avoids converter keeping alive longer
        // than needed and makes the closure dependency explicit).
        var converterKind = converter.Kind;

        ConversionResult conversion;
        try
        {
            conversion = await converter.ConvertAsync(issue.Path, stagedFilePath, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SafeDelete(stagedFilePath);
            throw;
        }
        catch (Exception ex)
        {
            SafeDelete(stagedFilePath);
            return FixResult.Fail(FixReason.ConverterFailed, "Converter threw unexpectedly.", ex.Message);
        }

        if (!conversion.Success || conversion.StagedPath is null || !File.Exists(conversion.StagedPath))
        {
            SafeDelete(stagedFilePath);
            return FixResult.Fail(FixReason.ConverterFailed, conversion.Reason, conversion.TechnicalDetail);
        }

        // 13. Verified replacement (swap-and-recycle protocol).
        var request = new ReplacementRequest(
            SourcePath: issue.Path,
            OutputPath: conversion.StagedPath,
            DestinationPath: destinationPath,
            ExpectedSourceLength: expectedSourceLength,
            ExpectedSourceMtimeUtcTicks: expectedSourceMtimeTicks,
            RecycleOriginal: config.FormatCompatibilityDisposal == DisposalMethod.RecycleBin);

        var vrs = _replacement ?? throw new InvalidOperationException("FormatCompatibilityFixer: VerifiedReplacementService is required for the live path but was not injected.");

        var stagedForVerify = conversion.StagedPath;

        // P1-1: re-consult live config + protection immediately before the commit so a mid-
        // conversion dry-run flip, fixer disable, per-kind toggle off, or protection addition
        // aborts before any file is touched. Pattern from TranscodeFixer.EvaluateCommitPolicy
        // (external audit P1-F3, 1.0.8.0). Routes through EvaluateCommitPolicy so folder-merge
        // and tests exercise identical logic.
        PolicyDecision CheckCurrentPolicy() => EvaluateCommitPolicy(
            _configProvider.GetSnapshot(),
            converterKind,
            _protection,
            issue.Path,
            issue.Type);

        var replaceResult = await vrs.ReplaceAsync(
            request,
            ct => VerifyOutputAsync(stagedForVerify, sourcePath: issue.Path, ct),
            CheckCurrentPolicy,
            cancellationToken).ConfigureAwait(false);

        if (!replaceResult.Applied)
        {
            // P2-4: only delete the staged output when NEITHER a recycle nor a recovery path was
            // recorded. If VRS moved the staged output to a recovery path, it's an artefact the
            // user may need; deleting stagedPath is either a no-op or wrong in that case.
            if (string.IsNullOrEmpty(replaceResult.RecyclePath) && string.IsNullOrEmpty(replaceResult.RecoveryPath))
            {
                SafeDelete(stagedFilePath);
            }

            var fail = FixResult.Fail(replaceResult.Reason, replaceResult.TechnicalDetail ?? "Replacement failed.");
            fail.RecyclePath = replaceResult.RecyclePath;
            fail.RecoveryPath = replaceResult.RecoveryPath;
            return fail;
        }

        // 14. Post-fix library notifications.
        _libraryMonitor?.ReportFileSystemChanged(destinationPath);
        if (_libraryManager is not null && item is not null && !string.Equals(item.Path, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            _libraryManager.DeleteItem(item, new MediaBrowser.Controller.Library.DeleteOptions
            {
                DeleteFileLocation = false,
                DeleteFromExternalProvider = false
            });
        }

        _logger.LogInformation("FormatCompatibilityFixer: converted {Source} → {Dest}", issue.Path, destinationPath);
        return new FixResult
        {
            Success = true,
            Message = "Converted " + Path.GetFileName(issue.Path) + " → " + Path.GetFileName(destinationPath),
            RecyclePath = replaceResult.RecyclePath
        };
    }

    private static bool IsKindEnabled(PluginConfiguration config, MediaKind kind) => kind switch
    {
        MediaKind.Ebook => config.ConvertEbooks,
        MediaKind.Comic => config.ConvertComics,
        MediaKind.Audiobook => config.ConvertAudiobooks,
        MediaKind.Image => config.ConvertImages,
        MediaKind.Music => config.ConvertMusic,
        MediaKind.Video => config.ConvertVideos,
        _ => false
    };

    /// <summary>
    /// Commit-time policy evaluator shared by the file-conversion and folder-merge paths. Both
    /// paths used to re-implement the gate inline; the test suite then had to mirror the same
    /// logic a third time (external review feedback: "policy tests duplicate production logic
    /// instead of exercising the actual commit callback"). Routing everything through one
    /// static method lets both callers and the test exercise the exact same code.
    /// </summary>
    /// <param name="freshConfig">Latest config snapshot (callers pass <c>_configProvider.GetSnapshot()</c>).</param>
    /// <param name="converterKind">The converter being invoked — gates against the per-kind toggle.</param>
    /// <param name="protection">Protection service; null skips the protection check (test seam).</param>
    /// <param name="path">The source path being touched; used for the protection lookup.</param>
    /// <param name="issueType">The issue type being fixed (<see cref="IssueType.FormatCompatibility"/> in practice).</param>
    /// <returns>Allow or a refusal reason.</returns>
    internal static PolicyDecision EvaluateCommitPolicy(
        PluginConfiguration freshConfig,
        MediaKind converterKind,
        ProtectionService? protection,
        string path,
        IssueType issueType)
    {
        if (freshConfig.DryRun)
        {
            return PolicyDecision.Refuse(FixReason.TypeDisabled);
        }

        var mode = freshConfig.GetFixMode(IssueType.FormatCompatibility);
        if (mode == FixMode.Off)
        {
            return PolicyDecision.Refuse(FixReason.TypeDisabled);
        }

        if (!IsKindEnabled(freshConfig, converterKind))
        {
            return PolicyDecision.Refuse(FixReason.TypeDisabled);
        }

        if (protection is not null && protection.IsFixerBlocked(path, issueType))
        {
            return PolicyDecision.Refuse(FixReason.Protected);
        }

        return PolicyDecision.Allow;
    }

    private static Details? ParseDetails(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var converterKey = root.TryGetProperty("converter", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? string.Empty : string.Empty;
            var targetExt = root.TryGetProperty("targetExt", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? string.Empty : string.Empty;
            var mixed = root.TryGetProperty("mixedLibrary", out var m) && m.ValueKind == JsonValueKind.True;
            var approvedInMixed = root.TryGetProperty("approvedInMixedLibrary", out var a) && a.ValueKind == JsonValueKind.True;
            var libraryKind = root.TryGetProperty("libraryKind", out var lk) && lk.ValueKind == JsonValueKind.String ? lk.GetString() ?? string.Empty : string.Empty;
            var kind = root.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? string.Empty : string.Empty;
            return new Details(converterKey, targetExt, mixed, approvedInMixed, libraryKind, kind);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // P1-3: dispatch to the existing BookProbeService/ComicProbeService structural probes so
    // an unreadable or truncated converted output is refused before the original is disposed.
    // External review #7: extended with ffprobe-based stream/duration checks for AV outputs
    // (JPG/MP3/FLAC/MKV/M4B). VRS verifyOutput signature is Func<CancellationToken, Task<string?>>;
    // stagedPath is captured by the caller's closure, ct is passed by VRS.
    // External audit P2 #6: optional sourcePath lets the verify compare source vs output
    // duration/stream count so a readable-but-truncated conversion is caught before VRS
    // commits. Null preserves the previous "existence + readable" behaviour (folder-merge path
    // where source is a directory, not a single file).
    // internal for direct unit testing (InternalsVisibleTo tests project).
    internal async Task<string?> VerifyOutputAsync(string stagedPath, CancellationToken ct)
        => await VerifyOutputAsync(stagedPath, sourcePath: null, ct).ConfigureAwait(false);

    internal async Task<string?> VerifyOutputAsync(string stagedPath, string? sourcePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!File.Exists(stagedPath))
        {
            return "Converted output is missing after conversion.";
        }

        if (new FileInfo(stagedPath).Length == 0)
        {
            return "Converted output is empty.";
        }

        var ext = Path.GetExtension(stagedPath).ToLowerInvariant();
        switch (ext)
        {
            case ".epub":
            case ".pdf":
            case ".mobi":
            case ".azw":
            case ".azw3":
            case ".djvu":
            {
                var probe = BookProbeService.Probe(stagedPath);
                if (!probe.Ok)
                {
                    return "Converted output failed integrity probe: " + probe.Reason;
                }

                break;
            }

            case ".cbz":
            case ".cbr":
            case ".cb7":
            {
                var probe = ComicProbeService.Probe(stagedPath);
                if (!probe.Ok)
                {
                    return "Converted output failed integrity probe: " + probe.Reason;
                }

                break;
            }

            case ".mp3":
            case ".flac":
            case ".m4b":
            case ".mkv":
            {
                var mediaError = await VerifyAvOutputAsync(stagedPath, sourcePath, requireAudio: true, requireVideo: ext == ".mkv", ct).ConfigureAwait(false);
                if (mediaError is not null)
                {
                    return mediaError;
                }

                break;
            }

            case ".jpg":
            case ".jpeg":
            {
                // Attached-picture / image conversions produce JPEG. ffprobe parses them as a
                // single-frame "video" stream — probe confirms the file is readable and the
                // image stream is present. Duration is always 0; don't gate on it.
                var mediaError = await VerifyAvOutputAsync(stagedPath, sourcePath: null, requireAudio: false, requireVideo: true, ct).ConfigureAwait(false);
                if (mediaError is not null)
                {
                    return mediaError;
                }

                break;
            }

            default:
                break;
        }

        return null;
    }

    private async Task<string?> VerifyAvOutputAsync(string stagedPath, string? sourcePath, bool requireAudio, bool requireVideo, CancellationToken ct)
    {
        // No ffprobe injected (test / seam path) — fall through to the length-only check already
        // enforced above. Production DI always supplies one.
        if (_ffprobe is null)
        {
            return null;
        }

        var probe = await _ffprobe.ProbeAsync(stagedPath, ct).ConfigureAwait(false);
        if (probe is null || probe.Error is not null || probe.Streams is null || probe.Streams.Count == 0)
        {
            return "Converted output could not be read back: " + (probe?.Error?.Message ?? "ffprobe returned no data");
        }

        var hasAudio = probe.Streams.Any(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase));
        var hasVideo = probe.Streams.Any(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase));

        if (requireAudio && !hasAudio)
        {
            return "Converted output has no audio stream.";
        }

        if (requireVideo && !hasVideo)
        {
            return "Converted output has no video stream.";
        }

        double outputSeconds = 0;
        var hasOutputDuration = requireAudio
            && double.TryParse(probe.Format?.Duration, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out outputSeconds);

        // Duration sanity — JPEG stills carry no duration, so skip the duration check when audio
        // is not required (image-converter path). For audio outputs, zero / negative duration
        // means the muxer produced an empty file that ffprobe can still open.
        if (requireAudio && (!hasOutputDuration || outputSeconds <= 0.1))
        {
            return "Converted output has zero duration.";
        }

        // External audit P2 #6: compare against source duration + audio-stream count when a
        // sourcePath is supplied. Catches a readable-but-truncated conversion that would
        // otherwise be accepted and the source permanently deleted. 5% tolerance absorbs
        // legitimate re-encode trimming (sample-rate rounding, lossy framing); anything
        // beyond that is user data loss. Audio-stream count must match exactly — a sudden
        // drop from 2 to 1 is track loss, not re-encode slack.
        if (requireAudio && !string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
        {
            var sourceProbe = await _ffprobe.ProbeAsync(sourcePath, ct).ConfigureAwait(false);
            if (sourceProbe?.Format?.Duration is not null
                && double.TryParse(sourceProbe.Format.Duration, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sourceSeconds)
                && sourceSeconds > 1.0
                && hasOutputDuration
                && outputSeconds < sourceSeconds * 0.95)
            {
                return string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Converted output is only {0:F1}s of the source's {1:F1}s — missing more than 5% of the content.",
                    outputSeconds,
                    sourceSeconds);
            }

            var sourceAudio = sourceProbe?.Streams?.Count(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase)) ?? 0;
            var outputAudio = probe.Streams.Count(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase));
            if (sourceAudio > 0 && outputAudio < sourceAudio)
            {
                return string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Converted output has only {0} audio stream(s); source had {1}.",
                    outputAudio,
                    sourceAudio);
            }
        }

        return null;
    }

    private static void SafeDelete(string path)
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
        }
    }

    private sealed record Details(string ConverterKey, string TargetExt, bool MixedLibrary, bool ApprovedInMixedLibrary, string LibraryKind = "", string Kind = "");
}
