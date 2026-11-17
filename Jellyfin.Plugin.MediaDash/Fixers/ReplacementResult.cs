namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Outcome of a <see cref="VerifiedReplacementService.ReplaceAsync"/> call. The service
/// only signals what actually executed on disk — friendly messaging remains the caller's
/// responsibility so media-specific wording (verify layer, encoder decisions, rung labels)
/// stays with the caller.
/// </summary>
/// <param name="Applied">True iff the source was disposed AND the verified output landed at
/// <see cref="ReplacementRequest.DestinationPath"/>. False in every other case, including
/// the "output preserved at recovery path but final rename failed" post-dispose state — in
/// that case <see cref="RecoveryPath"/> is non-null and <see cref="Reason"/> is
/// <see cref="FixReason.RecoveryRequired"/>.</param>
/// <param name="Reason">Typed classification of the outcome. <see cref="FixReason.None"/>
/// only when <see cref="Applied"/> is true.</param>
/// <param name="RecyclePath">Recycle-bin path of the disposed source, when
/// <see cref="ReplacementRequest.RecycleOriginal"/> was true AND disposal succeeded. Null
/// otherwise. Non-null RecyclePath + false Applied signals partial commit — recycle
/// succeeded but final rename didn't.</param>
/// <param name="RecoveryPath">Path where the verified output survives when the commit
/// couldn't complete. Non-null if disposal or the final rename failed, or
/// if cancellation occurred between staging and disposal.</param>
/// <param name="TechnicalDetail">Underlying exception message or verification failure text.
/// Optional. Rendered behind the UI's "Technical detail" disclosure — see FixResult.</param>
internal sealed record ReplacementResult(
    bool Applied,
    FixReason Reason,
    string? RecyclePath,
    string? RecoveryPath,
    string? TechnicalDetail)
{
    internal string DescribeRecovery(string originalPath)
        => $"Replacement could not finish. Keep the verified output at '{RecoveryPath}'. Check the original path '{originalPath}' and the recycle bin before restoring. Do not overwrite an existing file.";
}
