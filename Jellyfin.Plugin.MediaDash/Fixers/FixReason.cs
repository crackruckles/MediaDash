namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Typed cause code accompanying a <see cref="FixOutcome"/>. Together they replace
/// the string-substring matching that <c>FixTask.IsStaleFailure</c>,
/// <c>IsSubtitleProviderQuotaExhausted</c> and <c>BucketReason</c> currently do
/// against <see cref="FixResult.Message"/>. The mapping from (outcome, reason)
/// to queue behaviour is centralised in <see cref="FixResultPolicy"/>.
/// </summary>
/// <remarks>
/// Enum values are internal-only. They are NEVER persisted to the SQLite database
/// or the plugin HTTP JSON contract without an explicit persisted-enum migration —
/// see the master plan's "Preserve ... persisted enum numbers" constraint. Adding a
/// new reason at the end of this enum is safe; renumbering or reordering is not.
/// <see cref="None"/> is the default zero value used while producers are migrated;
/// treat it as "unclassified" — never as a successful outcome.
/// </remarks>
internal enum FixReason
{
    /// <summary>Default zero — producer has not classified. Never impersonate a terminal-success outcome.</summary>
    None = 0,

    /// <summary>Classified but the fixer explicitly could not narrow it further (e.g. generic <c>IOException</c>).</summary>
    Unknown,

    /// <summary>The file or containing folder was missing at fix time (rename/rebuild/removal between scan and fix).</summary>
    SourceMissing,

    /// <summary>The file's identity changed between scan and fix (probe reports a healed file, or the scanner's precondition is no longer true).</summary>
    SourceChanged,

    /// <summary>The scanner condition is no longer present — the file plays fine now / nothing to remove any more.</summary>
    AlreadyHealthy,

    /// <summary>The DuplicateFixer / OrphanCleanupFixer redundant-remove refusal ("nothing to remove any more").</summary>
    NothingToRemove,

    /// <summary>Rename/move target already occupied — needs manual resolution.</summary>
    DestinationCollision,

    /// <summary>Path is not inside any configured Jellyfin library (LibraryGuard refusal).</summary>
    OutsideLibrary,

    /// <summary>The issue's type is set to Off in Settings -> What to fix.</summary>
    TypeDisabled,

    /// <summary>The issue's owning library is disabled or has been removed since scan.</summary>
    LibraryDisabled,

    /// <summary>User consent changed between scan and fix (Never-modify / dismissal). Reserved for future consent gates.</summary>
    ConsentChanged,

    /// <summary>File is protected from modification (reserved for future Never-modify feature).</summary>
    Protected,

    /// <summary>Probe returned Indeterminate — MediaDash refuses destructive action on unknown files.</summary>
    ProbeUnavailable,

    /// <summary>File is DRM-protected (Playability fixer refusal).</summary>
    DrmProtected,

    /// <summary>File format not recognised or not in the fixer's supported role set (SubtitleFontFixer .ass check, TrickplayOptimizeFixer folder-shape check).</summary>
    UnsupportedFormat,

    /// <summary>Target container refuses source codec under stream-copy (e.g. TRUEHD in MP4).</summary>
    IncompatibleContainer,

    /// <summary>TrackFixer combined-pass invariants failed (wrong pairing count, or paths don't match).</summary>
    InvalidCombinedRequest,

    /// <summary>Folder cover already present — EmbeddedCoverArtFixer has nothing to extract.</summary>
    ExistingCover,

    /// <summary>Pre-flight or mid-op free-space guard tripped.</summary>
    InsufficientSpace,

    /// <summary>File was held open by another process (Jellyfin trickplay / chapter thumbs / active playback).</summary>
    FileLocked,

    /// <summary>External provider is over its quota (OpenSubtitles download limit reached / download quota).</summary>
    ProviderQuota,

    /// <summary>Post-encode verification failed — original preserved, output discarded.</summary>
    VerificationFailed,

    /// <summary>Fixer subprocess (ffmpeg, external tool) failed for reasons that aren't disk / permission / verify.</summary>
    ProcessFailed,

    /// <summary>Permission denied on the target file/folder.</summary>
    AccessDenied,

    /// <summary>Operation cancelled (user pressed Stop / task cancellation triggered).</summary>
    Cancelled,

    /// <summary>Swap failed mid-way and the SwapRecoveryHelper preserved the original at a recovery path. Requires operator attention.</summary>
    RecoveryRequired,

    /// <summary>02-B: this <c>(path, type)</c> was previously restored so auto-queue must skip it; a manual approve bypasses.</summary>
    RestoredPath,

    /// <summary>02-B: the type is in <see cref="Configuration.FixMode.ManualApprove"/> and no per-issue approval exists — a nonempty language allow-list is NOT approval.</summary>
    ManualApprovalRequired,

    /// <summary>Quality re-encode produced an output that missed the configured minimum savings threshold; original kept. Terminal so the queue doesn't re-attempt an identical encode forever.</summary>
    InsufficientSavings,

    /// <summary>Format-compat: parent CollectionType outside converter's AllowedLibraryTypes.</summary>
    LibraryTypeMismatch,

    /// <summary>Format-compat: CollectionType could not be resolved for the item.</summary>
    LibraryTypeUnknown,

    /// <summary>Format-compat: mixed-content library, no per-row approve on record.</summary>
    MixedLibraryUserReviewOnly,

    /// <summary>Format-compat: converter toolchain missing and lazy-download failed.</summary>
    ConverterUnavailable,

    /// <summary>Format-compat: converter ran but its output failed post-conversion probe.</summary>
    ConverterFailed,

    /// <summary>Format-compat: matrix row has no target extension for this file (DRM advisory).</summary>
    SourceExtensionNotConvertible,

    /// <summary>Format-compat: a file already exists at the converter's destination path and
    /// overwriting it would silently replace user content (external review #1 — folder-merge used
    /// to pass <c>overwrite: true</c> to <see cref="System.IO.File.Move(string,string,bool)"/>).</summary>
    OutputConflict,

    /// <summary>Repair exhausted without a verified replacement; preserved source needs explicit delete approval.</summary>
    RepairFailed,

    /// <summary>A recorded move needs review; its claimed paths must not be replayed or cleaned automatically.</summary>
    MoveNeedsReview,
}
