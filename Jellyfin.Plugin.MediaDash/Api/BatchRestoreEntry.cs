namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// One row inside <see cref="BatchRestoreResult.Results"/>. Success rows carry
/// <see cref="RestoredTo"/> and <see cref="Suffixed"/>; failure rows carry
/// <see cref="Error"/>.
/// </summary>
public sealed class BatchRestoreEntry
{
    /// <summary>Gets or sets the bin path this row is reporting on (verbatim from the request).</summary>
    public string BinPath { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the restore succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the actual path the file was restored to (may include a <c>-restored</c> suffix on collision). Null on failure.</summary>
    public string? RestoredTo { get; set; }

    /// <summary>Gets or sets a value indicating whether a <c>-restored</c> suffix was applied because the original slot was occupied.</summary>
    public bool Suffixed { get; set; }

    /// <summary>Gets or sets a human-readable failure reason. Null on success.</summary>
    public string? Error { get; set; }
}
